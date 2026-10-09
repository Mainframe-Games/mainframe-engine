using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0173: water that reads the scene behind it. <c>water-refraction</c> bends a striped bed (self-checked against the
/// bed alone and the unbent pool; golden), <c>water-ssr</c> reflects a red pillar only with screen-space reflections
/// (self-checked; golden; again under TAA, and in a post-processed sub-viewport), <c>water-fall</c> draws a River3D fall
/// with its jet, spray and caustics (self-checked; golden). Every run is validation-clean; refraction under TAA with an orbiting camera and the fall
/// allocate nothing per frame.
/// </summary>
public class WaterSceneTextureTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void RefractionBendsTheStripedBed()
    {
        var result = HostRunner.Run("water-refraction", Output("water-refraction"), "--capture", "10,20,30", "--hidden");
        AssertSelfChecks(result);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void ScreenSpaceReflectionsShowThePillar()
    {
        var result = HostRunner.Run("water-ssr", Output("water-ssr"), "--capture", "20,30", "--hidden");
        AssertSelfChecks(result);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void ScreenSpaceReflectionsSurviveTheTaaReactiveMask()
    {
        var result = HostRunner.Run("water-ssr", Output("water-ssr-taa"), "--capture", "20,30", "--count", "1", "--hidden");
        AssertSelfChecks(result);
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void APostProcessedSubViewportRefractsAndReflectsToo()
    {
        // The editor's case: the view's own scene copy and resumed pass, with its post stack and TAA.
        var result = HostRunner.Run("water-ssr", Output("water-ssr-subviewport"), "--capture", "20,30", "--frames", "40", "--count", "2", "--hidden");
        AssertSelfChecks(result);
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void AFallHasItsJetSprayAndCaustics()
    {
        var result = HostRunner.Run("water-fall", Output("water-fall"), "--capture", "30", "--hidden");
        AssertSelfChecks(result);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Theory]
    [InlineData("water-refraction")]
    [InlineData("water-fall")]
    public void RefractingWaterAllocatesNothingPerFrame(string scene)
    {
        const int warmup = 150, measured = 240;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            scene, Output($"{scene}-alloc"), "--alloc", $"{warmup}:{measured}", "--aa", "taa", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"{scene} allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }
}
