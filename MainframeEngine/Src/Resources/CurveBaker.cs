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

    // The closest-point grid (G13.1): baked segments bucketed into uniform cells, built on the first closest query after a
    // bake and kept (arrays grow, never shrink). _cellStart[c].._cellStart[c + 1] index _cellSegments.
    private const int GridMinSegments = 32;
    private const int GridMaxCells = 65536;
    private bool _gridValid;
    private Vector3 _gridMin;
    private float _cell;
    private int _nx, _ny, _nz;
    private int[] _cellStart = [];
    private int[] _cellSegments = [];
    private int[] _cellFill = [];

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
        _gridValid = false;
        Count = 0;
        Length = 0;
        Step = 0;
        Closed = false;
    }

    /// <summary>One point: a single baked point with tangent <c>-Z</c>.</summary>
    public void BakeSingle(Vector3 point)
    {
        _gridValid = false;
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
        _gridValid = false;
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

    /// <summary>
    /// The baked curve point closest to <paramref name="toPoint"/> and its offset (projected on each baked segment). Long
    /// curves search a grid of the baked segments outward from the point; the result is the full scan's, ties included
    /// (the lower offset wins).
    /// </summary>
    public void Closest(Vector3 toPoint, out float offset, out Vector3 point)
    {
        offset = 0;
        point = Vector3.Zero;
        if (Count == 0)
            return;
        point = Points[0];
        if (Count == 1)
            return;
        var segments = Count - 1;
        var best = float.PositiveInfinity;
        var bestSegment = -1;
        var bestT = 0f;
        if (segments < GridMinSegments)
        {
            for (var i = 0; i < segments; i++)
                Consider(i, toPoint, ref best, ref bestSegment, ref bestT);
        }
        else
        {
            if (!_gridValid)
                BuildGrid();
            SearchGrid(toPoint, ref best, ref bestSegment, ref bestT);
        }

        Result(bestSegment, bestT, out offset, out point);
    }

    /// <summary>
    /// As <see cref="Closest(Vector3, out float, out Vector3)"/>, searching only the baked segments within
    /// <paramref name="window"/> of <paramref name="hintOffset"/> (wrapping on a closed curve): for followers that track a
    /// moving point near where it was.
    /// </summary>
    public void Closest(Vector3 toPoint, float hintOffset, float window, out float offset, out Vector3 point)
    {
        if (Count < 2 || Step <= 0 || !(window >= 0) || window * 2 >= Length || float.IsNaN(hintOffset))
        {
            Closest(toPoint, out offset, out point);
            return;
        }

        var segments = Count - 1;
        var first = (int)MathF.Floor((hintOffset - window) / Step);
        var last = (int)MathF.Floor((hintOffset + window) / Step);
        if (!Closed)
        {
            first = Math.Max(first, 0);
            last = Math.Min(last, segments - 1);
        }

        var best = float.PositiveInfinity;
        var bestSegment = -1;
        var bestT = 0f;
        for (var k = first; k <= last; k++)
        {
            var i = ((k % segments) + segments) % segments;
            Consider(i, toPoint, ref best, ref bestSegment, ref bestT);
        }

        if (bestSegment < 0)
        {
            Closest(toPoint, out offset, out point);
            return;
        }

        Result(bestSegment, bestT, out offset, out point);
    }

    // The projection of `toPoint` on baked segment i; the smaller distance wins, and on a tie the lower segment, as the
    // linear scan's first strictly smaller hit does.
    private void Consider(int i, Vector3 toPoint, ref float best, ref int bestSegment, ref float bestT)
    {
        var a = Points[i];
        var d = Points[i + 1] - a;
        var lengthSquared = Dot(d, d);
        var t = lengthSquared > 0 ? Math.Clamp(Dot(toPoint - a, d) / lengthSquared, 0f, 1f) : 0f;
        var q = a + d * t;
        var distance = DistanceSquared(q, toPoint);
        if (distance < best || distance == best && i < bestSegment)
        {
            best = distance;
            bestSegment = i;
            bestT = t;
        }
    }

    private void Result(int segment, float t, out float offset, out Vector3 point)
    {
        var a = Points[segment];
        point = a + (Points[segment + 1] - a) * t;
        offset = Distances[segment] + (Distances[segment + 1] - Distances[segment]) * t;
    }

    private void BuildGrid()
    {
        _gridValid = true;
        var segments = Count - 1;
        var min = Points[0];
        var max = Points[0];
        for (var i = 1; i < Count; i++)
        {
            min = Vector3.Min(min, Points[i]);
            max = Vector3.Max(max, Points[i]);
        }

        var size = max - min;
        var cell = MathF.Max(MathF.Max(4 * Step, Length / 256f), 1e-4f);
        // Grow the cells until the grid fits the budget (a long, thin curve in a big box).
        while (Cells(size, cell) > GridMaxCells)
            cell *= 1.5f;
        _gridMin = min;
        _cell = cell;
        _nx = Axis(size.X, cell);
        _ny = Axis(size.Y, cell);
        _nz = Axis(size.Z, cell);
        var cells = _nx * _ny * _nz;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cellFill = new int[cells];
        }

        // Counting sort: each segment goes into every cell its box touches (segments are about one Step long).
        Array.Clear(_cellStart, 0, cells + 1);
        var total = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                var running = 0;
                for (var c = 0; c < cells; c++)
                {
                    var n = _cellStart[c];
                    _cellStart[c] = running;
                    _cellFill[c] = running;
                    running += n;
                }

                _cellStart[cells] = running;
                total = running;
                if (_cellSegments.Length < total)
                    _cellSegments = new int[total];
            }

            for (var i = 0; i < segments; i++)
            {
                var lo = Vector3.Min(Points[i], Points[i + 1]);
                var hi = Vector3.Max(Points[i], Points[i + 1]);
                CellOf(lo, out var x0, out var y0, out var z0);
                CellOf(hi, out var x1, out var y1, out var z1);
                for (var z = z0; z <= z1; z++)
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var c = (z * _ny + y) * _nx + x;
                            if (pass == 0)
                                _cellStart[c]++;
                            else
                                _cellSegments[_cellFill[c]++] = i;
                        }
            }
        }
    }

    private static long Cells(Vector3 size, float cell) => (long)Axis(size.X, cell) * Axis(size.Y, cell) * Axis(size.Z, cell);

    private static int Axis(float extent, float cell) => Math.Max(1, (int)MathF.Ceiling(extent / cell));

    private void CellOf(Vector3 p, out int x, out int y, out int z)
    {
        x = Math.Clamp((int)MathF.Floor((p.X - _gridMin.X) / _cell), 0, _nx - 1);
        y = Math.Clamp((int)MathF.Floor((p.Y - _gridMin.Y) / _cell), 0, _ny - 1);
        z = Math.Clamp((int)MathF.Floor((p.Z - _gridMin.Z) / _cell), 0, _nz - 1);
    }

    // Rings of cells outward from the point's cell (clamped into the grid) until no cell left out can hold a closer
    // segment: the distance from the point to the searched block's open faces bounds every cell outside it.
    private void SearchGrid(Vector3 toPoint, ref float best, ref int bestSegment, ref float bestT)
    {
        CellOf(toPoint, out var cx, out var cy, out var cz);
        var maxRing = Math.Max(Math.Max(_nx, _ny), _nz);
        for (var r = 0; r <= maxRing; r++)
        {
            int x0 = Math.Max(cx - r, 0), x1 = Math.Min(cx + r, _nx - 1);
            int y0 = Math.Max(cy - r, 0), y1 = Math.Min(cy + r, _ny - 1);
            int z0 = Math.Max(cz - r, 0), z1 = Math.Min(cz + r, _nz - 1);
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        // Only the ring's shell: inner cells were searched by smaller rings.
                        if (r > 0 && Math.Abs(x - cx) < r && Math.Abs(y - cy) < r && Math.Abs(z - cz) < r)
                            continue;
                        var c = (z * _ny + y) * _nx + x;
                        for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                            Consider(_cellSegments[k], toPoint, ref best, ref bestSegment, ref bestT);
                    }

            var bound = float.PositiveInfinity;
            Face(cx - r > 0, toPoint.X - (_gridMin.X + (cx - r) * _cell), ref bound);
            Face(cx + r < _nx - 1, _gridMin.X + (cx + r + 1) * _cell - toPoint.X, ref bound);
            Face(cy - r > 0, toPoint.Y - (_gridMin.Y + (cy - r) * _cell), ref bound);
            Face(cy + r < _ny - 1, _gridMin.Y + (cy + r + 1) * _cell - toPoint.Y, ref bound);
            Face(cz - r > 0, toPoint.Z - (_gridMin.Z + (cz - r) * _cell), ref bound);
            Face(cz + r < _nz - 1, _gridMin.Z + (cz + r + 1) * _cell - toPoint.Z, ref bound);
            if (float.IsPositiveInfinity(bound))
                return; // the whole grid is searched
            // Strictly closer than the bound: a segment at exactly the bound distance outside could tie on a lower index.
            if (bestSegment >= 0 && best < bound * bound)
                return;
        }
    }

    private static void Face(bool open, float distance, ref float bound)
    {
        if (open)
            bound = MathF.Min(bound, MathF.Max(distance, 0f));
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
