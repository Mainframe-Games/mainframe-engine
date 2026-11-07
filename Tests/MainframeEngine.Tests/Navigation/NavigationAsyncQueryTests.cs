using System.Numerics;
using static MainframeEngine.Tests.Navigation.NavigationMeshQueryTests;
using static MainframeEngine.Tests.Navigation.NavigationPolygonBuilderTests;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.10: async path queries — delivery exactly at the next sync, the same points as <see cref="NavigationMap2D.QueryPath"/>
/// whatever the worker count and thread timing, the map as it was at submission, cancelling, async agents, and the
/// allocation gate's async case (<b>0 bytes</b>).
/// </summary>
public sealed class NavigationAsyncQueryTests
{
    // A 64 × 64 grid of 16 px with offset walls, and a navmesh hall beside it (x ≥ 1100).
    private static NavigationGrid2D Level(NavigationHarness h)
    {
        var grid = h.AddGrid(64, 64, 16f);
        grid.Source = NavigationGridSource.Manual;
        for (var x = 8; x < 64; x += 8)
            for (var y = 0; y < 64; y++)
                if ((y + x) % 32 is not (3 or 4))
                    grid.SetCellSolid(new Vector2I(x, y), true);
        var outlines = new List<Vector2[]> { Box(1100, 0, 1900, 800) };
        for (var i = 0; i < 6; i++)
            outlines.Add(Box(1180 + i * 110, 100 + i * 20, 1230 + i * 110, 700 - i * 30));
        AddRegion(h, Mesh([.. outlines]));
        h.Run(1);
        return grid;
    }

    private static NavigationPathQuery2D RandomQuery(Random random)
    {
        var onGrid = random.Next(3) != 0;
        Vector2 Point() => onGrid
            ? new Vector2(random.NextSingle() * 1024f, random.NextSingle() * 1024f)
            : new Vector2(1100 + random.NextSingle() * 800f, random.NextSingle() * 800f);
        return new NavigationPathQuery2D(Point(), Point(), Postprocessing: (NavigationPathPostprocessing)random.Next(3));
    }

