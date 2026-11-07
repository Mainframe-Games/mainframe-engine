using System.Numerics;
using MainframeEngine.Tests.Physics;
using static MainframeEngine.Tests.Navigation.NavigationPolygonBuilderTests;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.9: baking a <see cref="NavigationRegion2D"/>'s polygon from the scene — shapes, tiles, obstacles, the agent radius.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class NavigationPolygonBakeTests
{
    private static float MeshArea(NavigationPolygon polygon) => polygon.Polygons.Sum(p => Area(polygon.Vertices, p));

    private static NavigationRegion2D AddRegion(NavigationHarness h, Rect2 rect, float radius = 0f, Vector2 position = default)
    {
        var polygon = new NavigationPolygon { BakingRect = rect, AgentRadius = radius };
        return h.Add(new NavigationRegion2D { Name = "Region", NavigationPolygon = polygon, Position = position });
    }

    private static bool Walkable(NavigationHarness h, Vector2 point) => Vector2.Distance(h.Map.GetClosestPoint(point), point) < 0.01f;

    [Fact]
    public void ARegionsStaticBodiesAreCarvedOutGrownByTheAgentRadius()
    {
        using var h = new NavigationHarness();
        var region = AddRegion(h, new Rect2(0, 0, 400, 300), radius: 10f);
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(200, 150) };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(100, 60) } });
        region.AddChild(wall);
        h.Run(1);
        Assert.True(region.Bake());
        AssertValid((region.NavigationPolygon!.Vertices, region.NavigationPolygon.Polygons));
        Assert.Equal(380f * 280f - 120f * 80f, MeshArea(region.NavigationPolygon));
        h.Run(1);
        Assert.False(Walkable(h, new Vector2(145, 150))); // within the radius of the wall
        Assert.True(Walkable(h, new Vector2(135, 150)));
        Assert.False(Walkable(h, new Vector2(5, 150)));   // within the radius of the edge
        Assert.True(region.LastBakeMilliseconds >= 0);

        var path = new NavigationPath2D();
        h.Map.QueryPath(new NavigationPathQuery2D(new Vector2(50, 150), new Vector2(350, 150)), path);
        Assert.Equal(GridPathStatus.Found, path.Status);
        Assert.Equal(4, path.Count); // round one pair of the grown wall's corners
    }

    [Fact]
    public void EveryShapeKindObstructsAndDisabledShapesDoNot()
    {
        using var h = new NavigationHarness();
        var region = AddRegion(h, new Rect2(0, 0, 600, 200));
        var body = new StaticBody2D { Name = "Body" };
        body.AddChild(new CollisionShape2D { Position = new Vector2(100, 100), Shape = new CircleShape2D { Radius = 30 } });
        body.AddChild(new CollisionShape2D { Position = new Vector2(200, 100), Shape = new CapsuleShape2D { Radius = 20, Height = 80 } });
        body.AddChild(new CollisionShape2D
        {
            Position = new Vector2(300, 100),
            Shape = new ConvexPolygonShape2D { Points = [new(-30, -30), new(30, -30), new(0, 30)] },
        });
        body.AddChild(new CollisionShape2D
        {
            Position = new Vector2(400, 100),
            Shape = new ConcavePolygonShape2D { Segments = [new(-20, -20), new(20, -20), new(20, -20), new(20, 20), new(20, 20), new(-20, 20), new(-20, 20), new(-20, -20)] },
        });
        body.AddChild(new CollisionShape2D { Position = new Vector2(500, 100), Shape = new RectangleShape2D { Size = new Vector2(40, 40) }, Disabled = true });
        region.AddChild(body);
        h.Run(1);
        Assert.True(region.Bake());
        h.Run(1);
        foreach (var x in new[] { 100f, 200f, 300f, 400f })
            Assert.False(Walkable(h, new Vector2(x, 100)), $"x = {x}");
        Assert.False(Walkable(h, new Vector2(200, 135)));  // the capsule's lower cap
        Assert.True(Walkable(h, new Vector2(500, 100)));   // disabled
        Assert.True(Walkable(h, new Vector2(150, 30)));
    }

    [Fact]
    public void TheCollisionMaskAndGeometryTypeFilterSources()
    {
        using var h = new NavigationHarness();
        var region = AddRegion(h, new Rect2(0, 0, 300, 100));
        var onTwo = new StaticBody2D { Name = "OnTwo", CollisionLayer = 2, Position = new Vector2(100, 50) };
        onTwo.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(20, 20) } });
        region.AddChild(onTwo);
        region.AddChild(new Polygon2D { Name = "Decor", Position = new Vector2(200, 50), Polygon = Box(-10, -10, 10, 10) });
        h.Run(1);

        var polygon = region.NavigationPolygon!;
        polygon.ParsedCollisionMask = 1;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f - 400f, MeshArea(polygon)); // only the Polygon2D

        polygon.ParsedCollisionMask = 3;
        polygon.ParsedGeometryType = NavigationParsedGeometryType.StaticColliders;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f - 400f, MeshArea(polygon)); // only the body

        polygon.ParsedGeometryType = NavigationParsedGeometryType.Both;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f - 800f, MeshArea(polygon));
    }

    [Fact]
    public void GroupModesParseGroupMembers()
    {
        using var h = new NavigationHarness();
        var region = AddRegion(h, new Rect2(0, 0, 300, 100));
        var walls = h.Add(new Node2D { Name = "Walls" }); // a sibling of the region: not parsed by default
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(100, 50) };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(20, 20) } });
        walls.AddChild(wall);
        walls.AddToGroup("navigation_polygon_source_geometry_group");
        h.Run(1);
        var polygon = region.NavigationPolygon!;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f, MeshArea(polygon));

        polygon.SourceGeometryMode = NavigationSourceGeometryMode.GroupsExplicit;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f, MeshArea(polygon)); // the group node itself has no geometry

        polygon.SourceGeometryMode = NavigationSourceGeometryMode.GroupsWithChildren;
        Assert.True(region.Bake());
        Assert.Equal(300f * 100f - 400f, MeshArea(polygon));
    }

    [Fact]
    public void TileCollisionAndCarvingObstaclesObstruct()
    {
        using var h = new NavigationHarness();
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(32, 16, new byte[32 * 16 * 4]) };
        source.CreateTile(new Vector2I(0, 0));
        source.CreateTile(new Vector2I(1, 0));
        var set = new TileSet();
        set.AddSource(source);
        set.AddPhysicsLayer();
        source.GetOrCreateTileData(new Vector2I(0, 0)).Collision.Add(TileCollisionPolygon.FullTile(set.TileSize));
        set.NotifyChanged();

        var region = AddRegion(h, new Rect2(0, 0, 320, 160));
        var layer = new TileMapLayer { Name = "Tiles", TileSet = set, CollisionEnabled = false };
        for (var y = 2; y < 8; y++)
            layer.SetCell(new Vector2I(5, y), 0, new Vector2I(0, 0)); // a wall of 6 tiles at x 80–96, y 32–128
        layer.SetCell(new Vector2I(10, 2), 0, new Vector2I(1, 0)); // a floor tile: no collision
        region.AddChild(layer);
        region.AddChild(new NavigationObstacle2D
        {
            Name = "Carver",
            Position = new Vector2(250, 80),
            AffectNavigationMesh = true,
            Vertices = Box(-10, -10, 10, 10),
        });
        region.AddChild(new NavigationObstacle2D { Name = "Ignored", Position = new Vector2(250, 20), Vertices = Box(-5, -5, 5, 5) });
        h.Run(1);
        Assert.True(region.Bake());
        Assert.Equal(320f * 160f - 16f * 96f - 400f, MeshArea(region.NavigationPolygon!));
    }

    [Fact]
    public void TransformedRegionsBakeInTheirLocalSpace()
    {
        using var h = new NavigationHarness();
        var region = AddRegion(h, new Rect2(0, 0, 200, 100), position: new Vector2(1000, 500));
        region.Rotation = MathF.PI / 2f;
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(100, 50) }; // local to the region
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(20, 20) } });
        region.AddChild(wall);
        h.Run(1);
        Assert.True(region.Bake());
        Assert.Equal(200f * 100f - 400f, MeshArea(region.NavigationPolygon!));
        h.Run(1);
        // Local (100, 50) is global (1000 - 50, 500 + 100).
        Assert.False(Walkable(h, new Vector2(950, 600)));
        Assert.True(Walkable(h, new Vector2(950, 560)));
    }

    [Fact]
    public void ABakeNeedsOutlinesOrARectangleAndGroupModesTheTree()
    {
        using var h = new NavigationHarness();
        var region = h.Add(new NavigationRegion2D { Name = "Region" });
        Assert.False(region.Bake()); // creates the polygon, which has neither
        Assert.NotNull(region.NavigationPolygon);
        Assert.Null(region.BakeData(out var error));
        Assert.Contains("no outlines", error);

        var detached = new NavigationRegion2D { NavigationPolygon = new NavigationPolygon { BakingRect = new Rect2(0, 0, 100, 100), AgentRadius = 0f } };
        var wall = new StaticBody2D { Position = new Vector2(50, 50) };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(10, 10) } });
        detached.AddChild(wall);
        Assert.Equal(10_000f - 100f, detached.BakeData(out error)!.Value.Polygons.Sum(p => Area(detached.BakeData(out _)!.Value.Vertices, p)));
        detached.NavigationPolygon.SourceGeometryMode = NavigationSourceGeometryMode.GroupsWithChildren;
        Assert.Null(detached.BakeData(out error));
        Assert.Contains("not in the tree", error);
        detached.Free();

        var finished = 0;
        region.BakeFinished += () => finished++;
        region.NavigationPolygon!.AddOutline(Box(0, 0, 50, 50));
        region.NavigationPolygon.AgentRadius = 0f;
        Assert.True(region.Bake());
        Assert.Equal(1, finished);
        Assert.Equal(2500f, MeshArea(region.NavigationPolygon));
    }
}
