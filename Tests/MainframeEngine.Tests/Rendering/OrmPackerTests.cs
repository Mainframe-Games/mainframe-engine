namespace MainframeEngine.Tests.Rendering;

/// <summary><see cref="OrmPacker"/>: separate maps into R = occlusion, G = roughness, B = metallic (ADR 0158).</summary>
public sealed class OrmPackerTests
{
    private static byte[] Grey(params byte[] values)
    {
        var rgba = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = values[i];
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    [Fact]
    public void PacksTheRedChannelOfEachMap()
    {
        var occlusion = Grey(10, 20);
        var roughness = Grey(100, 200);
        roughness[1] = 7; // green of a source is ignored: only red counts
        var metallic = Grey(0, 255);
        var orm = OrmPacker.Pack(2, 1, occlusion, roughness, metallic);
        Assert.Equal(new byte[] { 10, 100, 0, 255, 20, 200, 255, 255 }, orm);
    }

    [Fact]
    public void MissingMapsAreConstants()
    {
        var orm = OrmPacker.Pack(2, 1, [], Grey(64, 128), []);
        Assert.Equal(new byte[] { 255, 64, 0, 255, 255, 128, 0, 255 }, orm); // no occlusion, dielectric
        var custom = OrmPacker.Pack(1, 1, [], [], [], defaultOcclusion: 200, defaultRoughness: 30, defaultMetallic: 40);
        Assert.Equal(new byte[] { 200, 30, 40, 255 }, custom);
    }

    [Fact]
    public void RejectsWrongSizes()
    {
        Assert.Throws<ArgumentException>(() => OrmPacker.Pack(2, 2, Grey(1, 2), [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrmPacker.Pack(0, 1, [], [], []));
    }

    [Fact]
    public void TexturesPackIntoALinearMipmappedTexture()
    {
        var roughness = Texture2D.FromPixels(2, 1, Grey(30, 220));
        var metallic = Texture2D.FromPixels(2, 1, Grey(255, 0));
        var orm = OrmPacker.Pack(null, roughness, metallic);
        var (rgba, width, height) = orm.DecodePixels();
        Assert.Equal((2, 1), (width, height));
        Assert.Equal(new byte[] { 255, 30, 255, 255, 255, 220, 0, 255 }, rgba);
        Assert.Equal(TextureImportColorSpace.Linear, orm.ImportSettings.ColorSpace);
        Assert.True(orm.ImportSettings.Mipmaps);

        Assert.Throws<ArgumentException>(() => OrmPacker.Pack(null, roughness, Texture2D.FromPixels(1, 1, Grey(1))));
        Assert.Throws<ArgumentException>(() => OrmPacker.Pack(null, null, null));
    }

    [Fact]
    public void PacksTheBarkRoughnessJpeg()
    {
        var path = ContentPaths.Resolve(TreeMaterials.BarkMapPath("Bark001", "Roughness"), ContentPaths.BaseDirectory);
        var roughness = Texture2D.FromFile(path);
        var orm = OrmPacker.Pack(null, roughness, null);
        var (source, sourceWidth, sourceHeight) = roughness.DecodePixels();
        var (rgba, width, height) = orm.DecodePixels();
        Assert.Equal((sourceWidth, sourceHeight), (width, height));
        for (var i = 0; i < rgba.Length; i += 4099 * 4)
        {
            Assert.Equal(255, rgba[i]);
            Assert.Equal(source[i], rgba[i + 1]);
            Assert.Equal(0, rgba[i + 2]);
        }
    }
}