    [Fact]
    public void ResultsArriveAtTheNextSyncAndNeverBefore()
    {
        using var h = new NavigationHarness(physics: false);
        Level(h);
        var query = new NavigationPathQuery2D(new Vector2(8, 8), new Vector2(1000, 1000));
        var result = new NavigationPath2D();
        var request = h.Map.QueryPathAsync(query, result);
        Assert.True(request.IsValid);
        Assert.False(request.IsCompleted);
        Thread.Sleep(50); // the workers are done by now; the result still waits for the sync
        Assert.False(request.IsCompleted);
        Assert.Equal(0, result.Count);
        Assert.Equal(1, h.Navigation.PendingQueries);

        h.Run(1);
        Assert.True(request.IsCompleted);
        Assert.Equal(0, h.Navigation.PendingQueries);
        var expected = new NavigationPath2D();
        h.Map.QueryPath(query, expected);
        Assert.Equal(expected.Status, request.Status);
        Assert.Equal(expected.Points.ToArray(), result.Points.ToArray());
        Assert.Equal(expected.Owners.ToArray(), result.Owners.ToArray());
        Assert.True(result.Count > 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void ManyRequestsMatchTheImmediateQueriesForAnyWorkerCount(int workers)
    {
        using var h = new NavigationHarness(physics: false);
        h.Navigation.QueryWorkerCount = workers;
        Level(h);
        var random = new Random(7);
        var queries = new NavigationPathQuery2D[200];
        var results = new NavigationPath2D[queries.Length];
        var requests = new NavigationPathRequest[queries.Length];
        for (var i = 0; i < queries.Length; i++)
        {
            queries[i] = RandomQuery(random);
            results[i] = new NavigationPath2D();
            requests[i] = h.Map.QueryPathAsync(queries[i], results[i]);
        }

        h.Run(1);
        var expected = new NavigationPath2D();
        for (var i = 0; i < queries.Length; i++)
        {
            Assert.True(requests[i].IsCompleted);
            h.Map.QueryPath(queries[i], expected);
            Assert.Equal(expected.Status, results[i].Status);
            Assert.Equal(expected.Points.ToArray(), results[i].Points.ToArray());
            Assert.Equal(expected.Cells.ToArray(), results[i].Cells.ToArray());
        }
    }

    [Fact]
    public void RequestsSeeTheMapAsItWasWhenTheyWereMade()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(16, 4);
        grid.Source = NavigationGridSource.Manual;
        h.Run(1);
        var result = new NavigationPath2D();
        var request = h.Map.QueryPathAsync(new NavigationPathQuery2D(new Vector2(16, 48), new Vector2(496, 48),
            Postprocessing: NavigationPathPostprocessing.None), result);
        for (var y = 0; y < 4; y++)
            grid.SetCellSolid(new Vector2I(8, y), true); // a wall across, applied at the same sync
        var iteration = h.Map.IterationId;
        h.Run(1);
        Assert.True(h.Map.IterationId > iteration);
        Assert.Equal(GridPathStatus.Found, request.Status); // computed against the open map
        Assert.Equal(iteration, result.MapIteration);
        Assert.Contains(new Vector2I(8, 1), result.Cells.ToArray());

        var after = new NavigationPath2D();
        var second = h.Map.QueryPathAsync(new NavigationPathQuery2D(new Vector2(16, 48), new Vector2(496, 48), AllowPartialPath: false), after);
        h.Run(1);
        Assert.Equal(GridPathStatus.NotFound, second.Status);
    }

    [Fact]
    public void CancelledRequestsLeaveTheirResultAlone()
    {
        using var h = new NavigationHarness(physics: false);
        Level(h);
        var result = new NavigationPath2D();
        h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(8, 8), new Vector2(40, 8)), result);
        var before = result.Points.ToArray();
        var request = h.Map.QueryPathAsync(new NavigationPathQuery2D(new Vector2(8, 8), new Vector2(1000, 1000)), result);
        request.Cancel();
        Assert.True(request.IsCompleted);
        Assert.True(request.WasCancelled);
        Assert.Equal(GridPathStatus.NotFound, request.Status);
        h.Run(1);
        Assert.Equal(before, result.Points.ToArray());
        Assert.False(default(NavigationPathRequest).IsValid);
        Assert.True(default(NavigationPathRequest).IsCompleted);
    }

    [Fact]
    public void AsyncAgentsTakeTheirPathOneSyncLaterAndArrive()
    {
        using var h = new NavigationHarness(physics: false);
        Level(h);
        var immediate = h.AddWalker(new Vector2(8, 8), "Immediate");
        var async = h.AddWalker(new Vector2(8, 8), "Async");
        async.Agent.PathMode = NavigationPathMode.Async;
        h.Run(1);
        int immediateChanges = 0, asyncChanges = 0;
        immediate.Agent.PathChanged += () => immediateChanges++;
        async.Agent.PathChanged += () => asyncChanges++;
        immediate.Agent.TargetPosition = async.Agent.TargetPosition = new Vector2(1000, 1000);

        immediate.Agent.GetNextPathPosition();
        async.Agent.GetNextPathPosition();
        Assert.Equal(1, immediateChanges);
        Assert.Equal(0, asyncChanges);
        Assert.True(async.Agent.IsPathAsync);
        Assert.True(async.Agent.IsPathPending);
        Assert.Equal(async.GlobalPosition, async.Agent.GetNextPathPosition()); // stands still: no path for the new target yet
        Assert.False(async.Agent.IsNavigationFinished());

        h.Run(1);
        async.Agent.GetNextPathPosition();
        Assert.Equal(1, asyncChanges);
        Assert.False(async.Agent.IsPathPending);
        Assert.Equal(immediate.Agent.GetCurrentNavigationPath().ToArray(), async.Agent.GetCurrentNavigationPath().ToArray());

        for (var i = 0; i < 1200 && !(immediate.Agent.IsTargetReached() && async.Agent.IsTargetReached()); i++)
            h.Run(1);
        Assert.True(immediate.Agent.IsTargetReached());
        Assert.True(async.Agent.IsTargetReached());
    }

    [Fact]
    public void AnAsyncAgentKeepsItsOldPathWhileTheMapChanges()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = Level(h);
        var walker = h.AddWalker(new Vector2(8, 8));
        h.Navigation.AsyncAgentsByDefault = true;
        h.Run(1);
        Assert.True(walker.Agent.IsPathAsync);
        walker.Agent.TargetPosition = new Vector2(1000, 1000);
        h.Run(3);
        var path = walker.Agent.GetCurrentNavigationPath().ToArray();
        Assert.NotEmpty(path);
        grid.SetCellSolid(new Vector2I(30, 30), true); // a new map iteration: the agent repaths
        h.Run(1);
        walker.Agent.GetNextPathPosition();
        Assert.True(walker.Agent.IsPathPending);
        Assert.Equal(path, walker.Agent.GetCurrentNavigationPath().ToArray()); // still the old path meanwhile
        h.Run(1);
        walker.Agent.GetNextPathPosition();
        Assert.False(walker.Agent.IsPathPending);

        walker.Agent.PathMode = NavigationPathMode.Immediate;
        Assert.False(walker.Agent.IsPathAsync);
    }

    [Fact]
    public void SixtyFourAsyncAgentsAllocateNothing()
    {
        using var h = new NavigationHarness(physics: false);
        h.Navigation.DebugDrawEnabled = true;
        var grid = Level(h);
        h.Navigation.AsyncAgentsByDefault = true;
        var walkers = new Walker2D[64];
        for (var i = 0; i < walkers.Length; i++)
        {
            walkers[i] = h.AddWalker(i % 3 == 0 ? new Vector2(1120 + i * 9, 40 + i * 11) : new Vector2(8 + i % 8 * 16, 8 + i / 8 * 120), $"Walker{i}");
            walkers[i].Agent.TargetRepathDistance = 48f;
        }

        h.Run(1);
        var tick = 0;
        void Tick()
        {
            for (var i = 0; i < walkers.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                var centre = i % 3 == 0 ? new Vector2(1500, 400) : new Vector2(512, 512);
                walkers[i].Agent.TargetPosition = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (200f + i * 3f);
            }

            if (tick % 30 == 0)
                grid.SetCellSolid(new Vector2I(40, 20), tick / 30 % 2 == 0);
            h.Run(1);
            h.Tree.Root.DebugLines.Clear();
            tick++;
        }

        for (var i = 0; i < 600; i++)
            Tick();
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Tick();
        });
        Assert.Equal(0, allocated);
        Assert.Contains(walkers, w => w.Agent.CurrentPath.Count > 2);
    }
}
