using System.Text.Json;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// G6.1: PBR import (<c>"shading": "pbr"</c>): glTF's metallic and roughness factors, its metallic-roughness and occlusion
/// maps into one ORM texture, roughness from shininess; Blinn-Phong stays the default.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class ModelPbrImportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-model-pbr-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public ModelPbrImportTests()
    {
        var models = Path.Combine(_project, "Assets", "Models");
        Directory.CreateDirectory(models);
        // 2 × 2 maps: metallic-roughness (G = roughness 128, B = metallic 255, R = 7 junk), occlusion (R = 64), and a
        // packed ORM (R = 32, G = 96, B = 160).
        Png.WriteRgba8(Path.Combine(models, "mr.png"), 2, 2, Fill(7, 128, 255));
        Png.WriteRgba8(Path.Combine(models, "ao.png"), 2, 2, Fill(64, 0, 0));
        Png.WriteRgba8(Path.Combine(models, "orm.png"), 2, 2, Fill(32, 96, 160));
        File.WriteAllText(Path.Combine(models, "separate.gltf"), Gltf("mr.png", "ao.png", metallic: 0.5f, roughness: 0.25f));
        File.WriteAllText(Path.Combine(models, "packed.gltf"), Gltf("orm.png", "orm.png", metallic: 1f, roughness: 1f));
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

    private static readonly ModelImportSettings Pbr = new() { Shading = ModelShading.Pbr };

    private static StandardMaterial3D MaterialOf(Node root) =>
        Assert.IsType<StandardMaterial3D>(root.GetNode<MeshInstance3D>("Quad").Mesh!.GetSurfaceMaterial(0));

    [Fact]
    public void SeparateMapsPackIntoOneOrmTexture()
    {
        var root = ModelImporter.ImportNodes("Assets/Models/separate.gltf", Pbr);
        try
        {
            var material = MaterialOf(root);
            Assert.Equal(ShadingMode.Pbr, material.ShadingMode);
            Assert.Equal(0.5f, material.Metallic);
            Assert.Equal(0.25f, material.Roughness);
            var (rgba, width, height) = material.OrmTexture!.DecodePixels();
            Assert.Equal((2, 2), (width, height));
            Assert.Equal([64, 128, 255, 255], rgba[..4]); // occlusion's R, then the metallic-roughness map's G and B
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void APackedOrmImageBindsAsItIs()
    {
        var root = ModelImporter.ImportNodes("Assets/Models/packed.gltf", Pbr);
        try
        {
            var material = MaterialOf(root);
            Assert.Equal(1f, material.Metallic);
            var orm = material.OrmTexture!;
            Assert.Equal([32, 96, 160, 255], orm.DecodePixels().Rgba[..4]);
            Assert.EndsWith("orm.png", orm.ResourcePath ?? "", StringComparison.Ordinal);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void PbrIsTheDefault()
    {
        var root = ModelImporter.ImportNodes("Assets/Models/separate.gltf");
        try
        {
            Assert.Equal(ShadingMode.Pbr, MaterialOf(root).ShadingMode);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void BlinnPhongStillImportsWhenAsked()
    {
        var root = ModelImporter.ImportNodes("Assets/Models/separate.gltf", ModelImportSettings.LegacyDefault);
        try
        {
            var material = MaterialOf(root);
            Assert.Equal(ShadingMode.BlinnPhong, material.ShadingMode);
            Assert.Null(material.OrmTexture);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void ShadingReadsAndWritesMeta()
    {
        var meta = new AssetMeta { Uid = "mdl_aaaaaaaaaaad", Importer = ModelImportSettings.ImporterName, Settings = Pbr.ToMetaSettings() };
        Assert.Equal("pbr", meta.Settings!["shading"].GetString());
        Assert.Equal(ModelShading.Pbr, ModelImportSettings.FromMeta(meta).Shading);
        var blinn = new AssetMeta
        {
            Format = AssetMeta.CurrentFormat,
            Uid = "mdl_aaaaaaaaaaae",
            Settings = new Dictionary<string, JsonElement> { ["shading"] = JsonDocument.Parse("\"blinnPhong\"").RootElement },
        };
        Assert.Equal(ModelShading.BlinnPhong, ModelImportSettings.FromMeta(blinn).Shading);
        var bad = new AssetMeta
        {
            Format = AssetMeta.CurrentFormat,
            Uid = "mdl_aaaaaaaaaaaf",
            Settings = new Dictionary<string, JsonElement> { ["shading"] = JsonDocument.Parse("\"toon\"").RootElement },
        };
        Assert.Equal(ModelShading.Pbr, ModelImportSettings.FromMeta(bad).Shading); // warned, default kept
    }

    [Fact]
    public void SidecarsWrittenBeforeFormatsKeepBlinnPhong()
    {
        // ADR 0255: a sidecar without "format" was written when Blinn-Phong was the default.
        Assert.Equal(ModelShading.BlinnPhong, ModelImportSettings.FromMeta(new AssetMeta { Uid = "mdl_aaaaaaaaaab0" }).Shading);
        var legacyWithSettings = new AssetMeta
        {
            Uid = "mdl_aaaaaaaaaab1",
            Settings = new Dictionary<string, JsonElement> { ["scale"] = JsonDocument.Parse("1").RootElement },
        };
        Assert.Equal(ModelShading.BlinnPhong, ModelImportSettings.FromMeta(legacyWithSettings).Shading);
        var legacyPbr = new AssetMeta
        {
            Uid = "mdl_aaaaaaaaaab2",
            Settings = new Dictionary<string, JsonElement> { ["shading"] = JsonDocument.Parse("\"pbr\"").RootElement },
        };
        Assert.Equal(ModelShading.Pbr, ModelImportSettings.FromMeta(legacyPbr).Shading);

        // New sidecars and models without one: today's default.
        Assert.Equal(ModelShading.Pbr, ModelImportSettings.FromMeta(new AssetMeta { Format = AssetMeta.CurrentFormat, Uid = "mdl_aaaaaaaaaab3" }).Shading);
        Assert.Equal(ModelShading.Pbr, ModelImportSettings.FromMeta(null).Shading);
    }

    [Fact]
    public void NewSidecarsAreWrittenAtTheCurrentFormat()
    {
        var root = Directory.CreateTempSubdirectory("mf-meta-format-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            File.WriteAllText(Path.Combine(root, "Assets", "a.gltf"), "{}");
            File.WriteAllText(Path.Combine(root, "Assets", "old.gltf"), "{}");
            File.WriteAllText(Path.Combine(root, "Assets", "old.gltf.mfmeta"), "{\n  \"uid\": \"mdl_aaaaaaaaaac0\"\n}");
            var database = new AssetDatabase(root);
            Assert.Equal(AssetMeta.CurrentFormat, database.ReadOrCreateMeta("Assets/a.gltf", create: true)!.Format);
            Assert.Null(database.ReadOrCreateMeta("Assets/old.gltf", create: true)!.Format);
            database.WriteMeta("Assets/old.gltf", database.ReadOrCreateMeta("Assets/old.gltf", create: false)!);
            Assert.Null(database.ReadOrCreateMeta("Assets/old.gltf", create: false)!.Format); // replaced, not upgraded
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShininessMapsToRoughness()
    {
        Assert.Equal(1f, ModelImporter.RoughnessFromShininessForTests(0f));
        Assert.Equal(MathF.Sqrt(2f / 34f), ModelImporter.RoughnessFromShininessForTests(32f), 5);
        Assert.True(ModelImporter.RoughnessFromShininessForTests(1024f) < 0.05f);
    }

    private static byte[] Fill(byte r, byte g, byte b)
    {
        var rgba = new byte[16];
        for (var i = 0; i < 16; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        return rgba;
    }

    // One quad node with one material whose metallicRoughnessTexture and occlusionTexture are the given images.
    private static string Gltf(string metallicRoughness, string occlusion, float metallic, float roughness)
    {
        var bytes = new List<byte>();
        foreach (var f in new float[] { -1, 0, -1, 1, 0, -1, 1, 0, 1, -1, 0, 1 })
            bytes.AddRange(BitConverter.GetBytes(f));
        foreach (var f in new float[] { 0, 0, 1, 0, 1, 1, 0, 1 })
            bytes.AddRange(BitConverter.GetBytes(f));
        foreach (var i in new ushort[] { 0, 2, 1, 0, 3, 2 })
            bytes.AddRange(BitConverter.GetBytes(i));
        var data = Convert.ToBase64String(bytes.ToArray());
        var images = metallicRoughness == occlusion
            ? $$"""[ { "uri": "{{metallicRoughness}}" } ]"""
            : $$"""[ { "uri": "{{metallicRoughness}}" }, { "uri": "{{occlusion}}" } ]""";
        var textures = metallicRoughness == occlusion ? """[ { "source": 0 } ]""" : """[ { "source": 0 }, { "source": 1 } ]""";
        var occlusionIndex = metallicRoughness == occlusion ? 0 : 1;
        return $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0] } ],
          "nodes": [ { "name": "Quad", "mesh": 0 } ],
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0, "TEXCOORD_0": 1 }, "indices": 2, "material": 0 } ] } ],
          "materials": [ {
            "name": "Rock",
            "pbrMetallicRoughness": { "metallicFactor": {{metallic}}, "roughnessFactor": {{roughness}}, "metallicRoughnessTexture": { "index": 0 } },
            "occlusionTexture": { "index": {{occlusionIndex}} }
          } ],
          "images": {{images}},
          "textures": {{textures}},
          "buffers": [ { "byteLength": {{bytes.Count}}, "uri": "data:application/octet-stream;base64,{{data}}" } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 48 },
            { "buffer": 0, "byteOffset": 48, "byteLength": 32 },
            { "buffer": 0, "byteOffset": 80, "byteLength": 12 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 4, "type": "VEC3", "min": [-1, 0, -1], "max": [1, 0, 1] },
            { "bufferView": 1, "componentType": 5126, "count": 4, "type": "VEC2" },
            { "bufferView": 2, "componentType": 5123, "count": 6, "type": "SCALAR" }
          ]
        }
        """;
    }
}
