namespace MainframeEngine.RenderTests;

public class ImageComparisonTests
{
    private static PngImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return new PngImage(w, h, pixels);
    }

    [Fact]
    public void IdenticalImagesMatch()
    {
        var result = ImageComparison.Compare(Solid(8, 8, 10, 20, 30), Solid(8, 8, 10, 20, 30));

        Assert.True(ImageComparison.IsMatch(result));
        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(0, result.MaxChannelDelta);
    }

    [Fact]
    public void DifferencesWithinToleranceMatch()
    {
        var result = ImageComparison.Compare(Solid(8, 8, 10, 20, 30), Solid(8, 8, 13, 17, 30), channelTolerance: 3);

        Assert.True(ImageComparison.IsMatch(result));
        Assert.Equal(3, result.MaxChannelDelta);
    }

    [Fact]
    public void DifferencesBeyondToleranceAreCountedPerPixel()
    {
        var expected = Solid(10, 10, 0, 0, 0);
        var actual = Solid(10, 10, 0, 0, 0);
        actual.Pixels[0] = 200;      // pixel 0, red
        actual.Pixels[4 * 5 + 2] = 9; // pixel 5, blue

        var result = ImageComparison.Compare(expected, actual, channelTolerance: 8);

        Assert.Equal(2, result.DifferingPixels);
        Assert.Equal(2.0, result.DifferingPercent, 6);
        Assert.False(ImageComparison.IsMatch(result, maxDifferingPercent: 1.0));
        Assert.True(ImageComparison.IsMatch(result, maxDifferingPercent: 2.0));
        Assert.Equal(255, result.DiffImage![3]);
        Assert.True(result.DiffImage[0] > 128); // marked red
    }

    [Fact]
    public void DifferentSizesNeverMatch()
    {
        var result = ImageComparison.Compare(Solid(4, 4, 0, 0, 0), Solid(4, 5, 0, 0, 0));

        Assert.False(result.SizeMatches);
        Assert.False(ImageComparison.IsMatch(result, maxDifferingPercent: 100));
    }
}
