using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The bake shared by <see cref="Curve3D"/> and <see cref="Curve2D"/> (G13): cubic Bézier segments are adaptively
/// subdivided into a dense polyline and resampled at equal arc length, with a curve parameter (segment + t) and a unit
/// tangent per baked point. 2D curves pass z = 0. The owner fills <see cref="PrepareControls"/> and calls
/// <see cref="Bake"/>; it keeps any extra per-point data (tilt, width, up vectors) itself, from <see cref="Params"/>.
/// </summary>
/// <remarks>
/// Deterministic: only <c>+ − × ÷</c> and <see cref="MathF.Sqrt"/> on floats, scalar dot products in a fixed order (see
/// <see cref="Curve3D"/>'s determinism remark), so a curve bakes to the same bits on x64 and arm64. Arrays grow only when
/// the point count does; queries allocate nothing.
/// </remarks>
internal sealed class CurveBaker
{
    /// <summary>Each Bézier segment is split at least 2^MinDepth times, at most 2^MaxDepth times.</summary>
    private const int MinDepth = 3;

    private const int MaxDepth = 10;

    private Vector3[] _controls = [];

    // Scratch: the adaptively subdivided polyline, its cumulative lengths and curve parameters (segment + t).
    private Vector3[] _dense = [];
    private float[] _denseLengths = [];
    private float[] _denseParams = [];
    private int _denseCount;

    /// <summary>The baked points (the first <see cref="Count"/> are valid).</summary>
    public Vector3[] Points { get; private set; } = [];

    /// <summary>Arc length of each baked point (0, step, 2 × step, …, length).</summary>
    public float[] Distances { get; private set; } = [];

    /// <summary>Curve parameter of each baked point: segment index + t.</summary>
    public float[] Params { get; private set; } = [];

    /// <summary>Unit tangent of each baked point (central differences; wrapped across a closed seam).</summary>
    public Vector3[] Tangents { get; private set; } = [];

    /// <summary>Number of baked points.</summary>
    public int Count { get; private set; }

    /// <summary>Baked length.</summary>
    public float Length { get; private set; }

    /// <summary>Arc length between baked points.</summary>
    public float Step { get; private set; }

    /// <summary>Whether the last bake was of a closed curve.</summary>
    public bool Closed { get; private set; }

    /// <summary>Space for <paramref name="segments"/> × (p0, c0, c1, p1) absolute control points, for the owner to fill.</summary>
    public Span<Vector3> PrepareControls(int segments)
    {
        var needed = segments * 4;
        if (_controls.Length < needed)
            _controls = new Vector3[needed];
        return _controls.AsSpan(0, needed);
    }

    /// <summary>No points: an empty bake.</summary>
    public void BakeEmpty()
    {
        Count = 0;
        Length = 0;
        Step = 0;
        Closed = false;
    }

    /// <summary>One point: a single baked point with tangent <c>-Z</c>.</summary>
    public void BakeSingle(Vector3 point)
    {
        EnsureCapacity(1);
        Count = 1;
        Length = 0;
        Step = 0;
        Closed = false;
        Points[0] = point;
        Distances[0] = 0;
        Params[0] = 0;
        Tangents[0] = -Vector3.UnitZ;
    }

    /// <summary>Bakes the <paramref name="segments"/> (≥ 1) segments written by <see cref="PrepareControls"/>.</summary>
    public void Bake(int segments, float interval, bool closed)
    {
        Closed = closed;
        // 1. Adaptive subdivision of every segment into a dense polyline (fixed recursion order: deterministic).
        var tolerance = MathF.Max(interval * 0.01f, 1e-4f);
        _denseCount = 0;
        AppendDense(_controls[0], 0f, 0f);
        for (var segment = 0; segment < segments; segment++)
        {
            var o = segment * 4;
            var p0 = _controls[o];
            var c0 = _controls[o + 1];
            var c1 = _controls[o + 2];
            var p1 = _controls[o + 3];
            Subdivide(segment, p0, c0, c1, p1, 0f, p0, 1f, p1, 0, tolerance * tolerance);
        }

        var length = _denseLengths[_denseCount - 1];
        // 2. Resample at equal arc length.
        var steps = length > 0 ? Math.Max(1, (int)MathF.Ceiling(length / interval)) : 1;
        EnsureCapacity(steps + 1);
        Count = steps + 1;
        Length = length;
        Step = length / steps;
        var j = 0;
        for (var i = 0; i <= steps; i++)
        {
            var target = i == steps ? length : Step * i;
            while (j < _denseCount - 2 && _denseLengths[j + 1] < target)
                j++;
            var la = _denseLengths[j];
            var span = _denseLengths[j + 1] - la;
            var f = span > 0 ? Math.Clamp((target - la) / span, 0f, 1f) : 0f;
            var a = _dense[j];
            var b = _dense[j + 1];
            Points[i] = a + (b - a) * f;
            Distances[i] = target;
            Params[i] = _denseParams[j] + (_denseParams[j + 1] - _denseParams[j]) * f;
        }

        BakeTangents();
    }

