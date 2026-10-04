using MainframeEngine.RenderTests.Host;

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

    /// <summary>Layout points → capture pixels at the run's fixed content scale.</summary>
    private static int Px(HostResult result, int points) => (int)MathF.Round(points * result.ContentScale);

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
        Assert.Equal(HostOptions.CanonicalScale, result.ContentScale);
        Assert.Equal((Px(result, 1280), Px(result, 720)), (capture.Width, capture.Height));
        Gates.AssertMatchesGolden(result, capture.Frame);
    }

    [Fact]
    public void ProjectManagerMatchesGolden()
    {
        var result = HostRunner.RunEditor(Output("editor-project-manager"), null, "--smoke-golden", "project-manager", "--hidden", "--size", "1280x720");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        var capture = Assert.Single(result.Captures);
        Assert.Equal((Px(result, 1280), Px(result, 720)), (capture.Width, capture.Height));
        Gates.AssertMatchesGolden(result, capture.Frame);
    }

    [Fact]
    public void FileSystemPanelMatchesGolden()
    {
        // The template's content as a project in a fixed temp folder (no game code: nothing to build).
        var project = Path.Combine(Path.GetTempPath(), "mainframe-golden", "FsGame");
        if (Directory.Exists(project))
            Directory.Delete(project, recursive: true);
        CopyDirectory(Path.Combine(RenderTestEnvironment.RepositoryRoot, "Templates", "MainframeEngine.Templates", "content", "mfgame", "Content"),
            Path.Combine(project, "Content"));
        new ProjectSettings { Name = "FsGame", MainScene = "Content/Scenes/Main.mscene" }.Save(project);

        var result = HostRunner.RunEditor(Output("editor-filesystem"), null, "--smoke-golden", "filesystem", "--project", project,
            "--hidden", "--size", "1280x720");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        var capture = Assert.Single(result.Captures);
        Assert.Equal((Px(result, 1280), Px(result, 720)), (capture.Width, capture.Height));
        Gates.AssertMatchesGolden(result, capture.Frame);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    [Fact]
    public void SplashScreenMatchesGolden()
    {
        var result = HostRunner.RunEditor(Output("editor-splash"), null, "--smoke-splash", "--hidden", "--size", "960x600");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        var capture = Assert.Single(result.Captures);
        Assert.Equal((Px(result, 960), Px(result, 600)), (capture.Width, capture.Height));
        Gates.AssertMatchesGolden(result, 20);
    }
}
