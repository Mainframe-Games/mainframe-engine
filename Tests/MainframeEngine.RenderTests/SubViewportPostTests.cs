using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0169: a post-processed <see cref="SubViewport"/> (the editor's scene view). The <c>subviewport-post</c> scene checks
/// itself on the view's own image: with post-processing the scene is graded (grey) and vignetted while the debug and overlay
/// lines keep their exact colours; without it the view is the engine tonemap alone; with TAA the view's history accumulates
/// and the overlay does not jitter; the view's stack is the main view's built-in list. Goldens for on and off; 0 B per frame
/// with TAA, SSAO and glow; validation-clean throughout.
/// </summary>
public class SubViewportPostTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void APostProcessedViewGradesTheSceneButNotItsDebugLines()
    {
        var result = HostRunner.Run("subviewport-post", Output("subviewport-post"), "--capture", $"{SubViewportPostScene.CheckFrame}", "--frames", "40", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
        Gates.AssertMatchesGolden(result, SubViewportPostScene.CheckFrame);
    }

    [Fact]
    public void WithoutPostProcessingTheViewIsTonemappedOnly()
    {
        // Frame 9 (not 8): its own golden, the same scene with SubViewport.PostProcessing off.
        var result = HostRunner.Run("subviewport-post", Output("subviewport-post-off"), "--count", "1", "--capture", "9", "--frames", "40", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
        Gates.AssertMatchesGolden(result, 9);
    }

    [Fact]
    public void TaaInAViewAccumulatesWithoutJitteringTheOverlay()
    {
        var result = HostRunner.Run("subviewport-post", Output("subviewport-post-taa"), "--count", "2", "--frames", "40", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
    }

    [Fact]
    public void APostProcessedViewAllocatesNothingPerFrame()
    {
        // TAA (jitter, prepass, history), SSAO, glow, auto exposure's chain, the grade and the overlay pass, with the camera
        // orbiting so every pass runs each frame.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("subviewport-post", Output("subviewport-post-alloc"), "--count", "3", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"A post-processed sub-viewport allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}
