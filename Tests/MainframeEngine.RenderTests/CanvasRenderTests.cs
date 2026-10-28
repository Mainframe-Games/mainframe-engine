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

    [Fact]
    public void CanvasGroupsFadeAsOneImageLikeGodot()
    {
        // #109: the values Godot 4.7.2 renders for the same scene (black, white squares overlapping by half).
        var result = HostRunner.Run("canvas-group", Output("canvas-group"), "--capture", "5", "--size", "400x200", "--hidden");
        Gates.AssertValidationClean(result);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 200f;
        // SelfModulate 0.5: one image at half opacity, the same grey where the squares overlap (Godot: 0.502 → 128).
        AssertPixel(image, (int)(115 * scale), (int)(40 * scale), 128, 128, 128, tolerance: 2);
        AssertPixel(image, (int)(135 * scale), (int)(40 * scale), 128, 128, 128, tolerance: 2);
        // Modulate 0.5 reaches the children too, and the composite applies it again (Godot: 0.251 → 64, overlap 0.3765 → 96).
        AssertPixel(image, (int)(15 * scale), (int)(40 * scale), 64, 64, 64, tolerance: 2);
        AssertPixel(image, (int)(35 * scale), (int)(40 * scale), 96, 96, 96, tolerance: 2);
        AssertPixel(image, (int)(5 * scale), (int)(5 * scale), 0, 0, 0);
    }

    [Fact]
    public void AnimatedSpritesPlayTheirFramesFlippedOffsetAndAtTheirSpeedsAndMatchGoldens()
    {
        // ADR 0223: frame 1 shows each sprite's first frame (the backwards one its last, the stopped one row 1's third);
        // by frame 15 (0.25 s at 10 fps, half way through a frame) "run" is on its third frame, "Fast" on its eighth
        // (wrapped to the fourth), "Backwards" on its second, "Durations" still on its first (it lasts 0.3 s).
        var result = HostRunner.Run("animated-sprite", Output("animated-sprite"), "--capture", "1,15", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 1);
        Gates.AssertMatchesGolden(result, 15);

        var first = Png.ReadRgba8(result.Captures.Single(c => c.Frame == 1).Path);
        var later = Png.ReadRgba8(result.Captures.Single(c => c.Frame == 15).Path);
        var scale = first.Width / 480f;
        // Inside "Plain" (centred at 60, 60, 64 px wide), away from the notch and ticks: frame 0's red, then frame 2's green.
        AssertPixel(first, (int)(60 * scale), (int)(55 * scale), 220, 60, 60);
        AssertPixel(later, (int)(60 * scale), (int)(55 * scale), 60, 200, 90);
        // "Flipped" mirrors both axes: the notch is in the bottom-right corner (16 px cells × 4: 4..20 px in from it).
        AssertPixel(first, (int)((160 + 32 - 10) * scale), (int)((60 + 32 - 10) * scale), 255, 255, 255);
        AssertPixel(first, (int)((160 - 32 + 10) * scale), (int)((60 - 32 + 10) * scale), 220, 60, 60);
        // "Durations" holds frame 0 (red); "Stopped" shows row 1's third cell (pink) in both.
        AssertPixel(later, (int)(160 * scale), (int)(175 * scale), 220, 60, 60);
        AssertPixel(first, (int)(260 * scale), (int)(175 * scale), 230, 120, 170);
        AssertPixel(later, (int)(260 * scale), (int)(175 * scale), 230, 120, 170);
    }

    [Fact]
    public void AThousandPlayingAnimatedSpritesAllocateNothing()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("animated-sprite", Output("animated-sprite-alloc"), "--count", "1000", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"1000 animated sprites allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TileMapLayersDrawFlipsAndYSortedRowsAndMatchGolden()
    {
        // G2b: a ground layer (a checker of two tiles and the notched tile under every transform) and a y-sorted 2× objects
        // layer whose tree rows sort with two sprites: one behind a tree, one in front of another.
        var result = HostRunner.Run("tiles", Output("tiles"), "--capture", "3", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 3);

        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 480f;
        // Row 1 (y 16…32), tile x 1 (16…32): the notch at the top left; x 3 (FlipH): at the top right.
        AssertPixel(image, (int)(19 * scale), (int)(18 * scale), 255, 255, 255);
        AssertPixel(image, (int)((48 + 16 - 3) * scale), (int)(18 * scale), 255, 255, 255);
        AssertPixel(image, (int)((48 + 3) * scale), (int)(18 * scale), 220, 60, 60);
        // The objects layer is scaled 2× at y + 40: the front sprite (x 88, its top 50 → screen 176…200, y 140…164) covers
        // the trunk of the tree at x 5 (screen 192…200); the trunk of the tree at x 2 (screen 76…84, y 136…168) hides the sprite behind it.
        AssertPixel(image, (int)(178 * scale), (int)(150 * scale), 60, 90, 230);
        AssertPixel(image, (int)(80 * scale), (int)(150 * scale), 130, 90, 50);
    }

    [Fact]
    public void PaddedTilesShowNoNeighbourAtAFractionalZoomWithLinearFiltering()
    {
        // G2b: one solid tile, surrounded by magenta tiles in the atlas, fills the view at zoom 1.37 with linear filtering.
        // Texture padding (each tile's own edge copied around it) keeps every pixel the tile's colour.
        var result = HostRunner.Run("tiles-seam", Output("tiles-seam"), "--capture", "3", "--size", "480x270", "--hidden");
        Gates.AssertValidationClean(result);
        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var worst = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
            worst = Math.Max(worst, Math.Max(Math.Abs(image.Pixels[i] - 40), Math.Max(Math.Abs(image.Pixels[i + 1] - 120), Math.Abs(image.Pixels[i + 2] - 200))));
        Assert.True(worst <= 2, $"a pixel is {worst} away from the tile's colour: a neighbouring tile bled in.");
    }

    [Fact]
    public void AScrolledLargeTileMapAllocatesNothing()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("tiles", Output("tiles-alloc"), "--count", "256", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"a 256 × 256 tile map allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    private static void AssertPixel(PngImage image, int x, int y, byte r, byte g, byte b, int tolerance = 1)
    {
        var i = (y * image.Width + x) * 4;
        Assert.True(Math.Abs(image.Pixels[i] - r) <= tolerance && Math.Abs(image.Pixels[i + 1] - g) <= tolerance && Math.Abs(image.Pixels[i + 2] - b) <= tolerance,
            $"Pixel ({x},{y}) is ({image.Pixels[i]}, {image.Pixels[i + 1]}, {image.Pixels[i + 2]}), expected ({r}, {g}, {b}).");
    }
}
