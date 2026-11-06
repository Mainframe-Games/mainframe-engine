using System.Numerics;
using static MainframeEngine.Tests.Navigation.NavigationPolygonBuilderTests;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.9: navigation meshes on the map — regions, shared and near edges, links, costs, layers, the funnel.</summary>
public sealed class NavigationMeshQueryTests
{
    internal static NavigationPolygon Mesh(params Vector2[][] outlines)
    {
        var polygon = new NavigationPolygon();
        foreach (var outline in outlines)
            polygon.AddOutline(outline);
        polygon.MakePolygonsFromOutlines();
        return polygon;
    }

    internal static NavigationRegion2D AddRegion(NavigationHarness h, NavigationPolygon polygon, Vector2 position = default, string name = "Region") =>
        h.Add(new NavigationRegion2D { Name = name, NavigationPolygon = polygon, Position = position });

    private static NavigationPath2D Query(NavigationHarness h, Vector2 start, Vector2 target,
        NavigationPathPostprocessing post = NavigationPathPostprocessing.CorridorFunnel, uint layers = 1, bool partial = true)
    {
        var path = new NavigationPath2D();
        h.Map.QueryPath(new NavigationPathQuery2D(start, target, layers, post, partial), path);
        return path;
    }

    private static void AssertPoints(NavigationPath2D path, params Vector2[] expected)
    {
        Assert.Equal(expected.Length, path.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Vector2.Distance(expected[i], path.Points[i]) < 0.01f, $"point {i}: {path.Points[i]}, expected {expected[i]}");
    }

    // A 300 × 200 room with a wall hanging from the top (x 100–200, y 0–150): the way round is below it.
    private static NavigationPolygon Room() => Mesh(Box(0, 0, 300, 200), Box(100, 0, 200, 150));

    [Fact]
    public void RegionsJoinTheMapAtTheNextSync()
    {
        using var h = new NavigationHarness(physics: false);
        var region = AddRegion(h, Mesh(Box(0, 0, 100, 100)));
        Assert.Same(h.Map, region.Map);
        Assert.Same(region, Assert.Single(h.Map.PolygonRegions));
        Assert.Equal(0, h.Map.PolygonCount); // built at the sync
        var iteration = h.Map.IterationId;
        h.Run(1);
        Assert.Equal(1, h.Map.PolygonCount);
        Assert.True(h.Map.IterationId > iteration);

        iteration = h.Map.IterationId;
        h.Run(2);
        Assert.Equal(iteration, h.Map.IterationId);
        region.QueueFree();
        h.Run(2);
        Assert.Equal(0, h.Map.PolygonCount);
        Assert.Empty(h.Map.PolygonRegions);
    }

