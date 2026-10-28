using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.1/G3.2: on 200 seeded random grids A* costs what a brute-force Dijkstra costs, for every diagonal mode and every
/// admissible compute/estimate pair; jump point search costs what plain A* costs on uniform weights; paths are legal; and a
/// fixed maze gives golden paths (the determinism lock: the same ids on every OS and CPU).
/// </summary>
public sealed class AStarGridOptimalityTests
{
    private static readonly GridDiagonalMode[] Modes =
        [GridDiagonalMode.Always, GridDiagonalMode.AtLeastOneWalkable, GridDiagonalMode.OnlyIfNoObstacles, GridDiagonalMode.Never];

    // (compute, estimate) pairs where the estimate never exceeds the real cost (weights are ≥ 1).
    private static readonly (GridHeuristic Compute, GridHeuristic Estimate)[] Admissible =
    [
        (GridHeuristic.Euclidean, GridHeuristic.Euclidean),
        (GridHeuristic.Euclidean, GridHeuristic.Chebyshev),
        (GridHeuristic.Octile, GridHeuristic.Octile),
        (GridHeuristic.Octile, GridHeuristic.Euclidean),
        (GridHeuristic.Manhattan, GridHeuristic.Manhattan),
        (GridHeuristic.Manhattan, GridHeuristic.Octile),
        (GridHeuristic.Chebyshev, GridHeuristic.Chebyshev),
    ];

    [Fact]
    public void AStarIsOptimalOnRandomGridsForEveryModeAndAdmissibleHeuristic()
    {
        Span<Vector2I> buffer = stackalloc Vector2I[1024];
        for (var seed = 0; seed < 200; seed++)
        {
            var grid = GridMaps.Random(seed, 18 + seed % 7, 14 + seed % 5, 0.28, weights: true);
            var random = new System.Random(seed * 31 + 1);
            var from = new Vector2I(random.Next(grid.Region.Size.X), random.Next(grid.Region.Size.Y));
            var to = new Vector2I(random.Next(grid.Region.Size.X), random.Next(grid.Region.Size.Y));
            grid.SetPointSolid(from, false);
            grid.SetPointSolid(to, false);
            var mode = Modes[seed % Modes.Length];
            var (compute, estimate) = Admissible[seed % Admissible.Length];
            grid.DiagonalMode = mode;
            grid.DefaultComputeHeuristic = compute;
            grid.DefaultEstimateHeuristic = estimate;

            var expected = GridMaps.Dijkstra(grid, from, to);
            var status = grid.GetIdPath(from, to, buffer, out var count);
            if (float.IsPositiveInfinity(expected))
            {
                Assert.Equal(GridPathStatus.NotFound, status);
                continue;
            }

            Assert.Equal(GridPathStatus.Found, status);
            var path = buffer[..count];
            GridMaps.AssertValid(grid, path);
            Assert.Equal(from, path[0]);
            Assert.Equal(to, path[^1]);
            Assert.True(MathF.Abs(expected - GridMaps.Cost(grid, path)) < 1e-3f,
                $"seed {seed} ({mode}, {compute}/{estimate}): A* {GridMaps.Cost(grid, path)} vs Dijkstra {expected}");
        }
    }

    [Theory]
    [InlineData(GridDiagonalMode.Always, GridHeuristic.Octile)]
    [InlineData(GridDiagonalMode.Always, GridHeuristic.Euclidean)]
    [InlineData(GridDiagonalMode.AtLeastOneWalkable, GridHeuristic.Octile)]
    [InlineData(GridDiagonalMode.AtLeastOneWalkable, GridHeuristic.Euclidean)]
    [InlineData(GridDiagonalMode.OnlyIfNoObstacles, GridHeuristic.Octile)]
    [InlineData(GridDiagonalMode.OnlyIfNoObstacles, GridHeuristic.Euclidean)]
    [InlineData(GridDiagonalMode.Never, GridHeuristic.Manhattan)]
    [InlineData(GridDiagonalMode.Never, GridHeuristic.Euclidean)]
    public void JumpPointSearchCostsWhatAStarCostsOnUniformWeights(GridDiagonalMode mode, GridHeuristic heuristic)
    {
        Span<Vector2I> plain = stackalloc Vector2I[2048];
        Span<Vector2I> jumped = stackalloc Vector2I[2048];
        for (var seed = 0; seed < 200; seed++)
        {
            var grid = GridMaps.Random(1000 + seed, 24 + seed % 9, 18 + seed % 6, 0.15 + seed % 4 * 0.07, weights: false);
            var random = new System.Random(seed * 17 + 3);
            var from = new Vector2I(random.Next(grid.Region.Size.X), random.Next(grid.Region.Size.Y));
            var to = new Vector2I(random.Next(grid.Region.Size.X), random.Next(grid.Region.Size.Y));
            grid.SetPointSolid(from, false);
            grid.SetPointSolid(to, false);
            grid.DiagonalMode = mode;
            grid.DefaultComputeHeuristic = heuristic;
            grid.DefaultEstimateHeuristic = heuristic;

            var a = grid.GetIdPath(from, to, plain, out var plainCount);
            grid.JumpingEnabled = true;
            var j = grid.GetIdPath(from, to, jumped, out var jumpCount);
            grid.JumpingEnabled = false;
            Assert.Equal(a, j);
            if (a != GridPathStatus.Found)
                continue;
            var path = jumped[..jumpCount];
            GridMaps.AssertValid(grid, path); // JPS lists every cell, like plain A*
            Assert.Equal(from, path[0]);
            Assert.Equal(to, path[^1]);
            var plainCost = GridMaps.Cost(grid, plain[..plainCount]);
            var jumpCost = GridMaps.Cost(grid, path);
            Assert.True(MathF.Abs(plainCost - jumpCost) < 1e-3f, $"seed {seed}: JPS {jumpCost} vs A* {plainCost}");
        }
    }

