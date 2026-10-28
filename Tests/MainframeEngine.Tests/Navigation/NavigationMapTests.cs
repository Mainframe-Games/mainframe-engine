using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.3: the navigation server's maps — registration and sync points, queries, post-processing, debug draw.</summary>
public sealed class NavigationMapTests
{
    [Fact]
    public void RegionsJoinTheirWorldsMapAtTheNextSync()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(8, 4);
        Assert.Same(h.Map, grid.Map);
        Assert.Empty(h.Map.Regions); // queued until the map syncs
        var iteration = h.Map.IterationId;
        h.Run(1);
        Assert.Same(grid, Assert.Single(h.Map.Regions));
        Assert.True(h.Map.IterationId > iteration);
        Assert.Same(h.Map, h.Tree.Root.World2D.NavigationMap);

        iteration = h.Map.IterationId;
        h.Run(3);
        Assert.Equal(iteration, h.Map.IterationId); // nothing changed: no new iteration

        grid.QueueFree();
        h.Run(2);
        Assert.Empty(h.Map.Regions);
        Assert.Null(grid.Map);
    }

    [Fact]
    public void CellEditsApplyAtTheSyncAndBumpTheIteration()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(8, 4);
        h.Run(1);
        var iteration = h.Map.IterationId;
        grid.SetCellSolid(new Vector2I(3, 1), true);
        grid.SetCellWeight(new Vector2I(4, 1), 5f);
        Assert.False(grid.LiveGrid.IsPointSolid(new Vector2I(3, 1))); // not before the sync
        h.Run(1);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(3, 1)));
        Assert.Equal(5f, grid.Grid.GetPointWeightScale(new Vector2I(4, 1)));
        Assert.Equal(iteration + 1, h.Map.IterationId);
        grid.SetCellSolid(new Vector2I(99, 99), true); // outside: ignored at the sync
        h.Run(1);
    }

    [Fact]
    public void MapsSyncInProcessWhenNoFixedStepRuns()
    {
        using var h = new NavigationHarness(physics: false);
        h.Tree.EditMode = true; // the editor: fixed-step servers do not step
        var grid = h.AddGrid(4, 4);
        h.Run(1);
        Assert.Single(h.Map.Regions);
        h.Tree.EditMode = false;
        h.Tree.Paused = true;
        grid.SetCellSolid(Vector2I.One, true);
        h.Run(1);
        Assert.True(grid.Grid.IsPointSolid(Vector2I.One));
    }

    [Fact]
    public void AGridOutsideTheTreeAppliesChangesAtOnce()
    {
        var grid = new NavigationGrid2D { Region = new Rect2I(0, 0, 4, 4), CellSize = new Vector2(10) };
        grid.SetCellSolid(new Vector2I(1, 1), true);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(1, 1)));
        Assert.Equal(GridDiagonalMode.OnlyIfNoObstacles, grid.Grid.DiagonalMode);
        Assert.Equal(GridHeuristic.Octile, grid.Grid.DefaultComputeHeuristic);
        grid.Dispose();
    }

    [Fact]
    public void QueriesStartAndEndAtTheExactPointsAndPullStringsThroughOpenSpace()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(10, 6, 10f);
        h.Run(1);
        var result = new NavigationPath2D();
        var status = h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(12, 13), new Vector2(87, 54)), result);
        Assert.Equal(GridPathStatus.Found, status);
        Assert.Same(grid, result.Region);
        Assert.Equal(new Vector2(12, 13), result.Points[0]);
        Assert.Equal(new Vector2(87, 54), result.Points[^1]);
        Assert.Equal(2, result.Count); // open field: straight there
        Assert.Equal(new Vector2I(1, 1), result.Cells[0]);
        Assert.Equal(new Vector2I(8, 5), result.Cells[^1]);

        h.Run(1);
        status = h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(12, 13), new Vector2(87, 54),
            Postprocessing: NavigationPathPostprocessing.None), result);
        Assert.Equal(GridPathStatus.Found, status);
        Assert.Equal(8, result.Count); // every cell: (1,1) → (8,5) is 7 steps
    }

    [Fact]
    public void PostprocessingKeepsCornersOrOnlyWhatLineOfSightNeeds()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(9, 7, 10f);
        // A wall with a gap at the bottom: the path goes down, through the gap, and up.
        for (var y = 0; y < 6; y++)
            grid.SetCellSolid(new Vector2I(4, y), true);
        h.Run(1);
        var start = grid.CellCenter(new Vector2I(1, 1));
        var target = grid.CellCenter(new Vector2I(7, 1));
        var funnel = new NavigationPath2D();
        var corners = new NavigationPath2D();
        var all = new NavigationPath2D();
        h.Map.QueryPath(new NavigationPathQuery2D(start, target), funnel);
        h.Map.QueryPath(new NavigationPathQuery2D(start, target, Postprocessing: NavigationPathPostprocessing.EdgeCentered), corners);
        h.Map.QueryPath(new NavigationPathQuery2D(start, target, Postprocessing: NavigationPathPostprocessing.None), all);
        Assert.True(funnel.Count < corners.Count && corners.Count < all.Count, $"{funnel.Count} < {corners.Count} < {all.Count}");
        Assert.InRange(funnel.Count, 4, 5); // start, the corners of the gap, target
        Assert.Contains(funnel.Cells.ToArray(), c => c.Y == 6); // through the gap
        Assert.Equal(start, funnel.Points[0]);
        Assert.Equal(target, funnel.Points[^1]);
        // Every pulled segment keeps line of sight.
        for (var i = 1; i < funnel.Count; i++)
            Assert.True(GridLineOfSight.IsClear(grid.Grid, funnel.Cells[i - 1], funnel.Cells[i]));
    }

    [Fact]
    public void LineOfSightRespectsTheDiagonalModeAtCorners()
    {
        var grid = GridMaps.Parse("""
            .#.
            #..
            ...
            """, out _, out _, GridDiagonalMode.OnlyIfNoObstacles);
        Assert.False(GridLineOfSight.IsClear(grid, new Vector2I(0, 0), new Vector2I(1, 1))); // squeezes between two walls
        grid.DiagonalMode = GridDiagonalMode.Always;
        Assert.True(GridLineOfSight.IsClear(grid, new Vector2I(0, 0), new Vector2I(1, 1)));
        Assert.True(GridLineOfSight.IsClear(grid, new Vector2I(0, 2), new Vector2I(2, 1)));
        Assert.False(GridLineOfSight.IsClear(grid, new Vector2I(0, 0), new Vector2I(2, 0)));
    }

    [Fact]
    public void StartsInWallsMoveOutTargetsOutsideArePartial()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(8, 8, 10f);
        grid.SetCellSolid(new Vector2I(2, 2), true);
        h.Run(1);
        var result = new NavigationPath2D();

        // A start inside a solid cell moves to the nearest walkable one.
        Assert.Equal(GridPathStatus.Found, h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(25, 25), new Vector2(75, 75)), result));
        Assert.NotEqual(new Vector2I(2, 2), result.Cells[0]);

        // A target outside the region: the path to the nearest edge cell, partial.
        Assert.Equal(GridPathStatus.Partial, h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(5, 5), new Vector2(300, 35)), result));
        Assert.Equal(new Vector2I(7, 3), result.Cells[^1]);
        Assert.False(result.Points[^1] == new Vector2(300, 35));
        Assert.Equal(GridPathStatus.NotFound,
            h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(5, 5), new Vector2(300, 35), AllowPartialPath: false), result));

        // A start outside every region (or on another layer) is invalid.
        Assert.Equal(GridPathStatus.InvalidPoint, h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(-50, 5), new Vector2(5, 5)), result));
        Assert.Equal(GridPathStatus.InvalidPoint, h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(5, 5), new Vector2(15, 5), NavigationLayers: 2), result));
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void RegionsArePickedByLayerAndPosition()
    {
        using var h = new NavigationHarness(physics: false);
        var a = h.AddGrid(4, 4, 10f, "A");
        var b = h.AddGrid(4, 4, 10f, "B");
        b.Position = new Vector2(100, 0);
        var c = h.AddGrid(4, 4, 10f, "C");
        c.NavigationLayers = 2;
        h.Run(1);
        Assert.Same(a, h.Map.FindRegion(new Vector2(5, 5), 1));
        Assert.Same(b, h.Map.FindRegion(new Vector2(105, 5), 1));
        Assert.Same(c, h.Map.FindRegion(new Vector2(5, 5), 2));
        Assert.Null(h.Map.FindRegion(new Vector2(60, 5), 1));

        // Moving a grid moves its cells at the next sync.
        var iteration = h.Map.IterationId;
        b.Position = new Vector2(200, 0);
        h.Run(1);
        Assert.Same(b, h.Map.FindRegion(new Vector2(205, 5), 1));
        Assert.True(h.Map.IterationId > iteration);
        Assert.Equal(new Vector2(205, 5), b.CellCenter(Vector2I.Zero));
        Assert.Equal(Vector2I.Zero, b.CellAt(new Vector2(209, 9)));
    }

    [Fact]
    public void GetClosestPointSnapsOutOfWallsAndIntoRegions()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(4, 4, 10f);
        grid.SetCellSolid(new Vector2I(1, 1), true);
        h.Run(1);
        Assert.Equal(new Vector2(3, 3), h.Map.GetClosestPoint(new Vector2(3, 3)));
        var snapped = h.Map.GetClosestPoint(new Vector2(15, 15));
        Assert.NotEqual(new Vector2I(1, 1), grid.CellAt(snapped));
        Assert.Equal(new Vector2(35, 15), h.Map.GetClosestPoint(new Vector2(80, 15)));
    }

    [Fact]
    public void DebugDrawShowsGridsCulledToTheCamera()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(16, 16, 10f);
        grid.SetCellSolid(new Vector2I(2, 2), true);
        grid.SetCellWeight(new Vector2I(3, 3), 2f);
        h.Navigation.DebugDrawEnabled = true;
        h.Run(1);
        var lines = h.Tree.Root.DebugLines;
        // Outline 4 + 15 + 15 cell lines + solid (4 + 2) + weighted (8).
        Assert.Equal(4 + 30 + 6 + 8, lines.LineCount);
        lines.Clear();
        h.Navigation.DebugDrawEnabled = false;
        h.Run(1);
        Assert.Equal(0, lines.LineCount);
    }
}
