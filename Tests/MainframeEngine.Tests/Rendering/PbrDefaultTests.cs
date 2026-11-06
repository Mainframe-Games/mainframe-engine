using System.Text;
using System.Text.Json;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0255: PBR is the default shading; materials written under the Blinn-Phong default keep it.</summary>
[Collection(nameof(Tests.Scene.SerialResources))]
public sealed class PbrDefaultTests
{
    private static string SceneJson(string version, string props) => $$"""
        { "format": 2, "uid": "scn_000000000255",
          "resources": { "StandardMaterial3D_a": { "type": "StandardMaterial3D"{{version}}{{props}} } },
          "nodes": [
            { "name": "Root", "type": "Node3D" },
            { "name": "Box", "parent": ".", "type": "MeshInstance3D", "props": { "MaterialOverride": { "res": "StandardMaterial3D_a" } } } ] }
        """;

    private static StandardMaterial3D Load(string scene, out Node root)
    {
        root = PackedScene.Parse(Encoding.UTF8.GetBytes(scene)).Instantiate();
        return (StandardMaterial3D)((MeshInstance3D)root.GetChild(0)).MaterialOverride!;
    }

    [Fact]
    public void NewMaterialsArePbr()
    {
        Assert.Equal(ShadingMode.Pbr, new StandardMaterial3D().ShadingMode);
        Assert.Equal(ShadingMode.Pbr, StandardMaterial3D.Default.ShadingMode);
        Assert.Equal(2, Serialization.TypeRegistry.GetRequired(typeof(StandardMaterial3D)).Version);
    }

    [Theory]
    [InlineData("", """, "props": { "Specular": 0.5 }""")] // version 1, some props
    [InlineData("", "")] // version 1, every property at its default (no "props")
    [InlineData(""", "v": 1""", """, "props": { }""")]
    public void VersionOneMaterialsWithoutAModeStayBlinnPhong(string version, string props)
    {
        var material = Load(SceneJson(version, props), out var root);
        try
        {
            Assert.Equal(ShadingMode.BlinnPhong, material.ShadingMode);
        }
        finally
        {
            root.Free();
        }
    }

    [Theory]
    [InlineData("Unshaded", ShadingMode.Unshaded)]
    [InlineData("Pbr", ShadingMode.Pbr)]
    [InlineData("BlinnPhong", ShadingMode.BlinnPhong)]
    public void VersionOneMaterialsKeepAnExplicitMode(string mode, ShadingMode expected)
    {
        var material = Load(SceneJson("", $$""", "props": { "ShadingMode": "{{mode}}" }"""), out var root);
        try
        {
            Assert.Equal(expected, material.ShadingMode);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void VersionTwoMaterialsWithoutAModeArePbr()
    {
        var material = Load(SceneJson(""", "v": 2""", ""), out var root);
        try
        {
            Assert.Equal(ShadingMode.Pbr, material.ShadingMode);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void ASavedOldMaterialWritesVersionTwoAndItsMode()
    {
        Load(SceneJson("", ""), out var root);
        try
        {
            var saved = SceneSaver.ToJson(root, "scn_000000000255");
            using var document = JsonDocument.Parse(saved);
            var entry = document.RootElement.GetProperty("resources").EnumerateObject().Single().Value;
            Assert.Equal(2, entry.GetProperty("v").GetInt32());
            Assert.Equal("BlinnPhong", entry.GetProperty("props").GetProperty("ShadingMode").GetString());
            var reloaded = Load(Encoding.UTF8.GetString(saved), out var copy);
            Assert.Equal(ShadingMode.BlinnPhong, reloaded.ShadingMode);
            copy.Free();

            // And a new PBR material writes no mode at all: it is the default.
            var pbr = Encoding.UTF8.GetString(SceneSaverResource(new StandardMaterial3D { AlbedoColor = System.Drawing.Color.Red }));
            Assert.DoesNotContain("ShadingMode", pbr, StringComparison.Ordinal);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void OldResourceFilesStayBlinnPhong()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-pbr-default-" + Guid.NewGuid().ToString("N"));
        var previous = AssetDatabase.Current;
        Directory.CreateDirectory(Path.Combine(project, AssetDatabase.ContentFolder));
        File.WriteAllText(Path.Combine(project, AssetDatabase.ContentFolder, "old.mfres"),
            """{ "format": 2, "uid": "res_000000000255", "type": "StandardMaterial3D", "props": { "Specular": 0.5 } }""");
        File.WriteAllText(Path.Combine(project, AssetDatabase.ContentFolder, "bare.mfres"),
            """{ "format": 2, "uid": "res_000000000256", "type": "StandardMaterial3D" }""");
        File.WriteAllText(Path.Combine(project, AssetDatabase.ContentFolder, "new.mfres"),
            """{ "format": 2, "uid": "res_000000000257", "type": "StandardMaterial3D", "v": 2 }""");
        AssetDatabase.Current = new AssetDatabase(project);
        ResourceLoader.ClearCache();
        try
        {
            var old = ResourceLoader.Load<StandardMaterial3D>("Assets/old.mfres");
            Assert.Equal(ShadingMode.BlinnPhong, old.ShadingMode);
            Assert.Equal(0.5f, old.Specular);
            Assert.Equal(ShadingMode.BlinnPhong, ResourceLoader.Load<StandardMaterial3D>("Assets/bare.mfres").ShadingMode);
            Assert.Equal(ShadingMode.Pbr, ResourceLoader.Load<StandardMaterial3D>("Assets/new.mfres").ShadingMode);
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            Directory.Delete(project, recursive: true);
        }
    }

    private static byte[] SceneSaverResource(StandardMaterial3D material)
    {
        var root = new Node3D { Name = "Root" };
        var box = new MeshInstance3D { Name = "Box", MaterialOverride = material };
        root.AddChild(box);
        box.Owner = root;
        try
        {
            return SceneSaver.ToJson(root);
        }
        finally
        {
            root.Free();
        }
    }
}