    [Fact]
    public void AFixedMazeGivesTheGoldenPathsWithAndWithoutJumping()
    {
        var grid = Maze();
        var path = grid.GetIdPath(new Vector2I(1, 1), new Vector2I(29, 19));
        Assert.True(GoldenLength == path.Length && GoldenHash == Hash(path), $"A* path: {path.Length} cells, hash {Hash(path)}");

        grid.JumpingEnabled = true;
        var jumped = grid.GetIdPath(new Vector2I(1, 1), new Vector2I(29, 19));
        Assert.True(GoldenJpsLength == jumped.Length && GoldenJpsHash == Hash(jumped), $"JPS path: {jumped.Length} cells, hash {Hash(jumped)}");
        Assert.Equal(GridMaps.Cost(grid, path, weights: false), GridMaps.Cost(grid, jumped), 3);
    }

    // Recorded once from this implementation; a change means paths changed (a determinism or tie-break regression).
    private const int GoldenLength = 55;
    private const ulong GoldenHash = 9691817865205338602;
    private const int GoldenJpsLength = 55;
    private const ulong GoldenJpsHash = 17389575669537272453;

    /// <summary>A 31 × 21 maze from a seeded depth-first carve, with no weights.</summary>
    internal static AStarGrid2D Maze()
    {
        const int w = 31, h = 21;
        var grid = new AStarGrid2D
        {
            Region = new Rect2I(0, 0, w, h),
            DiagonalMode = GridDiagonalMode.OnlyIfNoObstacles,
            DefaultComputeHeuristic = GridHeuristic.Octile,
            DefaultEstimateHeuristic = GridHeuristic.Octile,
        };
        grid.Update();
        grid.FillSolidRegion(grid.Region);
        var random = new System.Random(20261028);
        var stack = new Stack<Vector2I>();
        stack.Push(new Vector2I(1, 1));
        grid.SetPointSolid(new Vector2I(1, 1), false);
        Span<Vector2I> dirs = [new(2, 0), new(-2, 0), new(0, 2), new(0, -2)];
        Span<Vector2I> open = stackalloc Vector2I[4];
        while (stack.Count > 0)
        {
            var c = stack.Peek();
            var options = 0;
            foreach (var d in dirs)
            {
                var n = c + d;
                if (n.X > 0 && n.Y > 0 && n.X < w - 1 && n.Y < h - 1 && grid.IsPointSolid(n))
                    open[options++] = n;
            }

            if (options == 0)
            {
                stack.Pop();
                continue;
            }

            var next = open[random.Next(options)];
            grid.SetPointSolid(next, false);
            grid.SetPointSolid(new Vector2I((c.X + next.X) / 2, (c.Y + next.Y) / 2), false);
            stack.Push(next);
        }

        // A few loops and open rooms, so diagonal moves and ties matter.
        for (var i = 0; i < 40; i++)
            grid.SetPointSolid(new Vector2I(1 + random.Next(w - 2), 1 + random.Next(h - 2)), false);
        grid.FillSolidRegion(new Rect2I(10, 6, 6, 5), false);
        return grid;
    }

    internal static ulong Hash(ReadOnlySpan<Vector2I> path)
    {
        var hash = 14695981039346656037ul;
        foreach (var c in path)
        {
            hash = (hash ^ (uint)c.X) * 1099511628211ul;
            hash = (hash ^ (uint)c.Y) * 1099511628211ul;
        }

        return hash;
    }
}
