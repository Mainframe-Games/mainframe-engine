using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// The terrain's height grid maths (terrain-local metres, origin at the map corner): <see cref="Quads"/> × Quads quads
/// of <see cref="Spacing"/> metres, heights row by row (<c>j · (Quads + 1) + i</c>, X along a row, Z down the rows). Each
/// quad splits along its <see cref="TerrainDiagonal"/> into two counter-clockwise (seen from above) triangles; every
/// query here is exact on those triangles, the ones the chunk meshes draw at LOD 0 and collision is built from.
/// </summary>
/// <remarks>
/// Diagonal A joins <c>(i+1, j)</c>–<c>(i, j+1)</c>: triangles <c>(00, 01, 10)</c> and <c>(10, 01, 11)</c>. Diagonal B
/// joins <c>(i, j)</c>–<c>(i+1, j+1)</c>: triangles <c>(00, 11, 10)</c> and <c>(00, 01, 11)</c>. Corner <c>ab</c> is
/// vertex <c>(i + a, j + b)</c>.
/// </remarks>
public readonly struct TerrainGrid
{
    public TerrainGrid(int quads, float spacing, TerrainDiagonal diagonal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quads);
        if (!(spacing > 0) || !float.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Must be positive and finite.");
        Quads = quads;
        Spacing = spacing;
        Diagonal = diagonal;
    }

    /// <summary>Quads along each side (vertices per side − 1).</summary>
    public int Quads { get; }

    /// <summary>Vertices per side (<see cref="Quads"/> + 1): the row stride of the height array.</summary>
    public int Stride => Quads + 1;

    /// <summary>Metres between neighbouring vertices.</summary>
    public float Spacing { get; }

    public TerrainDiagonal Diagonal { get; }

    /// <summary>Side length in metres.</summary>
    public float Size => Quads * Spacing;

    /// <summary>True when quad <c>(i, j)</c> splits along diagonal B (<c>(i, j)</c>–<c>(i+1, j+1)</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDiagonalB(TerrainDiagonal diagonal, int i, int j) =>
        diagonal == TerrainDiagonal.Checkerboard && ((i + j) & 1) != 0;

    /// <summary>True when quad <c>(i, j)</c> of this grid splits along diagonal B.</summary>
    public bool IsDiagonalB(int i, int j) => IsDiagonalB(Diagonal, i, j);

    /// <summary>
    /// Writes the six corner ids of the quad's two triangles (0 = 00, 1 = 10, 2 = 01, 3 = 11), counter-clockwise seen
    /// from above.
    /// </summary>
    public static void QuadCorners(bool diagonalB, Span<int> corners)
    {
        if (diagonalB)
        {
            corners[0] = 0; corners[1] = 3; corners[2] = 1;
            corners[3] = 0; corners[4] = 2; corners[5] = 3;
        }
        else
        {
            corners[0] = 0; corners[1] = 2; corners[2] = 1;
            corners[3] = 1; corners[4] = 2; corners[5] = 3;
        }
    }

    /// <summary>
    /// The height at fractions <paramref name="fx"/>, <paramref name="fz"/> (0..1) of a quad, on the triangle that
    /// contains the point.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Interpolate(bool diagonalB, float h00, float h10, float h01, float h11, float fx, float fz)
    {
        if (!diagonalB)
            return fx + fz <= 1f
                ? h00 + (h10 - h00) * fx + (h01 - h00) * fz
                : h11 + (h01 - h11) * (1f - fx) + (h10 - h11) * (1f - fz);
        return fx >= fz
            ? h00 + (h10 - h00) * fx + (h11 - h10) * fz
            : h00 + (h01 - h00) * fz + (h11 - h01) * fx;
    }

    /// <summary>The height change per unit <c>fx</c> and <c>fz</c> on the triangle containing the point (its plane's slope).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Slope(bool diagonalB, float h00, float h10, float h01, float h11, float fx, float fz, out float dfx, out float dfz)
    {
        if (!diagonalB)
        {
            if (fx + fz <= 1f)
            {
                dfx = h10 - h00;
                dfz = h01 - h00;
            }
            else
            {
                dfx = h11 - h01;
                dfz = h11 - h10;
            }
        }
        else if (fx >= fz)
        {
            dfx = h10 - h00;
            dfz = h11 - h10;
        }
        else
        {
            dfx = h11 - h01;
            dfz = h01 - h00;
        }
    }

    /// <summary>
    /// Finds the quad containing local point (<paramref name="x"/>, <paramref name="z"/>), clamped to the map, and the
    /// point's fractions in it (the far edges belong to the last quad, at fraction 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Locate(float x, float z, out int i, out int j, out float fx, out float fz)
    {
        var gx = Math.Clamp(x / Spacing, 0f, Quads);
        var gz = Math.Clamp(z / Spacing, 0f, Quads);
        if (float.IsNaN(gx)) gx = 0f;
        if (float.IsNaN(gz)) gz = 0f;
        i = Math.Min((int)gx, Quads - 1);
        j = Math.Min((int)gz, Quads - 1);
        fx = gx - i;
        fz = gz - j;
    }

    /// <summary>The height at local point (<paramref name="x"/>, <paramref name="z"/>), exact on the LOD 0 triangle.</summary>
    public float HeightAt(ReadOnlySpan<float> heights, float x, float z)
    {
        Locate(x, z, out var i, out var j, out var fx, out var fz);
        var k = j * Stride + i;
        return Interpolate(IsDiagonalB(i, j), heights[k], heights[k + 1], heights[k + Stride], heights[k + Stride + 1], fx, fz);
    }

    /// <summary>The face normal of the LOD 0 triangle under local point (<paramref name="x"/>, <paramref name="z"/>): what physics sees.</summary>
    public Vector3 FaceNormalAt(ReadOnlySpan<float> heights, float x, float z)
    {
        Locate(x, z, out var i, out var j, out var fx, out var fz);
        var k = j * Stride + i;
        Slope(IsDiagonalB(i, j), heights[k], heights[k + 1], heights[k + Stride], heights[k + Stride + 1], fx, fz, out var dfx, out var dfz);
        return Vector3.Normalize(new Vector3(-dfx, Spacing, -dfz));
    }

    /// <summary>
    /// The smooth normal of vertex (<paramref name="i"/>, <paramref name="j"/>): central differences of the heights
    /// (one-sided at the map edges), the normal the chunk meshes shade with. Neighbouring chunks agree at their seams.
    /// </summary>
    public Vector3 VertexNormal(ReadOnlySpan<float> heights, int i, int j)
    {
        var stride = Stride;
        var row = j * stride;
        var il = i > 0 ? i - 1 : i;
        var ir = i < Quads ? i + 1 : i;
        var jd = j > 0 ? j - 1 : j;
        var ju = j < Quads ? j + 1 : j;
        var dhdx = (heights[row + ir] - heights[row + il]) / ((ir - il) * Spacing);
        var dhdz = (heights[ju * stride + i] - heights[jd * stride + i]) / ((ju - jd) * Spacing);
        return Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz));
    }

    /// <summary>
    /// The vertex normals of the triangle under local point (<paramref name="x"/>, <paramref name="z"/>) interpolated
    /// across it (placement, shading).
    /// </summary>
    public Vector3 SmoothNormalAt(ReadOnlySpan<float> heights, float x, float z)
    {
        Locate(x, z, out var i, out var j, out var fx, out var fz);
        var diagonalB = IsDiagonalB(i, j);
        var n00 = VertexNormal(heights, i, j);
        var n10 = VertexNormal(heights, i + 1, j);
        var n01 = VertexNormal(heights, i, j + 1);
        var n11 = VertexNormal(heights, i + 1, j + 1);
        var n = new Vector3(
            Interpolate(diagonalB, n00.X, n10.X, n01.X, n11.X, fx, fz),
            Interpolate(diagonalB, n00.Y, n10.Y, n01.Y, n11.Y, fx, fz),
            Interpolate(diagonalB, n00.Z, n10.Z, n01.Z, n11.Z, fx, fz));
        return Vector3.Normalize(n);
    }

    /// <summary>
    /// Casts a ray against the height grid (terrain-local, analytic: no marching). Clips the ray to the map box, walks
    /// the chunks with a 2D DDA (a chunk whose height range the ray misses is skipped), then the quads, testing each
    /// quad's two triangles from either side. <paramref name="chunkMin"/>/<paramref name="chunkMax"/> hold each chunk's
    /// height range (row by row, <paramref name="chunkQuads"/> quads per chunk side).
    /// </summary>
    public bool Raycast(ReadOnlySpan<float> heights, ReadOnlySpan<float> chunkMin, ReadOnlySpan<float> chunkMax, int chunkQuads,
        float minHeight, float maxHeight, Vector3 origin, Vector3 direction, float maxDistance, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.UnitY;
        var length = direction.Length();
        if (!(length > 0f) || !(maxDistance >= 0f) || !float.IsFinite(length))
            return false;
        var dir = direction / length;

        // A vertical ray reads the height directly.
        const float verticalEpsilon = 1e-7f;
        if (MathF.Abs(dir.X) < verticalEpsilon && MathF.Abs(dir.Z) < verticalEpsilon)
        {
            var size = Size;
            if (origin.X < 0f || origin.Z < 0f || origin.X > size || origin.Z > size)
                return false;
            var h = HeightAt(heights, origin.X, origin.Z);
            var t = (h - origin.Y) / dir.Y;
            if (!(t >= 0f) || t > maxDistance)
                return false;
            distance = t;
            normal = FaceNormalAt(heights, origin.X, origin.Z);
            return true;
        }

        // Clip to the map box (a small vertical margin keeps grazing rays inside).
        var t0 = 0f;
        var t1 = maxDistance;
        var margin = 1e-3f + (maxHeight - minHeight) * 1e-6f;
        if (!Slab(origin.X, dir.X, 0f, Size, ref t0, ref t1) ||
            !Slab(origin.Y, dir.Y, minHeight - margin, maxHeight + margin, ref t0, ref t1) ||
            !Slab(origin.Z, dir.Z, 0f, Size, ref t0, ref t1))
            return false;

        var chunks = Quads / chunkQuads;
        var chunkSize = chunkQuads * Spacing;
        var chunkWalk = new GridWalk(origin, dir, chunkSize, chunks, t0, t1);
        while (chunkWalk.Next(out var cx, out var cz, out var ca, out var cb))
        {
            var c = cz * chunks + cx;
            var ya = origin.Y + dir.Y * ca;
            var yb = origin.Y + dir.Y * cb;
            if (MathF.Max(ya, yb) < chunkMin[c] - margin || MathF.Min(ya, yb) > chunkMax[c] + margin)
                continue;

            var quadWalk = new GridWalk(origin, dir, Spacing, Quads, ca, cb);
            while (quadWalk.Next(out var i, out var j, out _, out _))
            {
                if (RaycastQuad(heights, i, j, origin, dir, out var t, out var n) && t <= t1)
                {
                    distance = t;
                    normal = n;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Intersects a ray with quad (<paramref name="i"/>, <paramref name="j"/>)'s two triangles; the nearest hit at t ≥ 0.</summary>
    internal bool RaycastQuad(ReadOnlySpan<float> heights, int i, int j, Vector3 origin, Vector3 dir, out float distance, out Vector3 normal)
    {
        var k = j * Stride + i;
        var x0 = i * Spacing;
        var z0 = j * Spacing;
        var x1 = (i + 1) * Spacing;
        var z1 = (j + 1) * Spacing;
        var p00 = new Vector3(x0, heights[k], z0);
        var p10 = new Vector3(x1, heights[k + 1], z0);
        var p01 = new Vector3(x0, heights[k + Stride], z1);
        var p11 = new Vector3(x1, heights[k + Stride + 1], z1);

        distance = float.MaxValue;
        normal = Vector3.UnitY;
        var hit = false;
        if (IsDiagonalB(i, j))
        {
            hit |= Triangle(origin, dir, p00, p11, p10, ref distance, ref normal);
            hit |= Triangle(origin, dir, p00, p01, p11, ref distance, ref normal);
        }
        else
        {
            hit |= Triangle(origin, dir, p00, p01, p10, ref distance, ref normal);
            hit |= Triangle(origin, dir, p10, p01, p11, ref distance, ref normal);
        }

        return hit;
    }

    // Möller–Trumbore, both sides; keeps the nearest hit at t ≥ 0. The normal is the up-facing face normal.
    private static bool Triangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, ref float best, ref Vector3 normal)
    {
        const float edgeEpsilon = 1e-6f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(dir, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
            return false;
        var inv = 1f / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < -edgeEpsilon || u > 1f + edgeEpsilon)
            return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(dir, q) * inv;
        if (v < -edgeEpsilon || u + v > 1f + edgeEpsilon)
            return false;
        var t = Vector3.Dot(e2, q) * inv;
        if (t < 0f || t >= best)
            return false;
        best = t;
        normal = Vector3.Normalize(Vector3.Cross(e1, e2));
        return true;
    }

    private static bool Slab(float origin, float dir, float min, float max, ref float t0, ref float t1)
    {
        if (MathF.Abs(dir) < 1e-12f)
            return origin >= min && origin <= max;
        var inv = 1f / dir;
        var a = (min - origin) * inv;
        var b = (max - origin) * inv;
        if (a > b)
            (a, b) = (b, a);
        t0 = MathF.Max(t0, a);
        t1 = MathF.Min(t1, b);
        return t0 <= t1;
    }

    /// <summary>A 2D DDA (Amanatides–Woo) over a square grid in XZ, along a ray between two distances.</summary>
    private struct GridWalk
    {
        private int _x, _z;
        private readonly int _stepX, _stepZ, _cells;
        private float _tMaxX, _tMaxZ;
        private readonly float _tDeltaX, _tDeltaZ, _tEnd;
        private float _t;
        private bool _done;

        public GridWalk(Vector3 origin, Vector3 dir, float cellSize, int cells, float tStart, float tEnd)
        {
            _cells = cells;
            _tEnd = tEnd;
            _t = tStart;
            var px = (origin.X + dir.X * tStart) / cellSize;
            var pz = (origin.Z + dir.Z * tStart) / cellSize;
            _x = Math.Clamp((int)MathF.Floor(px), 0, cells - 1);
            _z = Math.Clamp((int)MathF.Floor(pz), 0, cells - 1);
            _stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
            _stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;
            _tDeltaX = _stepX != 0 ? cellSize / MathF.Abs(dir.X) : float.PositiveInfinity;
            _tDeltaZ = _stepZ != 0 ? cellSize / MathF.Abs(dir.Z) : float.PositiveInfinity;
            _tMaxX = _stepX != 0 ? ((_x + (_stepX > 0 ? 1 : 0)) * cellSize - origin.X) / dir.X : float.PositiveInfinity;
            _tMaxZ = _stepZ != 0 ? ((_z + (_stepZ > 0 ? 1 : 0)) * cellSize - origin.Z) / dir.Z : float.PositiveInfinity;
            _done = false;
        }

        public bool Next(out int x, out int z, out float tEnter, out float tExit)
        {
            x = _x;
            z = _z;
            tEnter = _t;
            if (_done || _t > _tEnd)
            {
                tExit = _t;
                return false;
            }

            if (_tMaxX < _tMaxZ)
            {
                tExit = MathF.Min(_tMaxX, _tEnd);
                _t = _tMaxX;
                _tMaxX += _tDeltaX;
                _x += _stepX;
            }
            else
            {
                tExit = MathF.Min(_tMaxZ, _tEnd);
                _t = _tMaxZ;
                _tMaxZ += _tDeltaZ;
                _z += _stepZ;
            }

            if (_x < 0 || _x >= _cells || _z < 0 || _z >= _cells || float.IsInfinity(_t))
                _done = true;
            return true;
        }
    }
}
