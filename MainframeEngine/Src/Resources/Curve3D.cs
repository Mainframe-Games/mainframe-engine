using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A 3D cubic Bézier path (a subset of Godot's <c>Curve3D</c>): points with <c>in</c>/<c>out</c> control offsets and a
/// tilt, plus two per-point values Godot does not have, <see cref="GetPointWidth">width</see> (default 1 m) and
/// <see cref="GetPointDepth">depth</see> (default 0 m), used by <see cref="River3D"/>. The curve is baked into points at
/// equal arc length (<see cref="BakeInterval"/>) for sampling by distance (<see cref="SampleBaked"/>) and closest-point
/// queries (<see cref="GetClosestOffset"/>). A <see cref="Closed"/> curve adds the segment from the last point back to
/// the first; the bake also keeps a unit tangent per baked point and, with <see cref="UpVectorEnabled"/>, a
/// rotation-minimising up vector (ADR 0215), which <see cref="PathFollow3D"/> uses.
/// </summary>
/// <remarks>
/// <para>Storage is one exported float array, <see cref="FloatsPerPoint"/> floats per point (position, in, out, tilt,
/// width, depth): a compact JSON array. Every setter raises <see cref="Resource.Changed"/> and marks the bake dirty; the
/// next query re-bakes (the only allocation, and only when the baked point count grows).</para>
/// <para><b>Determinism:</b> baking uses only <c>+ − × ÷</c> on floats and <see cref="MathF.Sqrt"/> (correctly rounded
/// by IEEE 754; the .NET JIT never contracts them into FMAs), with scalar dot products in a fixed order, so a curve bakes
/// to the same bits on x64 and arm64. <see cref="Vector3.Dot"/>, <see cref="Vector3.Length"/> and
/// <see cref="Vector3.Lerp"/> are avoided there because their SIMD forms may round differently per CPU.</para>
/// <para>Up vectors of a closed curve spread the frame's leftover twist along the curve with <see cref="MathF.Sin"/> and
/// <see cref="MathF.Cos"/>, which are not guaranteed to round the same on every CPU; open curves keep the bit-exact
/// contract.</para>
/// <para>Sampling and closest-point queries do not allocate. Main thread only.</para>
/// </remarks>
[EditorIcon("line", Family = EditorIconFamily.Resource)]
public sealed class Curve3D : Resource
{
    /// <summary>Floats per point in <see cref="Points"/>: position (3), in (3), out (3), tilt, width, depth.</summary>
    public const int FloatsPerPoint = 12;

    private const int InOffset = 3;
    private const int OutOffset = 6;
    private const int TiltOffset = 9;
    private const int WidthOffset = 10;
    private const int DepthOffset = 11;

    /// <summary>Each Bézier segment is split at least 2^MinDepth times, at most 2^MaxDepth times.</summary>
    private const int MinDepth = 3;

    private const int MaxDepth = 10;

    private float[] _points = [];
    private float _bakeInterval = 0.2f;
    private bool _closed;
    private bool _upVectorEnabled;
    private bool _dirty = true;

    // Baked data: _bakedCount points at equal arc-length steps (_bakedStep) from 0 to _bakedLength.
    private Vector3[] _bakedPoints = [];
    private float[] _bakedDistances = [];
    private float[] _bakedTilts = [];
    private float[] _bakedWidths = [];
    private float[] _bakedDepths = [];
    private Vector3[] _bakedTangents = [];
    private Vector3[] _bakedUps = [];
    private int _bakedCount;
    private float _bakedLength;
    private float _bakedStep;

    // Bake scratch: the adaptively subdivided polyline, its cumulative lengths and curve parameters (segment + t).
    private Vector3[] _dense = [];
    private float[] _denseLengths = [];
    private float[] _denseParams = [];
    private int _denseCount;

    /// <summary>Distance between baked points in metres (Godot's <c>bake_interval</c>); the last step is shortened evenly.</summary>
    [Export(Range = "0.01,64,0.01")]
    public float BakeInterval
    {
        get => _bakeInterval;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "BakeInterval must be positive.");
            if (_bakeInterval == value)
                return;
            _bakeInterval = value;
            Touch();
        }
    }

    /// <summary>
    /// Whether the curve continues from the last point back to the first (its <c>out</c> to the first point's <c>in</c>):
    /// the seam is an ordinary segment, sampling and tangents are continuous across it, and
    /// <see cref="WrapOffset"/> wraps instead of clamping.
    /// </summary>
    [Export]
    public bool Closed
    {
        get => _closed;
        set
        {
            if (_closed == value)
                return;
            _closed = value;
            Touch();
        }
    }

    /// <summary>
    /// Bake rotation-minimising up vectors (Godot's <c>up_vector_enabled</c>, default off here so rivers bake as
    /// before): <see cref="SampleBakedUpVector"/> and <see cref="SampleBakedWithRotation"/> then never flip on vertical
    /// climbs or loops.
    /// </summary>
    [Export]
    public bool UpVectorEnabled
    {
        get => _upVectorEnabled;
        set
        {
            if (_upVectorEnabled == value)
                return;
            _upVectorEnabled = value;
            Touch();
        }
    }

    /// <summary>
    /// The points, <see cref="FloatsPerPoint"/> floats each (position, in, out, tilt, width, depth). Assigning replaces
    /// the curve (a length that is not a multiple of <see cref="FloatsPerPoint"/> throws); the array is the curve's own
    /// storage, so callers must not edit it in place.
    /// </summary>
    [Export]
    internal float[] Points
    {
        get => _points;
        set
        {
            value ??= [];
            if (value.Length % FloatsPerPoint != 0)
                throw new ArgumentException($"Curve3D.Points needs a multiple of {FloatsPerPoint} floats (got {value.Length}).", nameof(value));
            _points = value;
            Touch();
        }
    }

    /// <summary>Number of points.</summary>
    public int PointCount => _points.Length / FloatsPerPoint;

    /// <summary>
    /// Adds a point at <paramref name="index"/> (-1: at the end) with control offsets <paramref name="in"/> and
    /// <paramref name="out"/> relative to it, tilt 0, width 1 m and depth 0 m.
    /// </summary>
    public void AddPoint(Vector3 position, Vector3 @in = default, Vector3 @out = default, int index = -1)
    {
        var count = PointCount;
        if (index < 0 || index > count)
            index = count;
        var points = new float[_points.Length + FloatsPerPoint];
        Array.Copy(_points, 0, points, 0, index * FloatsPerPoint);
        Array.Copy(_points, index * FloatsPerPoint, points, (index + 1) * FloatsPerPoint, (count - index) * FloatsPerPoint);
        var o = index * FloatsPerPoint;
        Write(points, o, position);
        Write(points, o + InOffset, @in);
        Write(points, o + OutOffset, @out);
        points[o + TiltOffset] = 0f;
        points[o + WidthOffset] = 1f;
        points[o + DepthOffset] = 0f;
        _points = points;
        Touch();
    }

    /// <summary>Removes point <paramref name="index"/>.</summary>
    public void RemovePoint(int index)
    {
        CheckIndex(index);
        var points = new float[_points.Length - FloatsPerPoint];
        Array.Copy(_points, 0, points, 0, index * FloatsPerPoint);
        Array.Copy(_points, (index + 1) * FloatsPerPoint, points, index * FloatsPerPoint, points.Length - index * FloatsPerPoint);
        _points = points;
        Touch();
    }

    /// <summary>Removes every point.</summary>
    public void ClearPoints()
    {
        if (_points.Length == 0)
            return;
        _points = [];
        Touch();
    }

    public Vector3 GetPointPosition(int index) => ReadVector(index, 0);

    public void SetPointPosition(int index, Vector3 position) => WriteVector(index, 0, position);

    /// <summary>The incoming control point, relative to the point.</summary>
    public Vector3 GetPointIn(int index) => ReadVector(index, InOffset);

    public void SetPointIn(int index, Vector3 @in) => WriteVector(index, InOffset, @in);

    /// <summary>The outgoing control point, relative to the point.</summary>
    public Vector3 GetPointOut(int index) => ReadVector(index, OutOffset);

    public void SetPointOut(int index, Vector3 @out) => WriteVector(index, OutOffset, @out);

    /// <summary>Roll around the curve in radians (Godot's tilt), used by <see cref="SampleBakedWithRotation"/>.</summary>
    public float GetPointTilt(int index) => ReadScalar(index, TiltOffset);

    public void SetPointTilt(int index, float tilt) => WriteScalar(index, TiltOffset, tilt);

    /// <summary>Full width at the point in metres (extension; default 1). A river's half width is half of it.</summary>
    public float GetPointWidth(int index) => ReadScalar(index, WidthOffset);

    public void SetPointWidth(int index, float width) => WriteScalar(index, WidthOffset, width);

    /// <summary>Depth at the point in metres (extension; default 0): a river's water column on its centreline.</summary>
    public float GetPointDepth(int index) => ReadScalar(index, DepthOffset);

    public void SetPointDepth(int index, float depth) => WriteScalar(index, DepthOffset, depth);

    /// <summary>
    /// The Bézier position on segment <paramref name="index"/> (from point index to index + 1) at
    /// <paramref name="t"/> (0..1). Indices before the first or at/after the last point return that point (Godot); a
    /// <see cref="Closed"/> curve also has the seam segment, index <c>count − 1</c>.
    /// </summary>
    public Vector3 Sample(int index, float t)
    {
        var count = PointCount;
        if (count == 0)
            return Vector3.Zero;
        if (index < 0)
            return GetPointPosition(0);
        if (index >= SegmentCount)
            return GetPointPosition(count - 1);
        SegmentControls(index, out var p0, out var c0, out var c1, out var p1);
        return Bezier(p0, c0, c1, p1, t);
    }

    /// <summary>Number of Bézier segments: <c>count − 1</c>, plus the seam when <see cref="Closed"/> (0 below two points).</summary>
    public int SegmentCount => PointCount < 2 ? 0 : _closed ? PointCount : PointCount - 1;

    /// <summary>
    /// <paramref name="offset"/> brought onto the curve: modulo the baked length when <see cref="Closed"/> (negative
    /// offsets count back from the end), else clamped to 0..length.
    /// </summary>
    public float WrapOffset(float offset)
    {
        EnsureBaked();
        if (float.IsNaN(offset) || _bakedLength <= 0)
            return 0f;
        if (!_closed)
            return Math.Clamp(offset, 0f, _bakedLength);
        var wrapped = offset % _bakedLength;
        return wrapped < 0 ? wrapped + _bakedLength : wrapped;
    }

    /// <summary>
    /// Replaces the points with a smooth Bézier through <paramref name="points"/> (a Catmull-Rom spline converted to
    /// Bézier handles; <paramref name="tension"/> 0.5 is the uniform Catmull-Rom). Sets <see cref="Closed"/>. Extension.
    /// </summary>
    public void SetSmoothPoints(ReadOnlySpan<Vector3> points, bool closed, float tension = 0.5f)
    {
        var count = points.Length;
        var data = new float[count * FloatsPerPoint];
        for (var i = 0; i < count; i++)
        {
            var previous = i > 0 ? points[i - 1] : closed && count > 2 ? points[count - 1] : points[i];
            var next = i < count - 1 ? points[i + 1] : closed && count > 2 ? points[0] : points[i];
            var handle = (next - previous) * (tension / 3f);
            var o = i * FloatsPerPoint;
            Write(data, o, points[i]);
            Write(data, o + InOffset, -handle);
            Write(data, o + OutOffset, handle);
            data[o + WidthOffset] = 1f;
        }

        _points = data;
        _closed = closed && count > 2;
        Touch();
    }

    /// <summary>Length of the baked curve in metres (0 with fewer than two points).</summary>
    public float GetBakedLength()
    {
        EnsureBaked();
        return _bakedLength;
    }

    /// <summary>The baked points, equally spaced along the curve (valid until the curve changes).</summary>
    public ReadOnlySpan<Vector3> GetBakedPoints()
    {
        EnsureBaked();
        return _bakedPoints.AsSpan(0, _bakedCount);
    }

    /// <summary>Arc length of each baked point (0, step, 2 × step, …, length).</summary>
    public ReadOnlySpan<float> GetBakedDistances()
    {
        EnsureBaked();
        return _bakedDistances.AsSpan(0, _bakedCount);
    }

    /// <summary>
    /// The position <paramref name="offset"/> metres along the curve (clamped to 0..length): linear between baked
    /// points, or a Catmull-Rom cubic through them when <paramref name="cubic"/>.
    /// </summary>
    public Vector3 SampleBaked(float offset, bool cubic = false)
    {
        EnsureBaked();
        if (_bakedCount == 0)
            return Vector3.Zero;
        if (_bakedCount == 1)
            return _bakedPoints[0];
        var index = Locate(offset, out var t);
        var a = _bakedPoints[index];
        var b = _bakedPoints[index + 1];
        if (!cubic)
            return a + (b - a) * t;
        var pre = index > 0 ? _bakedPoints[index - 1] : _closed && _bakedCount > 2 ? _bakedPoints[_bakedCount - 2] : a;
        var post = index + 2 < _bakedCount ? _bakedPoints[index + 2] : _closed && _bakedCount > 2 ? _bakedPoints[1] : b;
        return CubicInterpolate(pre, a, b, post, t);
    }

    /// <summary>
    /// The unit direction of the curve at <paramref name="offset"/>: baked tangents (central differences, continuous
    /// across a closed seam) interpolated between baked points. <c>-Z</c> for a curve without length.
    /// </summary>
    public Vector3 SampleBakedTangent(float offset)
    {
        EnsureBaked();
        if (_bakedCount < 2 || _bakedLength <= 0)
            return -Vector3.UnitZ;
        var index = Locate(offset, out var t);
        return NormalizeOr(_bakedTangents[index] + (_bakedTangents[index + 1] - _bakedTangents[index]) * t, _bakedTangents[index]);
    }

    /// <summary>
    /// The baked rotation-minimising up vector at <paramref name="offset"/>, perpendicular to
    /// <see cref="SampleBakedTangent"/> and rolled by the tilt when <paramref name="applyTilt"/>. Without
    /// <see cref="UpVectorEnabled"/> it is world up made perpendicular to the tangent.
    /// </summary>
    public Vector3 SampleBakedUpVector(float offset, bool applyTilt = false)
    {
        var tangent = SampleBakedTangent(offset);
        Vector3 up;
        if (_upVectorEnabled && _bakedCount >= 2 && _bakedLength > 0)
        {
            var index = Locate(offset, out var t);
            up = _bakedUps[index] + (_bakedUps[index + 1] - _bakedUps[index]) * t;
        }
        else
        {
            up = Vector3.UnitY;
        }

        up = Perpendicular(up, tangent);
        if (applyTilt)
        {
            var tilt = SampleBakedTilt(offset);
            if (tilt != 0)
                up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(tangent, tilt));
        }

        return up;
    }

    /// <summary>
    /// A transform at <paramref name="offset"/>: the origin on the curve, <c>-Z</c> along the curve's interpolated
    /// tangent (the engine's forward, as Godot's <c>PathFollow3D.use_model_front</c>), <c>+Y</c> the baked up vector with
    /// <see cref="UpVectorEnabled"/>, else as close to world up as the direction allows, rolled by the interpolated tilt
    /// when <paramref name="applyTilt"/>. A zero-length curve gives the identity basis.
    /// </summary>
    public Transform3D SampleBakedWithRotation(float offset, bool cubic = false, bool applyTilt = false)
    {
        var origin = SampleBaked(offset, cubic);
        if (_bakedCount < 2 || _bakedLength <= 0)
            return new Transform3D(Basis.Identity, origin);
        var direction = SampleBakedTangent(offset);
        Basis basis;
        if (_upVectorEnabled)
        {
            var z = -direction;
            var y = SampleBakedUpVector(offset);
            basis = new Basis(Vector3.Cross(y, z), y, z);
        }
        else
        {
            basis = Transform3D.BasisLookingAlong(direction, Vector3.UnitY);
        }

        if (applyTilt)
        {
            var tilt = SampleBakedTilt(offset);
            if (tilt != 0)
            {
                var roll = Quaternion.CreateFromAxisAngle(direction, tilt);
                basis = new Basis(Vector3.Transform(basis.X, roll), Vector3.Transform(basis.Y, roll), basis.Z);
            }
        }

        return new Transform3D(basis, origin);
    }

    /// <summary>The tilt at <paramref name="offset"/> (smoothstep between points, linear between baked points).</summary>
    public float SampleBakedTilt(float offset) => SampleScalar(_bakedTilts, offset, 0f);

    /// <summary>The full width at <paramref name="offset"/> (extension; smoothstep between points).</summary>
    public float SampleBakedWidth(float offset) => SampleScalar(_bakedWidths, offset, 1f);

    /// <summary>The depth at <paramref name="offset"/> (extension; smoothstep between points).</summary>
    public float SampleBakedDepth(float offset) => SampleScalar(_bakedDepths, offset, 0f);

    /// <summary>The arc offset of the baked curve point closest to <paramref name="toPoint"/> (projected on each baked segment).</summary>
    public float GetClosestOffset(Vector3 toPoint)
    {
        ClosestOnBaked(toPoint, out var offset, out _);
        return offset;
    }

    /// <summary>The baked curve point closest to <paramref name="toPoint"/>.</summary>
    public Vector3 GetClosestPoint(Vector3 toPoint)
    {
        ClosestOnBaked(toPoint, out _, out var point);
        return point;
    }

    // ------------------------------------------------------------------------------------------------
    // Baking
    // ------------------------------------------------------------------------------------------------

    private void EnsureBaked()
    {
        if (_dirty)
            Bake();
    }

    private void Bake()
    {
        _dirty = false;
        var count = PointCount;
        _bakedLength = 0;
        _bakedStep = 0;
        if (count == 0)
        {
            _bakedCount = 0;
            return;
        }

        if (count == 1)
        {
            EnsureBakedCapacity(1);
            _bakedCount = 1;
            _bakedPoints[0] = GetPointPosition(0);
            _bakedDistances[0] = 0;
            _bakedTilts[0] = GetPointTilt(0);
            _bakedWidths[0] = GetPointWidth(0);
            _bakedDepths[0] = GetPointDepth(0);
            _bakedTangents[0] = -Vector3.UnitZ;
            _bakedUps[0] = Vector3.UnitY;
            return;
        }

        var segments = SegmentCount;
        // 1. Adaptive subdivision of every segment into a dense polyline (fixed recursion order: deterministic).
        var tolerance = MathF.Max(_bakeInterval * 0.01f, 1e-4f);
        _denseCount = 0;
        SegmentControls(0, out var first, out _, out _, out _);
        AppendDense(first, 0f, 0f);
        for (var segment = 0; segment < segments; segment++)
        {
            SegmentControls(segment, out var p0, out var c0, out var c1, out var p1);
            Subdivide(segment, p0, c0, c1, p1, 0f, p0, 1f, p1, 0, tolerance * tolerance);
        }

        var length = _denseLengths[_denseCount - 1];
        // 2. Resample at equal arc length.
        var steps = length > 0 ? Math.Max(1, (int)MathF.Ceiling(length / _bakeInterval)) : 1;
        EnsureBakedCapacity(steps + 1);
        _bakedCount = steps + 1;
        _bakedLength = length;
        _bakedStep = length / steps;
        var j = 0;
        for (var i = 0; i <= steps; i++)
        {
            var target = i == steps ? length : _bakedStep * i;
            while (j < _denseCount - 2 && _denseLengths[j + 1] < target)
                j++;
            var la = _denseLengths[j];
            var span = _denseLengths[j + 1] - la;
            var f = span > 0 ? Math.Clamp((target - la) / span, 0f, 1f) : 0f;
            var a = _dense[j];
            var b = _dense[j + 1];
            _bakedPoints[i] = a + (b - a) * f;
            _bakedDistances[i] = target;
            var param = _denseParams[j] + (_denseParams[j + 1] - _denseParams[j]) * f;
            var seg = Math.Min((int)param, segments - 1);
            var s = Smoothstep(param - seg);
            var end = seg + 1 == count ? 0 : seg + 1;
            _bakedTilts[i] = Mix(ReadScalar(seg, TiltOffset), ReadScalar(end, TiltOffset), s);
            _bakedWidths[i] = Mix(ReadScalar(seg, WidthOffset), ReadScalar(end, WidthOffset), s);
            _bakedDepths[i] = Mix(ReadScalar(seg, DepthOffset), ReadScalar(end, DepthOffset), s);
        }

        BakeTangents();
        if (_upVectorEnabled)
            BakeUpVectors();
    }

    // 3. Unit tangents by central differences of the baked points (one-sided at open ends, wrapped across a closed
    // seam, where the first and last baked points coincide). A degenerate difference repeats the previous tangent.
    private void BakeTangents()
    {
        var n = _bakedCount;
        var wrap = _closed && n > 2;
        var previousTangent = -Vector3.UnitZ;
        for (var i = 0; i < n; i++)
        {
            var before = i > 0 ? _bakedPoints[i - 1] : wrap ? _bakedPoints[n - 2] : _bakedPoints[i];
            var after = i < n - 1 ? _bakedPoints[i + 1] : wrap ? _bakedPoints[1] : _bakedPoints[i];
            var tangent = NormalizeOr(after - before, previousTangent);
            _bakedTangents[i] = tangent;
            previousTangent = tangent;
        }
    }

    // 4. Rotation-minimising up vectors by double reflection (Wang et al. 2008), starting from world up made
    // perpendicular to the first tangent. A closed curve spreads the leftover twist at the seam linearly by arc length.
    private void BakeUpVectors()
    {
        var n = _bakedCount;
        _bakedUps[0] = Perpendicular(Vector3.UnitY, _bakedTangents[0]);
        for (var i = 0; i < n - 1; i++)
        {
            var r = _bakedUps[i];
            var t = _bakedTangents[i];
            var v1 = _bakedPoints[i + 1] - _bakedPoints[i];
            var c1 = Dot(v1, v1);
            if (c1 > 1e-12f)
            {
                r -= v1 * (2f / c1 * Dot(v1, r));
                t -= v1 * (2f / c1 * Dot(v1, t));
            }

            var v2 = _bakedTangents[i + 1] - t;
            var c2 = Dot(v2, v2);
            if (c2 > 1e-12f)
                r -= v2 * (2f / c2 * Dot(v2, r));
            _bakedUps[i + 1] = Perpendicular(r, _bakedTangents[i + 1]);
        }

        if (!_closed || n < 3 || _bakedLength <= 0)
            return;
        var first = _bakedUps[0];
        var last = _bakedUps[n - 1];
        var twist = MathF.Atan2(Dot(Cross(last, first), _bakedTangents[0]), Dot(last, first));
        for (var i = 1; i < n; i++)
        {
            var angle = twist * (_bakedDistances[i] / _bakedLength);
            var axis = _bakedTangents[i];
            var up = _bakedUps[i];
            // Rodrigues around the tangent (up is perpendicular to it).
            _bakedUps[i] = Perpendicular(up * MathF.Cos(angle) + Cross(axis, up) * MathF.Sin(angle), axis);
        }
    }

    private void Subdivide(int segment, Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t0, Vector3 a, float t1, Vector3 b,
        int depth, float toleranceSquared)
    {
        var tm = (t0 + t1) * 0.5f;
        var m = Bezier(p0, c0, c1, p1, tm);
        var chordMid = (a + b) * 0.5f;
        if (depth < MaxDepth && (depth < MinDepth || DistanceSquared(m, chordMid) > toleranceSquared))
        {
            Subdivide(segment, p0, c0, c1, p1, t0, a, tm, m, depth + 1, toleranceSquared);
            Subdivide(segment, p0, c0, c1, p1, tm, m, t1, b, depth + 1, toleranceSquared);
            return;
        }

        AppendDense(b, segment + t1, MathF.Sqrt(DistanceSquared(a, b)));
    }

    private void AppendDense(Vector3 point, float param, float pieceLength)
    {
        if (_denseCount == _dense.Length)
        {
            var size = Math.Max(64, _dense.Length * 2);
            Array.Resize(ref _dense, size);
            Array.Resize(ref _denseLengths, size);
            Array.Resize(ref _denseParams, size);
        }

        _dense[_denseCount] = point;
        _denseLengths[_denseCount] = _denseCount == 0 ? 0f : _denseLengths[_denseCount - 1] + pieceLength;
        _denseParams[_denseCount] = param;
        _denseCount++;
    }

    private void EnsureBakedCapacity(int count)
    {
        if (_bakedPoints.Length >= count)
            return;
        _bakedPoints = new Vector3[count];
        _bakedDistances = new float[count];
        _bakedTilts = new float[count];
        _bakedWidths = new float[count];
        _bakedDepths = new float[count];
        _bakedTangents = new Vector3[count];
        _bakedUps = new Vector3[count];
    }

    // ------------------------------------------------------------------------------------------------
    // Sampling helpers
    // ------------------------------------------------------------------------------------------------

    /// <summary>The baked segment holding <paramref name="offset"/> (clamped) and the fraction along it.</summary>
    private int Locate(float offset, out float t)
    {
        var o = float.IsNaN(offset) ? 0f : Math.Clamp(offset, 0f, _bakedLength);
        var index = _bakedStep > 0 ? Math.Min((int)(o / _bakedStep), _bakedCount - 2) : 0;
        var span = _bakedDistances[index + 1] - _bakedDistances[index];
        t = span > 0 ? Math.Clamp((o - _bakedDistances[index]) / span, 0f, 1f) : 0f;
        return index;
    }

    private float SampleScalar(float[] baked, float offset, float empty)
    {
        EnsureBaked();
        if (_bakedCount == 0)
            return empty;
        if (_bakedCount == 1)
            return baked[0];
        var index = Locate(offset, out var t);
        return Mix(baked[index], baked[index + 1], t);
    }

    private void ClosestOnBaked(Vector3 toPoint, out float offset, out Vector3 point)
    {
        EnsureBaked();
        offset = 0;
        point = Vector3.Zero;
        if (_bakedCount == 0)
            return;
        point = _bakedPoints[0];
        if (_bakedCount == 1)
            return;
        var best = float.PositiveInfinity;
        for (var i = 0; i < _bakedCount - 1; i++)
        {
            var a = _bakedPoints[i];
            var d = _bakedPoints[i + 1] - a;
            var lengthSquared = Dot(d, d);
            var t = lengthSquared > 0 ? Math.Clamp(Dot(toPoint - a, d) / lengthSquared, 0f, 1f) : 0f;
            var q = a + d * t;
            var distance = DistanceSquared(q, toPoint);
            if (distance < best)
            {
                best = distance;
                point = q;
                offset = _bakedDistances[i] + (_bakedDistances[i + 1] - _bakedDistances[i]) * t;
            }
        }
    }

    private void SegmentControls(int segment, out Vector3 p0, out Vector3 c0, out Vector3 c1, out Vector3 p1)
    {
        var end = segment + 1 == PointCount ? 0 : segment + 1; // the closed seam
        p0 = GetPointPosition(segment);
        c0 = p0 + GetPointOut(segment);
        p1 = GetPointPosition(end);
        c1 = p1 + GetPointIn(end);
    }

    internal static Vector3 Bezier(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
    {
        var u = 1f - t;
        var uu = u * u;
        var tt = t * t;
        return p0 * (uu * u) + c0 * (3f * uu * t) + c1 * (3f * u * tt) + p1 * (tt * t);
    }

    // Godot's cubic_interpolate (Catmull-Rom through pre, from, to, post).
    private static Vector3 CubicInterpolate(Vector3 pre, Vector3 from, Vector3 to, Vector3 post, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (from * 2f + (to - pre) * t + (pre * 2f - from * 5f + to * 4f - post) * t2 + (from * 3f - pre - to * 3f + post) * t3) * 0.5f;
    }

    // Scalar, fixed-order arithmetic (see the determinism remark).
    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
    {
        var lengthSquared = Dot(v, v);
        return lengthSquared > 1e-20f ? v * (1f / MathF.Sqrt(lengthSquared)) : fallback;
    }

    // v with its component along the unit vector `axis` removed, normalised; another perpendicular when v is parallel.
    private static Vector3 Perpendicular(Vector3 v, Vector3 axis)
    {
        var p = v - axis * Dot(v, axis);
        if (Dot(p, p) > 1e-12f)
            return NormalizeOr(p, Vector3.UnitY);
        var other = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        return NormalizeOr(other - axis * Dot(other, axis), Vector3.UnitY);
    }

    private static float DistanceSquared(Vector3 a, Vector3 b)
    {
        var d = a - b;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
    }

    private static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    // ------------------------------------------------------------------------------------------------
    // Storage
    // ------------------------------------------------------------------------------------------------

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)PointCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Curve3D has {PointCount} points.");
    }

    private Vector3 ReadVector(int index, int offset)
    {
        CheckIndex(index);
        var o = index * FloatsPerPoint + offset;
        return new Vector3(_points[o], _points[o + 1], _points[o + 2]);
    }

    private void WriteVector(int index, int offset, Vector3 value)
    {
        CheckIndex(index);
        Write(_points, index * FloatsPerPoint + offset, value);
        Touch();
    }

    private float ReadScalar(int index, int offset)
    {
        CheckIndex(index);
        return _points[index * FloatsPerPoint + offset];
    }

    private void WriteScalar(int index, int offset, float value)
    {
        CheckIndex(index);
        _points[index * FloatsPerPoint + offset] = value;
        Touch();
    }

    private static void Write(float[] points, int o, Vector3 value)
    {
        points[o] = value.X;
        points[o + 1] = value.Y;
        points[o + 2] = value.Z;
    }

    private void Touch()
    {
        _dirty = true;
        EmitChanged();
    }
}
