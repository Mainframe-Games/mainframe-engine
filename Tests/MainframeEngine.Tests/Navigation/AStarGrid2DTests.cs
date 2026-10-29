using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.1: <see cref="AStarGrid2D"/> — Godot's diagonal rules, heuristics, weights, partial paths, spans and limits.</summary>
public sealed class AStarGrid2DTests
{
    private static Vector2I[] Path(string map, GridDiagonalMode mode, bool jumping = false, bool partial = false)
    {
        var grid = GridMaps.Parse(map, out var s, out var t, mode);
        grid.JumpingEnabled = jumping;
        return grid.GetIdPath(s, t, partial);
    }

    [Theory]
    [InlineData(GridDiagonalMode.Always, true)]
    [InlineData(GridDiagonalMode.AtLeastOneWalkable, false)]
    [InlineData(GridDiagonalMode.OnlyIfNoObstacles, false)]
    [InlineData(GridDiagonalMode.Never, false)]
    public void ADiagonalBetweenTwoSolidCellsIsOnlyAllowedByAlways(GridDiagonalMode mode, bool allowed)
    {
        foreach (var jumping in new[] { false, true })
        {
            var path = Path("""
                S#
                #T
                """, mode, jumping);
            Assert.Equal(allowed ? [new(0, 0), new(1, 1)] : [], path);
        }
    }

    [Theory]
    [InlineData(GridDiagonalMode.Always, 2)]
    [InlineData(GridDiagonalMode.AtLeastOneWalkable, 2)]
    [InlineData(GridDiagonalMode.OnlyIfNoObstacles, 3)]
    [InlineData(GridDiagonalMode.Never, 3)]
    public void ACornerWithOneWalkableSideCutsUnlessObstaclesForbidIt(GridDiagonalMode mode, int cells)
    {
        foreach (var jumping in new[] { false, true })
        {
            var path = Path("""
                S#
                .T
                """, mode, jumping);
            Assert.Equal(cells, path.Length);
            Assert.Equal(new Vector2I(0, 0), path[0]);
            Assert.Equal(new Vector2I(1, 1), path[^1]);
            if (cells == 3)
                Assert.Equal(new Vector2I(0, 1), path[1]);
        }
    }

