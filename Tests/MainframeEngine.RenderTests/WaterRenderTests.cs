namespace MainframeEngine.RenderTests;

/// <summary>ADR 0159: <see cref="WaterMaterial3D"/> on a stream carved into a terrain, and a pond from its water layer.</summary>
public class WaterRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void ACarvedStreamAndAPondRenderCleanlyAndMatchGolden()
    {
        // Frame 30 at the fixed 60 Hz step: the flow phases are at a fixed time.
        var result = HostRunner.Run("water", Output("water"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void WaterFramesAllocateNothingWhileTheCameraOrbits()
    {
        const int warmup = 150, measured = 240;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "water", Output("water-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Water frames allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }
}
