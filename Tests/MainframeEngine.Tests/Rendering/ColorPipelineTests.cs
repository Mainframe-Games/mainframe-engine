using System.Numerics;
using Silk.NET.Vulkan;
using Encoding = MainframeEngine.VulkanRenderer.SwapchainEncoding;

namespace MainframeEngine.Tests.Rendering;

public sealed class ColorSpaceTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.21404f)]
    [InlineData(0.04045f, 0.0031308f)]
    [InlineData(0.2f, 0.03310f)]
    public void SrgbDecodesToTheIecCurve(float srgb, float linear)
    {
        Assert.Equal(linear, ColorSpace.SrgbToLinear(srgb), 4);
    }

    [Fact]
    public void EncodeInvertsDecode()
    {
        for (var i = 0; i <= 255; i++)
        {
            var v = i / 255f;
            Assert.Equal(v, ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(v)), 4);
        }
    }

    [Fact]
    public void NegativeInputsClampToBlack()
    {
        Assert.Equal(0f, ColorSpace.SrgbToLinear(-0.5f));
        Assert.Equal(0f, ColorSpace.LinearToSrgb(-0.5f));
    }

    [Fact]
    public void VectorOverloadsKeepAlpha()
    {
        var c = ColorSpace.SrgbToLinear(new Vector4(0.5f, 0.5f, 0.5f, 0.25f));
        Assert.Equal(0.25f, c.W);
        Assert.Equal(0.21404f, c.X, 4);
    }
}

public sealed class AcesTonemapTests
{
    [Fact]
    public void BlackStaysBlackAndHighlightsRollOffBelowWhite()
    {
        Assert.True(ColorSpace.AcesFitted(Vector3.Zero).X < 0.001f);
        var bright = ColorSpace.AcesFitted(new Vector3(20f));
        Assert.InRange(bright.X, 0.95f, 1f);
        Assert.InRange(bright.Y, 0.95f, 1f);
    }

    [Fact]
    public void TheCurveIsMonotonic()
    {
        var previous = -1f;
        for (var x = 0f; x < 16f; x += 0.05f)
        {
            var y = ColorSpace.AcesFitted(new Vector3(x)).X;
            Assert.True(y >= previous, $"ACES is not monotonic at {x}");
            previous = y;
        }
    }

    [Fact]
    public void GreysStayGrey()
    {
        var y = ColorSpace.AcesFitted(new Vector3(0.18f));
        Assert.Equal(y.X, y.Y, 2);
        Assert.Equal(y.Y, y.Z, 2);
    }

    [Fact]
    public void TheDefaultExposureKeepsALitWhiteSurfaceNearItsPreHdrBrightness()
    {
        // 0.73: white albedo under the test scenes' 0.9-intensity sun at ~36° incidence. Before the HDR pipeline
        // it was written as-is (186/255); the calibrated exposure keeps it within a few percent.
        var shown = ColorSpace.LinearToSrgb(ColorSpace.AcesFitted(new Vector3(0.73f * IVulkanContext.DefaultExposure))).X;
        Assert.InRange(shown * 255f, 186f - 20f, 186f + 25f);
    }
}

public sealed class SwapchainFormatTests
{
    private static SurfaceFormatKHR F(Format format) => new(format, ColorSpaceKHR.SpaceSrgbNonlinearKhr);

    [Fact]
    public void UnormIsPreferredSoImGuiStaysExact()
    {
        var (format, encoding, overlay) = VulkanRenderer.ChooseSurfaceFormat(
            [F(Format.B8G8R8A8Srgb), F(Format.B8G8R8A8Unorm)], mutableFormatAvailable: false);

        Assert.Equal(Format.B8G8R8A8Unorm, format.Format);
        Assert.Equal(Encoding.UnormShaderEncode, encoding);
        Assert.Equal(Format.B8G8R8A8Unorm, overlay);
    }

    [Fact]
    public void ASrgbOnlySurfaceUsesTheMutableUnormViewWhenAvailable()
    {
        var (format, encoding, overlay) = VulkanRenderer.ChooseSurfaceFormat([F(Format.B8G8R8A8Srgb)], mutableFormatAvailable: true);

        Assert.Equal(Format.B8G8R8A8Srgb, format.Format);
        Assert.Equal(Encoding.SrgbWithUnormOverlay, encoding);
        Assert.Equal(Format.B8G8R8A8Unorm, overlay);
    }

    [Fact]
    public void ASrgbOnlySurfaceWithoutMutableFormatLinearisesTheOverlay()
    {
        var (_, encoding, _) = VulkanRenderer.ChooseSurfaceFormat([F(Format.R8G8B8A8Srgb)], mutableFormatAvailable: false);

        Assert.Equal(Encoding.SrgbOnly, encoding);
    }

    [Theory]
    [InlineData("srgb-mutable", nameof(Encoding.SrgbWithUnormOverlay), Format.B8G8R8A8Srgb)]
    [InlineData("srgb", nameof(Encoding.SrgbOnly), Format.B8G8R8A8Srgb)]
    [InlineData("unorm", nameof(Encoding.UnormShaderEncode), Format.B8G8R8A8Unorm)]
    public void TheEnvironmentOverrideForcesAPath(string value, string expectedEncoding, Format expectedFormat)
    {
        var expected = Enum.Parse<Encoding>(expectedEncoding);
        var requested = VulkanRenderer.ParseEncoding(value);
        var (format, encoding, _) = VulkanRenderer.ChooseSurfaceFormat(
            [F(Format.B8G8R8A8Unorm), F(Format.B8G8R8A8Srgb)], mutableFormatAvailable: true, requested);

        Assert.Equal(expected, encoding);
        Assert.Equal(expectedFormat, format.Format);
    }

    [Fact]
    public void UnknownOverridesAreRejected()
    {
        Assert.Null(VulkanRenderer.ParseEncoding(null));
        Assert.Throws<ArgumentException>(() => VulkanRenderer.ParseEncoding("hdr10"));
    }

    [Fact]
    public void FormatsInAnotherColourSpaceAreNotPicked()
    {
        var (format, encoding, _) = VulkanRenderer.ChooseSurfaceFormat(
            [new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceDisplayP3NonlinearExt), F(Format.R8G8B8A8Unorm)],
            mutableFormatAvailable: false);

        Assert.Equal(Format.R8G8B8A8Unorm, format.Format);
        Assert.Equal(Encoding.UnormShaderEncode, encoding);
    }
}
