namespace MainframeEngine.RenderTests;

/// <summary>
/// G8d.15, <c>forest-mini</c> (<see cref="Host.Scenes.ForestMiniScene"/>): the Forest showcase's frame in miniature from
/// engine assets only: a splat-textured Realistic terrain, six Ez Tree trees with G8e.5's foliage, grass, a carved stream,
/// light probes, the physical sky, height and volumetric fog, histogram auto exposure, staggered PCSS sun shadows and TAAU.
/// </summary>
public class ForestMiniTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void ForestMiniRendersCleanlyAndMatchesGolden()
    {
        // Frame 90: 1.5 s of wind (a fixed phase), auto exposure settled, the staggered cascades all rendered.
        var result = HostRunner.Run("forest-mini", Output("forest-mini"), "--capture", "90", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 90);
    }

    [Fact]
    public void ForestMiniFramesAllocateNothingWhileTheCameraWalks()
    {
        // One lap of the glade (150 frames) as warm-up, so every tree level, impostor and grass tile has been shown, then two
        // laps measured. On a CPU device (lavapipe) the walk runs without sun shadows (the scene's doc comment).
        const int warmup = (int)Host.Scenes.ForestMiniScene.LapFrames + 30, measured = 300;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "forest-mini", Output("forest-mini-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"forest-mini frames allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }
}