    [Fact]
    public void AnOpenRoomIsAStraightLine()
    {
        using var h = new NavigationHarness(physics: false);
        var region = AddRegion(h, Mesh(Box(0, 0, 300, 200)));
        h.Run(1);
        var path = Query(h, new Vector2(10, 10), new Vector2(290, 190));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, new(10, 10), new(290, 190));
        Assert.Null(path.Region); // not a grid
        Assert.All(path.Owners.ToArray(), o => Assert.Same(region, o));
    }

    [Fact]
    public void TheFunnelTurnsAtTheWallsCorners()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Room());
        h.Run(1);
        var path = Query(h, new Vector2(50, 50), new Vector2(250, 50));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, new(50, 50), new(100, 150), new(200, 150), new(250, 50));

        var back = Query(h, new Vector2(250, 50), new Vector2(50, 50));
        AssertPoints(back, new(250, 50), new(200, 150), new(100, 150), new(50, 50));
    }

    [Fact]
    public void EdgeCenteredAndNoneStayOnTheMesh()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Room());
        h.Run(1);
        foreach (var post in new[] { NavigationPathPostprocessing.EdgeCentered, NavigationPathPostprocessing.None })
        {
            var path = Query(h, new Vector2(50, 50), new Vector2(250, 50), post);
            Assert.Equal(GridPathStatus.Found, path.Status);
            Assert.Equal(new Vector2(50, 50), path.Points[0]);
            Assert.Equal(new Vector2(250, 50), path.Points[^1]);
            Assert.True(path.Count >= 3);
            foreach (var p in path.Points)
                Assert.Equal(p, h.Map.GetClosestPoint(p)); // every point walkable
            Assert.Contains(path.Points.ToArray(), p => p.Y >= 150f); // went under the wall
        }
    }

    [Fact]
    public void RegionsSharingAnEdgeConnect()
    {
        using var h = new NavigationHarness(physics: false);
        var left = AddRegion(h, Mesh(Box(0, 0, 100, 100)), name: "Left");
        var right = AddRegion(h, Mesh(Box(0, 0, 100, 100)), new Vector2(100, 0), "Right");
        h.Run(1);
        var path = Query(h, new Vector2(10, 50), new Vector2(190, 50));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, new(10, 50), new(190, 50));
        Assert.Same(left, path.Owners[0]);
        Assert.Same(right, path.Owners[^1]);
        Assert.Empty(h.Map.Mesh.EdgeConnections); // exact shared edge, no margin connection needed
    }

    [Fact]
    public void NearEdgesConnectWithinTheMargin()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 100)), name: "Left");
        var right = AddRegion(h, Mesh(Box(0, 0, 100, 100)), new Vector2(100.75f, 20f), "Right");
        h.Run(1);
        Assert.Single(h.Map.Mesh.EdgeConnections);
        var path = Query(h, new Vector2(10, 50), new Vector2(190, 50));
        Assert.Equal(GridPathStatus.Found, path.Status);

        right.Position = new Vector2(105f, 20f); // beyond the 1 px margin
        h.Run(1);
        Assert.Empty(h.Map.Mesh.EdgeConnections);
        path = Query(h, new Vector2(10, 50), new Vector2(190, 50));
        Assert.Equal(GridPathStatus.Partial, path.Status);
        Assert.True(path.Points[^1].X <= 100f);

        h.Map.EdgeConnectionMargin = 6f;
        h.Run(1);
        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);

        right.UseEdgeConnections = false;
        h.Run(1);
        Assert.Equal(GridPathStatus.Partial, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);
    }

    [Fact]
    public void LinksJoinIslandsOneWayOrBoth()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 100), Box(200, 0, 300, 100)));
        var link = h.Add(new NavigationLink2D { Name = "Jump", Position = new Vector2(90, 50), StartPosition = Vector2.Zero, EndPosition = new Vector2(120, 0) });
        h.Run(1);
        Assert.True(link.IsConnected());
        var path = Query(h, new Vector2(10, 50), new Vector2(290, 50));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, new(10, 50), new(90, 50), new(210, 50), new(290, 50));
        Assert.Same(link, path.Owners[1]);
        Assert.Same(link, path.Owners[2]);

        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(290, 50), new Vector2(10, 50)).Status);
        link.Bidirectional = false;
        h.Run(1);
        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(10, 50), new Vector2(290, 50)).Status);
        Assert.Equal(GridPathStatus.Partial, Query(h, new Vector2(290, 50), new Vector2(10, 50)).Status);

        link.Enabled = false;
        h.Run(1);
        Assert.False(link.IsConnected());
        Assert.Equal(GridPathStatus.Partial, Query(h, new Vector2(10, 50), new Vector2(290, 50)).Status);

        link.Enabled = true;
        link.EndPosition = new Vector2(70, 0); // 40 px short of the island: beyond the 4 px radius
        h.Run(1);
        Assert.False(link.IsConnected());
    }

    [Fact]
    public void TheFunnelRunsOnBothSidesOfALink()
    {
        using var h = new NavigationHarness(physics: false);
        // Two rooms with walls, joined by a link from the first room's right edge to the second's left edge.
        AddRegion(h, Mesh(Box(0, 0, 300, 200), Box(100, 0, 200, 150)));
        AddRegion(h, Mesh(Box(0, 0, 300, 200), Box(100, 0, 200, 150)), new Vector2(400, 0), "Second");
        h.Add(new NavigationLink2D { Name = "Link", StartPosition = new Vector2(298, 50), EndPosition = new Vector2(402, 50) });
        h.Run(1);
        var path = Query(h, new Vector2(50, 50), new Vector2(650, 50));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, new(50, 50), new(100, 150), new(200, 150), new(298, 50), new(402, 50), new(500, 150), new(600, 150), new(650, 50));
    }

    [Fact]
    public void TravelAndEnterCostsChooseTheRoute()
    {
        using var h = new NavigationHarness(physics: false);
        // Start and goal rooms joined by a short middle corridor (y 0–100) and a long detour (y 200–300 via side columns).
        AddRegion(h, Mesh(Box(0, 0, 100, 300)), name: "West");
        AddRegion(h, Mesh(Box(300, 0, 400, 300)), name: "East");
        var middle = AddRegion(h, Mesh(Box(100, 0, 300, 100)), name: "Middle");
        AddRegion(h, Mesh(Box(100, 200, 300, 300)), name: "South");
        h.Run(1);
        var path = Query(h, new Vector2(50, 50), new Vector2(350, 50));
        Assert.DoesNotContain(path.Points.ToArray(), p => p.Y > 150f);

        middle.TravelCost = 10f;
        h.Run(1);
        path = Query(h, new Vector2(50, 50), new Vector2(350, 50));
        Assert.Contains(path.Points.ToArray(), p => p.Y >= 200f);

        middle.TravelCost = 1f;
        middle.EnterCost = 1000f;
        h.Run(1);
        path = Query(h, new Vector2(50, 50), new Vector2(350, 50));
        Assert.Contains(path.Points.ToArray(), p => p.Y >= 200f);
    }

    [Fact]
    public void LayersFilterRegionsAndLinks()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 100)), name: "A");
        var b = AddRegion(h, Mesh(Box(0, 0, 100, 100)), new Vector2(100, 0), "B");
        b.NavigationLayers = 2;
        h.Run(1);
        Assert.Equal(GridPathStatus.Partial, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);
        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(10, 50), new Vector2(190, 50), layers: 3).Status);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(h, new Vector2(10, 50), new Vector2(190, 50), layers: 4).Status);
    }

    [Fact]
    public void OffMeshPointsSnapAndUnreachableTargetsArePartial()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 100), Box(200, 0, 300, 100)));
        h.Run(1);
        var path = Query(h, new Vector2(-50, 50), new Vector2(50, 150));
        Assert.Equal(GridPathStatus.Partial, path.Status); // the target is off the mesh
        AssertPoints(path, new(0, 50), new(50, 100));

        path = Query(h, new Vector2(10, 50), new Vector2(250, 50));
        Assert.Equal(GridPathStatus.Partial, path.Status); // another island
        Assert.Equal(new Vector2(100, 50), path.Points[^1]);
        Assert.Equal(GridPathStatus.NotFound, Query(h, new Vector2(10, 50), new Vector2(250, 50), partial: false).Status);
        Assert.Equal(GridPathStatus.NotFound, Query(h, new Vector2(10, 50), new Vector2(50, 150), partial: false).Status);

        Assert.Equal(new Vector2(50, 50), h.Map.GetClosestPoint(new Vector2(50, 50)));
        Assert.Equal(new Vector2(100, 40), h.Map.GetClosestPoint(new Vector2(140, 40)));
    }

    [Fact]
    public void RotatedAndScaledRegionsPlaceTheirPolygons()
    {
        using var h = new NavigationHarness(physics: false);
        var region = AddRegion(h, Room());
        region.Position = new Vector2(500, 100);
        region.Rotation = MathF.PI / 2f; // local +x → global +y
        region.Scale = new Vector2(2f, 2f);
        h.Run(1);
        static Vector2 G(Vector2 local) => new(500 - local.Y * 2f, 100 + local.X * 2f);
        var path = Query(h, G(new Vector2(50, 50)), G(new Vector2(250, 50)));
        Assert.Equal(GridPathStatus.Found, path.Status);
        AssertPoints(path, G(new(50, 50)), G(new(100, 150)), G(new(200, 150)), G(new(250, 50)));
    }

    [Fact]
    public void ChangesRebuildTheMeshAtTheNextSync()
    {
        using var h = new NavigationHarness(physics: false);
        var region = AddRegion(h, Mesh(Box(0, 0, 100, 100)));
        h.Run(1);
        var iteration = h.Map.IterationId;
        region.NavigationPolygon!.SetMesh([new(0, 0), new(200, 0), new(200, 100), new(0, 100)], [[0, 1, 2, 3]]);
        Assert.Equal(iteration, h.Map.IterationId);
        h.Run(1);
        Assert.True(h.Map.IterationId > iteration);
        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);

        region.Enabled = false;
        h.Run(1);
        Assert.Equal(0, h.Map.PolygonCount);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);
    }

    [Fact]
    public void GridsWinWhereTheyContainTheStart()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 400, 400)));
        var grid = h.AddGrid(4, 4);
        grid.Source = NavigationGridSource.Manual;
        h.Run(1);
        var onGrid = Query(h, new Vector2(16, 16), new Vector2(100, 16));
        Assert.Same(grid, onGrid.Region);
        Assert.Same(grid, onGrid.Owners[0]);
        var onMesh = Query(h, new Vector2(300, 300), new Vector2(350, 350));
        Assert.Null(onMesh.Region);
        Assert.IsType<NavigationRegion2D>(onMesh.Owners[0]);
    }

    [Fact]
    public void BadPolygonsAreSkippedAndWindingIsFixed()
    {
        using var h = new NavigationHarness(physics: false);
        var polygon = new NavigationPolygon();
        polygon.SetMesh([new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(200, 0), new(200, 100)],
            [[3, 2, 1, 0], [1, 4, 5, 2], [0, 1, 99], [0, 1]]);
        AddRegion(h, polygon);
        h.Run(1);
        Assert.Equal(2, h.Map.PolygonCount);
        Assert.Equal(GridPathStatus.Found, Query(h, new Vector2(10, 50), new Vector2(190, 50)).Status);
    }

    [Fact]
    public void QueriesAreDeterministicAndAllocationFree()
    {
        using var h = new NavigationHarness(physics: false);
        var holes = new List<Vector2[]> { Box(0, 0, 1000, 1000) };
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 6; x++)
                holes.Add(Box(60 + x * 150 + y * 7, 60 + y * 150, 120 + x * 150, 140 + y * 150 - x * 5));
        AddRegion(h, Mesh([.. holes]));
        h.Run(1);
        var a = Query(h, new Vector2(20, 20), new Vector2(980, 970));
        var b = Query(h, new Vector2(20, 20), new Vector2(980, 970));
        Assert.Equal(GridPathStatus.Found, a.Status);
        Assert.Equal(a.Points.ToArray(), b.Points.ToArray());

        var path = new NavigationPath2D();
        var query = new NavigationPathQuery2D(new Vector2(20, 980), new Vector2(970, 30));
        for (var i = 0; i < 4; i++)
            h.Map.QueryPath(query, path);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            h.Map.QueryPath(query, path);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(GridPathStatus.Found, path.Status);
    }
}
