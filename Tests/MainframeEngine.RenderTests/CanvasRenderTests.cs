using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// The 2D canvas (ADR 0111): a golden for sprites, y-sort, z-index, every draw primitive, blending, CanvasModulate and
/// a CanvasLayer; exact gamma-space colours (Godot's clear grey, a tinted rect); the validation gate.
/// </summary>
public class CanvasRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void CanvasMatchesGoldenWithExactGammaSpaceColours()
    {
        var result = HostRunner.Run("canvas", Output("canvas"), "--capture", "5", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        // Canvas pixels are framebuffer pixels (no stretch yet). Godot's default clear colour, 0.3 grey, written as-is
        // (no tonemap): 77.
        AssertPixel(image, image.Width - 2, image.Height - 2, 77, 77, 77);
        // The filled red rect (1, 0.2, 0.2) × the CanvasModulate tint (0.85, 0.9, 1), in gamma space.
        AssertPixel(image, 40, 140, 217, 46, 51);
        Gates.AssertMatchesGolden(result, 5);
    }

    private static void AssertPixel(PngImage image, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * image.Width + x) * 4;
        Assert.True(Math.Abs(image.Pixels[i] - r) <= 1 && Math.Abs(image.Pixels[i + 1] - g) <= 1 && Math.Abs(image.Pixels[i + 2] - b) <= 1,
            $"Pixel ({x},{y}) is ({image.Pixels[i]}, {image.Pixels[i + 1]}, {image.Pixels[i + 2]}), expected ({r}, {g}, {b}).");
    }
}
