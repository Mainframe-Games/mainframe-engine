using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A terrain height grid for bake rays (ADR 0170): a min-max pyramid over blocks of 4ᵏ quads, walked top-down with a 2D
/// DDA per level, so a ray skips every block it passes over (or under) and tests triangles only in the quads it crosses
/// at their height. Exact: the leaf test is <see cref="TerrainGrid.RaycastQuad"/> (both triangles, either side).
/// Terrain-local coordinates (the map's corner at the origin).
/// </summary>
internal sealed class ProbeHeightfield
{
    private const int Branch = 4; // quads per block side grow 4× per level

    private readonly TerrainGrid _grid;
    private readonly float[] _heights;
    private readonly float[][] _min;
    private readonly float[][] _max;
    private readonly int[] _cells;      // blocks per side at each level
    private readonly int[] _blockQuads; // quads per block side at each level

    public ProbeHeightfield(TerrainGrid grid, float[] heights)
    {
        _grid = grid;
        _heights = heights;
        var quads = grid.Quads;
        var levels = 1;
        for (var q = 1; q < quads; q *= Branch)
            levels++;
        _min = new float[levels][];
        _max = new float[levels][];
        _cells = new int[levels];
        _blockQuads = new int[levels];
        var stride = grid.Stride;

        // Level 0: one quad (its four corners).
        _cells[0] = quads;
        _blockQuads[0] = 1;
        _min[0] = new float[quads * quads];
        _max[0] = new float[quads * quads];
        for (var j = 0; j < quads; j++)
        {
            for (var i = 0; i < quads; i++)
            {
                var k = j * stride + i;
                float a = heights[k], b = heights[k + 1], c = heights[k + stride], d = heights[k + stride + 1];
                _min[0][j * quads + i] = MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
                _max[0][j * quads + i] = MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
            }
        }

        for (var level = 1; level < levels; level++)
        {
            var below = _cells[level - 1];
            var cells = (below + Branch - 1) / Branch;
            _cells[level] = cells;
            _blockQuads[level] = _blockQuads[level - 1] * Branch;
            var min = _min[level] = new float[cells * cells];
            var max = _max[level] = new float[cells * cells];
            Array.Fill(min, float.MaxValue);
            Array.Fill(max, float.MinValue);
            for (var j = 0; j < below; j++)
            {
                for (var i = 0; i < below; i++)
                {
                    var parent = j / Branch * cells + i / Branch;
                    min[parent] = MathF.Min(min[parent], _min[level - 1][j * below + i]);
                    max[parent] = MathF.Max(max[parent], _max[level - 1][j * below + i]);
                }
            }
        }

        MinHeight = _min[levels - 1].Min();
        MaxHeight = _max[levels - 1].Max();
    }

    public float MinHeight { get; }

    public float MaxHeight { get; }

    /// <summary>The nearest hit of the ray (unit <paramref name="dir"/>, terrain-local) within <paramref name="tMax"/>.</summary>
    public bool Raycast(Vector3 origin, Vector3 dir, float tMax, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.UnitY;
        var size = _grid.Size;
        float t0 = 0f, t1 = tMax;
        const float margin = 1e-3f;
        if (!Slab(origin.X, dir.X, 0f, size, ref t0, ref t1) ||
            !Slab(origin.Y, dir.Y, MinHeight - margin, MaxHeight + margin, ref t0, ref t1) ||
            !Slab(origin.Z, dir.Z, 0f, size, ref t0, ref t1))
            return false;
        var top = _min.Length - 1;
        return Walk(top, 0, 0, _cells[top] - 1, _cells[top] - 1, origin, dir, t0, t1, out distance, out normal);
    }

    // Walks level `level`'s blocks inside [x0..x1] × [z0..z1] (the parent block) along [ta, tb], descending into those the
    // ray's height range overlaps.
    private bool Walk(int level, int x0, int z0, int x1, int z1, Vector3 origin, Vector3 dir, float ta, float tb,
        out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.UnitY;
        var cellSize = _blockQuads[level] * _grid.Spacing;
        var cells = _cells[level];
        var min = _min[level];
        var max = _max[level];
        const float margin = 1e-3f;

        var px = (origin.X + dir.X * ta) / cellSize;
        var pz = (origin.Z + dir.Z * ta) / cellSize;
        var x = Math.Clamp((int)MathF.Floor(px), x0, x1);
        var z = Math.Clamp((int)MathF.Floor(pz), z0, z1);
        var stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
        var stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;
        var dx = stepX != 0 ? cellSize / MathF.Abs(dir.X) : float.PositiveInfinity;
        var dz = stepZ != 0 ? cellSize / MathF.Abs(dir.Z) : float.PositiveInfinity;
        var nextX = stepX != 0 ? ((x + (stepX > 0 ? 1 : 0)) * cellSize - origin.X) / dir.X : float.PositiveInfinity;
        var nextZ = stepZ != 0 ? ((z + (stepZ > 0 ? 1 : 0)) * cellSize - origin.Z) / dir.Z : float.PositiveInfinity;
        var t = ta;
        while (t <= tb)
        {
            var exit = MathF.Min(MathF.Min(nextX, nextZ), tb);
            var cell = z * cells + x;
            var ya = origin.Y + dir.Y * t;
            var yb = origin.Y + dir.Y * exit;
            var lo = MathF.Min(ya, yb);
            var hi = MathF.Max(ya, yb);
            if (lo <= max[cell] + margin && hi >= min[cell] - margin)
            {
                if (level == 0)
                {
                    if (_grid.RaycastQuad(_heights, x, z, origin, dir, out var d, out var n) && d >= 0f && d <= tb + margin)
                    {
                        distance = d;
                        normal = n;
                        return true;
                    }
                }
                else
                {
                    var b = Branch;
                    var child = _cells[level - 1] - 1;
                    if (Walk(level - 1, x * b, z * b, Math.Min(x * b + b - 1, child), Math.Min(z * b + b - 1, child),
                            origin, dir, MathF.Max(t - margin, 0f), exit + margin, out distance, out normal))
                        return true;
                }
            }

            if (exit >= tb)
                break;
            t = exit;
            if (nextX < nextZ)
            {
                x += stepX;
                nextX += dx;
                if (x < x0 || x > x1)
                    break;
            }
            else
            {
                z += stepZ;
                nextZ += dz;
                if (z < z0 || z > z1)
                    break;
            }
        }

        return false;
    }

    private static bool Slab(float origin, float dir, float min, float max, ref float t0, ref float t1)
    {
        if (MathF.Abs(dir) < 1e-12f)
            return origin >= min && origin <= max;
        var a = (min - origin) / dir;
        var b = (max - origin) / dir;
        if (a > b)
            (a, b) = (b, a);
        t0 = MathF.Max(t0, a);
        t1 = MathF.Min(t1, b);
        return t0 <= t1;
    }
}
