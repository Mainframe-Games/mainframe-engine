using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// <see cref="GpuParticles3D"/> (ADR 0206): a campfire (additive HDR flames and embers that glow, lit blended smoke sorted
/// back to front) and a rain box of velocity-aligned streaks, deterministic, validation-clean, allocation-free per frame
/// and inside the 120 fps budget of issue #120.
/// </summary>
public class ParticleTests
{
    private const uint Frame = 30;

    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == Frame).Path);

    [Fact]
    public void CampfireMatchesGoldenAndItsEmissiveFlamesBloom()
    {
        var glow = HostRunner.Run("particles-campfire", Output("particles-campfire"), "--capture", $"{Frame}", "--hidden");
        var noGlow = HostRunner.Run("particles-campfire", Output("particles-campfire-noglow"), "--capture", $"{Frame}", "--count", "1", "--hidden");

        Gates.AssertValidationClean(glow);
        Gates.AssertValidationClean(noGlow);
        Gates.AssertMatchesGolden(glow, Frame);

        // The flames are bright and warm in the middle of the frame.
        var image = Capture(glow);
        var flame = WarmBrightPixels(image);
        Assert.True(flame > image.Width * image.Height / 200, $"only {flame} bright flame pixels");

        // HDR flames feed the glow: with it, the dark air around the fire lights up.
        var halo = Luminance(image, ring: true);
        var plain = Luminance(Capture(noGlow), ring: true);
        TestContext.Current.SendDiagnosticMessage($"Flame pixels {flame}; luminance around the fire: glow {halo:0}, none {plain:0}");
        Assert.True(halo > plain * 1.04, $"the glow adds too little around the fire ({halo:0} vs {plain:0})");
    }

    [Fact]
    public void CampfireDrawsWithTheDepthPrepassAndTaa()
    {
        // ADR 0163: particles are not in the prepass (blended) and mark TAA's reactive mask; they still draw over the
        // prepassed scene and the frame stays validation-clean.
        var result = HostRunner.Run("particles-campfire", Output("particles-campfire-taa"), "--capture", $"{Frame}", "--count", "2", "--hidden");

        Gates.AssertValidationClean(result);
        var image = Capture(result);
        var flame = WarmBrightPixels(image);
        Assert.True(flame > image.Width * image.Height / 200, $"only {flame} bright flame pixels with TAA");
    }

    [Fact]
    public void CampfireIsDeterministic()
    {
        var a = Capture(HostRunner.Run("particles-campfire", Output("particles-campfire-a"), "--capture", $"{Frame}", "--hidden"));
        var b = Capture(HostRunner.Run("particles-campfire", Output("particles-campfire-b"), "--capture", $"{Frame}", "--hidden"));

        var comparison = ImageComparison.Compare(a, b, channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs differ in {comparison.DifferingPixels} pixels (fixed seeds and the fixed step should make them identical).");
    }

    [Fact]
    public void RainBoxMatchesGolden()
    {
        var result = HostRunner.Run("particles-rain", Output("particles-rain"), "--capture", $"{Frame}", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, Frame);

        // Pale streaks over the dark ground (the lower half of the frame).
        var image = Capture(result);
        var streaks = 0;
        for (var y = image.Height / 2; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                if (image.Pixels[i + 2] > 110 && image.Pixels[i + 2] >= image.Pixels[i])
                    streaks++;
            }

        TestContext.Current.SendDiagnosticMessage($"Rain pixels over the ground: {streaks}");
        Assert.True(streaks > image.Width * image.Height / 200, $"only {streaks} rain pixels over the ground");
    }

    [Fact]
    public void ParticleBudgetAllocatesNothingPerFrame()
    {
        const int warmup = 90, measured = 180;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "particles-stress", Output("particles-stress-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"20 × 200 particles + a 4,000-particle rain box allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void ParticleBudgetHoldsTheFrameRate()
    {
        // Issue #120: 20 emitters × 200 particles plus a 4,000-particle rain box inside a 120 fps frame on an M-series Mac
        // (Release). VSync and validation off; MoltenVK still paces frames to the display (120 Hz on a ProMotion screen),
        // which also caps the measured CPU time (it includes the drawable wait), so the gate compares the budget scene with
        // an almost empty one on the same machine: in Release on a GPU device the particles must not cost frames. Their CPU
        // cost on its own is ParticleBenchmarks (≈0.1 ms simulation step + ≈0.4 ms sorted instance build on an M5).
        var budget = HostRunner.Run("particles-stress", Output("particles-stress-perf"), "--perf", "60:300", "--no-validation", "--hidden");
        var empty = HostRunner.Run("lit-shapes", Output("particles-stress-baseline"), "--perf", "60:300", "--no-validation", "--hidden");

        Assert.Equal(300, budget.PerfMeasuredFrames);
        TestContext.Current.SendDiagnosticMessage(
            $"Particle budget ({budget.Configuration}, {budget.DeviceName}, {budget.DeviceType}): wall clock " +
            $"{budget.AverageFrameMs:0.00} ms average, {budget.P95FrameMs:0.00} ms p95 (empty scene {empty.AverageFrameMs:0.00} ms); " +
            $"CPU {budget.AverageCpuFrameMs:0.00} ms average, {budget.P95CpuFrameMs:0.00} ms p95");
        Assert.True(budget.AverageCpuFrameMs > 0, "The CPU frame time was not measured.");
        if (budget.Configuration != "Release" || budget.IsCpuDevice)
            return;
        Assert.True(budget.AverageFrameMs < Math.Max(1000.0 / 120, empty.AverageFrameMs * 1.1),
            $"The particle budget averages {budget.AverageFrameMs:0.00} ms per frame on {budget.DeviceName} " +
            $"(an almost empty scene: {empty.AverageFrameMs:0.00} ms; 8.33 ms is 120 fps).");
    }

    // Bright, warm (red > blue) pixels: the flames.
    private static int WarmBrightPixels(PngImage image)
    {
        var count = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            if (p[i] > 200 && p[i + 1] > 120 && p[i] > p[i + 2] + 40)
                count++;
        return count;
    }

    // Summed luminance of the frame, or with `ring` of the band around the fire (a box around the centre minus its core).
    private static double Luminance(PngImage image, bool ring)
    {
        double sum = 0;
        int cx = image.Width / 2, cy = image.Height * 3 / 5;
        int outer = image.Height * 2 / 5, inner = image.Height / 6;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                int dx = Math.Abs(x - cx), dy = Math.Abs(y - cy);
                if (ring && (Math.Max(dx, dy) > outer || Math.Max(dx, dy) < inner))
                    continue;
                var i = (y * image.Width + x) * 4;
                sum += 0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2];
            }

        return sum;
    }
}
