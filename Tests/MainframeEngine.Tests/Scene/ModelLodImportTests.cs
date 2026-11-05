using System.Numerics;
using System.Text.Json;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// G6.4: authored levels of detail. <c>&lt;name&gt;_LODn</c> sibling nodes merge into one mesh with levels; the
/// <c>lodErrors</c> and <c>generateLods</c> model import settings.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class ModelLodImportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-model-lod-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private const string ModelPath = "Assets/Models/rock.gltf";

    public ModelLodImportTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "Assets", "Models"));
        File.WriteAllText(Path.Combine(_project, "Assets", "Models", "rock.gltf"), RockGltf());
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
    public void LodSiblingsImportAsOneMeshWithLevels()
    {
        var root = ModelImporter.ImportNodes(ModelPath);
        try
        {
            // Rock_LOD0..2 became "Rock"; "Pebble" (no suffix) and the lone "Stray_LOD1" (no LOD0) are left as they were.
            Assert.Equal(["Rock", "Pebble", "Stray_LOD1"], root.Children.Select(c => c.Name));
            var rock = root.GetNode<MeshInstance3D>("Rock");
            Assert.Equal(new Vector3(0, 0, 0), rock.Position);
            var surface = rock.Mesh!.GetSurface(0);
            Assert.Equal(6, surface.IndexCount);
            Assert.Equal(2, surface.Lods.Length);
            surface.Validate();

            // Defaults: 1 % then 2 % of LOD0's bounds radius (a 2 × 2 quad: √2).
            var radius = MathF.Sqrt(2f);
            Assert.Equal(0.01f * radius, surface.Lods[0].Error, 5);
            Assert.Equal(0.02f * radius, surface.Lods[1].Error, 5);
            // LOD1 sits 3 m to the right of LOD0 in the file and keeps that offset in LOD0's space (Unity's LOD groups).
            var lod1 = surface.Lods[0].Geometry!;
            Assert.Equal(3, lod1.VertexCount);
            Assert.Equal(2f, lod1.Positions.Min(p => p.X), 4);
            Assert.Equal(4f, lod1.Positions.Max(p => p.X), 4);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void MetaErrorsScaleTheLevels()
    {
        var root = ModelImporter.ImportNodes(ModelPath, new ModelImportSettings { LodErrors = [0.05f] });
        try
        {
            var lods = root.GetNode<MeshInstance3D>("Rock").Mesh!.GetSurface(0).Lods;
            var radius = MathF.Sqrt(2f);
            Assert.Equal(0.05f * radius, lods[0].Error, 5);
            Assert.Equal(0.02f * radius, lods[1].Error, 5); // missing entries continue the default series
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void LodSettingsReadAndWriteMeta()
    {
        var meta = new AssetMeta
        {
            Uid = "mdl_aaaaaaaaaaab",
            Importer = ModelImportSettings.ImporterName,
            Settings = new ModelImportSettings { LodErrors = [0.02f, 0.08f], GenerateLods = true }.ToMetaSettings(),
        };
        var settings = ModelImportSettings.FromMeta(meta);
        Assert.Equal([0.02f, 0.08f], settings.LodErrors);
        Assert.True(settings.GenerateLods);
        Assert.False(ModelImportSettings.Default.GenerateLods);
        Assert.Empty(ModelImportSettings.Default.LodErrors);

        var parsed = ModelImportSettings.FromMeta(new AssetMeta
        {
            Uid = "mdl_aaaaaaaaaaac",
            Settings = new Dictionary<string, JsonElement> { ["lodErrors"] = JsonDocument.Parse("[0.03]").RootElement },
        });
        Assert.Equal([0.03f], parsed.LodErrors);
    }

    [Fact]
    public void MergeSkipsGapsAndKeepsSurfaceChainsContiguous()
    {
        var root = new Node3D { Name = "Root" };
        root.AddChild(new MeshInstance3D { Name = "Crate_lod0", Mesh = TwoSurfaces() });
        root.AddChild(new MeshInstance3D { Name = "Crate_LOD2", Mesh = OneSurface(), Position = new Vector3(0, 5, 0) });
        root.AddChild(new MeshInstance3D { Name = "Crate_LOD3", Mesh = TwoSurfaces() });
        try
        {
            Assert.Equal(1, ModelLods.Merge(root, null, "test"));

            var crate = Assert.IsType<MeshInstance3D>(Assert.Single(root.Children));
            Assert.Equal("Crate", crate.Name);
            var mesh = crate.Mesh!;
            // LOD2 became level 1 (a gap is closed); it had one surface, so surface 1 has no level 2 either.
            Assert.Equal(2, mesh.GetSurface(0).Lods.Length);
            Assert.Empty(mesh.GetSurface(1).Lods);
            Assert.Equal(5f, mesh.GetSurface(0).Lods[0].Geometry!.Positions[0].Y, 4);
            Assert.True(mesh.GetSurface(0).Lods[1].Error > mesh.GetSurface(0).Lods[0].Error);
        }
        finally
        {
            root.Free();
        }
    }

    private static ArrayMesh OneSurface()
    {
        var mesh = new ArrayMesh();
        mesh.AddSurface([new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)], [], [], [0, 2, 1]);
        return mesh;
    }

    private static ArrayMesh TwoSurfaces()
    {
        var mesh = OneSurface();
        mesh.AddSurface([new(0, 1, 0), new(1, 1, 0), new(0, 1, 1)], [], [], [0, 2, 1]);
        return mesh;
    }

    // Nodes: Rock_LOD0 (a 2 × 2 quad at the origin), Rock_LOD1 (a triangle at x = 3), Rock_LOD2 (the same triangle),
    // Pebble (the quad) and Stray_LOD1 (the triangle), all under the scene root.
    private static string RockGltf()
    {
        float[] quad = [-1, 0, -1, 1, 0, -1, 1, 0, 1, -1, 0, 1];
        float[] triangle = [-1, 0, -1, 1, 0, -1, 0, 0, 1];
        ushort[] quadIndices = [0, 2, 1, 0, 3, 2];
        ushort[] triangleIndices = [0, 2, 1, 0]; // padded to 4 bytes
        var bytes = new List<byte>();
        foreach (var f in quad)
            bytes.AddRange(BitConverter.GetBytes(f));
        foreach (var f in triangle)
            bytes.AddRange(BitConverter.GetBytes(f));
        foreach (var i in quadIndices)
            bytes.AddRange(BitConverter.GetBytes(i));
        foreach (var i in triangleIndices)
            bytes.AddRange(BitConverter.GetBytes(i));
        var data = Convert.ToBase64String(bytes.ToArray());
        return $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0, 1, 2, 3, 4] } ],
          "nodes": [
            { "name": "Rock_LOD0", "mesh": 0 },
            { "name": "Rock_LOD1", "mesh": 1, "translation": [3, 0, 0] },
            { "name": "Rock_LOD2", "mesh": 1 },
            { "name": "Pebble", "mesh": 0, "translation": [0, 0, 4] },
            { "name": "Stray_LOD1", "mesh": 1, "translation": [0, 0, -4] }
          ],
          "meshes": [
            { "name": "quad", "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 2 } ] },
            { "name": "triangle", "primitives": [ { "attributes": { "POSITION": 1 }, "indices": 3 } ] }
          ],
          "buffers": [ { "byteLength": {{bytes.Count}}, "uri": "data:application/octet-stream;base64,{{data}}" } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 48 },
            { "buffer": 0, "byteOffset": 48, "byteLength": 36 },
            { "buffer": 0, "byteOffset": 84, "byteLength": 12 },
            { "buffer": 0, "byteOffset": 96, "byteLength": 6 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 4, "type": "VEC3", "min": [-1, 0, -1], "max": [1, 0, 1] },
            { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3", "min": [-1, 0, -1], "max": [1, 0, 1] },
            { "bufferView": 2, "componentType": 5123, "count": 6, "type": "SCALAR" },
            { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" }
          ]
        }
        """;
    }
}
