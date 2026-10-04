using MainframeEngine.RenderTests.Host;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)] // one GPU, one window at a time

namespace MainframeEngine.RenderTests;

public class SceneTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void LitShapesRenderCleanlyAndMatchGoldens()
    {
        var result = HostRunner.Run("lit-shapes", Output("lit-shapes"), "--capture", "1,60", "--hidden");

        Assert.Equal(2, result.Captures.Count);
        Assert.All(result.Captures, c => Assert.Equal(c.Width * 3, c.Height * 4)); // 4:3 window, any DPI scale
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 1);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void SpineRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("spine", Output("spine"), "--capture", "60", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void SandboxSteadyStateAllocatesNothing()
    {
        const int warmup = 120, measured = 300;
        var result = HostRunner.Run("sandbox", Output("sandbox"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Steady-state frames allocated {result.AllocatedBytes} managed bytes over {measured} frames " +
            $"(~{result.AllocatedBytes / (double)measured:0.#} B/frame); per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void CapturesAreDeterministicAcrossRuns()
    {
        var first = HostRunner.Run("lit-shapes", Output("determinism-a"), "--capture", "30", "--hidden");
        var second = HostRunner.Run("lit-shapes", Output("determinism-b"), "--capture", "30", "--hidden");

        var a = Png.ReadRgba8(first.Captures[0].Path);
        var b = Png.ReadRgba8(second.Captures[0].Path);
        var comparison = ImageComparison.Compare(a, b, channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs of the same frame differ in {comparison.DifferingPixels} pixels (fixed timestep should make them identical).");
    }
}