    [Theory]
    [InlineData(GridDiagonalMode.Always, 5)]
    [InlineData(GridDiagonalMode.Never, 9)]
    public void OpenGridsGoDiagonallyUnlessFourWay(GridDiagonalMode mode, int cells)
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 5, 5), DiagonalMode = mode };
        grid.Update();
        var path = grid.GetIdPath(Vector2I.Zero, new Vector2I(4, 4));
        Assert.Equal(cells, path.Length);
        GridMaps.AssertValid(grid, path);
    }

    [Theory]
    [InlineData(GridHeuristic.Euclidean, 3f + 1.4142135f)]
    [InlineData(GridHeuristic.Octile, 3f + 1.4142135f)]
    [InlineData(GridHeuristic.Manhattan, 5f)]
    [InlineData(GridHeuristic.Chebyshev, 4f)]
    public void EachHeuristicPricesStepsInCells(GridHeuristic heuristic, float cost)
    {
        // (0, 0) → (4, 1) on an open grid: three straight steps and one diagonal is optimal for every heuristic.
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 6, 3), DefaultComputeHeuristic = heuristic, DefaultEstimateHeuristic = heuristic };
        grid.Update();
        var path = grid.GetIdPath(Vector2I.Zero, new Vector2I(4, 1));
        Assert.Equal(cost, GridMaps.Cost(grid, path), 4);
        Assert.Equal(cost, GridMaps.Dijkstra(grid, Vector2I.Zero, new Vector2I(4, 1)), 4);
    }

    [Fact]
    public void HeuristicsMatchGodotsFormulas()
    {
        Assert.Equal(5f, AStarGrid2D.Distance(GridHeuristic.Euclidean, 3, -4));
        Assert.Equal(7f, AStarGrid2D.Distance(GridHeuristic.Manhattan, -3, 4));
        Assert.Equal((float)(Math.Sqrt(2) - 1) * 3 + 4, AStarGrid2D.Distance(GridHeuristic.Octile, 3, 4));
        Assert.Equal(4f, AStarGrid2D.Distance(GridHeuristic.Chebyshev, 3, 4));
    }

    [Fact]
    public void WeightedCellsRerouteThePathAroundThem()
    {
        const string map = """
            .......
            S~~~~~T
            .......
            """;
        var plain = GridMaps.Parse(map.Replace('~', '.'), out var s, out var t);
        Assert.All(plain.GetIdPath(s, t), c => Assert.Equal(1, c.Y)); // straight along the middle row

        var mud = GridMaps.Parse(map, out s, out t);
        var path = mud.GetIdPath(s, t);
        Assert.DoesNotContain(path[1..^1], c => c.Y == 1); // around the mud
        Assert.Equal(GridMaps.Dijkstra(mud, s, t), GridMaps.Cost(mud, path), 4);
        Assert.Equal(3f, mud.GetPointWeightScale(new Vector2I(2, 1)));
        Assert.Equal(5, mud.WeightedCount);
    }

    [Fact]
    public void JumpPointSearchIgnoresWeightsLikeGodot()
    {
        var grid = GridMaps.Parse("""
            .......
            S~~~~~T
            .......
            """, out var s, out var t);
        grid.JumpingEnabled = true;
        Assert.All(grid.GetIdPath(s, t), c => Assert.Equal(1, c.Y));
    }

    [Fact]
    public void ASolidOrOutsideStartIsAnInvalidPoint()
    {
        var grid = GridMaps.Parse("""
            #..
            ...
            """, out _, out _);
        Span<Vector2I> buffer = stackalloc Vector2I[16];
        Assert.Equal(GridPathStatus.InvalidPoint, grid.GetIdPath(new Vector2I(0, 0), new Vector2I(2, 1), buffer, out var count));
        Assert.Equal(0, count);
        Assert.Equal(GridPathStatus.InvalidPoint, grid.GetIdPath(new Vector2I(-1, 0), new Vector2I(2, 1), buffer, out _));
        Assert.Equal(GridPathStatus.InvalidPoint, grid.GetIdPath(new Vector2I(1, 0), new Vector2I(3, 1), buffer, out _));
        Assert.Empty(grid.GetIdPath(new Vector2I(0, 0), new Vector2I(2, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreachableTargetIsNotFoundOrThePartialPathToTheClosestCell(bool jumping)
    {
        var grid = GridMaps.Parse("""
            S...#..
            ....#.T
            ....#..
            """, out var s, out var t);
        grid.JumpingEnabled = jumping;
        Span<Vector2I> buffer = stackalloc Vector2I[32];
        Assert.Equal(GridPathStatus.NotFound, grid.GetIdPath(s, t, buffer, out var count));
        Assert.Equal(0, count);

        Assert.Equal(GridPathStatus.Partial, grid.GetIdPath(s, t, buffer, out count, allowPartialPath: true));
        Assert.Equal(new Vector2I(3, 1), buffer[count - 1]); // the reachable cell nearest the target
        Assert.Equal(s, buffer[0]);

        // A solid target: NotFound at once without partial paths, else the cell next to it.
        grid.SetPointSolid(new Vector2I(6, 1), false);
        grid.SetPointSolid(new Vector2I(3, 1));
        Assert.Equal(GridPathStatus.NotFound, grid.GetIdPath(s, new Vector2I(3, 1), buffer, out _));
        Assert.Equal(GridPathStatus.Partial, grid.GetIdPath(s, new Vector2I(3, 1), buffer, out count, allowPartialPath: true));
        Assert.Equal(1f, AStarGrid2D.Distance(GridHeuristic.Chebyshev, buffer[count - 1].X - 3, buffer[count - 1].Y - 1));
    }

    [Fact]
    public void ABufferTooSmallReportsTheSizeItNeeds()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 8, 1) };
        grid.Update();
        Span<Vector2I> small = stackalloc Vector2I[3];
        Assert.Equal(GridPathStatus.BufferTooSmall, grid.GetIdPath(Vector2I.Zero, new Vector2I(7, 0), small, out var needed));
        Assert.Equal(8, needed);
        Span<Vector2I> exact = stackalloc Vector2I[8];
        Assert.Equal(GridPathStatus.Found, grid.GetIdPath(Vector2I.Zero, new Vector2I(7, 0), exact, out var count));
        Assert.Equal(8, count);
        Span<Vector2> points = stackalloc Vector2[8];
        Assert.Equal(GridPathStatus.Found, grid.GetPointPath(Vector2I.Zero, new Vector2I(7, 0), points, out count));
        Assert.Equal(new Vector2(7.5f, 0.5f), points[7]);
    }

    [Fact]
    public void ADirtyGridThrowsUntilUpdate()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 4, 4) };
        Assert.True(grid.IsDirty);
        Assert.Throws<InvalidOperationException>(() => grid.GetIdPath(Vector2I.Zero, Vector2I.One));
        Assert.Throws<InvalidOperationException>(() => grid.SetPointSolid(Vector2I.One));
        grid.Update();
        Assert.False(grid.IsDirty);
        grid.SetPointSolid(Vector2I.One);
        Assert.True(grid.IsPointSolid(Vector2I.One));

        // Cell size, offset and the search settings need no update and keep the cells; a new region resets them.
        grid.CellSize = new Vector2(16);
        grid.Offset = new Vector2(100);
        grid.DiagonalMode = GridDiagonalMode.Never;
        Assert.False(grid.IsDirty);
        Assert.True(grid.IsPointSolid(Vector2I.One));
        grid.Region = new Rect2I(0, 0, 4, 4);
        Assert.False(grid.IsDirty);
        grid.Region = new Rect2I(0, 0, 5, 4);
        Assert.True(grid.IsDirty);
        grid.Update();
        Assert.False(grid.IsPointSolid(Vector2I.One));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Region = new Rect2I(0, 0, 4096, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetPointSolid(new Vector2I(9, 9)));
    }

    [Fact]
    public void PositionsAreCellCentresAndRoundTrip()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(-4, -2, 10, 6), CellSize = new Vector2(32, 16), Offset = new Vector2(100, 50) };
        grid.Update();
        Assert.Equal(new Vector2(100 + 2.5f * 32, 50 + 1.5f * 16), grid.GetPointPosition(new Vector2I(2, 1)));
        for (var y = -2; y < 4; y++)
            for (var x = -4; x < 6; x++)
                Assert.Equal(new Vector2I(x, y), grid.GetPointAt(grid.GetPointPosition(new Vector2I(x, y))));
        Assert.Equal(new Vector2I(-1, -1), grid.GetPointAt(new Vector2(99.9f, 49.9f)));
        Assert.Equal(Vector2I.Zero, grid.GetPointAt(new Vector2(100, 50)));
    }

    [Fact]
    public void FillsClipToTheRegionAndClearCellsResets()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 4, 4) };
        grid.Update();
        var version = grid.Version;
        grid.FillSolidRegion(new Rect2I(-2, -2, 4, 4));
        Assert.Equal(4, grid.SolidCount);
        Assert.True(grid.Version > version);
        grid.FillWeightScaleRegion(new Rect2I(2, 2, 10, 10), 2f);
        Assert.Equal(4, grid.WeightedCount);
        Assert.Equal(2f, grid.GetPointWeightScale(new Vector2I(3, 3)));
        grid.FillWeightScaleRegion(new Rect2I(2, 2, 1, 1), 1f);
        Assert.Equal(3, grid.WeightedCount);
        grid.ClearCells();
        Assert.Equal(0, grid.SolidCount);
        Assert.Equal(0, grid.WeightedCount);
        grid.Clear();
        Assert.Equal(default, grid.Region);
        Assert.False(grid.IsInBounds(Vector2I.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetPointWeightScale(Vector2I.Zero, -1f));
    }

    [Fact]
    public void TheQueryStampWrapsAroundWithoutStaleCells()
    {
        var grid = GridMaps.Random(7, 20, 20, 0.25, weights: true);
        grid.SetPointSolid(Vector2I.Zero, false);
        grid.SetPointSolid(new Vector2I(19, 19), false);
        var expected = grid.GetIdPath(Vector2I.Zero, new Vector2I(19, 19));
        grid.Search.QueryStamp = 0xFFFF_FFE8u;
        for (var i = 0; i < 12; i++)
            Assert.Equal(expected, grid.GetIdPath(Vector2I.Zero, new Vector2I(19, 19)));
        Assert.True(grid.Search.QueryStamp < 0x100);
    }

    [Fact]
    public void ASearchCappedByMaxSearchNodesIsPartialOrNotFound()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 64, 64), MaxSearchNodes = 10 };
        grid.Update();
        grid.FillSolidRegion(new Rect2I(32, 0, 1, 63));
        Span<Vector2I> buffer = stackalloc Vector2I[128];
        Assert.Equal(GridPathStatus.NotFound, grid.GetIdPath(Vector2I.Zero, new Vector2I(63, 0), buffer, out _));
        Assert.Equal(GridPathStatus.Partial, grid.GetIdPath(Vector2I.Zero, new Vector2I(63, 0), buffer, out var count, allowPartialPath: true));
        Assert.True(count > 1);
        grid.MaxSearchNodes = 0;
        Assert.Equal(GridPathStatus.Found, grid.GetIdPath(Vector2I.Zero, new Vector2I(63, 0), buffer, out _));
    }

    [Fact]
    public void TheSameStartAndTargetIsAOneCellPath()
    {
        var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 3, 3) };
        grid.Update();
        Assert.Equal([Vector2I.One], grid.GetIdPath(Vector2I.One, Vector2I.One));
    }

    [Fact]
    public void OverriddenCostsAreUsed()
    {
        // A grid that makes moving up expensive takes the long way round a pillar instead.
        var grid = new UphillGrid { Region = new Rect2I(0, 0, 3, 3), DiagonalMode = GridDiagonalMode.Never };
        grid.Update();
        var path = grid.GetIdPath(new Vector2I(0, 2), new Vector2I(2, 0));
        Assert.Equal(new Vector2I(0, 2), path[0]);
        Assert.Equal([new(0, 2), new(1, 2), new(2, 2), new(2, 1), new(2, 0)], path);
    }

    private sealed class UphillGrid : AStarGrid2D
    {
        protected override float ComputeCost(Vector2I fromId, Vector2I toId) => toId.Y < fromId.Y ? (toId.X == 2 ? 1f : 10f) : 1f;

        protected override float EstimateCost(Vector2I fromId, Vector2I endId) => 0f;
    }
}
