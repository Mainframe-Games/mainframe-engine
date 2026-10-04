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
    public void MultipleShadowCastingLightsEachUseTheirOwnMatrix()
    {
        // Directional + spot + point (6 cube faces): 8 shadow sub-passes in one frame.
        var result = HostRunner.Run("multi-light", Output("multi-light"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void SpineRendersWithoutAShadowSystem()
    {
        var result = HostRunner.Run("spine-no-shadows", Output("spine-no-shadows"), "--capture", "60", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void SwapchainRecreationOnResizeAndVSyncToggleIsClean()
    {
        var result = HostRunner.Run("lit-shapes", Output("recreate"),
            "--resize", "400x300@10", "--toggle-vsync", "20", "--capture", "5,40", "--hidden");

        var before = result.Captures.Single(c => c.Frame == 5);
        var after = result.Captures.Single(c => c.Frame == 40);
        Assert.Equal(before.Width * 400 / 320, after.Width); // same DPI scale, new size
        Assert.Equal(before.Height * 300 / 240, after.Height);
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void QuitWithErrorReturnsErrorExitCode()
    {
        var result = HostRunner.RunExpectingExit((int)ExitCode.Error, "lit-shapes", Output("exit-code"),
            "--quit-error", "3", "--frames", "100", "--hidden");

        Assert.Equal((int)ExitCode.Error, result.ExitCode);
        Assert.InRange(result.RenderedFrames, 2, 3); // closes after the render of the update that quit
        Gates.AssertValidationClean(result);
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
