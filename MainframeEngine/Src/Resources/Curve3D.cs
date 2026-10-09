using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A 3D cubic Bézier path (a subset of Godot's <c>Curve3D</c>): points with <c>in</c>/<c>out</c> control offsets and a
/// tilt, plus two per-point values Godot does not have, <see cref="GetPointWidth">width</see> (default 1 m) and
/// <see cref="GetPointDepth">depth</see> (default 0 m), used by <see cref="River3D"/>. The curve is baked into points at
/// equal arc length (<see cref="BakeInterval"/>) for sampling by distance (<see cref="SampleBaked"/>) and closest-point
/// queries (<see cref="GetClosestOffset"/>).
/// </summary>
/// <remarks>
/// <para>Storage is one exported float array, <see cref="FloatsPerPoint"/> floats per point (position, in, out, tilt,
/// width, depth): a compact JSON array. Every setter raises <see cref="Resource.Changed"/> and marks the bake dirty; the
/// next query re-bakes (the only allocation, and only when the baked point count grows).</para>
/// <para><b>Determinism:</b> baking uses only <c>+ − × ÷</c> on floats and <see cref="MathF.Sqrt"/> (correctly rounded
/// by IEEE 754; the .NET JIT never contracts them into FMAs), with scalar dot products in a fixed order, so a curve bakes
/// to the same bits on x64 and arm64. <see cref="Vector3.Dot"/>, <see cref="Vector3.Length"/> and
/// <see cref="Vector3.Lerp"/> are avoided there because their SIMD forms may round differently per CPU.</para>
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
    private bool _dirty = true;

    // Baked data: _bakedCount points at equal arc-length steps (_bakedStep) from 0 to _bakedLength.
    private Vector3[] _bakedPoints = [];
    private float[] _bakedDistances = [];
    private float[] _bakedTilts = [];
    private float[] _bakedWidths = [];
    private float[] _bakedDepths = [];
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
    /// <paramref name="t"/> (0..1). Indices before the first or at/after the last point return that point (Godot).
    /// </summary>
    public Vector3 Sample(int index, float t)
    {
        var count = PointCount;
        if (count == 0)
            return Vector3.Zero;
        if (index < 0)
            return GetPointPosition(0);
        if (index >= count - 1)
            return GetPointPosition(count - 1);
        SegmentControls(index, out var p0, out var c0, out var c1, out var p1);
        return Bezier(p0, c0, c1, p1, t);
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
        var pre = index > 0 ? _bakedPoints[index - 1] : a;
        var post = index + 2 < _bakedCount ? _bakedPoints[index + 2] : b;
        return CubicInterpolate(pre, a, b, post, t);
    }

    /// <summary>
    /// A transform at <paramref name="offset"/>: the origin on the curve, <c>-Z</c> along the curve's direction (the
    /// engine's forward, as Godot's <c>PathFollow3D.use_model_front</c>), <c>+Y</c> as close to world up as the direction
    /// allows, rolled by the interpolated tilt when <paramref name="applyTilt"/>. A zero-length curve gives the identity basis.
    /// </summary>
    public Transform3D SampleBakedWithRotation(float offset, bool cubic = false, bool applyTilt = false)
    {
        var origin = SampleBaked(offset, cubic);
        if (_bakedCount < 2 || _bakedLength <= 0)
            return new Transform3D(Basis.Identity, origin);
        var index = Locate(offset, out _);
        var direction = _bakedPoints[index + 1] - _bakedPoints[index];
        if (direction.LengthSquared() < 1e-20f)
            return new Transform3D(Basis.Identity, origin);
        var basis = Transform3D.BasisLookingAlong(direction, Vector3.UnitY);
        if (applyTilt)
        {
            var tilt = SampleBakedTilt(offset);
            if (tilt != 0)
            {
                var roll = Quaternion.CreateFromAxisAngle(Vector3.Normalize(direction), tilt);
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
            return;
        }

        // 1. Adaptive subdivision of every segment into a dense polyline (fixed recursion order: deterministic).
        var tolerance = MathF.Max(_bakeInterval * 0.01f, 1e-4f);
        _denseCount = 0;
        SegmentControls(0, out var first, out _, out _, out _);
        AppendDense(first, 0f, 0f);
        for (var segment = 0; segment < count - 1; segment++)
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
            var seg = Math.Min((int)param, count - 2);
            var s = Smoothstep(param - seg);
            _bakedTilts[i] = Mix(ReadScalar(seg, TiltOffset), ReadScalar(seg + 1, TiltOffset), s);
            _bakedWidths[i] = Mix(ReadScalar(seg, WidthOffset), ReadScalar(seg + 1, WidthOffset), s);
            _bakedDepths[i] = Mix(ReadScalar(seg, DepthOffset), ReadScalar(seg + 1, DepthOffset), s);
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
        p0 = GetPointPosition(segment);
        c0 = p0 + GetPointOut(segment);
        p1 = GetPointPosition(segment + 1);
        c1 = p1 + GetPointIn(segment + 1);
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
