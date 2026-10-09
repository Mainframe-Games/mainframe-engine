using System.Numerics;

namespace Forest;

/// <summary>
/// Distance from every point of a regular XZ grid to a polyline (the stream's centreline, the path), with the arc-length
/// offset of the nearest point, sampled bilinearly. Built once by exact point-to-segment distances, accelerated by a
/// coarse bucket grid; deterministic.
/// </summary>
public sealed class PolylineField
{
    private readonly float[] _distance;
    private readonly float[] _offset;
    private readonly int _side;
    private readonly float _cell;

    private PolylineField(int side, float cell, float[] distance, float[] offset)
    {
        _side = side;
        _cell = cell;
        _distance = distance;
        _offset = offset;
    }

    /// <summary>Total arc length of the polyline.</summary>
    public float Length { get; private init; }

    /// <summary>Builds the field over [0, <paramref name="size"/>]² with grid points every <paramref name="cell"/> metres.</summary>
    public static PolylineField Build(ReadOnlySpan<Vector2> points, float size, float cell = 1f)
    {
        if (points.Length < 2)
            throw new ArgumentException("A polyline needs two points.", nameof(points));
        var segments = points.Length - 1;
        var starts = new float[segments + 1];
        for (var i = 0; i < segments; i++)
            starts[i + 1] = starts[i] + Vector2.Distance(points[i], points[i + 1]);

        // Buckets of 8 m holding every segment whose box touches them.
        const float bucket = 8f;
        var buckets = (int)MathF.Ceiling(size / bucket) + 1;
        var lists = new List<int>[buckets * buckets];
        for (var i = 0; i < lists.Length; i++)
            lists[i] = [];
        for (var s = 0; s < segments; s++)
        {
            var a = points[s];
            var b = points[s + 1];
            int x0 = Bucket(MathF.Min(a.X, b.X)), x1 = Bucket(MathF.Max(a.X, b.X));
            int z0 = Bucket(MathF.Min(a.Y, b.Y)), z1 = Bucket(MathF.Max(a.Y, b.Y));
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    lists[z * buckets + x].Add(s);
        }

        int Bucket(float v) => Math.Clamp((int)MathF.Floor(v / bucket), 0, buckets - 1);

        var side = (int)MathF.Round(size / cell) + 1;
        var distance = new float[side * side];
        var offset = new float[side * side];
        var pts = points.ToArray();
        for (var j = 0; j < side; j++)
            for (var i = 0; i < side; i++)
            {
                var p = new Vector2(i * cell, j * cell);
                int bx = Bucket(p.X), bz = Bucket(p.Y);
                var best = float.MaxValue;
                var bestOffset = 0f;
                for (var ring = 0; ring < buckets; ring++)
                {
                    // Every segment in a further ring is at least (ring − 1) buckets away.
                    if (best < (ring - 1) * bucket)
                        break;
                    for (var z = bz - ring; z <= bz + ring; z++)
                        for (var x = bx - ring; x <= bx + ring; x++)
                        {
                            if (x < 0 || z < 0 || x >= buckets || z >= buckets)
                                continue;
                            if (Math.Max(Math.Abs(x - bx), Math.Abs(z - bz)) != ring)
                                continue;
                            foreach (var s in lists[z * buckets + x])
                            {
                                var d = SegmentDistance(p, pts[s], pts[s + 1], out var t);
                                if (d < best)
                                {
                                    best = d;
                                    bestOffset = starts[s] + t * (starts[s + 1] - starts[s]);
                                }
                            }
                        }
                }

                distance[j * side + i] = best;
                offset[j * side + i] = bestOffset;
            }

        return new PolylineField(side, cell, distance, offset) { Length = starts[segments] };
    }

    /// <summary>Distance from (x, z) to the polyline, bilinear between grid points.</summary>
    public float Distance(float x, float z) => Sample(_distance, x, z);

    /// <summary>Arc-length offset of the nearest polyline point at the grid point nearest (x, z) (no blending across a loop's seam).</summary>
    public float NearestOffset(float x, float z)
    {
        var i = Math.Clamp((int)MathF.Round(x / _cell), 0, _side - 1);
        var j = Math.Clamp((int)MathF.Round(z / _cell), 0, _side - 1);
        return _offset[j * _side + i];
    }

    /// <summary>Arc-length offset of the nearest polyline point (bilinear; not meaningful far from the line).</summary>
    public float Offset(float x, float z) => Sample(_offset, x, z);

    private float Sample(float[] values, float x, float z)
    {
        var u = Math.Clamp(x / _cell, 0f, _side - 1.001f);
        var v = Math.Clamp(z / _cell, 0f, _side - 1.001f);
        var i = (int)u;
        var j = (int)v;
        var fu = u - i;
        var fv = v - j;
        var row = j * _side + i;
        var a = values[row] + (values[row + 1] - values[row]) * fu;
        var b = values[row + _side] + (values[row + _side + 1] - values[row + _side]) * fu;
        return a + (b - a) * fv;
    }

    /// <summary>Distance from <paramref name="p"/> to segment ab; <paramref name="t"/> is the nearest point's parameter.</summary>
    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var abx = b.X - a.X;
        var abz = b.Y - a.Y;
        var len2 = abx * abx + abz * abz;
        t = len2 > 0f ? Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * abz) / len2, 0f, 1f) : 0f;
        var dx = p.X - (a.X + abx * t);
        var dz = p.Y - (a.Y + abz * t);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
