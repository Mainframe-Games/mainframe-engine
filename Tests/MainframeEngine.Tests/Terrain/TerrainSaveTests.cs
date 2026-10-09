using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Terrain;

/// <summary>Saving a scene writes its terrain's layers next to it; loading the scene reads them back.</summary>
[Collection(nameof(SerialResources))] // process-wide asset database and loader cache
public sealed class TerrainSaveTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-terrain-save", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public TerrainSaveTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Scenes"));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ARuntimeTerrainIsNotSavedWithTheScene()
    {
        // A [Tool] node's generated terrain (no owner, like the Forest's valley in the editor) is not part of the scene:
        // saving writes neither the node nor its layers (ADR 0169).
        var root = new Node3D { Name = "World" };
        var generator = new Node3D { Name = "Valley" };
        root.AddChild(generator);
        generator.Owner = root;
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        generator.AddChild(new Terrain3D { Name = "Terrain", Data = data });

        SceneSaver.Save(root, "Content/Scenes/world.mscene");

        Assert.False(Directory.Exists(Path.Combine(_project, "Content", "Scenes", "world_terrain")));
        Assert.False(data.IsExternal);
        Assert.DoesNotContain("Terrain3D", File.ReadAllText(Path.Combine(_project, "Content", "Scenes", "world.mscene")), StringComparison.Ordinal);
        root.Free();
    }

    [Fact]
    public void SavingTheSceneWritesTheLayersBesideItAndLoadingReadsThemBack()
    {
        var root = new Node3D { Name = "World" };
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        data.PaintSurface(new System.Numerics.Vector2(10, 10), 3, 4);
        var terrain = new Terrain3D { Name = "Terrain", Data = data, LodBias = 2f };
        root.AddChild(terrain);
        terrain.Owner = root;

        SceneSaver.Save(root, "Content/Scenes/world.mscene");

        var folder = Path.Combine(_project, "Content", "Scenes", "world_terrain");
        Assert.True(File.Exists(Path.Combine(folder, TerrainData.ResourceFileName)));
        Assert.True(File.Exists(Path.Combine(folder, TerrainData.HeightmapFile)));
        Assert.True(File.Exists(Path.Combine(folder, TerrainData.SplatFile(0))));
        Assert.True(File.Exists(Path.Combine(folder, TerrainData.SplatFile(1)))); // layer 4 is in the second map
        Assert.False(File.Exists(Path.Combine(folder, TerrainData.WaterFile)));    // all default
        Assert.Equal("Content/Scenes/world_terrain/terrain.mres", data.ResourcePath);
        var heights = data.Heights.ToArray();
        root.Free();
        ResourceLoader.ClearCache();

        var scene = ResourceLoader.Load<PackedScene>("Content/Scenes/world.mscene");
        var loaded = (Node3D)scene.Instantiate();
        var loadedTerrain = (Terrain3D)loaded.GetNode("Terrain")!;
        Assert.Equal(2f, loadedTerrain.LodBias);
        Assert.NotNull(loadedTerrain.Data);
        Assert.Equal(heights, loadedTerrain.Data!.Heights.ToArray());
        Assert.Equal(4, loadedTerrain.Data.GetSurface(10, 10));
        loaded.Free();
        scene.Release();
    }

    [Fact]
    public void ResavingWritesOnlyChangedLayersIntoTheSameFolder()
    {
        var root = new Node3D { Name = "World" };
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        root.AddChild(terrain);
        terrain.Owner = root;
        SceneSaver.Save(root, "Content/Scenes/world.mscene");
        var heightmap = Path.Combine(_project, "Content", "Scenes", "world_terrain", TerrainData.HeightmapFile);
        File.WriteAllBytes(heightmap, [9, 9]); // a rewrite would replace this

        data.SetWaterDepth(new Rect2I(3, 3, 1, 1), [0.5f]);
        SceneSaver.Save(root, "Content/Scenes/world.mscene");
        Assert.Equal([9, 9], File.ReadAllBytes(heightmap));
        Assert.True(File.Exists(Path.Combine(_project, "Content", "Scenes", "world_terrain", TerrainData.WaterFile)));
        root.Free();
    }
}