    // 3. Unit tangents by central differences of the baked points (one-sided at open ends, wrapped across a closed
    // seam, where the first and last baked points coincide). A degenerate difference repeats the previous tangent.
    private void BakeTangents()
    {
        var n = Count;
        var wrap = Closed && n > 2;
        var previousTangent = -Vector3.UnitZ;
        for (var i = 0; i < n; i++)
        {
            var before = i > 0 ? Points[i - 1] : wrap ? Points[n - 2] : Points[i];
            var after = i < n - 1 ? Points[i + 1] : wrap ? Points[1] : Points[i];
            var tangent = NormalizeOr(after - before, previousTangent);
            Tangents[i] = tangent;
            previousTangent = tangent;
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

    private void EnsureCapacity(int count)
    {
        if (Points.Length >= count)
            return;
        Points = new Vector3[count];
        Distances = new float[count];
        Params = new float[count];
        Tangents = new Vector3[count];
    }

    // ------------------------------------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------------------------------------

    /// <summary>The baked segment holding <paramref name="offset"/> (clamped) and the fraction along it. Needs two points.</summary>
    public int Locate(float offset, out float t)
    {
        var o = float.IsNaN(offset) ? 0f : Math.Clamp(offset, 0f, Length);
        var index = Step > 0 ? Math.Min((int)(o / Step), Count - 2) : 0;
        var span = Distances[index + 1] - Distances[index];
        t = span > 0 ? Math.Clamp((o - Distances[index]) / span, 0f, 1f) : 0f;
        return index;
    }

    /// <summary>
    /// <paramref name="offset"/> brought onto the curve: modulo the length when closed (negative offsets count back from
    /// the end), else clamped to 0..length.
    /// </summary>
    public float WrapOffset(float offset)
    {
        if (float.IsNaN(offset) || Length <= 0)
            return 0f;
        if (!Closed)
            return Math.Clamp(offset, 0f, Length);
        var wrapped = offset % Length;
        return wrapped < 0 ? wrapped + Length : wrapped;
    }

    /// <summary>The position at <paramref name="offset"/>: linear between baked points, or Catmull-Rom when <paramref name="cubic"/>.</summary>
    public Vector3 SampleBaked(float offset, bool cubic)
    {
        if (Count == 0)
            return Vector3.Zero;
        if (Count == 1)
            return Points[0];
        var index = Locate(offset, out var t);
        var a = Points[index];
        var b = Points[index + 1];
        if (!cubic)
            return a + (b - a) * t;
        var pre = index > 0 ? Points[index - 1] : Closed && Count > 2 ? Points[Count - 2] : a;
        var post = index + 2 < Count ? Points[index + 2] : Closed && Count > 2 ? Points[1] : b;
        return CubicInterpolate(pre, a, b, post, t);
    }

    /// <summary>The unit tangent at <paramref name="offset"/>, interpolated between baked tangents; <paramref name="empty"/> without length.</summary>
    public Vector3 SampleTangent(float offset, Vector3 empty)
    {
        if (Count < 2 || Length <= 0)
            return empty;
        var index = Locate(offset, out var t);
        return NormalizeOr(Tangents[index] + (Tangents[index + 1] - Tangents[index]) * t, Tangents[index]);
    }

    /// <summary>A baked scalar (one per baked point) at <paramref name="offset"/>, linear between baked points.</summary>
    public float SampleScalar(float[] baked, float offset, float empty)
    {
        if (Count == 0)
            return empty;
        if (Count == 1)
            return baked[0];
        var index = Locate(offset, out var t);
        return baked[index] + (baked[index + 1] - baked[index]) * t;
    }

    /// <summary>The baked curve point closest to <paramref name="toPoint"/> and its offset (projected on each baked segment).</summary>
    public void Closest(Vector3 toPoint, out float offset, out Vector3 point)
    {
        offset = 0;
        point = Vector3.Zero;
        if (Count == 0)
            return;
        point = Points[0];
        if (Count == 1)
            return;
        var best = float.PositiveInfinity;
        for (var i = 0; i < Count - 1; i++)
        {
            var a = Points[i];
            var d = Points[i + 1] - a;
            var lengthSquared = Dot(d, d);
            var t = lengthSquared > 0 ? Math.Clamp(Dot(toPoint - a, d) / lengthSquared, 0f, 1f) : 0f;
            var q = a + d * t;
            var distance = DistanceSquared(q, toPoint);
            if (distance < best)
            {
                best = distance;
                point = q;
                offset = Distances[i] + (Distances[i + 1] - Distances[i]) * t;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Math (scalar, fixed order)
    // ------------------------------------------------------------------------------------------------

    public static Vector3 Bezier(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
    {
        var u = 1f - t;
        var uu = u * u;
        var tt = t * t;
        return p0 * (uu * u) + c0 * (3f * uu * t) + c1 * (3f * u * tt) + p1 * (tt * t);
    }

    // Godot's cubic_interpolate (Catmull-Rom through pre, from, to, post).
    public static Vector3 CubicInterpolate(Vector3 pre, Vector3 from, Vector3 to, Vector3 post, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (from * 2f + (to - pre) * t + (pre * 2f - from * 5f + to * 4f - post) * t2 + (from * 3f - pre - to * 3f + post) * t3) * 0.5f;
    }

    public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
    {
        var lengthSquared = Dot(v, v);
        return lengthSquared > 1e-20f ? v * (1f / MathF.Sqrt(lengthSquared)) : fallback;
    }

    public static float DistanceSquared(Vector3 a, Vector3 b)
    {
        var d = a - b;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
    }

    /// <summary>
    /// Splits the Bézier (p0, c0, c1, p1) at <paramref name="t"/> by de Casteljau: the left half is (p0, l1, l2, m) and the
    /// right half (m, r1, r2, p1), so inserting <paramref name="m"/> with these handles keeps the shape.
    /// </summary>
    public static void Split(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t,
        out Vector3 l1, out Vector3 l2, out Vector3 m, out Vector3 r1, out Vector3 r2)
    {
        var a = p0 + (c0 - p0) * t;
        var b = c0 + (c1 - c0) * t;
        var c = c1 + (p1 - c1) * t;
        l1 = a;
        r2 = c;
        l2 = a + (b - a) * t;
        r1 = b + (c - b) * t;
        m = l2 + (r1 - l2) * t;
    }
}
