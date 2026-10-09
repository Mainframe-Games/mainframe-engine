using System.Numerics;

namespace MainframeEngine.Tests.Terrain;

public class TerrainGridTests
{
    private const int Quads = 24;
    private const float Spacing = 0.5f;

    private static float[] RandomHeights(int seed = 3)
    {
        var rng = new Random(seed);
        var heights = new float[(Quads + 1) * (Quads + 1)];
        for (var i = 0; i < heights.Length; i++)
            heights[i] = (float)(rng.NextDouble() * 4 - 1);
        return heights;
    }

    private static float Vertex(float[] heights, int i, int j) => heights[j * (Quads + 1) + i];

    /// <summary>The triangle of quad (i, j) containing fractions (fx, fz), as three world points, by the documented table.</summary>
    private static (Vector3 A, Vector3 B, Vector3 C) TriangleOf(float[] h, TerrainDiagonal diagonal, int i, int j, float fx, float fz)
    {
        Vector3 P(int a, int b) => new((i + a) * Spacing, Vertex(h, i + a, j + b), (j + b) * Spacing);
        var diagonalB = diagonal == TerrainDiagonal.Checkerboard && (i + j) % 2 == 1;
        if (!diagonalB)
            return fx + fz <= 1 ? (P(0, 0), P(0, 1), P(1, 0)) : (P(1, 0), P(0, 1), P(1, 1));
        return fx >= fz ? (P(0, 0), P(1, 1), P(1, 0)) : (P(0, 0), P(0, 1), P(1, 1));
    }

    private static float PlaneHeight((Vector3 A, Vector3 B, Vector3 C) t, float x, float z)
    {
        var n = Vector3.Cross(t.B - t.A, t.C - t.A);
        return t.A.Y - (n.X * (x - t.A.X) + n.Z * (z - t.A.Z)) / n.Y;
    }

