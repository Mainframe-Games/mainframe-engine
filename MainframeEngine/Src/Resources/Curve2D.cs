using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A 2D cubic Bézier path in pixels (Godot's <c>Curve2D</c>, G13): points with <c>in</c>/<c>out</c> control offsets,
/// baked into points at equal arc length (<see cref="BakeInterval"/>) for sampling by distance
/// (<see cref="SampleBaked"/>) and closest-point queries (<see cref="GetClosestOffset"/>). A <see cref="Closed"/> curve
/// adds the segment from the last point back to the first. <see cref="Path2D"/> holds one; <see cref="PathFollow2D"/>
/// follows it.
/// </summary>
/// <remarks>
/// <para>Storage is one exported float array, <see cref="FloatsPerPoint"/> floats per point (position, in, out). Every
/// setter raises <see cref="Resource.Changed"/> and marks the bake dirty; the next query re-bakes (the only allocation,
/// and only when the baked point count grows).</para>
/// <para>The bake is <see cref="Curve3D"/>'s (<see cref="CurveBaker"/>, z = 0) and as deterministic: the same bits on x64
/// and arm64. Sampling and closest-point queries do not allocate. Main thread only.</para>
/// </remarks>
[EditorIcon("vector-bezier", Family = EditorIconFamily.Resource)]
public sealed class Curve2D : Resource
{
    /// <summary>Floats per point in <see cref="Points"/>: position (2), in (2), out (2).</summary>
    public const int FloatsPerPoint = 6;

    private const int InOffset = 2;
    private const int OutOffset = 4;

    private float[] _points = [];
    private float _bakeInterval = 5f;
    private bool _closed;
    private bool _dirty = true;

    private readonly CurveBaker _baker = new();
    private Vector2[] _bakedPoints = [];

    /// <summary>Distance between baked points in pixels (Godot's <c>bake_interval</c>); the last step is shortened evenly.</summary>
    [Export(Range = "0.01,1024,0.01")]
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
    /// The points, <see cref="FloatsPerPoint"/> floats each (position, in, out). Assigning replaces the curve (a length
    /// that is not a multiple of <see cref="FloatsPerPoint"/> throws); the array is the curve's own storage, so callers
    /// must not edit it in place.
    /// </summary>
    [Export]
    internal float[] Points
    {
        get => _points;
        set
        {
            value ??= [];
            if (value.Length % FloatsPerPoint != 0)
                throw new ArgumentException($"Curve2D.Points needs a multiple of {FloatsPerPoint} floats (got {value.Length}).", nameof(value));
            _points = value;
            Touch();
        }
    }

    /// <summary>Number of points.</summary>
    public int PointCount => _points.Length / FloatsPerPoint;

    /// <summary>Number of Bézier segments: <c>count − 1</c>, plus the seam when <see cref="Closed"/> (0 below two points).</summary>
    public int SegmentCount => PointCount < 2 ? 0 : _closed ? PointCount : PointCount - 1;

    /// <summary>
    /// Adds a point at <paramref name="index"/> (-1: at the end) with control offsets <paramref name="in"/> and
    /// <paramref name="out"/> relative to it.
    /// </summary>
    public void AddPoint(Vector2 position, Vector2 @in = default, Vector2 @out = default, int index = -1)
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

    public Vector2 GetPointPosition(int index) => Read(index, 0);

    public void SetPointPosition(int index, Vector2 position) => WriteAt(index, 0, position);

    /// <summary>The incoming control point, relative to the point.</summary>
    public Vector2 GetPointIn(int index) => Read(index, InOffset);

    public void SetPointIn(int index, Vector2 @in) => WriteAt(index, InOffset, @in);

    /// <summary>The outgoing control point, relative to the point.</summary>
    public Vector2 GetPointOut(int index) => Read(index, OutOffset);

    public void SetPointOut(int index, Vector2 @out) => WriteAt(index, OutOffset, @out);

    /// <summary>
    /// The Bézier position on segment <paramref name="index"/> (from point index to index + 1) at <paramref name="t"/>
    /// (0..1). Indices before the first or at/after the last segment return that end point (Godot); a
    /// <see cref="Closed"/> curve also has the seam segment, index <c>count − 1</c>.
    /// </summary>
    public Vector2 Sample(int index, float t)
    {
        var count = PointCount;
        if (count == 0)
            return Vector2.Zero;
        if (index < 0)
            return GetPointPosition(0);
        if (index >= SegmentCount)
            return GetPointPosition(count - 1);
        SegmentControls(index, out var p0, out var c0, out var c1, out var p1);
        return Flat(CurveBaker.Bezier(p0, c0, c1, p1, t));
    }

    /// <summary>
    /// <paramref name="offset"/> brought onto the curve: modulo the baked length when <see cref="Closed"/> (negative
    /// offsets count back from the end), else clamped to 0..length.
    /// </summary>
    public float WrapOffset(float offset)
    {
        EnsureBaked();
        return _baker.WrapOffset(offset);
    }

    /// <summary>
    /// Replaces the points with a smooth Bézier through <paramref name="points"/> (a Catmull-Rom spline converted to
    /// Bézier handles; <paramref name="tension"/> 0.5 is the uniform Catmull-Rom). Sets <see cref="Closed"/>. Extension.
    /// </summary>
    public void SetSmoothPoints(ReadOnlySpan<Vector2> points, bool closed, float tension = 0.5f)
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
        }

        _points = data;
        _closed = closed && count > 2;
        Touch();
    }

    /// <summary>
    /// Reverses the curve: the points in the opposite order with their <c>in</c> and <c>out</c> swapped, so the shape is
    /// unchanged. Extension.
    /// </summary>
    public void Reverse()
    {
        var count = PointCount;
        if (count < 2)
            return;
        var points = new float[_points.Length];
        for (var i = 0; i < count; i++)
        {
            var from = (count - 1 - i) * FloatsPerPoint;
            var o = i * FloatsPerPoint;
            Array.Copy(_points, from, points, o, 2);
            Array.Copy(_points, from + OutOffset, points, o + InOffset, 2);
            Array.Copy(_points, from + InOffset, points, o + OutOffset, 2);
        }

        _points = points;
        Touch();
    }

    /// <summary>Length of the baked curve in pixels (0 with fewer than two points).</summary>
    public float GetBakedLength()
    {
        EnsureBaked();
        return _baker.Length;
    }

    /// <summary>The baked points, equally spaced along the curve (valid until the curve changes).</summary>
    public ReadOnlySpan<Vector2> GetBakedPoints()
    {
        EnsureBaked();
        return _bakedPoints.AsSpan(0, _baker.Count);
    }

    /// <summary>Arc length of each baked point (0, step, 2 × step, …, length).</summary>
    public ReadOnlySpan<float> GetBakedDistances()
    {
        EnsureBaked();
        return _baker.Distances.AsSpan(0, _baker.Count);
    }

    /// <summary>
    /// The position <paramref name="offset"/> pixels along the curve (clamped to 0..length): linear between baked points,
    /// or a Catmull-Rom cubic through them when <paramref name="cubic"/>.
    /// </summary>
    public Vector2 SampleBaked(float offset, bool cubic = false)
    {
        EnsureBaked();
        return Flat(_baker.SampleBaked(offset, cubic));
    }

    /// <summary>
    /// The unit direction of the curve at <paramref name="offset"/>: baked tangents interpolated between baked points,
    /// continuous across a closed seam. <c>+X</c> for a curve without length.
    /// </summary>
    public Vector2 SampleBakedTangent(float offset)
    {
        EnsureBaked();
        return Flat(_baker.SampleTangent(offset, Vector3.UnitX));
    }

    /// <summary>
    /// A transform at <paramref name="offset"/> (Godot): the origin on the curve, <c>+X</c> along the interpolated tangent
    /// and <c>+Y</c> 90° clockwise from it on screen (y down). A zero-length curve gives the identity basis.
    /// </summary>
    public Transform2D SampleBakedWithRotation(float offset, bool cubic = false)
    {
        var origin = SampleBaked(offset, cubic);
        var x = SampleBakedTangent(offset);
        return new Transform2D(x, new Vector2(-x.Y, x.X), origin);
    }

    /// <summary>The arc offset of the baked curve point closest to <paramref name="toPoint"/> (projected on each baked segment).</summary>
    public float GetClosestOffset(Vector2 toPoint)
    {
        EnsureBaked();
        _baker.Closest(new Vector3(toPoint, 0f), out var offset, out _);
        return offset;
    }

    /// <summary>The baked curve point closest to <paramref name="toPoint"/>.</summary>
    public Vector2 GetClosestPoint(Vector2 toPoint)
    {
        EnsureBaked();
        _baker.Closest(new Vector3(toPoint, 0f), out _, out var point);
        return Flat(point);
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
        if (count == 0)
            _baker.BakeEmpty();
        else if (count == 1)
            _baker.BakeSingle(new Vector3(GetPointPosition(0), 0f));
        else
        {
            var segments = SegmentCount;
            var controls = _baker.PrepareControls(segments);
            for (var segment = 0; segment < segments; segment++)
            {
                SegmentControls(segment, out var p0, out var c0, out var c1, out var p1);
                controls[segment * 4] = p0;
                controls[segment * 4 + 1] = c0;
                controls[segment * 4 + 2] = c1;
                controls[segment * 4 + 3] = p1;
            }

            _baker.Bake(segments, _bakeInterval, _closed);
        }

        if (_bakedPoints.Length < _baker.Points.Length)
            _bakedPoints = new Vector2[_baker.Points.Length];
        var baked = _baker.Points;
        for (var i = 0; i < _baker.Count; i++)
            _bakedPoints[i] = Flat(baked[i]);
    }

    private void SegmentControls(int segment, out Vector3 p0, out Vector3 c0, out Vector3 c1, out Vector3 p1)
    {
        var end = segment + 1 == PointCount ? 0 : segment + 1; // the closed seam
        var a = GetPointPosition(segment);
        var b = GetPointPosition(end);
        p0 = new Vector3(a, 0f);
        c0 = new Vector3(a + GetPointOut(segment), 0f);
        p1 = new Vector3(b, 0f);
        c1 = new Vector3(b + GetPointIn(end), 0f);
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    // ------------------------------------------------------------------------------------------------
    // Storage
    // ------------------------------------------------------------------------------------------------

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)PointCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Curve2D has {PointCount} points.");
    }

    private Vector2 Read(int index, int offset)
    {
        CheckIndex(index);
        var o = index * FloatsPerPoint + offset;
        return new Vector2(_points[o], _points[o + 1]);
    }

    private void WriteAt(int index, int offset, Vector2 value)
    {
        CheckIndex(index);
        Write(_points, index * FloatsPerPoint + offset, value);
        Touch();
    }

    private static void Write(float[] points, int o, Vector2 value)
    {
        points[o] = value.X;
        points[o + 1] = value.Y;
    }

    private void Touch()
    {
        _dirty = true;
        EmitChanged();
    }
}
