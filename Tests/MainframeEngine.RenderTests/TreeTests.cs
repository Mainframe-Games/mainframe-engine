namespace MainframeEngine.RenderTests;

/// <summary>ADR 0158: <see cref="Tree3D"/> in both styles and a <see cref="TreeScatter"/> forest.</summary>
public class TreeTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void RealisticTreesSwayInTheWindAndMatchGolden()
    {
        // An oak and a pine at level 0, t = 90 / 60 s under a fixed wind; the still run (--count 1) must differ: leaves,
        // branches and their shadows moved.
        var windy = HostRunner.Run("tree-realistic", Output("tree-realistic"), "--capture", "90", "--hidden");
        var still = HostRunner.Run("tree-realistic", Output("tree-realistic-still"), "--capture", "90", "--count", "1", "--hidden");

        Assert.True(windy.SceneCheckFailures.Count == 0, string.Join("\n", windy.SceneCheckFailures));
        Gates.AssertValidationClean(windy);
        Gates.AssertValidationClean(still);
        Gates.AssertMatchesGolden(windy, 90);
        var comparison = ImageComparison.Compare(Png.ReadRgba8(windy.Captures[0].Path), Png.ReadRgba8(still.Captures[0].Path), channelTolerance: 8);
        Assert.True(comparison.DifferingPixels > comparison.TotalPixels / 100,
            $"Only {comparison.DifferingPixels} of {comparison.TotalPixels} pixels moved with the wind on.");
    }

    [Fact]
    public void LowPolyTreesMatchGolden()
    {
        var result = HostRunner.Run("tree-lowpoly", Output("tree-lowpoly"), "--capture", "90", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 90);
    }

    [Fact]
    public void SmallForestDrawsOneLevelPerChunkAndMatchesGolden()
    {
        var result = HostRunner.Run("tree-forest", Output("tree-forest"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void FlyingThroughTwoThousandTreesAllocatesNothingPerFrame()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("tree-forest", Output("tree-forest-alloc"), "--count", "2000", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Forest frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FlyingThroughTwoThousandTreesCostsLittleCpu()
    {
        // 2,000 trees over 256 m (three species, two seeds each, 32 m chunks; 120 without sun shadows on a CPU device such as lavapipe): the
        // CPU cost of a frame is the batches' culling and draws, a few ms at most (Release on a GPU device). Reports the
        // GPU-bound frame time too.
        var result = HostRunner.Run("tree-forest", Output("tree-forest-perf"), "--count", "2000", "--perf", "60:240", "--no-validation", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        TestContext.Current.SendDiagnosticMessage(
            $"tree-forest 2000 ({result.Configuration}, {result.DeviceName}): frame {result.AverageFrameMs:0.00} ms " +
            $"(p95 {result.P95FrameMs:0.00}), CPU {result.AverageCpuFrameMs:0.00} ms (p95 {result.P95CpuFrameMs:0.00}), shadow GPU " +
            $"{result.ShadowGpuMs:0.00} ms; {result.MeshDrawCalls} draws, {result.MeshShadowDrawCalls} shadow draws");
        if (result.Configuration == "Release" && !result.IsCpuDevice)
            Assert.True(result.AverageCpuFrameMs < 4.0, $"The forest costs {result.AverageCpuFrameMs:0.00} ms of CPU per frame.");
    }
}
