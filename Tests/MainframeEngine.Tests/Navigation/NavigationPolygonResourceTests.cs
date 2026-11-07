using System.Numerics;
using static MainframeEngine.Tests.Navigation.NavigationPolygonBuilderTests;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.9: <see cref="NavigationPolygon"/> as a <c>.mfres</c> and inline in a scene.</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class NavigationPolygonResourceTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-navpoly-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public NavigationPolygonResourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Levels"));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previous;
        Directory.Delete(_project, recursive: true);
    }

    private static NavigationPolygon Sample()
    {
        var polygon = new NavigationPolygon { AgentRadius = 4f, BakingRect = new Rect2(0, 0, 100, 50), ParsedCollisionMask = 5 };
        polygon.AddOutline(Box(0, 0, 100, 50));
        polygon.AddOutline(Box(40, 10, 60, 40));
        polygon.MakePolygonsFromOutlines();
        return polygon;
    }

    private static void AssertSame(NavigationPolygon expected, NavigationPolygon actual)
    {
        Assert.Equal(expected.Vertices, actual.Vertices);
        Assert.Equal(expected.Polygons.Length, actual.Polygons.Length);
        for (var i = 0; i < expected.Polygons.Length; i++)
            Assert.Equal(expected.Polygons[i], actual.Polygons[i]);
        Assert.Equal(expected.Outlines.Length, actual.Outlines.Length);
        for (var i = 0; i < expected.Outlines.Length; i++)
            Assert.Equal(expected.Outlines[i], actual.Outlines[i]);
        Assert.Equal(expected.AgentRadius, actual.AgentRadius);
        Assert.Equal(expected.BakingRect, actual.BakingRect);
        Assert.Equal(expected.ParsedCollisionMask, actual.ParsedCollisionMask);
    }

    [Fact]
    public void AResourceFileLoadsBack()
    {
        var polygon = Sample();
        ResourceSaver.Save(polygon, "Assets/Levels/room.mfres");
        ResourceLoader.ClearCache();
        var loaded = ResourceLoader.Load<NavigationPolygon>("Assets/Levels/room.mfres");
        AssertSame(polygon, loaded);
        loaded.Release();
    }

    [Fact]
    public void AnInlinePolygonLoadsWithItsScene()
    {
        var root = new Node2D { Name = "Level" };
        var region = new NavigationRegion2D { Name = "Region", NavigationPolygon = Sample() };
        root.AddChild(region);
        region.Owner = root;
        root.AddChild(new NavigationLink2D { Name = "Link", StartPosition = new Vector2(1, 2), EndPosition = new Vector2(3, 4), Bidirectional = false });
        root.GetNode<NavigationLink2D>("Link").Owner = root;
        SceneSaver.Save(root, "Assets/Levels/level.mfscene");
        ResourceLoader.ClearCache();
        var scene = ResourceLoader.Load<PackedScene>("Assets/Levels/level.mfscene");
        var copy = (Node2D)scene.Instantiate();
        AssertSame(region.NavigationPolygon!, copy.GetNode<NavigationRegion2D>("Region").NavigationPolygon!);
        var link = copy.GetNode<NavigationLink2D>("Link");
        Assert.Equal(new Vector2(3, 4), link.EndPosition);
        Assert.False(link.Bidirectional);
        copy.Free();
        root.Free();
        scene.Release();
    }

    [Fact]
    public void ABaked3DNavigationMeshLoadsBackAndQueries()
    {
        // G3.12: a NavigationMesh with its Detour tile as a .mfres; the loaded copy answers the same path.
        var region = new NavigationRegion3D { Name = "Level", NavigationMesh = new NavigationMesh { CellSize = 0.2f, CellHeight = 0.1f, AgentRadius = 0.3f } };
        var floor = new StaticBody3D { Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(8, 1, 8) } });
        region.AddChild(floor);
        Assert.True(region.Bake()); // the default source mode bakes outside the tree
        ResourceSaver.Save(region.NavigationMesh!, "Assets/Levels/level.mfres");
        ResourceLoader.ClearCache();
        var loaded = ResourceLoader.Load<NavigationMesh>("Assets/Levels/level.mfres");
        Assert.Equal(region.NavigationMesh!.DetourData, loaded.DetourData);
        Assert.Equal(region.NavigationMesh.Vertices, loaded.Vertices);
        Assert.Equal(0.3f, loaded.AgentRadius);
        var runtime = new DetourMesh3D();
        Assert.True(runtime.Load(loaded, Transform3D.Identity, out var error), error);
        var path = new NavigationPath3D();
        Assert.Equal(GridPathStatus.Found, runtime.FindPath(new Vector3(-3, 0, -3), new Vector3(3, 0, 3), true, NavigationPathPostprocessing.CorridorFunnel, path, region));
        Assert.Equal(2, path.Count);
        region.Free();
        loaded.Release();
    }
}