    [Fact]
    public void DiagonalRuleIsCheckerboardOrUniform()
    {
        Assert.False(TerrainGrid.IsDiagonalB(TerrainDiagonal.Checkerboard, 0, 0));
        Assert.True(TerrainGrid.IsDiagonalB(TerrainDiagonal.Checkerboard, 1, 0));
        Assert.True(TerrainGrid.IsDiagonalB(TerrainDiagonal.Checkerboard, 0, 3));
        Assert.False(TerrainGrid.IsDiagonalB(TerrainDiagonal.Checkerboard, 5, 7));
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                Assert.False(TerrainGrid.IsDiagonalB(TerrainDiagonal.Uniform, i, j));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuadTrianglesAreCounterClockwiseFromAbove(bool diagonalB)
    {
        Span<int> corners = stackalloc int[6];
        TerrainGrid.QuadCorners(diagonalB, corners);
        Vector3 P(int c) => new(c & 1, 0, c >> 1);
        for (var t = 0; t < 2; t++)
        {
            var n = Vector3.Cross(P(corners[t * 3 + 1]) - P(corners[t * 3]), P(corners[t * 3 + 2]) - P(corners[t * 3]));
            Assert.True(n.Y > 0, $"triangle {t} faces down");
        }
    }

    [Theory]
    [InlineData(TerrainDiagonal.Checkerboard)]
    [InlineData(TerrainDiagonal.Uniform)]
    public void HeightAtEqualsTheVertexHeights(TerrainDiagonal diagonal)
    {
        var h = RandomHeights();
        var grid = new TerrainGrid(Quads, Spacing, diagonal);
        for (var j = 0; j <= Quads; j++)
            for (var i = 0; i <= Quads; i++)
                Assert.Equal(Vertex(h, i, j), grid.HeightAt(h, i * Spacing, j * Spacing), 1e-5f);
    }

    [Theory]
    [InlineData(TerrainDiagonal.Checkerboard)]
    [InlineData(TerrainDiagonal.Uniform)]
    public void HeightAtLiesOnEachTrianglesPlane(TerrainDiagonal diagonal)
    {
        var h = RandomHeights();
        var grid = new TerrainGrid(Quads, Spacing, diagonal);
        var rng = new Random(11);
        for (var n = 0; n < 4000; n++)
        {
            var x = (float)rng.NextDouble() * grid.Size;
            var z = (float)rng.NextDouble() * grid.Size;
            grid.Locate(x, z, out var i, out var j, out var fx, out var fz);
            var expected = PlaneHeight(TriangleOf(h, diagonal, i, j, fx, fz), x, z);
            Assert.Equal(expected, grid.HeightAt(h, x, z), 1e-3f);
        }
    }

    [Fact]
    public void HeightAtIsContinuousAcrossQuadEdges()
    {
        var h = RandomHeights();
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        const float e = 1e-4f;
        var rng = new Random(5);
        for (var k = 1; k < Quads; k++)
        {
            var along = (float)rng.NextDouble() * grid.Size;
            var edge = k * Spacing;
            Assert.Equal(grid.HeightAt(h, edge - e, along), grid.HeightAt(h, edge + e, along), 5e-3f);
            Assert.Equal(grid.HeightAt(h, along, edge - e), grid.HeightAt(h, along, edge + e), 5e-3f);
        }
    }

    [Fact]
    public void QueriesClampToTheMap()
    {
        var h = RandomHeights();
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        Assert.Equal(grid.HeightAt(h, 0, 0), grid.HeightAt(h, -50, -3));
        Assert.Equal(Vertex(h, Quads, Quads), grid.HeightAt(h, 1000, 1000), 1e-5f);
        Assert.Equal(grid.HeightAt(h, grid.Size, 2.2f), grid.HeightAt(h, grid.Size + 5, 2.2f));
        Assert.True(float.IsFinite(grid.HeightAt(h, float.NaN, 1)));
    }

    [Fact]
    public void NormalAtIsTheFaceNormal()
    {
        var h = RandomHeights();
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        var rng = new Random(2);
        for (var n = 0; n < 500; n++)
        {
            var x = (float)rng.NextDouble() * grid.Size;
            var z = (float)rng.NextDouble() * grid.Size;
            grid.Locate(x, z, out var i, out var j, out var fx, out var fz);
            var t = TriangleOf(h, TerrainDiagonal.Checkerboard, i, j, fx, fz);
            var expected = Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A));
            var actual = grid.FaceNormalAt(h, x, z);
            Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{actual} vs {expected}");
        }
    }

    [Fact]
    public void VertexNormalsAreCentralDifferencesAndSmoothNormalsInterpolateThem()
    {
        // A plane h = 0.5x − 0.25z: every normal is the plane's.
        var h = new float[(Quads + 1) * (Quads + 1)];
        for (var j = 0; j <= Quads; j++)
            for (var i = 0; i <= Quads; i++)
                h[j * (Quads + 1) + i] = 0.5f * i * Spacing - 0.25f * j * Spacing;
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        var expected = Vector3.Normalize(new Vector3(-0.5f, 1f, 0.25f));
        Assert.True(Vector3.Distance(expected, grid.VertexNormal(h, 0, 0)) < 1e-5f);
        Assert.True(Vector3.Distance(expected, grid.VertexNormal(h, 7, 9)) < 1e-5f);
        Assert.True(Vector3.Distance(expected, grid.SmoothNormalAt(h, 3.3f, 7.1f)) < 1e-5f);
        Assert.True(Vector3.Distance(expected, grid.FaceNormalAt(h, 3.3f, 7.1f)) < 1e-5f);
    }

    // ── Raycast ──

    private static float[] BedFor(TerrainGrid grid, float[] heights, int chunkQuads, out float[] min, out float[] max)
    {
        var chunks = grid.Quads / chunkQuads;
        min = new float[chunks * chunks];
        max = new float[chunks * chunks];
        for (var cz = 0; cz < chunks; cz++)
            for (var cx = 0; cx < chunks; cx++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (var j = cz * chunkQuads; j <= (cz + 1) * chunkQuads; j++)
                    for (var i = cx * chunkQuads; i <= (cx + 1) * chunkQuads; i++)
                    {
                        lo = MathF.Min(lo, heights[j * grid.Stride + i]);
                        hi = MathF.Max(hi, heights[j * grid.Stride + i]);
                    }

                min[cz * chunks + cx] = lo;
                max[cz * chunks + cx] = hi;
            }

        return heights;
    }

    /// <summary>The nearest hit over every triangle of the map (brute force).</summary>
    private static float BruteForce(TerrainGrid grid, float[] h, Vector3 origin, Vector3 dir)
    {
        var best = float.MaxValue;
        for (var j = 0; j < grid.Quads; j++)
            for (var i = 0; i < grid.Quads; i++)
                if (grid.RaycastQuad(h, i, j, origin, dir, out var t, out _) && t < best)
                    best = t;
        return best;
    }

    [Fact]
    public void RaycastAgreesWithHeightAtAndBruteForce()
    {
        var h = RandomHeights(9);
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        BedFor(grid, h, 6, out var min, out var max);
        var rng = new Random(21);
        var hits = 0;
        for (var n = 0; n < 1500; n++)
        {
            var origin = new Vector3((float)rng.NextDouble() * 20 - 4, (float)rng.NextDouble() * 12 - 2, (float)rng.NextDouble() * 20 - 4);
            var dir = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1.3f, (float)rng.NextDouble() * 2 - 1));
            var hit = grid.Raycast(h, min, max, 6, -2f, 4f, origin, dir, 100f, out var t, out var normal);
            var brute = BruteForce(grid, h, origin, dir);
            if (!hit)
            {
                Assert.True(brute > 100f, $"ray {n} missed but brute force hits at {brute}");
                continue;
            }

            hits++;
            Assert.Equal(brute, t, 1e-3f);
            var p = origin + dir * t;
            Assert.Equal(grid.HeightAt(h, p.X, p.Z), p.Y, 1e-4f);
            Assert.True(normal.Y > 0);
        }

        Assert.True(hits > 250, $"only {hits} hits");
    }

    [Fact]
    public void RaycastHandlesVerticalGrazingFromBelowAndMissingRays()
    {
        var h = RandomHeights(4);
        var grid = new TerrainGrid(Quads, Spacing, TerrainDiagonal.Checkerboard);
        BedFor(grid, h, 6, out var min, out var max);

        // Vertical, down and up.
        Assert.True(grid.Raycast(h, min, max, 6, -2, 4, new Vector3(3.3f, 10, 7.7f), -Vector3.UnitY, 50, out var t, out _));
        Assert.Equal(grid.HeightAt(h, 3.3f, 7.7f), 10 - t, 1e-4f);
        Assert.True(grid.Raycast(h, min, max, 6, -2, 4, new Vector3(3.3f, -10, 7.7f), Vector3.UnitY, 50, out t, out _));
        Assert.Equal(grid.HeightAt(h, 3.3f, 7.7f), -10 + t, 1e-4f);

        // From below at a slant: hits the underside.
        var origin = new Vector3(2, -3, 2);
        var dir = Vector3.Normalize(new Vector3(0.3f, 1, 0.2f));
        Assert.True(grid.Raycast(h, min, max, 6, -2, 4, origin, dir, 50, out t, out _));
        var p = origin + dir * t;
        Assert.Equal(grid.HeightAt(h, p.X, p.Z), p.Y, 1e-4f);

        // Grazing: nearly horizontal across the whole map just above the lowest ground.
        origin = new Vector3(-1, 2.5f, 5.1f);
        dir = Vector3.Normalize(new Vector3(1, -0.02f, 0.01f));
        var hit = grid.Raycast(h, min, max, 6, -2, 4, origin, dir, 100, out t, out _);
        var brute = BruteForce(grid, h, origin, dir);
        Assert.Equal(brute < 100, hit);
        if (hit)
            Assert.Equal(brute, t, 1e-3f);

        // Missing: pointing up from above, outside the map, too short.
        Assert.False(grid.Raycast(h, min, max, 6, -2, 4, new Vector3(5, 10, 5), Vector3.UnitY, 50, out _, out _));
        Assert.False(grid.Raycast(h, min, max, 6, -2, 4, new Vector3(-5, 10, -5), -Vector3.UnitY, 50, out _, out _));
        Assert.False(grid.Raycast(h, min, max, 6, -2, 4, new Vector3(5, 10, 5), -Vector3.UnitY, 1, out _, out _));
    }
}
