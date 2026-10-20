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
        // Canvas_items stretch from 480×270: canvas units × (capture width / 480). Godot's default clear colour, 0.3
        // grey, written as-is (no tonemap): 77.
        var scale = image.Width / 480f;
        AssertPixel(image, image.Width - 2, image.Height - 2, 77, 77, 77);
        // The filled red rect (1, 0.2, 0.2) × the CanvasModulate tint (0.85, 0.9, 1), in gamma space.
        AssertPixel(image, (int)(40 * scale), (int)(140 * scale), 217, 46, 51);
        Gates.AssertMatchesGolden(result, 5);
    }

    [Fact]
    public void SkinnedPolygonsBendWithTheirBonesAndMatchGolden()
    {
        // ADR 0202: a textured strip at rest, the same strip skinned to three bent bones, a vertex-coloured fan.
        var result = HostRunner.Run("polygon2d-skin", Output("polygon2d-skin"), "--capture", "5", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 5);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 480f;
        // The bent strip's free end curls up: where its unbent tip would be (x 215, y 150) is background now, and the
        // rest strip above is drawn where it was authored (x 210, y 58: inside it).
        AssertPixel(image, (int)(215 * scale), (int)(150 * scale), 77, 77, 77);
        var i = ((int)(58 * scale) * image.Width + (int)(210 * scale)) * 4;
        Assert.False(image.Pixels[i] == 77 && image.Pixels[i + 1] == 77 && image.Pixels[i + 2] == 77, "the rest strip is missing");
    }

    private static void AssertPixel(PngImage image, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * image.Width + x) * 4;
        Assert.True(Math.Abs(image.Pixels[i] - r) <= 1 && Math.Abs(image.Pixels[i + 1] - g) <= 1 && Math.Abs(image.Pixels[i + 2] - b) <= 1,
            $"Pixel ({x},{y}) is ({image.Pixels[i]}, {image.Pixels[i + 1]}, {image.Pixels[i + 2]}), expected ({r}, {g}, {b}).");
    }
}
