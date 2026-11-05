using System.Text;

namespace MainframeEngine.Tests.Rendering;

/// <summary>G6.2: Radiance .hdr panoramas through StbImageSharp's float path; the sky's radiance size and process mode.</summary>
public sealed class HdrSkyTests
{
    [Fact]
    public void HdrDecodesToLinearHalfFloatsAboveOne()
    {
        // Two flat RGBE texels: (1, 0.5, 2) = (64, 32, 128, e 130) and (4, 0, 0.25) = (128, 0, 8, e 131).
        var header = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 2\n");
        byte[] encoded = [.. header, 64, 32, 128, 130, 128, 0, 8, 131];

        var (texels, width, height) = HdrImage.DecodeHalf(encoded);

        Assert.Equal((2, 1), (width, height));
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(texels);
        Assert.Equal([1f, 0.5f, 2f, 1f, 4f, 0f, 0.25f, 1f], halves.ToArray().Select(h => (float)h));
        Assert.True(HdrImage.IsHdr("Assets/Sky/studio.HDR"));
        Assert.False(HdrImage.IsHdr("Assets/Sky/studio.png"));
        Assert.Throws<InvalidOperationException>(() => HdrImage.DecodeHalf([1, 2, 3]));
    }

    [Fact]
    public void RadianceSizeAndProcessModeDefaults()
    {
        var sky = new Sky();
        Assert.Equal(SkyRadianceSize.Size128, sky.RadianceSize);
        Assert.Equal(128u, sky.RadianceSizePixels);
        Assert.Equal(SkyProcessMode.Automatic, sky.ProcessMode);
        uint[] pixels = [32, 64, 128, 256, 512, 1024];
        Assert.Equal(pixels, Enum.GetValues<SkyRadianceSize>().Select(size => new Sky { RadianceSize = size }.RadianceSizePixels));
    }
}
