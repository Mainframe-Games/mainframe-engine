using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// M3 resources and nodes in scene/resource files: MeshInstance3D with primitive and array meshes, materials,
/// textures (external, with import settings), removed node types (Box3d/Quad) and missing types keeping their
/// resources.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class MeshSerializationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-mesh-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public MeshSerializationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
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

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    private static T Own<T>(Node root, T child) where T : Node
    {
        root.AddChild(child);
        child.Owner = root;
        return child;
    }

    private static Node RoundTrip(Node root)
    {
        var json = SceneSaver.ToJson(root);
        root.Free();
        return PackedScene.Parse(json).Instantiate();
    }

    [Fact]
    public void MeshInstancesWithPrimitivesAndMaterialsRoundTrip()
    {
        var root = new Node3D { Name = "Root" };
        var shared = new BoxMesh { Size = new Vector3(1, 2, 3) };
        var material = new StandardMaterial3D
        {
            AlbedoColor = Color.FromArgb(255, 200, 100, 50),
            Transparency = AlphaMode.Cutout,
            AlphaCutoff = 0.25f,
            CullMode = CullMode.Disabled,
            DoubleSided = true,
            Specular = 0.8f,
            Shininess = 128,
            EmissionColor = Color.FromArgb(255, 10, 20, 30),
            EmissionEnergy = 3f,
            ShadingMode = ShadingMode.Unshaded,
            UvScale = new Vector2(4, 2),
            RenderPriority = 3,
        };
        Own(root, new MeshInstance3D { Name = "A", Mesh = shared, MaterialOverride = material, CastShadows = false });
        Own(root, new MeshInstance3D { Name = "B", Mesh = shared, Position = new Vector3(5, 0, 0) });
        Own(root, new MeshInstance3D { Name = "S", Mesh = new SphereMesh { Radius = 2, Rings = 7, Material = material } });
        Own(root, new MeshInstance3D { Name = "Q", Mesh = new QuadMesh { FlipFaces = true, Size = new Vector2(3, 1) } });
        Own(root, new MeshInstance3D { Name = "C", Mesh = new CapsuleMesh { Height = 4 } });
        Own(root, new MeshInstance3D { Name = "Y", Mesh = new CylinderMesh { TopRadius = 0, CapBottom = false } });
        Own(root, new MeshInstance3D { Name = "P", Mesh = new PlaneMesh { Size = new Vector2(8, 8), SubdivideDepth = 2 } });

        var loaded = RoundTrip(root);
        var a = loaded.GetNode<MeshInstance3D>("A");
        var b = loaded.GetNode<MeshInstance3D>("B");
        Assert.Same(a.Mesh, b.Mesh); // shared inline resource stays shared
        Assert.Equal(new Vector3(1, 2, 3), ((BoxMesh)a.Mesh!).Size);
        Assert.False(a.CastShadows);
        var m = (StandardMaterial3D)a.MaterialOverride!;
        Assert.Equal(Color.FromArgb(255, 200, 100, 50).ToArgb(), m.AlbedoColor.ToArgb());
        Assert.Equal(AlphaMode.Cutout, m.Transparency);
        Assert.Equal(0.25f, m.AlphaCutoff);
        Assert.Equal(CullMode.Disabled, m.CullMode);
        Assert.True(m.DoubleSided);
        Assert.Equal(0.8f, m.Specular);
        Assert.Equal(128f, m.Shininess);
        Assert.Equal(3f, m.EmissionEnergy);
        Assert.Equal(ShadingMode.Unshaded, m.ShadingMode);
        Assert.Equal(new Vector2(4, 2), m.UvScale);
        Assert.Equal(3, m.RenderPriority);
        Assert.Null(b.MaterialOverride);

        var sphere = (SphereMesh)loaded.GetNode<MeshInstance3D>("S").Mesh!;
        Assert.Equal(2f, sphere.Radius);
        Assert.Equal(7, sphere.Rings);
        Assert.Same(m, sphere.Material); // one material resource referenced twice
        Assert.True(((QuadMesh)loaded.GetNode<MeshInstance3D>("Q").Mesh!).FlipFaces);
        Assert.Equal(4f, ((CapsuleMesh)loaded.GetNode<MeshInstance3D>("C").Mesh!).Height);
        Assert.False(((CylinderMesh)loaded.GetNode<MeshInstance3D>("Y").Mesh!).CapBottom);
        Assert.Equal(2, ((PlaneMesh)loaded.GetNode<MeshInstance3D>("P").Mesh!).SubdivideDepth);
        loaded.Free();
    }

    [Fact]
    public void ArrayMeshSurfacesRoundTripInline()
    {
        var mesh = new ArrayMesh();
        var red = new StandardMaterial3D { AlbedoColor = Color.Red };
        mesh.AddSurface([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2], red);
        mesh.AddSurface([Vector3.Zero, -Vector3.UnitX, -Vector3.UnitY], [], [], [0, 2, 1]);

        var root = new Node3D { Name = "Root" };
        Own(root, new MeshInstance3D { Name = "M", Mesh = mesh });
        var loaded = RoundTrip(root);

        var copy = (ArrayMesh)loaded.GetNode<MeshInstance3D>("M").Mesh!;
        Assert.Equal(2, copy.SurfaceCount);
        Assert.Equal(mesh.GetSurface(0).Positions, copy.GetSurface(0).Positions);
        Assert.Equal(mesh.GetSurface(0).Normals, copy.GetSurface(0).Normals);
        Assert.Equal(mesh.GetSurface(0).UVs, copy.GetSurface(0).UVs);
        Assert.Equal([0, 2, 1], copy.GetSurface(1).Indices);
        Assert.Empty(copy.GetSurface(1).Normals);
        Assert.Equal(Color.Red.ToArgb(), ((StandardMaterial3D)copy.GetSurfaceMaterial(0)!).AlbedoColor.ToArgb());
        Assert.Null(copy.GetSurfaceMaterial(1));
        Assert.Equal(new Aabb(new Vector3(-1, -1, 0), new Vector3(1, 1, 0)), copy.Bounds);
        loaded.Free();
    }

    [Fact]
    public void MaterialsSaveAsResourceFilesAndTexturesAsReferences()
    {
        WritePng(ContentPath("Textures/brick.png"), 4, 2);
        File.WriteAllText(ContentPath("Textures/brick.png.meta"), """
            { "uid": "tex_0123456789ab", "importer": "texture",
              "settings": { "colorSpace": "linear", "mipmaps": false, "filter": "nearest", "wrap": "clamp", "anisotropy": 4 } }
            """);
        AssetDatabase.Current.Scan();

        var texture = ResourceLoader.Load<Texture2D>("Content/Textures/brick.png");
        Assert.Equal("tex_0123456789ab", texture.Uid);
        Assert.Equal(4, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.Equal(TextureImportColorSpace.Linear, texture.ImportSettings.ColorSpace);
        Assert.False(texture.ImportSettings.Mipmaps);
        Assert.Equal(TextureFilter.Nearest, texture.ImportSettings.Filter);
        Assert.Equal(TextureWrap.Clamp, texture.ImportSettings.Wrap);
        Assert.Equal(4, texture.ImportSettings.Anisotropy);
        Assert.Same(texture, ResourceLoader.Load<Texture2D>("tex_0123456789ab")); // cached by UID

        var material = new StandardMaterial3D { AlbedoTexture = texture, NormalTexture = texture };
        var materialUid = ResourceSaver.Save(material, ContentPath("Materials/Brick.mres"));
        var json = File.ReadAllText(ContentPath("Materials/Brick.mres"));
        Assert.Contains("\"ref\": \"tex_0123456789ab\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"Content/Textures/brick.png\"", json, StringComparison.Ordinal);

        texture.Release();
        texture.Release();
        ResourceLoader.ClearCache();
        var reloaded = ResourceLoader.Load<StandardMaterial3D>(materialUid);
        Assert.NotNull(reloaded.AlbedoTexture);
        Assert.Same(reloaded.AlbedoTexture, reloaded.NormalTexture);
        var (pixels, w, h) = reloaded.AlbedoTexture!.DecodePixels();
        Assert.Equal((4, 2), (w, h));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        reloaded.Release();
    }

    [Fact]
    public void TexturesWithoutMetaUseDefaultSettings()
    {
        WritePng(ContentPath("plain.png"), 1, 1);
        var texture = ResourceLoader.Load<Texture2D>("Content/plain.png");
        Assert.Null(texture.Uid);
        Assert.Equal(TextureImportSettings.Default, texture.ImportSettings);
        Assert.Equal(TextureColorSpace.Srgb, texture.ImportSettings.ResolveColorSpace(colorUsage: true));
        Assert.Equal(TextureColorSpace.Linear, texture.ImportSettings.ResolveColorSpace(colorUsage: false));
        texture.Release();
    }

    [Fact]
    public void RemovedShapeTypesLoadAsMeshInstances()
    {
        var scene = PackedScene.Parse(Encoding.UTF8.GetBytes("""
            { "format": 1, "root": { "type": "Node3D", "name": "Old", "children": [
              { "type": "Box3d", "name": "Box", "props": { "Position": [1, 2, 3], "Color": [1, 0, 0, 1], "CastShadows": false } },
              { "type": "Quad", "name": "Floor", "props": { "RotationDegrees": [90, 0, 0], "Scale": [10, 10, 1] } } ] } }
            """));
        var root = scene.Instantiate();

        var box = root.GetNode<MeshInstance3D>("Box");
        Assert.IsType<BoxMesh>(box.Mesh);
        Assert.Equal(new Vector3(1, 2, 3), box.Position);
        Assert.False(box.CastShadows);
        Assert.Equal(Color.Red.ToArgb(), ((StandardMaterial3D)box.MaterialOverride!).AlbedoColor.ToArgb());

        var floor = root.GetNode<MeshInstance3D>("Floor");
        Assert.True(((QuadMesh)floor.Mesh!).FlipFaces);
        Assert.Equal(CullMode.Disabled, ((StandardMaterial3D)floor.MaterialOverride!).CullMode);
        Assert.Equal(Color.White.ToArgb(), ((StandardMaterial3D)floor.MaterialOverride!).AlbedoColor.ToArgb());
        Assert.Equal(new Vector3(10, 10, 1), floor.Scale);
        // The old quad faced -Z; rotated +90° about X it faces up, like the floor always did.
        var up = Vector3.TransformNormal(floor.Mesh!.GetSurface(0).Normals[0], floor.ModelMatrix);
        Assert.True(Vector3.Normalize(up).Y > 0.999f, $"floor normal {up}");

        // Saving writes the new types.
        var json = Encoding.UTF8.GetString(SceneSaver.ToJson(root));
        Assert.DoesNotContain("Box3d", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"MeshInstance3D\"", json, StringComparison.Ordinal);
        Assert.Contains(RemovedNodeTypes.Names, n => n == "Quad");
        root.Free();
    }

    [Fact]
    public void MissingNodesKeepTheResourcesTheyReference()
    {
        var json = Encoding.UTF8.GetBytes("""
            { "format": 1,
              "resources": { "1": { "type": "BoxMesh", "props": { "Size": [2, 2, 2] } }, "2": { "type": "SphereMesh" } },
              "root": { "type": "Node3D", "name": "R", "children": [
                { "type": "GameOnlyType", "name": "G", "props": { "Shape": { "res": "1" }, "List": [ { "res": "2" } ], "Speed": 3 } } ] } }
            """);
        var root = PackedScene.Parse(json).Instantiate();
        Assert.IsType<MissingNode>(root.GetNode("G"));

        var saved = JsonDocument.Parse(SceneSaver.ToJson(root)).RootElement;
        var resources = saved.GetProperty("resources");
        var props = saved.GetProperty("root").GetProperty("children")[0].GetProperty("props");
        var shapeKey = props.GetProperty("Shape").GetProperty("res").GetString()!;
        var listKey = props.GetProperty("List")[0].GetProperty("res").GetString()!;
        Assert.Equal("BoxMesh", resources.GetProperty(shapeKey).GetProperty("type").GetString());
        Assert.Equal("SphereMesh", resources.GetProperty(listKey).GetProperty("type").GetString());
        Assert.Equal(3, props.GetProperty("Speed").GetInt32());
        root.Free();
    }

    [Fact]
    public void TextureImportSettingsReadAndWriteMeta()
    {
        var meta = new AssetMeta
        {
            Uid = "tex_aaaaaaaaaaaa",
            Importer = TextureImportSettings.ImporterName,
            Settings = new TextureImportSettings
            {
                ColorSpace = TextureImportColorSpace.Srgb,
                Mipmaps = false,
                Filter = TextureFilter.Nearest,
                Wrap = TextureWrap.Mirror,
                Anisotropy = 2,
            }.ToMetaSettings(),
        };
        var settings = TextureImportSettings.FromMeta(meta);
        Assert.Equal(TextureImportColorSpace.Srgb, settings.ColorSpace);
        Assert.False(settings.Mipmaps);
        Assert.Equal(TextureFilter.Nearest, settings.Filter);
        Assert.Equal(TextureWrap.Mirror, settings.Wrap);
        Assert.Equal(2, settings.Anisotropy);

        var sampling = settings.ToSampling();
        Assert.Equal(Silk.NET.Vulkan.Filter.Nearest, sampling.Filter);
        Assert.Equal(Silk.NET.Vulkan.SamplerAddressMode.MirroredRepeat, sampling.AddressMode);
        Assert.Equal(2f, sampling.MaxAnisotropy);

        // Bad and unknown values fall back to the defaults.
        var bad = new AssetMeta
        {
            Uid = "tex_bbbbbbbbbbbb",
            Settings = new Dictionary<string, JsonElement>
            {
                ["filter"] = JsonDocument.Parse("\"blurry\"").RootElement,
                ["anisotropy"] = JsonDocument.Parse("99").RootElement,
                ["mystery"] = JsonDocument.Parse("1").RootElement,
            },
        };
        var fallback = TextureImportSettings.FromMeta(bad);
        Assert.Equal(TextureFilter.Linear, fallback.Filter);
        Assert.Equal(16, fallback.Anisotropy); // clamped
        Assert.Equal(TextureImportSettings.Default, TextureImportSettings.FromMeta(null));
    }

    [Fact]
    public void CodeCreatedTexturesKeepTheirPixels()
    {
        byte[] rgba = [1, 2, 3, 4, 5, 6, 7, 8];
        var texture = Texture2D.FromPixels(2, 1, rgba);
        Assert.Equal((2, 1), (texture.Width, texture.Height));
        Assert.Equal(rgba, texture.DecodePixels().Rgba);
        Assert.Throws<ArgumentException>(() => Texture2D.FromPixels(2, 2, rgba));

        var version = texture.Version;
        texture.ImportSettings = texture.ImportSettings with { Wrap = TextureWrap.Clamp };
        Assert.NotEqual(version, texture.Version);
    }

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> PNG: blue first pixel, then white.</summary>
    internal static void WritePng(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[width * height * 4];
        pixels.AsSpan().Fill(255);
        pixels[0] = 0;
        pixels[1] = 0;
        Png.WriteRgba8(path, width, height, pixels);
    }
}
