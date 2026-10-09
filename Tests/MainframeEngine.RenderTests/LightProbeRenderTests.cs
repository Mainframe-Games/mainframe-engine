using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0170: light probes. The <c>probes</c> scene (a roofed shed open towards the camera, a sunlit red wall, a box probe
/// volume baked at load) checks itself: the shed's floor is darker than open floor by the ratio the bake predicts, alike
/// without the volume, and the floor by the red wall is redder (bounce light); the sunlit frame matches its golden; the
/// occlusion tint colours the shade; the probes, SSAO's bent normals, the contact shadows and the volumetric fog's
/// probe-darkened ambient together allocate nothing per frame. Every run is validation-clean.
/// </summary>
public class LightProbeRenderTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void TheShedIsDarkAndTheRedWallBleeds()
    {
        var result = HostRunner.Run("probes", Output("probes"), "--capture", "10", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void TheShedDarkensByTheBakedVisibility()
    {
        var result = HostRunner.Run("probes", Output("probes-sky"), "--capture", "10", "--count", "1", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
    }

    [Fact]
    public void AnOcclusionTintColoursTheShade()
    {
        var result = HostRunner.Run("probes", Output("probes-tint"), "--capture", "10", "--count", "4", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
    }

    [Fact]
    public void WithoutAVolumeTheShedShadesLikeOpenFloor()
    {
        var result = HostRunner.Run("probes", Output("probes-none"), "--capture", "10", "--count", "2", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
    }

    [Fact]
    public void ProbesWithSsaoContactShadowsAndFogAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("probes", Output("probes-alloc"), "--count", "3", "--jitter", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Probes with SSAO, contact shadows and volumetric fog allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}
