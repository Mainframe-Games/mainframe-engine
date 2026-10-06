using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Scene;

using Color = System.Drawing.Color;

/// <summary>
/// Assimp import of the generated glTF test model into a node tree with meshes, materials and textures; the
/// import cache; model import settings; models instanced from scene files.
/// </summary>
/// <remarks>Needs the Assimp native library (Silk.NET.Assimp's runtime package).</remarks>
[Collection(nameof(SerialResources))]
public sealed class ModelImportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-model-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private const string ModelPath = "Content/Models/TestModel/" + TestModel.GltfFile;

    public ModelImportTests()
    {
        TestModel.Write(Path.Combine(_project, "Content", "Models", "TestModel"));
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
    public void AssimpIsAlsoLookedUpInThisRidsRuntimesFolder()
    {
        // Silk.NET alone misses runtimes/linux-x64/native on distros newer than its RID graph (Ubuntu 24.04).
        Assert.Contains(ModelImporter.AssimpLibraryNames(), name => Path.IsPathRooted(name) && File.Exists(name));
    }

    [Fact]
    public void GltfImportsAsANodeHierarchyWithMeshesAndMaterials()
    {
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        Assert.True(scene.IsImported);
        Assert.Equal(TestModel.ModelUid, scene.Uid);
        var root = scene.Instantiate<Node3D>();

        Assert.Equal("test_model", root.Name);
        Assert.Equal(ModelPath, root.SceneFilePath);
        Assert.Equal(["Base", "Empty"], root.Children.Select(c => c.Name));

        var baseNode = root.GetNode<MeshInstance3D>("Base");
        AssertNear(new Vector3(0, 0.5f, 0), baseNode.Position);
        var boxMesh = Assert.IsType<ArrayMesh>(baseNode.Mesh);
        Assert.Equal(1, boxMesh.SurfaceCount);
        var expectedBox = new BoxMesh().GetSurface(0);
        var surface = boxMesh.GetSurface(0);
        Assert.Equal(expectedBox.IndexCount, surface.IndexCount);
        Assert.Equal(expectedBox.VertexCount, surface.VertexCount);
        Assert.Equal(new Aabb(new Vector3(-0.5f), new Vector3(0.5f)), boxMesh.Bounds);
        // UVs keep glTF's top-left origin (Assimp's FlipUVs undoes its own flip): every (position, uv) pair survives.
        for (var i = 0; i < expectedBox.VertexCount; i++)
        {
            var match = Enumerable.Range(0, surface.VertexCount).Any(j =>
                Vector3.Distance(surface.Positions[j], expectedBox.Positions[i]) < 1e-5f &&
                Vector2.Distance(surface.UVs[j], expectedBox.UVs[i]) < 1e-5f &&
                Vector3.Distance(surface.Normals[j], expectedBox.Normals[i]) < 1e-4f);
            Assert.True(match, $"vertex {i} ({expectedBox.Positions[i]}, uv {expectedBox.UVs[i]}) not found");
        }

        var checker = Assert.IsType<StandardMaterial3D>(surface.Material);
        Assert.Equal("Checker", checker.ResourceName);
        Assert.NotNull(checker.AlbedoTexture);
        Assert.Equal(TestModel.TextureUid, checker.AlbedoTexture!.Uid);
        Assert.Equal(TextureFilter.Nearest, checker.AlbedoTexture.ImportSettings.Filter); // the PNG's own .meta
        Assert.Equal((8, 8), (checker.AlbedoTexture.Width, checker.AlbedoTexture.Height));
        Assert.Equal(AlphaMode.Opaque, checker.Transparency);

        var pillar = baseNode.GetNode<MeshInstance3D>("Pillar");
        Assert.Same(boxMesh, pillar.Mesh); // the same glTF mesh → one shared ArrayMesh
        AssertNear(new Vector3(0, 1, 0), pillar.Position);
        AssertNear(new Vector3(0.5f, 1, 0.5f), pillar.Scale);
        AssertNear(new Vector3(0, 45, 0), pillar.RotationDegrees, 0.01f);

        var banner = pillar.GetNode<MeshInstance3D>("Banner");
        var bannerMesh = Assert.IsType<ArrayMesh>(banner.Mesh);
        Assert.Equal(2, bannerMesh.SurfaceCount); // two primitives → two surfaces
        var red = (StandardMaterial3D)bannerMesh.GetSurfaceMaterial(0)!;
        var glass = (StandardMaterial3D)bannerMesh.GetSurfaceMaterial(1)!;
        Assert.Equal("Red", red.ResourceName);
        Assert.True(red.DoubleSided);
        Assert.Equal(AlphaMode.Opaque, red.Transparency);
        // Linear 0.8 / 0.05 → sRGB 231 / 63.
        AssertColor(Color.FromArgb(255, 231, 63, 63), red.AlbedoColor);
        Assert.Equal("Glass", glass.ResourceName);
        Assert.Equal(AlphaMode.Blend, glass.Transparency);
        Assert.Equal(128, glass.AlbedoColor.A);
        Assert.Equal(6, bannerMesh.GetSurface(0).IndexCount);
        Assert.Equal(6, bannerMesh.GetSurface(1).IndexCount);

        var empty = root.GetNode<Node3D>("Empty");
        Assert.IsNotType<MeshInstance3D>(empty);
        AssertNear(new Vector3(2, 0, 0), empty.Position);

        // Every node from the file is owned by the instance root (saving it writes them).
        Assert.All(new Node[] { baseNode, pillar, banner, empty }, n => Assert.Same(root, n.Owner));
        root.Free();
        scene.Release();
    }

    [Fact]
    public void InstancesShareResourcesAndTheImportIsCached()
    {
        var before = ModelImporter.ImportCount;
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        var a = scene.Instantiate();
        var b = scene.Instantiate();
        Assert.NotSame(a, b);
        Assert.NotSame(a.GetNode("Base"), b.GetNode("Base"));
        Assert.Same(a.GetNode<MeshInstance3D>("Base").Mesh, b.GetNode<MeshInstance3D>("Base").Mesh);
        a.Free();
        b.Free();

        // Released and loaded again: the import cache answers, Assimp does not run again.
        scene.Release();
        Assert.False(ResourceLoader.IsCached(TestModel.ModelUid));
        var again = ResourceLoader.Load<PackedScene>(TestModel.ModelUid);
        Assert.Equal(before + 1, ModelImporter.ImportCount);
        Assert.True(ModelImporter.CacheCount >= 1);
        again.Release();

        // Changing the settings re-imports.
        var meta = Path.Combine(_project, ModelPath + ".meta");
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("\"scale\": 1", "\"scale\": 2", StringComparison.Ordinal));
        var scaled = ResourceLoader.Load<PackedScene>(ModelPath);
        var root = scaled.Instantiate<Node3D>();
        Assert.Equal(new Vector3(2), root.Scale);
        Assert.Equal(before + 2, ModelImporter.ImportCount);
        root.Free();
        scaled.Release();
    }

    [Fact]
    public void ScenesInstanceModelsByUidAndSaveOnlyTheirOverrides()
    {
        var model = ResourceLoader.Load<PackedScene>(ModelPath);
        var level = new Node3D { Name = "Level" };
        var instance = model.Instantiate<Node3D>();
        instance.Position = new Vector3(4, 0, 0);
        instance.GetNode<MeshInstance3D>("Base").CastShadows = false;
        level.AddChild(instance);
        instance.Owner = level;
        model.Release();

        var json = SceneSaver.ToJson(level);
        level.Free();
        var entry = SceneJson.ChildrenOf(SceneJson.Parse(json), ".")[0];
        Assert.Equal(TestModel.ModelUid, entry.GetProperty("instance").GetString());
        Assert.Equal(ModelPath, entry.GetProperty("path").GetString());
        Assert.Equal(4, entry.GetProperty("props").GetProperty("Position")[0].GetSingle());
        Assert.False(entry.GetProperty("overrides").GetProperty("Base").GetProperty("CastShadows").GetBoolean());
        Assert.DoesNotContain("Positions", Encoding.UTF8.GetString(json), StringComparison.Ordinal); // no mesh data in the scene

        ResourceLoader.ClearCache();
        var reloaded = PackedScene.Parse(json).Instantiate();
        var copy = reloaded.GetNode<Node3D>("test_model");
        Assert.Equal(new Vector3(4, 0, 0), copy.Position);
        Assert.False(copy.GetNode<MeshInstance3D>("Base").CastShadows);
        Assert.NotNull(copy.GetNode<MeshInstance3D>("Base/Pillar/Banner").Mesh);
        reloaded.Free();
    }

    [Fact]
    public void ModelImportSettingsReadAndWriteMeta()
    {
        var meta = new AssetMeta
        {
            Uid = "mdl_aaaaaaaaaaaa",
            Importer = ModelImportSettings.ImporterName,
            Settings = new ModelImportSettings { Scale = 0.01f, GenerateNormals = false, ImportMaterials = false, OptimizeMeshes = true }.ToMetaSettings(),
        };
        var settings = ModelImportSettings.FromMeta(meta);
        Assert.Equal(0.01f, settings.Scale);
        Assert.False(settings.GenerateNormals);
        Assert.False(settings.ImportMaterials);
        Assert.True(settings.OptimizeMeshes);

        var bad = new AssetMeta
        {
            Uid = "mdl_bbbbbbbbbbbb",
            Settings = new Dictionary<string, JsonElement> { ["scale"] = JsonDocument.Parse("-3").RootElement },
        };
        Assert.Equal(1f, ModelImportSettings.FromMeta(bad).Scale);
        Assert.Equal(ModelImportSettings.Default, ModelImportSettings.FromMeta(null));
    }

    [Fact]
    public void MaterialsCanBeSkipped()
    {
        var root = ModelImporter.ImportNodes(ModelPath, new ModelImportSettings { ImportMaterials = false });
        Assert.Null(root.GetNode<MeshInstance3D>("Base").Mesh!.GetSurfaceMaterial(0));
        root.Free();
    }

    [Fact]
    public void MissingAndBrokenModelsReportErrors()
    {
        Assert.Throws<FileNotFoundException>(() => ResourceLoader.Load<PackedScene>("Content/Models/missing.gltf"));
        File.WriteAllText(Path.Combine(_project, "Content", "Models", "broken.gltf"), "{ this is not gltf");
        Assert.Throws<InvalidDataException>(() => ResourceLoader.Load<PackedScene>("Content/Models/broken.gltf"));
    }

    [Fact]
    public void TheCommittedTestModelMatchesTheGenerator()
    {
        // The tests (and the render tests) use the committed copy; it must be what the generator writes.
        // UPDATE_TEST_ASSETS=1 rewrites the committed copy from the generator.
        if (Environment.GetEnvironmentVariable("UPDATE_TEST_ASSETS") == "1")
            TestModel.Write(Path.Combine(TestPaths.RepositoryRoot(), "Tests", "Content", "Models", "TestModel"));
        var committed = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "TestModel");
        var generated = Path.Combine(_project, "Content", "Models", "TestModel");
        foreach (var file in new[] { TestModel.GltfFile, TestModel.BinFile, TestModel.GltfFile + ".meta", TestModel.TextureFile + ".meta" })
            Assert.True(File.ReadAllBytes(Path.Combine(committed, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(generated, file))),
                $"{file} differs from the generator: run the generator (TestModel.Write) into Tests/Content/Models/TestModel.");
        Assert.Equal(TestModel.Checker(8), Png.ReadRgba8(Path.Combine(committed, TestModel.TextureFile)).Pixels);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void AssertColor(Color expected, Color actual)
    {
        Assert.InRange(actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange(actual.G, expected.G - 1, expected.G + 1);
        Assert.InRange(actual.B, expected.B - 1, expected.B + 1);
        Assert.Equal(expected.A, actual.A);
    }
}
