using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Terrain;

using Color = System.Drawing.Color;

/// <summary>ADR 0156: <see cref="TerrainLayer"/>, <see cref="TerrainSplatMaterial3D"/>, the layer packing and the terrain link.</summary>
public sealed class TerrainSplatMaterialTests
{
    private static Texture2D Solid(int size, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[size * size * 4];
        for (var p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = r;
            pixels[p + 1] = g;
            pixels[p + 2] = b;
            pixels[p + 3] = a;
        }

        return Texture2D.FromPixels(size, size, pixels);
    }

    private static Texture2D Ramp(int size)
    {
        // R = x, G = y, B = 7, A = 255 - x.
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = (y * size + x) * 4;
                pixels[p] = (byte)x;
                pixels[p + 1] = (byte)y;
                pixels[p + 2] = 7;
                pixels[p + 3] = (byte)(255 - x);
            }

        return Texture2D.FromPixels(size, size, pixels);
    }

    private static byte[] Pixel(Texture2DArray array, int layer, int x, int y) =>
        array.GetLayerPixels(layer).Slice((y * array.Width + x) * 4, 4).ToArray();

    [Fact]
    public void PackingBuildsThreeArraysOfOneSizeWithALayerEach()
    {
        var material = new TerrainSplatMaterial3D
        {
            LayerTextureSize = 8,
            Layers =
            [
                new TerrainLayer { Albedo = Solid(8, 200, 100, 50), Normal = Solid(4, 140, 120, 250), Orm = Solid(16, 255, 128, 0) },
                new TerrainLayer(),
                new TerrainLayer { Albedo = Solid(2, 10, 20, 30, 77) },
            ],
        };

        var packed = TerrainLayerPacker.Pack(material);
        foreach (var array in new[] { packed.Albedo, packed.Normal, packed.Orm })
        {
            Assert.Equal((8, 8, 3), (array.Width, array.Height, array.Layers));
            Assert.True(array.ImportSettings.Mipmaps);
            Assert.Equal(TextureWrap.Repeat, array.ImportSettings.Wrap);
        }

        Assert.Equal(TextureImportColorSpace.Srgb, packed.Albedo.ImportSettings.ColorSpace);
        Assert.Equal(TextureImportColorSpace.Linear, packed.Normal.ImportSettings.ColorSpace);
        Assert.Equal(TextureImportColorSpace.Linear, packed.Orm.ImportSettings.ColorSpace);

        // Layer 0: textures resampled (normal 4 → 8 up, ORM 16 → 8 down); opaque albedo: height from the ORM's occlusion.
        Assert.Equal([200, 100, 50, 255], Pixel(packed.Albedo, 0, 3, 5));
        Assert.Equal([140, 120, 250, 255], Pixel(packed.Normal, 0, 7, 7));
        Assert.Equal([255, 128, 0, 255], Pixel(packed.Orm, 0, 0, 0));
        // Layer 1: defaults (white albedo at mid height, flat normal, ORM 1 / 0.9 / 0).
        Assert.Equal([255, 255, 255, TerrainLayerPacker.DefaultHeight], Pixel(packed.Albedo, 1, 1, 1));
        Assert.Equal(TerrainLayerPacker.DefaultNormal, Pixel(packed.Normal, 1, 1, 1));
        Assert.Equal(TerrainLayerPacker.DefaultOrm, Pixel(packed.Orm, 1, 1, 1));
        // Layer 2: an albedo with alpha keeps it as the height.
        Assert.Equal([10, 20, 30, 77], Pixel(packed.Albedo, 2, 6, 2));
    }

    [Fact]
    public void HeightComesFromTheHeightMapThenAlbedoAlphaThenOrmThenDefault()
    {
        const int size = 4;
        var albedoWithAlpha = Solid(size, 1, 2, 3, 40);
        var opaque = Solid(size, 1, 2, 3);
        var orm = Solid(size, 90, 0, 0);
        var height = Solid(size, 200, 0, 0);

        byte HeightOf(TerrainLayer layer) => TerrainLayerPacker.Pack([layer], size).Albedo.GetLayerPixels(0)[3];

        Assert.Equal(200, HeightOf(new TerrainLayer { Albedo = albedoWithAlpha, Orm = orm, Height = height }));
        Assert.Equal(40, HeightOf(new TerrainLayer { Albedo = albedoWithAlpha, Orm = orm }));
        Assert.Equal(90, HeightOf(new TerrainLayer { Albedo = opaque, Orm = orm }));
        Assert.Equal(TerrainLayerPacker.DefaultHeight, HeightOf(new TerrainLayer { Albedo = opaque }));
        Assert.Equal(200, HeightOf(new TerrainLayer { Height = height })); // white albedo, given height
    }

    [Fact]
    public void ResamplingBoxFiltersDownAndWrapsBilinearUp()
    {
        var ramp = Ramp(4).DecodePixels().Rgba;

        // 4 → 2: each texel averages a 2 × 2 block (rounded half up).
        var down = TerrainLayerPacker.Resample(ramp, 4, 4, 2);
        Assert.Equal([1, 1, 7, 255], down.AsSpan(0, 4).ToArray());     // x 0–1, y 0–1
        Assert.Equal([3, 3, 7, 253], down.AsSpan(12, 4).ToArray());    // x 2–3, y 2–3

        // 4 → 8: texel centres map to texel centres, and the edges wrap (the layers tile).
        var up = TerrainLayerPacker.Resample(ramp, 4, 4, 8);
        Assert.Equal(1, up[0]);               // x = −0.25: lerp(x 3, x 0, 0.75) = 0.75 → 1
        Assert.Equal(2, up[(0 * 8 + 7) * 4]); // x = 3.25: lerp(x 3, x 0, 0.25) = 2.25 → 2
        Assert.Equal(1, up[(0 * 8 + 3) * 4]); // x = 1.25 → 1

        // The same size copies.
        var same = TerrainLayerPacker.Resample(ramp, 4, 4, 4);
        Assert.NotSame(ramp, same);
        Assert.Equal(ramp, same);
    }

    [Fact]
    public void ContentStampFollowsTexturesNotLookSettings()
    {
        var albedo = Solid(4, 9, 9, 9);
        var layer = new TerrainLayer { Albedo = albedo };
        var material = new TerrainSplatMaterial3D { Layers = [layer], LayerTextureSize = 4 };
        var stamp = TerrainLayerPacker.ContentStamp(material);

        layer.TilingMeters = 9f;
        layer.Tint = Color.Red;
        material.MacroStrength = 0.5f;
        Assert.Equal(stamp, TerrainLayerPacker.ContentStamp(material));

        albedo.SetPixels(new byte[4 * 4 * 4]); // re-import / edit: new version
        var edited = TerrainLayerPacker.ContentStamp(material);
        Assert.NotEqual(stamp, edited);
        layer.Normal = Solid(4, 128, 128, 255);
        Assert.NotEqual(edited, TerrainLayerPacker.ContentStamp(material));
        var normal = TerrainLayerPacker.ContentStamp(material);
        material.LayerTextureSize = 8;
        Assert.NotEqual(normal, TerrainLayerPacker.ContentStamp(material));
    }

    [Fact]
    public void LayerChangesTouchTheMaterialAndExtraLayersAreIgnored()
    {
        var layer = new TerrainLayer();
        var material = new TerrainSplatMaterial3D { Layers = [layer] };
        var version = material.Version;
        layer.HeightBlendContrast = 0.7f;
        Assert.True(material.Version > version);

        material.Layers = [];
        version = material.Version;
        layer.TilingMeters = 2f; // unsubscribed
        Assert.Equal(version, material.Version);

        material.Layers = Enumerable.Range(0, 10).Select(_ => new TerrainLayer()).ToArray();
        Assert.Equal(TerrainSplatMaterial3D.MaxLayers, material.LayerCount);
        Assert.Equal(TerrainSplatMaterial3D.MaxLayers, TerrainLayerPacker.Pack(new TerrainSplatMaterial3D { Layers = material.Layers, LayerTextureSize = 2 }).Albedo.Layers);
        Assert.Equal(new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), material.RenderState);
    }

    [Fact]
    public void ParametersPackTilingContrastTintsBandsAndSlopes()
    {
        Assert.Equal(TerrainSplatParams.Size, Marshal.SizeOf<TerrainSplatParams>());
        var material = new TerrainSplatMaterial3D
        {
            Layers =
            [
                new TerrainLayer { TilingMeters = 4f, HeightBlendContrast = 0.3f, NormalScale = 2f, Tint = Color.FromArgb(255, 255, 0, 128) },
                new TerrainLayer { TilingMeters = 0.5f },
            ],
            TriplanarStartDegrees = 30f,
            TriplanarEndDegrees = 60f,
            DetailDistance = 40f,
            FarDistance = 100f,
            FarTilingScale = 5f,
            MacroStrength = 0.25f,
            MacroScaleMeters = 20f,
            AntiTiling = false,
        };

        var p = TerrainSplatParams.From(material);
        unsafe
        {
            Assert.Equal(0.25f, p.Layers[0]);
            Assert.Equal(0.3f, p.Layers[1]);
            Assert.Equal(2f, p.Layers[2]);
            Assert.Equal(2f, p.Layers[4]);
            Assert.Equal(1f, p.Tints[0]);
            Assert.Equal(0f, p.Tints[1]);
            Assert.Equal(ColorSpace.SrgbToLinear(128 / 255f), p.Tints[2], 5);
            Assert.Equal(0.25f, p.Layers[8]); // unused layers: harmless defaults
        }

        Assert.Equal(new Vector4(40f, 100f, 0.2f, 0.25f), p.Bands);
        Assert.Equal(MathF.Cos(MathF.PI / 6f), p.Slope.X, 5);
        Assert.Equal(0.5f, p.Slope.Y, 5);
        Assert.Equal(0.05f, p.Slope.Z, 5);
        Assert.Equal(2f, p.Slope.W);
        Assert.Equal(0f, p.Options.X);
    }

    [Fact]
    public void SplatMaterialsHaveTheirOwnShaderSetOnTheStandardVertexLayout()
    {
        var material = new TerrainSplatMaterial3D();
        var key = PipelineKey.ForMaterial(ShaderSetId.MeshTerrainSplat, material.RenderState, false, new RenderPass(1), streams: true);
        Assert.Equal(VertexLayoutId.MeshInstanced, key.VertexLayout);
        Assert.NotEqual(PipelineKey.ForMaterial(ShaderSetId.MeshLit, material.RenderState, false, new RenderPass(1)), key);
        Assert.True((int)ShaderSetId.MeshTerrainSplat < MaterialGpu.ShaderSetCount);
    }

    [Fact]
    public void TerrainLinksItsSplatMaterialAndNamesSurfaces()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 16, 1f, 8);
        data.SetWeightsFrom((x, _, w) => w[x < 8f ? 0 : 1] = 1f);
        var terrain = new Terrain3D { Data = data, Position = new Vector3(-8, 0, -8) };
        Assert.Null(terrain.SurfaceTagAt(0, 0)); // no splat material

        var material = new TerrainSplatMaterial3D { Layers = [new TerrainLayer { Tag = "grass" }, new TerrainLayer { Tag = "rock" }] };
        terrain.Material = material;
        Assert.Same(terrain, material.Terrain);
        Assert.Equal("grass", terrain.SurfaceTagAt(-4f, 0f));
        Assert.Equal("rock", terrain.SurfaceTagAt(4f, 0f));

        material.Layers = [new TerrainLayer { Tag = "grass" }]; // layer 1 is gone
        Assert.Null(terrain.SurfaceTagAt(4f, 0f));

        terrain.Material = new StandardMaterial3D();
        Assert.Null(material.Terrain);
        terrain.Free();
    }

    [Fact]
    public void SplatMaterialAndLayersRoundTripInAScene()
    {
        var root = new Node3D { Name = "Root" };
        var terrain = new Terrain3D
        {
            Name = "Terrain",
            Data = TerrainData.Create(TerrainProfile.Realistic, 16, 1f, 8),
            Material = new TerrainSplatMaterial3D
            {
                LayerTextureSize = 512,
                TriplanarStartDegrees = 30f,
                TriplanarEndDegrees = 50f,
                AntiTiling = false,
                DetailDistance = 45f,
                FarDistance = 120f,
                FarTilingScale = 3f,
                MacroStrength = 0.3f,
                MacroScaleMeters = 64f,
                Layers =
                [
                    new TerrainLayer { Name = "Grass", Tag = "grass", TilingMeters = 3f, HeightBlendContrast = 0.4f, Tint = Color.FromArgb(255, 200, 220, 180), NormalScale = 0.5f },
                    new TerrainLayer { Name = "Rock", Tag = "rock", TilingMeters = 8f },
                ],
            },
        };
        root.AddChild(terrain);
        terrain.Owner = root;

        var json = SceneSaver.ToJson(root);
        root.Free();
        var loaded = PackedScene.Parse(json).Instantiate();
        var copy = loaded.GetNode<Terrain3D>("Terrain");
        var material = Assert.IsType<TerrainSplatMaterial3D>(copy.Material);
        Assert.Same(copy, material.Terrain); // linked again on load
        Assert.Equal((512, 30f, 50f, false), (material.LayerTextureSize, material.TriplanarStartDegrees, material.TriplanarEndDegrees, material.AntiTiling));
        Assert.Equal((45f, 120f, 3f, 0.3f, 64f), (material.DetailDistance, material.FarDistance, material.FarTilingScale, material.MacroStrength, material.MacroScaleMeters));
        Assert.Equal(2, material.Layers.Length);
        var grass = material.Layers[0];
        Assert.Equal(("Grass", "grass", 3f, 0.4f, 0.5f), (grass.Name, grass.Tag, grass.TilingMeters, grass.HeightBlendContrast, grass.NormalScale));
        Assert.Equal(Color.FromArgb(255, 200, 220, 180).ToArgb(), grass.Tint.ToArgb());
        Assert.Equal(("Rock", 8f), (material.Layers[1].Name, material.Layers[1].TilingMeters));

        var version = material.Version;
        grass.TilingMeters = 5f; // loaded layers are subscribed too
        Assert.True(material.Version > version);
        loaded.Free();
    }
}
