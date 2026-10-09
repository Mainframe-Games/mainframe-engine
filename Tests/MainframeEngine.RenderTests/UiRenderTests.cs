using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// The game UI (RmlUi via <see cref="VulkanUiRenderer"/>): goldens for a HUD over the 3D scene, effects (clip masks,
/// transforms, filters, gradients), text and the widget library, exact sRGB compositing after the tonemap, the
/// validation gate and determinism. The allocation gate covers the UI through the <c>showcase</c> scene.
/// </summary>
public class UiRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void HudOverTheSceneMatchesGoldenWithExactSrgbColours()
    {
        var result = HostRunner.Run("ui-hud", Output("ui-hud"), "--capture", "30", "--size", "480x270", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);

        // The opaque swatch (#3366cc) sits 8dp from the top-right, after the 50% and rounded swatches; the UI is
        // composited after the tonemap, so its sRGB value reaches the swapchain unchanged.
        var capture = result.Captures.Single();
        var image = Png.ReadRgba8(capture.Path);
        var dp = image.Width / 480f;
        var x = (int)((480 - 8 - 3 * 34 + 4 + 15) * dp);
        var y = (int)((8 + 15) * dp);
        var i = (y * image.Width + x) * 4;
        var c = UiHudScene.SwatchColor;
        Assert.True(Math.Abs(image.Pixels[i] - c[0]) <= 1 && Math.Abs(image.Pixels[i + 1] - c[1]) <= 1 && Math.Abs(image.Pixels[i + 2] - c[2]) <= 1,
            $"Swatch pixel ({x},{y}) is ({image.Pixels[i]}, {image.Pixels[i + 1]}, {image.Pixels[i + 2]}), expected #3366cc exactly.");

        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void ScaleWithScreenSizeKeepsTheUiProportionalAcrossWindowSizes()
    {
        // ADR 0181: a document authored for 640×360, captured at 320×180 points and again after a resize to 640×360
        // (× the canonical scale in pixels: 0.5× then 1× on lavapipe, 1× then 2× on moltenvk). The UI covers the same
        // fraction of the frame at both sizes: the box's edges land at the same relative positions.
        var result = HostRunner.Run("ui-scaling", Output("ui-scaling"), "--size", "320x180", "--resize", "640x360@12", "--capture", "10,24", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        var small = Png.ReadRgba8(result.Captures.Single(c => c.Frame == 10).Path);
        var large = Png.ReadRgba8(result.Captures.Single(c => c.Frame == 24).Path);
        Assert.Equal((small.Width * 2, small.Height * 2), (large.Width, large.Height));

        foreach (var image in (PngImage[])[small, large])
        {
            var scale = image.Height / UiScalingScene.ReferenceHeight;
            bool IsBox(float dpX, float dpY)
            {
                var x = (int)(dpX * scale);
                var y = (int)(dpY * scale);
                var i = ((y * image.Width) + x) * 4;
                var c = UiScalingScene.BoxColor;
                return Math.Abs(image.Pixels[i] - c[0]) <= 2 && Math.Abs(image.Pixels[i + 1] - c[1]) <= 2 && Math.Abs(image.Pixels[i + 2] - c[2]) <= 2;
            }

            const float x0 = UiScalingScene.BoxX, y0 = UiScalingScene.BoxY;
            const float x1 = x0 + UiScalingScene.BoxWidth, y1 = y0 + UiScalingScene.BoxHeight;
            Assert.True(IsBox(x0 + 3, y0 + 3) && IsBox(x1 - 3, y1 - 3), $"{image.Width}x{image.Height}: the box is not inside its scaled rectangle.");
            Assert.False(IsBox(x0 - 3, y0 + 10) || IsBox(x1 + 3, y0 + 10) || IsBox(x0 + 10, y1 + 3),
                $"{image.Width}x{image.Height}: the box spills outside its scaled rectangle.");
        }

        Gates.AssertMatchesGolden(result, 10);
        Gates.AssertMatchesGolden(result, 24);
    }

    [Fact]
    public void ImagesArePreloadedWithTheirDocument()
    {
        var result = HostRunner.Run("ui-preload", Output("ui-preload"), "--frames", "32", "--size", "320x240", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void ClipMasksTransformsFiltersAndGradientsMatchGolden()
    {
        var result = HostRunner.Run("ui-effects", Output("ui-effects"), "--capture", "10", "--size", "480x270", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void ARegionLayerIsOffsetAndClippedWithItsEffectsIntact()
    {
        var result = HostRunner.Run("ui-region", Output("ui-region"), "--capture", "10", "--size", "480x270", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void TextMatchesGolden()
    {
        var result = HostRunner.Run("ui-text", Output("ui-text"), "--capture", "5", "--size", "400x300", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 5);
    }

    [Fact]
    public void WidgetLibraryMatchesGolden()
    {
        var result = HostRunner.Run("ui-widgets", Output("ui-widgets"), "--capture", "10", "--size", "520x440", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void UiCapturesAreDeterministicAcrossRuns()
    {
        var first = HostRunner.Run("ui-effects", Output("ui-determinism-a"), "--capture", "12", "--size", "480x270", "--hidden");
        var second = HostRunner.Run("ui-effects", Output("ui-determinism-b"), "--capture", "12", "--size", "480x270", "--hidden");

        var a = Png.ReadRgba8(first.Captures[0].Path);
        var b = Png.ReadRgba8(second.Captures[0].Path);
        var comparison = ImageComparison.Compare(a, b, channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs of the same UI frame differ in {comparison.DifferingPixels} pixels.");
    }

    [Fact]
    public void HiddenUiStillRendersTheLayersItSavesIntoTextures()
    {
        // Box-shadows are rendered once into a saved texture and then cached by RmlUi: while the UI is hidden that
        // texture must still get its content, or the shadows stay empty once the UI is shown (review regression).
        var result = HostRunner.Run("ui-effects", Output("ui-hidden"), "--ui-hidden-until", "9", "--capture", "10", "--size", "480x270", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void RendersWithoutAnUpdateRecompositeTheLastUi()
    {
        // Updates at 30 Hz, renders unthrottled: renders between updates must not replay a list whose temporary
        // geometry was already released (review regression); the UI keeps showing and validation stays clean.
        var result = HostRunner.Run("ui-effects", Output("ui-update-rate"), "--update-rate", "30", "--capture", "10", "--frames", "100000",
            "--size", "480x270", "--hidden");

        Assert.Single(result.Captures);
        Assert.True(result.RenderedFrames > 10, $"{result.RenderedFrames} renders for 10 updates: expected more renders than updates.");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void UiSurvivesSwapchainRecreation()
    {
        // Resizing recreates the swapchain and the UI's layer targets; the HUD must keep rendering cleanly.
        var result = HostRunner.Run("ui-hud", Output("ui-hud-resize"), "--resize", "400x300@10", "--capture", "5,30", "--hidden");

        Assert.Equal(2, result.Captures.Count);
        Gates.AssertValidationClean(result);
    }
}
