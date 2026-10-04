namespace MainframeEngine.RenderTests;

/// <summary>
/// The editor in a real (hidden) window on the GPU, through its <c>--smoke</c> run: Sandbox.mscene open, a mesh selected by
/// GPU picking at its projected pixel, a property changed through the inspector model, undo/redo, save, reload and
/// compare; the editor window golden; frame times on the scene; and the allocation gate (0 B per idle frame with a
/// 1 000-node scene). Also the splash screen golden.
/// </summary>
public class EditorRenderTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static string SandboxScene =>
        Path.Combine(RenderTestEnvironment.RepositoryRoot, "MainframeEngine.Sandbox", "Content", "Scenes", "Sandbox.mscene");

    [Fact]
    public void SmokeRunPicksEditsUndoesSavesReloadsAndAllocatesNothingWhenIdle()
    {
        var result = HostRunner.RunEditor(Output("editor"),
            new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "--smoke-scene", SandboxScene, "--hidden", "--size", "1280x720");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Assert.True(result.AllocatedBytes == 0,
            $"Idle editor frames with 1 000 nodes allocated {result.AllocatedBytes} managed bytes over {result.MeasuredFrames} frames.");
        Assert.True(result.PerfMeasuredFrames > 0);
        var capture = Assert.Single(result.Captures);
        Gates.AssertMatchesGolden(result, capture.Frame);
    }

    [Fact]
    public void SplashScreenMatchesGolden()
    {
        var result = HostRunner.RunEditor(Output("editor-splash"), null, "--smoke-splash", "--hidden", "--size", "960x600");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }
}
