using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>ASCII grids for the navigation tests: <c>.</c> walkable, <c>#</c> solid, <c>~</c> weight 3, <c>S</c> start, <c>T</c> target.</summary>
internal static class GridMaps
{
    public static AStarGrid2D Parse(string map, out Vector2I start, out Vector2I target, GridDiagonalMode mode = GridDiagonalMode.Always,
        GridHeuristic heuristic = GridHeuristic.Euclidean)
    {
        var rows = map.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var grid = new AStarGrid2D
        {
            Region = new Rect2I(0, 0, rows[0].Length, rows.Length),
            DiagonalMode = mode,
            DefaultComputeHeuristic = heuristic,
            DefaultEstimateHeuristic = heuristic,
        };
        grid.Update();
        start = target = default;
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                var c = new Vector2I(x, y);
                switch (rows[y][x])
                {
                    case '#': grid.SetPointSolid(c); break;
                    case '~': grid.SetPointWeightScale(c, 3f); break;
                    case 'S': start = c; break;
                    case 'T': target = c; break;
                }
            }
        }

        return grid;
    }

    /// <summary>A seeded random grid: <paramref name="solidChance"/> of the cells solid, optional weights 1–4.</summary>
    public static AStarGrid2D Random(int seed, int width, int height, double solidChance, bool weights)
    {
        var random = new System.Random(seed);
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, width, height) };
        grid.Update();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (random.NextDouble() < solidChance)
                    grid.SetPointSolid(new Vector2I(x, y));
                else if (weights && random.NextDouble() < 0.3)
                    grid.SetPointWeightScale(new Vector2I(x, y), 1 + random.Next(4));
            }
        }

        return grid;
    }

    /// <summary>The cost of a cell path under the grid's compute heuristic and weights (cells must be neighbours).</summary>
    public static float Cost(AStarGrid2D grid, ReadOnlySpan<Vector2I> path, bool weights = true)
    {
        var cost = 0f;
        for (var i = 1; i < path.Length; i++)
        {
            var d = path[i] - path[i - 1];
            var step = AStarGrid2D.Distance(grid.DefaultComputeHeuristic, d.X, d.Y);
            cost += step * (weights ? grid.GetPointWeightScale(path[i]) : 1f);
        }

        return cost;
    }

    /// <summary>Every step is to a neighbour the grid's diagonal mode allows.</summary>
    public static void AssertValid(AStarGrid2D grid, ReadOnlySpan<Vector2I> path)
    {
        for (var i = 1; i < path.Length; i++)
        {
            var d = path[i] - path[i - 1];
            Assert.True(Math.Abs(d.X) <= 1 && Math.Abs(d.Y) <= 1 && d != Vector2I.Zero, $"step {i} {path[i - 1]} → {path[i]} is not to a neighbour");
            Assert.True(grid.CanStep(path[i - 1].X, path[i - 1].Y, d.X, d.Y), $"step {i} {path[i - 1]} → {path[i]} is not allowed");
        }
    }

    /// <summary>Brute-force Dijkstra (a plain priority queue, no heuristic): the cheapest cost, or +∞.</summary>
    public static float Dijkstra(AStarGrid2D grid, Vector2I from, Vector2I to, bool weights = true)
    {
        var best = new Dictionary<Vector2I, float> { [from] = 0f };
        var queue = new PriorityQueue<Vector2I, float>();
        queue.Enqueue(from, 0f);
        while (queue.TryDequeue(out var cell, out var cost))
        {
            if (cost > best[cell])
                continue;
            if (cell == to)
                return cost;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 || !grid.CanStep(cell.X, cell.Y, dx, dy))
                        continue;
                    var next = new Vector2I(cell.X + dx, cell.Y + dy);
                    var step = AStarGrid2D.Distance(grid.DefaultComputeHeuristic, dx, dy) * (weights ? grid.GetPointWeightScale(next) : 1f);
                    var total = cost + step;
                    if (!best.TryGetValue(next, out var known) || total < known)
                    {
                        best[next] = total;
                        queue.Enqueue(next, total);
                    }
                }
            }
        }

        return float.PositiveInfinity;
    }
}
