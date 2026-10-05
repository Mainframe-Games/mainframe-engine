using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// M4 shadows: cascades, PCF, every light type at once, cutout casters and stability under camera motion. Each run
/// is validation-clean; images are compared with goldens and measured.
/// </summary>
public class ShadowTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void CascadesCoverTheShadowDistanceAndMatchGoldens()
    {
        var result = HostRunner.Run("csm", Output("csm"), "--capture", $"{CsmScene.NormalFrame},{CsmScene.DebugFrame}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, CsmScene.NormalFrame);
        Gates.AssertMatchesGolden(result, CsmScene.DebugFrame);

        // The debug view tints the floor by cascade (red, green, blue, yellow from near to far): all four are on screen.
        var debug = Capture(result, CsmScene.DebugFrame);
        var (red, green, blue, yellow) = CountCascadeTints(debug);
        var total = debug.Width * debug.Height;
        foreach (var (name, count) in new[] { ("red", red), ("green", green), ("blue", blue), ("yellow", yellow) })
            Assert.True(count > total / 200, $"cascade tint {name} covers {count} of {total} pixels: that cascade is missing.");
    }

    // Floor pixels (below the horizon) whose colour is dominated by a cascade's debug tint.
    private static (int Red, int Green, int Blue, int Yellow) CountCascadeTints(PngImage image)
    {
        int red = 0, green = 0, blue = 0, yellow = 0;
        for (var y = (int)(image.Height * 0.44f); y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                float r = image.Pixels[i], g = image.Pixels[i + 1], b = image.Pixels[i + 2];
                if (r > 1.5f * g && r > 1.5f * b)
                    red++;
                else if (g > 1.3f * r && g > 1.3f * b)
                    green++;
                else if (b > 1.45f * r && b > 1.2f * g)
                    blue++;
                else if (r > 1.5f * b && g > 1.5f * b && MathF.Abs(r - g) < 0.25f * r)
                    yellow++;
            }
        }

        return (red, green, blue, yellow);
    }

    [Fact]
    public void PcfSoftensShadowEdges()
    {
        var soft = HostRunner.Run("shadow-pcf", Output("shadow-pcf"), "--capture", "8", "--hidden");
        var hard = HostRunner.Run("shadow-pcf", Output("shadow-pcf-hard"), "--capture", "8", "--count", "1", "--hidden");

        Gates.AssertValidationClean(soft);
        Gates.AssertValidationClean(hard);
        Gates.AssertMatchesGolden(soft, 8);

        // Penumbra: pixels between the shadow and the lit floor. The Poisson kernel spreads the edge over more pixels.
        var softPenumbra = PenumbraPixels(Capture(soft, 8));
        var hardPenumbra = PenumbraPixels(Capture(hard, 8));
        TestContext.Current.SendDiagnosticMessage($"Penumbra pixels: Poisson 16 {softPenumbra}, hard {hardPenumbra}");
        Assert.True(hardPenumbra > 0, "the hard edge was not found");
        Assert.True(softPenumbra > hardPenumbra * 1.3, $"PCF penumbra {softPenumbra} px is not wider than the hard edge's {hardPenumbra} px");
    }

    // Floor pixels clearly between the darkest (shadow) and brightest (lit) luminance of the image.
    private static int PenumbraPixels(PngImage image)
    {
        var lo = 255f;
        var hi = 0f;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var l = Luminance(image.Pixels, i);
            lo = MathF.Min(lo, l);
            hi = MathF.Max(hi, l);
        }

        var count = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var l = Luminance(image.Pixels, i);
            if (l > lo + (hi - lo) * 0.1f && l < hi - (hi - lo) * 0.1f && image.Pixels[i + 2] < image.Pixels[i] + 20) // not the blue box
                count++;
        }

        return count;
    }

    private static float Luminance(byte[] p, int i) => 0.2126f * p[i] + 0.7152f * p[i + 1] + 0.0722f * p[i + 2];

    [Fact]
    public void EveryLightTypeCastsShadowsAtOnce()
    {
        // GitHub issue #2: a sun (cascades), three spot lights (atlas) and two point lights (cubes), all shadowed.
        var result = HostRunner.Run("shadow-lights", Output("shadow-lights"), "--capture", "20", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void DevOverlayShadowsPanelIsValidationClean()
    {
        // The dev overlay's Shadows panel samples the cascade layers and the atlas in their read-only depth layout, also
        // across a swapchain rebuild (--resize). The panel's values refresh at 4 Hz, so the maps only appear after the
        // first refresh that sees the shadow system (~frame 16): resize and capture after it, in a window tall enough
        // for the maps to be on screen before the resize.
        var result = HostRunner.Run("shadow-lights", Output("shadow-lights-overlay"), "--count", "2", "--size", "500x1000", "--resize", "400x300@20", "--capture", "30", "--hidden");

        Assert.Empty(result.SceneCheckFailures);
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void ShadowsOfEveryLightTypeAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("shadow-lights", Output("shadow-lights-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Frames with 6 shadowed lights allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void CutoutMaterialsCastCutoutShadows()
    {
        var cutout = HostRunner.Run("shadow-cutout", Output("shadow-cutout"), "--capture", "10", "--hidden");
        var opaque = HostRunner.Run("shadow-cutout", Output("shadow-cutout-opaque"), "--capture", "10", "--count", "1", "--hidden");

        Gates.AssertValidationClean(cutout);
        Gates.AssertValidationClean(opaque);
        Gates.AssertMatchesGolden(cutout, 10);

        // The shadow lies on the floor in the lower half of the frame; the holes let light through.
        var holed = DarkPixelsInLowerHalf(Capture(cutout, 10));
        var solid = DarkPixelsInLowerHalf(Capture(opaque, 10));
        TestContext.Current.SendDiagnosticMessage($"Shadowed floor pixels: cutout {holed}, opaque {solid}");
        Assert.True(holed > 0, "the cutout fence casts no shadow");
        Assert.True(holed < solid * 0.85, $"the cutout shadow ({holed} px) should have holes compared with the opaque one ({solid} px)");
    }

    private static int DarkPixelsInLowerHalf(PngImage image)
    {
        var count = 0;
        for (var y = image.Height / 2; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (Luminance(image.Pixels, (y * image.Width + x) * 4) < 80f)
                    count++;
        return count;
    }

    [Fact]
    public void ShadowEdgesDoNotShimmerWhenTheCameraMoves()
    {
        // The camera moves sideways by exactly one screen pixel at the floor (less than a shadow texel) between frames
        // 10 and 20. With texel snapping, frame 20 is frame 10 shifted by one pixel; without it the shadow map slides
        // with the camera and edge pixels change (the test's own sensitivity check).
        var stable = HostRunner.Run("shadow-shimmer", Output("shadow-shimmer"), "--capture", "10,20", "--hidden");
        var unstable = HostRunner.Run("shadow-shimmer", Output("shadow-shimmer-unsnapped"), "--capture", "10,20", "--count", "1", "--hidden");

        Gates.AssertValidationClean(stable);
        Gates.AssertValidationClean(unstable);
        Gates.AssertMatchesGolden(stable, 10);

        var (stableMax, stableChanged) = ShiftedDifference(Capture(stable, 10), Capture(stable, 20));
        var (unstableMax, unstableChanged) = ShiftedDifference(Capture(unstable, 10), Capture(unstable, 20));
        TestContext.Current.SendDiagnosticMessage(
            $"Shimmer: snapped max delta {stableMax}, {stableChanged} px changed; unsnapped max delta {unstableMax}, {unstableChanged} px changed");

        var image = Capture(stable, 10);
        var pixels = image.Width * image.Height;
        Assert.True(stableMax <= 3 && stableChanged <= pixels / 2000,
            $"Snapped shadows changed under a sub-texel camera move: max delta {stableMax}, {stableChanged} pixels beyond ±2.");
        Assert.True(unstableChanged > pixels / 100 && unstableChanged > 20 * Math.Max(1, stableChanged),
            $"The unsnapped run changed only {unstableChanged} pixels: the test no longer detects shimmering.");
    }

    // Frame b against frame a shifted one pixel (the camera moved +X, so the floor moved one pixel left).
    private static (int Max, int Changed) ShiftedDifference(PngImage a, PngImage b)
    {
        var max = 0;
        var changed = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 1; x < a.Width - 1; x++)
            {
                var ia = (y * a.Width + x + 1) * 4;
                var ib = (y * b.Width + x) * 4;
                var delta = Math.Max(Math.Abs(a.Pixels[ia] - b.Pixels[ib]),
                    Math.Max(Math.Abs(a.Pixels[ia + 1] - b.Pixels[ib + 1]), Math.Abs(a.Pixels[ia + 2] - b.Pixels[ib + 2])));
                max = Math.Max(max, delta);
                if (delta > 2)
                    changed++;
            }
        }

        return (max, changed);
    }
}
