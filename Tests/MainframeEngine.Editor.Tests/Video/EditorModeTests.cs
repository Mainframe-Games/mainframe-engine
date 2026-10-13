using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>A minimal video-mode tab for mode tests before VideoTab exists (Task 10 adds the real one).</summary>
internal sealed class FakeVideoTab(string name) : IEditorTab
{
    public string? FilePath => null;
    public string DisplayName => name;
    public string Title => name;
    public bool IsDirty => false;
    public string IconClasses => "icon icon-video";
    public string Tooltip => name;
    public EditorMode Mode => EditorMode.Video;
    public void Dispose() { }
}

/// <summary>
/// Editor modes: the Game|Video switch swaps the layout, the panels, the menus, the toolbar and the tab strip, remembers
/// each mode's active tab, follows the active tab's mode, and persists across restarts.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class EditorModeTests : IDisposable
{
    // Created on first use: RmlUi allows one UiServer at a time, so tests that start their own editor never touch it.
    private HeadlessEditor? _editor;

    private HeadlessEditor Editor => _editor ??= new HeadlessEditor();

    private EditorWorkspace W => Editor.Workspace;

    public void Dispose() => _editor?.Dispose();

    [Fact]
    public void TheEditorStartsInGameMode()
    {
        Assert.Equal(EditorMode.Game, W.Mode);
        Assert.True(W.SceneTree.Visible);
        Assert.False(W.VideoPanel.Visible);
    }

    [Fact]
    public void VideoModeHidesTheGamePanelsAndShowsTheVideoPanel()
    {
        W.SetMode(EditorMode.Video);
        Editor.Tick();
        Assert.False(W.SceneTree.Visible);
        Assert.False(W.FileSystem.Visible);
        Assert.False(W.Inspector.Visible);
        Assert.False(W.OutputPanel.Visible);
        Assert.True(W.VideoPanel.Visible);
        Assert.Equal(default, W.Layout.SceneTree);
        Assert.Equal(0, W.Layout.Viewport.X);
        Assert.Equal(W.Layout.MenuBar.Width, W.Layout.Viewport.Width);
    }

    [Fact]
    public void SwitchingBackRestoresTheGameLayout()
    {
        var before = W.Layout.SceneTree;
        W.SetMode(EditorMode.Video);
        W.SetMode(EditorMode.Game);
        Assert.Equal(before, W.Layout.SceneTree);
        Assert.True(W.SceneTree.Visible);
        Assert.False(W.VideoPanel.Visible);
    }

    [Fact]
    public void VideoModeHasNoActiveTabWithoutAVideoTab()
    {
        W.SetMode(EditorMode.Video);
        Assert.Null(W.Session.ActiveTab);
        Assert.Empty(W.ViewportPanel.VisibleTabs);
    }

    [Fact]
    public void ActivatingAVideoTabSwitchesToVideoMode()
    {
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Same(video, W.Session.ActiveTab);
    }

    [Fact]
    public void SwitchingModesKeepsEachModesActiveTab()
    {
        var scene = W.Session.ActiveTab!;
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        W.SetMode(EditorMode.Game);
        Assert.Same(scene, W.Session.ActiveTab);
        W.SetMode(EditorMode.Video);
        Assert.Same(video, W.Session.ActiveTab);
    }

    [Fact]
    public void TheTabStripShowsOnlyTheModesTabs()
    {
        W.Session.NewScene();
        W.Session.Add(new FakeVideoTab("run.mfcut"));
        Assert.Single(W.ViewportPanel.VisibleTabs);
        W.SetMode(EditorMode.Game);
        Assert.Equal(2, W.ViewportPanel.VisibleTabs.Count);
    }

    [Fact]
    public void ClosingAFilteredTabClosesThatTab()
    {
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        W.ViewportPanel.CloseAt(0); // the first *visible* tab: the video tab, not the session's first (a scene)
        Assert.DoesNotContain(video, W.Session.Tabs);
        Assert.Single(W.Session.Tabs);
    }

    [Fact]
    public void TheMenusFollowTheMode()
    {
        Assert.Contains(W.Commands.MenuItems("file"), i => i.Command == "file.new");
        W.SetMode(EditorMode.Video);
        var file = W.Commands.MenuItems("file");
        Assert.Contains(file, i => i.Command == "video.export");
        Assert.DoesNotContain(file, i => i.Command == "file.new");
        Assert.Contains(W.Commands.MenuItems("video"), i => i.Command == "video.transcribe");
    }

    [Fact]
    public void TheModeIsSavedAndRestoredAtStartUp()
    {
        var directory = Directory.CreateTempSubdirectory("mf-editor-mode").FullName;
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            new EditorSettings { LastMode = EditorMode.Video }.Save(settings);
            Assert.Equal(EditorMode.Video, EditorSettings.Load(settings).LastMode);

            using var editor = new HeadlessEditor(configure: o => o with { EditorSettingsPath = settings, ShowProjectManager = true });
            Assert.Equal(EditorMode.Video, editor.Workspace.Mode);
            Assert.False(editor.Workspace.ProjectManager.Visible);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SpaceInVideoModeWithoutATabDoesNothingQuietly()
    {
        W.SetMode(EditorMode.Video);
        Assert.Null(W.Session.ActiveTab);
        var warnings = W.Output.Messages.Count(m => m.Text.Contains("Unknown command", StringComparison.Ordinal));
        Editor.Key(Key.Space);
        Editor.Key(Key.Space);
        Editor.Tick(2);
        Assert.Equal(warnings, W.Output.Messages.Count(m => m.Text.Contains("Unknown command", StringComparison.Ordinal)));
        Assert.True(W.Commands.Execute("video.play")); // known, and nothing to do

        Assert.False(W.Commands.Execute("video.no_such_command")); // the warning this test looks for does reach the output
        Editor.Tick();
        Assert.Equal(warnings + 1, W.Output.Messages.Count(m => m.Text.Contains("Unknown command", StringComparison.Ordinal)));
    }

    [Fact]
    public void SwitchingModesKeepsTheAutosaveCountdown()
    {
        W.ApplySettings(new EditorSettings { AutosaveMinutes = 1 });
        Editor.Tick(30);
        var elapsed = W.AutosaveElapsed;
        Assert.True(elapsed > 0.4);

        W.SetMode(EditorMode.Video);
        W.SetMode(EditorMode.Game);
        Assert.True(W.AutosaveElapsed >= elapsed, $"{W.AutosaveElapsed} < {elapsed}: the countdown restarted");
        Assert.Equal(EditorMode.Game, W.Settings.LastMode);
    }

    [Fact]
    public void StartingInTheSavedModeDoesNotRewriteTheSettings()
    {
        var directory = Directory.CreateTempSubdirectory("mf-editor-mode").FullName;
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            const string Written = """{"lastMode":"video"}""";
            File.WriteAllText(settings, Written); // not how Save formats it: any write would show
            using (var editor = new HeadlessEditor(configure: o => o with { EditorSettingsPath = settings }))
            {
                Assert.Equal(EditorMode.Video, editor.Workspace.Mode);
                Assert.Equal(Written, File.ReadAllText(settings));

                editor.Workspace.SetMode(EditorMode.Game); // a change is still saved
            }

            Assert.Equal(EditorMode.Game, EditorSettings.Load(settings).LastMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheProjectManagerOpensTheVideoEditorWithoutAProject()
    {
        using var editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true });
        Assert.True(editor.Workspace.ProjectManager.Visible);
        editor.Workspace.Commands.Execute("editor.video_editor");
        Assert.False(editor.Workspace.ProjectManager.Visible);
        Assert.Equal(EditorMode.Video, editor.Workspace.Mode);
    }

    [Fact]
    public void GameModeWithoutAProjectOrAGameTabOpensTheProjectManager()
    {
        using var editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true });
        var w = editor.Workspace;
        w.Commands.Execute("editor.video_editor");
        Assert.False(w.ProjectManager.Visible);

        w.SetMode(EditorMode.Game); // nothing to show: no project, no scene
        Assert.True(w.ProjectManager.Visible);
    }

    [Fact]
    public void GameModeWithAGameTabDoesNotOpenTheProjectManager()
    {
        W.SetMode(EditorMode.Video);
        W.SetMode(EditorMode.Game); // the untitled scene is there
        Assert.False(W.ProjectManager.Visible);
    }

    [Theory]
    [InlineData(Key.O, EditorModifiers.Command, "file.open")]
    [InlineData(Key.S, EditorModifiers.Command | EditorModifiers.Shift, "file.save_as")]
    [InlineData(Key.Z, EditorModifiers.Command | EditorModifiers.Shift, "edit.redo")]
    [InlineData(Key.E, EditorModifiers.Command, "video.export")]
    [InlineData(Key.Backspace, EditorModifiers.None, "edit.delete")]
    [InlineData(Key.W, EditorModifiers.None, null)]   // the gizmo keys are game-only
    [InlineData(Key.F5, EditorModifiers.None, null)]  // so is Play
    public void VideoShortcuts(Key key, EditorModifiers modifiers, string? command) =>
        Assert.Equal(command, EditorWorkspace.VideoShortcutFor(key, modifiers));
}
