namespace MainframeEngine.RenderTests;

/// <summary>ADR 0151: vertex streams, <see cref="MultiMesh"/>, visibility ranges and <see cref="FoliageMaterial3D"/>.</summary>
public class ForestFeatureTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void VertexColoursMultiplyTheAlbedoAndMatchGolden()
    {
        var result = HostRunner.Run("vertex-colors", Output("vertex-colors"), "--capture", "10", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void AThousandMultiMeshInstancesDrawInOneDrawAndMatchGolden()
    {
        var result = HostRunner.Run("multimesh", Output("multimesh"), "--capture", "20", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void FiftyThousandMultiMeshInstancesAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("multimesh", Output("multimesh-50k-alloc"), "--count", "50000", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"50k-instance multimesh frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FiftyThousandMultiMeshInstancesCostAlmostNoCpu()
    {
        // The CPU cost of a frame does not grow with the instances of a static multimesh (one item, one draw per pass):
        // 50,000 instances cost the same CPU time per frame as one, within 1 ms. Enforced on Release builds on a GPU device
        // (as the 10k-instance bar); other runs report the numbers.
        var many = HostRunner.Run("multimesh", Output("multimesh-50k-perf"), "--count", "50000", "--perf", "60:240", "--no-validation", "--hidden");
        var one = HostRunner.Run("multimesh", Output("multimesh-1-perf"), "--count", "1", "--perf", "60:240", "--no-validation", "--hidden");

        Assert.True(many.SceneCheckFailures.Count == 0, string.Join("\n", many.SceneCheckFailures));
        Assert.True(one.SceneCheckFailures.Count == 0, string.Join("\n", one.SceneCheckFailures));
        TestContext.Current.SendDiagnosticMessage(
            $"multimesh ({many.Configuration}, {many.DeviceName}): CPU {many.AverageCpuFrameMs:0.00} ms per frame with 50k instances, " +
            $"{one.AverageCpuFrameMs:0.00} ms with 1; wall clock {many.AverageFrameMs:0.00} / {one.AverageFrameMs:0.00} ms; {many.MeshDrawCalls} draws");
        Assert.True(many.MeshDrawCalls <= 4, $"{many.MeshDrawCalls} draws for 50k instances in one multimesh.");
        if (many.Configuration == "Release" && !many.IsCpuDevice)
            Assert.True(many.AverageCpuFrameMs - one.AverageCpuFrameMs < 1.0,
                $"50k multimesh instances cost {many.AverageCpuFrameMs:0.00} ms of CPU per frame, 1 instance {one.AverageCpuFrameMs:0.00} ms.");
    }

    [Fact]
    public void FoliageSwaysInTheWindAndMatchesGolden()
    {
        // t = 90 / 60 s under a fixed wind; the still run (--count 1: no wind) must differ (the leaves and their shadows moved).
        var windy = HostRunner.Run("foliage-wind", Output("foliage-wind"), "--capture", "90", "--hidden");
        var still = HostRunner.Run("foliage-wind", Output("foliage-still"), "--capture", "90", "--count", "1", "--hidden");

        Gates.AssertValidationClean(windy);
        Gates.AssertValidationClean(still);
        Gates.AssertMatchesGolden(windy, 90);
        var comparison = ImageComparison.Compare(Png.ReadRgba8(windy.Captures[0].Path), Png.ReadRgba8(still.Captures[0].Path), channelTolerance: 8);
        Assert.True(comparison.DifferingPixels > comparison.TotalPixels / 100,
            $"Only {comparison.DifferingPixels} of {comparison.TotalPixels} pixels moved with the wind on.");
    }

    [Fact]
    public void FoliageSteadyStateAllocatesNothing()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("foliage-wind", Output("foliage-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Foliage frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}
