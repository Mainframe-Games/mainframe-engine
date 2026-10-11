using System.Numerics;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// The 2D canvas's post-processing (ADR 0185) and more 2D lights (ADR 0186): goldens, graded colours against
/// <see cref="CanvasPostMath"/>, an untouched layer above the max layer, glow around a bright disc, 32 lights on screen
/// with off-screen ones culled; the validation gate.
/// </summary>
public class CanvasPostRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void CanvasPostGradesTheCanvasAndLeavesTheLayerAboveUntouched()
    {
        var result = HostRunner.Run("canvas-post", Output("canvas-post"), "--capture", "5", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 480f;
        var s = CanvasPostScene.Settings;

        // The red swatch's centre (80, 70): graded, then the vignette at its place.
        var uv = new Vector2(80f / 480f, 70f / 270f);
        var swatch = CanvasPostScene.SwatchColor;
        var graded = CanvasPostMath.Grade(new Vector3(swatch.X, swatch.Y, swatch.Z), s);
        var expected = Vector3.Lerp(graded, s.VignetteColor, CanvasPostMath.Vignette(uv, 480f / 270f, s));
        AssertPixel(image, (int)(80 * scale), (int)(70 * scale), expected, tolerance: 3);

        // The HUD layer (layer 1) is drawn after the post-process: its exact colour.
        var hud = CanvasPostScene.HudColor;
        AssertPixel(image, (int)(440 * scale), (int)(240 * scale), new Vector3(hud.X, hud.Y, hud.Z), tolerance: 1);

        // Glow: just outside the bright disc is lighter than its mirror image across the centre (same vignette, no glow).
        var lit = Luma(image, (int)(318 * scale), (int)(135 * scale));
        var mirror = Luma(image, (int)(162 * scale), (int)(135 * scale));
        Assert.True(lit > mirror + 10, $"Glow beside the disc: luma {lit}, mirror {mirror}.");
        Gates.AssertMatchesGolden(result, 5);
    }

    [Fact]
    public void ThirtyTwoLightsLightTheirCellsAndOffScreenLightsTakeNoSlot()
    {
        var result = HostRunner.Run("canvas-many-lights", Output("canvas-many-lights"), "--capture", "5", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 480f;
        // The gap between cells: the floor under the night tint only (0.8 × 0.15 → about 31).
        var gap = Luma(image, (int)((CanvasManyLightsScene.Origin.X + CanvasManyLightsScene.Spacing / 2) * scale), (int)((CanvasManyLightsScene.Origin.Y + CanvasManyLightsScene.Spacing / 2) * scale));
        Assert.True(gap < 50, $"The gap between lights is lit: luma {gap}.");
        for (var i = 0; i < CanvasManyLightsScene.Columns * CanvasManyLightsScene.Rows; i++)
        {
            var p = CanvasManyLightsScene.Origin + new Vector2(i % CanvasManyLightsScene.Columns, i / CanvasManyLightsScene.Columns) * CanvasManyLightsScene.Spacing;
            var luma = Luma(image, (int)(p.X * scale), (int)(p.Y * scale));
            Assert.True(luma > gap + 60, $"Light {i} does not light its cell: luma {luma}, gap {gap}.");
        }

        Gates.AssertMatchesGolden(result, 5);
    }

    private static int Luma(PngImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i] * 54 + image.Pixels[i + 1] * 183 + image.Pixels[i + 2] * 19) / 256;
    }

    private static void AssertPixel(PngImage image, int x, int y, Vector3 expected, int tolerance)
    {
        var i = (y * image.Width + x) * 4;
        var e = Vector3.Clamp(expected, Vector3.Zero, Vector3.One) * 255f;
        var (r, g, b) = ((int)MathF.Round(e.X), (int)MathF.Round(e.Y), (int)MathF.Round(e.Z));
        Assert.True(Math.Abs(image.Pixels[i] - r) <= tolerance && Math.Abs(image.Pixels[i + 1] - g) <= tolerance && Math.Abs(image.Pixels[i + 2] - b) <= tolerance,
            $"Pixel ({x},{y}) is ({image.Pixels[i]}, {image.Pixels[i + 1]}, {image.Pixels[i + 2]}), expected ({r}, {g}, {b}).");
    }
}
