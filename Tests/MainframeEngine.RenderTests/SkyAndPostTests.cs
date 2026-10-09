using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>ADR 0154: the physical sky, FXAA and auto exposure. Every run is validation-clean; frames match goldens.</summary>
public class SkyAndPostTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void PhysicalSkyAtNoonAndSunsetMatchesGoldens()
    {
        // Different frames, so the two goldens have different names.
        var noon = HostRunner.Run("sky-physical", Output("sky-physical-noon"), "--capture", "5", "--hidden");
        var sunset = HostRunner.Run("sky-physical", Output("sky-physical-sunset"), "--capture", "6", "--count", "1", "--hidden");

        Gates.AssertValidationClean(noon);
        Gates.AssertValidationClean(sunset);
        AssertSelfChecks(noon);
        AssertSelfChecks(sunset);
        Gates.AssertMatchesGolden(noon, 5);
        Gates.AssertMatchesGolden(sunset, 6);

        // The horizon is at about 65 % of the height (the camera looks 7° up).
        var day = Capture(noon, 5);
        var zenith = Pixel(day, 0.5f, 0.05f);
        var horizon = Pixel(day, 0.5f, 0.6f);
        TestContext.Current.SendDiagnosticMessage($"Noon: top {zenith}, near the horizon {horizon}");
        Assert.True(zenith.B > zenith.R + 40, $"the noon sky is not blue overhead: {zenith}");
        Assert.True(horizon.B - horizon.R < zenith.B - zenith.R, $"the horizon is not paler than the sky above: {horizon} vs {zenith}");
        Assert.True(Luminance(horizon) > Luminance(zenith), $"the horizon is not brighter than the sky above: {horizon} vs {zenith}");

        var dusk = Capture(sunset, 6);
        var glow = Pixel(dusk, 0.5f, 0.62f);
        var high = Pixel(dusk, 0.5f, 0.05f);
        TestContext.Current.SendDiagnosticMessage($"Sunset: near the horizon {glow}, top {high}");
        Assert.True(glow.R > glow.B + 60, $"the sunset horizon is not orange: {glow}");
        Assert.True(Luminance(glow) > Luminance(high) + 40, $"the sky above the sunset is not darker than the horizon: {high} vs {glow}");
    }

    [Fact]
    public void PhysicalSkyFxaaAndAutoExposureAllocateNothingPerFrame()
    {
        // A sun that moves every frame (the sky-view LUT re-renders), auto exposure, glow and FXAA.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("sky-physical", Output("sky-physical-alloc"), "--count", "2", "--alloc", $"{warmup}:{measured}", "--hidden");

        AssertSelfChecks(result);
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"The physical sky with FXAA and auto exposure allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FxaaSmoothsEdgesButNotTheOverlay()
    {
        var fxaa = HostRunner.Run("fxaa", Output("fxaa"), "--capture", "5", "--hidden");
        var none = HostRunner.Run("fxaa", Output("fxaa-off"), "--capture", "5", "--count", "1", "--hidden");

        Gates.AssertValidationClean(fxaa);
        Gates.AssertValidationClean(none);
        Gates.AssertMatchesGolden(fxaa, 5);

        var smooth = Capture(fxaa, 5);
        var aliased = Capture(none, 5);

        // Without AA every pixel is the white box or the background; FXAA blends the oblique edges.
        var blendedWith = GreyPixelsBetween(smooth, 20, 200);
        var blendedWithout = GreyPixelsBetween(aliased, 20, 200);
        TestContext.Current.SendDiagnosticMessage($"Blended edge pixels: FXAA {blendedWith}, none {blendedWithout}");
        Assert.True(blendedWith > 200 && blendedWith > 10 * (blendedWithout + 1), $"FXAA blended {blendedWith} edge pixels (none: {blendedWithout})");

        // The gizmo square is drawn after FXAA: exact inside, and nothing of it bleeds outside.
        var scale = smooth.Width / 320;
        var (min, max) = FxaaScene.GizmoRect;
        int x0 = (int)min.X * scale, y0 = (int)min.Y * scale, x1 = (int)max.X * scale - 1, y1 = (int)max.Y * scale - 1;
        foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1), ((x0 + x1) / 2, (y0 + y1) / 2) })
            Assert.Equal((255, 0, 0), PixelAt(smooth, x, y));
        foreach (var (x, y) in new[] { (x0 - 1, (y0 + y1) / 2), (x1 + 1, (y0 + y1) / 2), ((x0 + x1) / 2, y0 - 1), ((x0 + x1) / 2, y1 + 1) })
            Assert.Equal(PixelAt(aliased, x, y), PixelAt(smooth, x, y));
    }

    [Fact]
    public void AutoExposureBrightensADarkSceneOverTime()
    {
        var before = AutoExposureScene.DimFrame - 1;
        var after = AutoExposureScene.DimFrame + 1;
        const uint adapted = 80;
        var result = HostRunner.Run("auto-exposure", Output("auto-exposure"), "--capture", $"{before},{after},{adapted}", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, adapted);

        var bright = MeanLuminance(Capture(result, before));
        var dark = MeanLuminance(Capture(result, after));
        var recovered = MeanLuminance(Capture(result, adapted));
        TestContext.Current.SendDiagnosticMessage($"Mean luminance: lit {bright:F1}, right after the light dims {dark:F1}, {adapted - after} frames later {recovered:F1}");
        Assert.True(dark < bright * 0.5f, $"dimming the light did not darken the frame ({bright:F1} → {dark:F1})");
        Assert.True(recovered > dark * 1.6f, $"auto exposure did not brighten the dark scene ({dark:F1} → {recovered:F1})");
        Assert.True(recovered < bright, $"auto exposure overshot ({recovered:F1} vs the lit {bright:F1})");
    }

    [Fact]
    public void HistogramHighlightProtectionKeepsABrightPatchBelowClipping()
    {
        // ADR 0177: a view that is mostly shade with a bright patch. Average mode exposes for the shade and clips the
        // patch; histogram mode with highlight protection exposes the patch to the white target (0.8 → ≈ 190 of 255).
        const uint frame = 5;
        var histogram = HostRunner.Run("auto-exposure-histogram", Output("auto-exposure-histogram"), "--capture", $"{frame}", "--hidden");
        var average = HostRunner.Run("auto-exposure-average", Output("auto-exposure-average"), "--capture", $"{frame}", "--hidden");

        Gates.AssertValidationClean(histogram);
        Gates.AssertValidationClean(average);
        Gates.AssertMatchesGolden(histogram, frame);

        var (min, max) = AutoExposureHistogramScene.PatchUv;
        var centre = (min + max) * 0.5f;
        var protectedPatch = Pixel(Capture(histogram, frame), centre.X, centre.Y);
        var clippedPatch = Pixel(Capture(average, frame), centre.X, centre.Y);
        var protectedShade = Pixel(Capture(histogram, frame), 0.25f, 0.5f);
        var averageShade = Pixel(Capture(average, frame), 0.25f, 0.5f);
        TestContext.Current.SendDiagnosticMessage(
            $"Patch: histogram {protectedPatch}, average {clippedPatch}; shade: histogram {protectedShade}, average {averageShade}");

        Assert.True(Math.Min(clippedPatch.R, Math.Min(clippedPatch.G, clippedPatch.B)) >= 248, $"average mode did not clip the patch (the ACES fit only nears 255): {clippedPatch}");
        Assert.True(Math.Max(protectedPatch.R, Math.Max(protectedPatch.G, protectedPatch.B)) <= 235, $"the protected patch clipped: {protectedPatch}");
        Assert.True(Luminance(protectedPatch) >= 150, $"the protected patch is too dark: {protectedPatch}");
        Assert.True(Luminance(averageShade) > Luminance(protectedShade), $"protection did not lower the exposure ({averageShade} vs {protectedShade})");
        Assert.True(Luminance(protectedShade) > 5, $"the shade went black: {protectedShade}");
    }

    [Fact]
    public void HistogramAutoExposureAllocatesNothingPerFrame()
    {
        const int warmup = 30, measured = 120;
        var result = HostRunner.Run("auto-exposure-histogram", Output("auto-exposure-histogram-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Histogram auto exposure allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    private static (int R, int G, int B) Pixel(PngImage image, float u, float v) =>
        PixelAt(image, (int)(u * (image.Width - 1)), (int)(v * (image.Height - 1)));

    private static (int R, int G, int B) PixelAt(PngImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    private static float Luminance((int R, int G, int B) p) => 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;

    private static float MeanLuminance(PngImage image)
    {
        double sum = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
            sum += 0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2];
        return (float)(sum / (image.Width * image.Height));
    }

    private static int GreyPixelsBetween(PngImage image, int low, int high)
    {
        var count = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            int r = image.Pixels[i], g = image.Pixels[i + 1], b = image.Pixels[i + 2];
            if (r == g && g == b && g > low && g < high)
                count++;
        }

        return count;
    }
}
