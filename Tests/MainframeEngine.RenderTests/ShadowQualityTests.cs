using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// G8e.2 shadow quality (ADR 0167): PCSS from the sun's angular size, screen-space contact shadows, staggered cascade
/// caching and the far shadow. Each run is validation-clean; the measurements are self-checks, the goldens catch drift.
/// </summary>
public class ShadowQualityTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static float Luminance(byte[] p, int i) => 0.2126f * p[i] + 0.7152f * p[i + 1] + 0.0722f * p[i + 2];

    [Fact]
    public void PcssPenumbraWidensWithDistanceFromTheBlocker()
    {
        var soft = HostRunner.Run("shadow-pcss", Output("shadow-pcss"), "--capture", "8", "--hidden");
        var fixedRadius = HostRunner.Run("shadow-pcss", Output("shadow-pcss-fixed"), "--capture", "8", "--count", "1", "--hidden");
        Gates.AssertValidationClean(soft);
        Gates.AssertValidationClean(fixedRadius);
        Gates.AssertMatchesGolden(soft, 8);

        // The plank's shadow (left of the split: two edges, 6.5 m below the plank) and the wall's (right: one edge, 1 m
        // below the wall's top).
        var (plank, wall) = PenumbraWidths(Capture(soft, 8));
        var (plankFixed, wallFixed) = PenumbraWidths(Capture(fixedRadius, 8));
        TestContext.Current.SendDiagnosticMessage(
            $"Penumbra width per edge (px): PCSS plank {plank:F1}, wall {wall:F1}; fixed radius plank {plankFixed:F1}, wall {wallFixed:F1}");
        Assert.True(wall > 0f && wallFixed > 0f, "the wall's shadow edge was not found");
        Assert.True(plank > 2f * wall, $"PCSS: the plank's penumbra ({plank:F1} px) is not much wider than the wall's ({wall:F1} px)");
        Assert.True(plankFixed < 1.6f * wallFixed, $"fixed radius: the plank's penumbra ({plankFixed:F1} px) should match the wall's ({wallFixed:F1} px)");
        Assert.True(plank > 2f * plankFixed, $"PCSS did not widen the plank's penumbra ({plank:F1} px vs {plankFixed:F1} px)");
    }

    // Average penumbra width per edge, in pixels, of the floor's luminance profile (rows of the middle third averaged):
    // the plank's two edges left of ShadowPcssScene.Split, the wall shadow's far edge right of it (the blue wall is
    // skipped).
    private static (float Plank, float Wall) PenumbraWidths(PngImage image)
    {
        var profile = new float[image.Width];
        var wall = new bool[image.Width];
        int y0 = image.Height / 3, y1 = image.Height * 2 / 3;
        for (var x = 0; x < image.Width; x++)
        {
            float sum = 0f;
            for (var y = y0; y < y1; y++)
            {
                var i = (y * image.Width + x) * 4;
                sum += Luminance(image.Pixels, i);
                wall[x] |= image.Pixels[i + 2] > image.Pixels[i] + 30;
            }

            profile[x] = sum / (y1 - y0);
        }

        float lo = float.MaxValue, hi = 0f;
        for (var x = 0; x < image.Width; x++)
        {
            if (wall[x])
                continue;
            lo = MathF.Min(lo, profile[x]);
            hi = MathF.Max(hi, profile[x]);
        }

        int plank = 0, wallEdge = 0;
        for (var x = 0; x < image.Width; x++)
        {
            if (wall[x] || profile[x] <= lo + (hi - lo) * 0.1f || profile[x] >= hi - (hi - lo) * 0.1f)
                continue;
            if (x < ShadowPcssScene.Split * image.Width)
                plank++;
            else
                wallEdge++;
        }

        return (plank / 2f, wallEdge);
    }

    [Fact]
    public void ASmallObjectGainsAContactShadow()
    {
        var on = HostRunner.Run("contact-shadows", Output("contact-shadows"), "--capture", "8", "--hidden");
        var off = HostRunner.Run("contact-shadows", Output("contact-shadows-off"), "--capture", "8", "--count", "1", "--hidden");
        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        Gates.AssertMatchesGolden(on, 8);

        // The pebbles cast no shadow-map shadow: only the contact shadows darken the floor beside them.
        var image = Capture(on, 8);
        var reference = Capture(off, 8);
        int darker = 0, lighter = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var delta = Luminance(reference.Pixels, i) - Luminance(image.Pixels, i);
            if (delta > 20f)
                darker++;
            else if (delta < -4f)
                lighter++;
        }

        TestContext.Current.SendDiagnosticMessage($"Pixels darkened by contact shadows: {darker}; lightened: {lighter}");
        Assert.True(darker > 300, $"contact shadows darkened only {darker} pixels next to the pebbles");
        Assert.True(lighter < 20, $"contact shadows lightened {lighter} pixels (they only darken)");
    }

    [Fact]
    public void CachedCascadesMatchFreshOnesForAStillCamera()
    {
        // Frames 20–23 cover all four phases of the schedule (cascades 2 and 3 are 1–3 frames old in some of them).
        var cached = HostRunner.Run("shadow-staggered", Output("shadow-staggered"), "--capture", "20,21,22,23", "--hidden");
        var fresh = HostRunner.Run("shadow-staggered", Output("shadow-staggered-fresh"), "--capture", "20", "--count", "1", "--hidden");
        Assert.True(cached.SceneCheckFailures.Count == 0, string.Join("\n", cached.SceneCheckFailures));
        Assert.True(fresh.SceneCheckFailures.Count == 0, string.Join("\n", fresh.SceneCheckFailures));
        Gates.AssertValidationClean(cached);
        Gates.AssertValidationClean(fresh);
        Gates.AssertMatchesGolden(cached, 20);

        var reference = Capture(fresh, 20);
        foreach (var frame in new uint[] { 20, 21, 22, 23 })
        {
            // A still camera: a cached cascade is the fresh one (its sphere is a little larger: a slightly wider filter).
            var comparison = ImageComparison.Compare(reference, Capture(cached, frame), channelTolerance: 8);
            TestContext.Current.SendDiagnosticMessage($"Frame {frame}: {comparison.DifferingPercent:F3} % of pixels differ from every-frame cascades (max Δ {comparison.MaxChannelDelta})");
            Assert.True(comparison.DifferingPercent < 0.5, $"frame {frame}: {comparison.DifferingPercent:F3} % of pixels differ from fresh cascades");
            var stable = ImageComparison.Compare(Capture(cached, 20), Capture(cached, frame), channelTolerance: 1);
            Assert.True(stable.DifferingPixels == 0, $"frame {frame} differs from frame 20 in {stable.DifferingPixels} pixels: a still camera must not change");
        }
    }

    [Fact]
    public void CachedCascadesKeepUpWithAMovingCamera()
    {
        // 8 m/s along the posts: the staggered frames (cascades up to 3 frames old, grown spheres) against every-frame ones
        // at the same camera positions show no gap and no missing shadow.
        uint[] frames = [40, 41, 42, 43];
        var capture = string.Join(',', frames);
        var cached = HostRunner.Run("shadow-staggered", Output("shadow-staggered-walk"), "--capture", capture, "--count", "2", "--hidden");
        var fresh = HostRunner.Run("shadow-staggered", Output("shadow-staggered-walk-fresh"), "--capture", capture, "--count", "3", "--hidden");
        Assert.True(cached.SceneCheckFailures.Count == 0, string.Join("\n", cached.SceneCheckFailures));
        Assert.True(fresh.SceneCheckFailures.Count == 0, string.Join("\n", fresh.SceneCheckFailures));
        Gates.AssertValidationClean(cached);

        foreach (var frame in frames)
        {
            var comparison = ImageComparison.Compare(Capture(fresh, frame), Capture(cached, frame), channelTolerance: 12);
            TestContext.Current.SendDiagnosticMessage($"Frame {frame}: {comparison.DifferingPercent:F3} % of pixels differ (max Δ {comparison.MaxChannelDelta})");
            Assert.True(comparison.DifferingPercent < 1.0, $"frame {frame}: {comparison.DifferingPercent:F3} % of pixels differ from fresh cascades");
        }
    }

    [Fact]
    public void ShadowQualityFeaturesAllocateNothingPerFrame()
    {
        // Staggered cascades, PCSS with TAA's rotation (--jitter), contact shadows, coarse casters and the far shadow,
        // with the camera walking.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("shadow-staggered", Output("shadow-quality-alloc"), "--count", "4", "--jitter", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"G8e.2 shadows allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TheFarShadowShadowsThePlainBeyondTheCascades()
    {
        var far = HostRunner.Run("far-shadow", Output("far-shadow"), "--capture", "10", "--hidden");
        var none = HostRunner.Run("far-shadow", Output("far-shadow-off"), "--capture", "10", "--count", "1", "--hidden");
        Assert.True(far.SceneCheckFailures.Count == 0, string.Join("\n", far.SceneCheckFailures));
        Assert.True(none.SceneCheckFailures.Count == 0, string.Join("\n", none.SceneCheckFailures));
        Gates.AssertValidationClean(far);
        Gates.AssertValidationClean(none);
        Gates.AssertMatchesGolden(far, 10);

        // The plain between the cascades' end (40 m) and the ridge: the rows just below the horizon.
        var image = Capture(far, 10);
        var reference = Capture(none, 10);
        int darker = 0, total = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                var delta = Luminance(reference.Pixels, i) - Luminance(image.Pixels, i);
                if (Math.Abs(delta) > 1f)
                    total++;
                if (delta > 25f)
                    darker++;
            }
        }

        TestContext.Current.SendDiagnosticMessage($"Pixels the far shadow darkens: {darker} (changed: {total})");
        Assert.True(darker > image.Width * image.Height / 50, $"the ridge's far shadow darkened only {darker} pixels");
    }
}
