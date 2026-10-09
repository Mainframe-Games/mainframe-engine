using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

using Color = System.Drawing.Color;

/// <summary>ADR 0175: the terrain macro texture (RVT-lite), its bake, the terrain's re-bakes and the world's macro terrain.</summary>
public sealed class TerrainMacroTextureTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static Texture2D Solid(int size, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[size * size * 4];
        for (var p = 0; p < pixels.Length; p += 4)
            (pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]) = (r, g, b, a);
        return Texture2D.FromPixels(size, size, pixels);
    }

    private static TerrainData Sloped(int size = 32)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, size, 1f, 16);
        data.SetHeightsFrom((x, z) => 2f + 0.25f * x); // a 14° slope rising to +X
        return data;
    }

    [Fact]
    public void APlainMaterialBakesItsColourWithTheGroundsHeightAndNormal()
    {
        var data = Sloped();
        var material = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 128, 64, 32), Roughness = 0.7f };
        var macro = TerrainMacroTexture.Bake(data, material, 64);

        Assert.Equal(64, macro.Resolution);
        Assert.Equal(TerrainMacroTexture.Layers, macro.Texture.Layers);
        Assert.Equal(32f, macro.SizeMeters);
        var expected = ColorSpace.SrgbToLinear(new Vector3(128, 64, 32) / 255f);
        var albedo = macro.AlbedoAt(10f, 20f);
        Assert.Equal(expected.X, albedo.X, 0.01f); // square-root encoding keeps the darks
        Assert.Equal(expected.Y, albedo.Y, 0.01f);
        Assert.Equal(expected.Z, albedo.Z, 0.005f);
        Assert.Equal(0.7f, macro.RoughnessAt(10f, 20f), 0.01f);

        // The height is the bed's, to within a code step and the bilinear between texel centres.
        foreach (var x in new[] { 3.1f, 12.5f, 20.07f, 28.9f })
            Assert.Equal(2f + 0.25f * x, macro.HeightAt(x, 9f), 0.02f);
        Assert.True(macro.HeightStep < 0.001f);

        // The normal leans away from the rise (−X), with no Z.
        var n = macro.NormalAt(16f, 16f);
        var slope = Vector3.Normalize(new Vector3(-0.25f, 1f, 0f));
        Assert.Equal(slope.X, n.X, 0.02f);
        Assert.Equal(slope.Y, n.Y, 0.02f);
        Assert.Equal(0f, n.Z, 0.02f);
    }

    [Fact]
    public void ASplatMaterialBakesEachLayersTintedColourWhereItIsPainted()
    {
        var data = Sloped();
        data.SetWeightsFrom((x, _, w) => w[x < 16f ? 0 : 1] = 1f); // layer 0 west, layer 1 east
        var material = new TerrainSplatMaterial3D
        {
            MacroStrength = 0f,
            Layers =
            [
                new TerrainLayer { Albedo = Solid(8, 200, 200, 200), Tint = Color.FromArgb(255, 255, 128, 128), Orm = Solid(8, 255, 51, 0) },
                new TerrainLayer { Albedo = Solid(8, 40, 120, 40), Orm = Solid(8, 255, 230, 0) },
            ],
        };

        var macro = TerrainMacroTexture.Bake(data, material, 128);
        var west = macro.AlbedoAt(4f, 16f);
        var east = macro.AlbedoAt(28f, 16f);
        var grey = ColorSpace.SrgbToLinear(200 / 255f);
        Assert.Equal(grey, west.X, 0.01f);
        Assert.Equal(grey * ColorSpace.SrgbToLinear(128 / 255f), west.Y, 0.01f); // × the layer's tint
        Assert.Equal(ColorSpace.SrgbToLinear(120 / 255f), east.Y, 0.01f);
        Assert.Equal(0.2f, macro.RoughnessAt(4f, 16f), 0.01f);
        Assert.Equal(0.9f, macro.RoughnessAt(28f, 16f), 0.01f);
    }

    [Fact]
    public void TheBakeIsDeterministic()
    {
        var data = Sloped();
        data.SetWeightsFrom((x, z, w) => (w[0], w[1]) = (MathF.Abs(MathF.Sin(x * 0.3f)), MathF.Abs(MathF.Cos(z * 0.2f))));
        var material = new TerrainSplatMaterial3D
        {
            Layers = [new TerrainLayer { Albedo = Solid(4, 90, 80, 70) }, new TerrainLayer { Albedo = Solid(4, 30, 60, 20) }],
        };
        var a = TerrainMacroTexture.Bake(data, material, 64).Texture.Pixels.ToArray();
        var b = TerrainMacroTexture.Bake(data, material, 64).Texture.Pixels.ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void TheTerrainBakesWhenBuiltAndAgainAfterEditsPause()
    {
        var data = Sloped();
        var terrain = _h.Add(new Terrain3D
        {
            Data = data,
            Material = new StandardMaterial3D { AlbedoColor = Color.Gray },
            MacroTextureEnabled = true,
            MacroTextureResolution = 64,
        });
        Assert.Null(terrain.MacroTexture); // the build bakes on a worker thread
        Assert.True(terrain.IsBakingMacroTexture);
        for (var i = 0; i < 600 && terrain.MacroTexture is null; i++)
        {
            Thread.Sleep(5);
            _h.Run(1);
        }

        var first = terrain.MacroTexture;
        Assert.NotNull(first);
        Assert.False(terrain.IsBakingMacroTexture);
        Assert.Same(terrain, _h.Tree.Root.World3D.MacroTerrain);

        terrain.SetHeightsFrom((_, _) => 5f);
        _h.Run(10); // still editing: not yet
        Assert.Same(first, terrain.MacroTexture);
        _h.RunSeconds(0.6f);
        Assert.NotSame(first, terrain.MacroTexture);
        Assert.Equal(5f, terrain.MacroTexture!.HeightAt(10f, 10f), 0.01f);

        terrain.MacroTextureEnabled = false;
        Assert.Null(terrain.MacroTexture);
        Assert.Null(_h.Tree.Root.World3D.MacroTerrain);
        terrain.MacroTextureEnabled = true;
        Assert.NotNull(terrain.MacroTexture);

        terrain.Free();
        _h.Run(1);
        Assert.Null(_h.Tree.Root.World3D.MacroTerrain);
    }

    [Fact]
    public void ATerrainWithoutTheMacroTextureBakesNothing()
    {
        var terrain = _h.Add(new Terrain3D { Data = Sloped() });
        Assert.False(terrain.MacroTextureEnabled);
        Assert.Null(terrain.MacroTexture);
        Assert.Null(_h.Tree.Root.World3D.MacroTerrain);
    }

    [Fact]
    public void TerrainBlendIsOffByDefaultAndPacksIntoTheMaterialBlock()
    {
        var material = new StandardMaterial3D();
        Assert.Equal(0f, material.TerrainBlend);
        Assert.Equal(0.3f, material.TerrainBlendHeight);
        Assert.Equal(Vector4.Zero, MaterialParams.From(material, 0).TerrainBlend);

        var version = material.Version;
        material.TerrainBlend = 0.8f;
        material.TerrainBlendHeight = 0.5f;
        Assert.Equal(version + 2, material.Version);
        Assert.Equal(new Vector4(0.8f, 0.5f, 0f, 0f), MaterialParams.From(material, 0).TerrainBlend);

        material.TerrainBlend = 3f;
        material.TerrainBlendHeight = 0f;
        Assert.Equal(new Vector4(1f, 0.01f, 0f, 0f), MaterialParams.From(material, 0).TerrainBlend);
    }
}
