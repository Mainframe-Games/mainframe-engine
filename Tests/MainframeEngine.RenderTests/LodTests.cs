using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>G6.4: mesh levels of detail by projected error. Validation-clean, self-checked, matching golden, allocation-free.</summary>
public class LodTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    [Fact]
    public void LevelsFollowDistanceAndMatchGolden()
    {
        // Spheres receding from the camera draw coarser levels (and cast shadows at them); a low LodBias forces a coarse
        // level near the camera; a distant plane draws its index-only level; one sphere is past its visibility range.
        var result = HostRunner.Run("lod", Output("lod"), "--capture", "6", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 6);
    }

    [Fact]
    public void LodFramesAllocateNothing()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("lod", Output("lod-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"LOD frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}
