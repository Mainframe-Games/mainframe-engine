using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>ADR 0150: PBR shading, the sky's image-based lighting and fog. Validation-clean, matching goldens, allocation-free.</summary>
public class PbrTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    [Fact]
    public void PbrSpheresUnderTheSkyMatchGolden()
    {
        // Metallic 1 / 0.5 / 0 rows × roughness 0 … 1 columns, lit by a sun and the captured procedural sky.
        var result = HostRunner.Run("pbr", Output("pbr"), "--capture", "10", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void FogMatchesGolden()
    {
        // Distance + height fog with sun scatter on lit surfaces, and the sky fading into it at the horizon.
        var result = HostRunner.Run("fog", Output("fog"), "--capture", "10", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void PbrFramesWithAMovingSunAllocateNothing()
    {
        // The sun turns every frame: the sky lighting re-bakes every few frames inside the measured window.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("pbr", Output("pbr-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"PBR + sky lighting frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}
