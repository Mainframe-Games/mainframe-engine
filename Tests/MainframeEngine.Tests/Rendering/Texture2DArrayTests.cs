namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0151: the CPU side of <see cref="Texture2DArray"/> (the GPU upload runs in the multimesh render test).</summary>
public sealed class Texture2DArrayTests
{
    private static byte[] Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        pixels.AsSpan().Fill(value);
        return pixels;
    }

    [Fact]
    public void LayersComeFromImagesInOrder()
    {
        var array = Texture2DArray.FromImages([Texture2D.FromPixels(2, 1, Solid(2, 1, 10)), Texture2D.FromPixels(2, 1, Solid(2, 1, 20))]);
        Assert.Equal((2, 1, 2), (array.Width, array.Height, array.Layers));
        Assert.Equal(16, array.Pixels.Length);
        Assert.All(array.GetLayerPixels(0).ToArray(), b => Assert.Equal(10, b));
        Assert.All(array.GetLayerPixels(1).ToArray(), b => Assert.Equal(20, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => array.GetLayerPixels(2));
    }

    [Fact]
    public void LayersMustShareOneSize()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Texture2DArray.FromImages([Texture2D.FromPixels(2, 2, Solid(2, 2, 0)), Texture2D.FromPixels(1, 2, Solid(1, 2, 0))]));
        Assert.Contains("1×2", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Texture2DArray.FromImages(2, 2, [Solid(2, 2, 0), Solid(2, 1, 0)]));
        Assert.Throws<ArgumentException>(() => Texture2DArray.FromImages(Array.Empty<Texture2D>()));
        Assert.Throws<ArgumentException>(() => new Texture2DArray(2, 2, 2, Solid(2, 2, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Texture2DArray(0, 2, 1, []));
    }

    [Fact]
    public void RawLayersAreCopiedAndEditsBumpTheVersion()
    {
        var raw = new byte[3 * 2 * 4 * 3];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = (byte)(i / 24);
        var array = new Texture2DArray(3, 2, 3, raw, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Mipmaps = false });
        raw[0] = 99; // copied
        Assert.Equal(0, array.GetLayerPixels(0)[0]);
        Assert.Equal(2, array.GetLayerPixels(2)[23]);
        Assert.Equal(TextureImportColorSpace.Linear, array.ImportSettings.ColorSpace);

        var version = array.Version;
        array.SetLayerPixels(1, Solid(3, 2, 7));
        Assert.Equal(version + 1, array.Version);
        Assert.All(array.GetLayerPixels(1).ToArray(), b => Assert.Equal(7, b));
        Assert.Equal(0, array.GetLayerPixels(0)[5]);
        Assert.Throws<ArgumentException>(() => array.SetLayerPixels(0, Solid(1, 1, 0)));

        array.ImportSettings = array.ImportSettings with { Mipmaps = true };
        Assert.Equal(version + 2, array.Version);
    }

    [Fact]
    public void AnEmptyArrayHasNoLayers()
    {
        var array = new Texture2DArray();
        Assert.Equal(0, array.Layers);
        Assert.True(array.Pixels.IsEmpty);
    }
}
