using MainframeEngine.Editor.Music;
using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Editor.Tests.Video;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// Music mode: songs open in it over the whole window (no docks), with the transport in the toolbar and its own menus;
/// without a song it lists the project's songs (or, with no project, points to one); songs belong to the project, so a
/// project switch asks about and closes them; the mode persists across restarts.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class MusicModeTests : IDisposable
{
    private readonly TestProject _project = new("Music");
    private readonly HeadlessEditor _editor;

    public MusicModeTests()
    {
        _editor = new HeadlessEditor(configure: o => o with
        {
            InitialProject = _project.Root,
            GameBuilder = new FakeGameBuilder(),
            GameLauncher = new FakeGameLauncher(),
        });
        _editor.Tick(3);
    }

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose()
    {
        _editor.Dispose();
        _project.Dispose();
    }

    private SongTab OpenSong(string name = "Theme")
    {
        var path = SongTab.CreateFile(_project.Abs($"Content/Music/{name}.msong"));
        W.Commands.OpenSong(path);
        _editor.Tick(3);
        return Assert.IsType<SongTab>(W.Session.ActiveTab);
    }

    private bool ToolbarClass(string id, string css) => W.Toolbar.Document.GetElementById(id).IsClassSet(css);

    private void TickUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition());
    }

    [Fact]
    public void OpeningASongSwitchesToMusicModeOverTheWholeWindow()
    {
        Assert.Equal(EditorMode.Game, W.Mode);
        var tab = OpenSong();

        Assert.Equal(EditorMode.Music, W.Mode);
        Assert.False(W.SceneTree.Visible);
        Assert.False(W.FileSystem.Visible);
        Assert.False(W.Inspector.Visible);
        Assert.False(W.OutputPanel.Visible);
        Assert.False(W.VideoPanel.Visible);
        Assert.True(W.SongPanel.Visible);
        Assert.Equal(default, W.Layout.SceneTree);
        Assert.Equal(0, W.Layout.Viewport.X);
        Assert.Equal(W.Layout.MenuBar.Width, W.Layout.Viewport.Width);
        Assert.True(W.SongPanel.ArrangeRect.Width > W.Layout.Viewport.Width * 0.6f);
        Assert.Same(tab, W.SongView.Tab);
        Assert.Same(tab, Assert.Single(W.ViewportPanel.VisibleTabs));
        Assert.Equal(EditorMode.Music, W.Settings.LastMode);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void SwitchingToGameKeepsTheSongTabAndBringsItBack()
    {
        var scene = W.Session.Scenes[0];
        var tab = OpenSong();

        W.SetMode(EditorMode.Game);
        _editor.Tick(2);
        Assert.Same(scene, W.Session.ActiveTab);
        Assert.DoesNotContain(tab, W.ViewportPanel.VisibleTabs);
        Assert.Contains(tab, W.Session.Tabs);
        Assert.True(W.SceneTree.Visible);
        Assert.False(W.SongPanel.Visible);
        Assert.Null(W.SongView.Tab);

        W.SetMode(EditorMode.Music);
        _editor.Tick(2);
        Assert.Same(tab, W.Session.ActiveTab);
        Assert.Same(tab, W.SongView.Tab);
        Assert.DoesNotContain(scene, W.ViewportPanel.VisibleTabs);
    }

    [Fact]
    public void TheToolbarAndMenusFollowMusicMode()
    {
        W.SetMode(EditorMode.Music);
        _editor.Tick(2);
        Assert.False(ToolbarClass("music-tools", "hidden"));
        Assert.True(ToolbarClass("game-tools", "hidden"));
        Assert.True(ToolbarClass("video-tools", "hidden"));
        Assert.True(ToolbarClass("mode-music", "active"));
        Assert.False(ToolbarClass("mode-game", "active"));
        Assert.True(ToolbarClass("song-play", "disabled")); // no song yet
        Assert.False(W.MenuBar.Document.GetElementById("menu-song").IsClassSet("hidden"));
        Assert.True(W.MenuBar.Document.GetElementById("menu-project").IsClassSet("hidden"));
        Assert.True(W.MenuBar.Document.GetElementById("menu-run").IsClassSet("hidden"));

        var file = W.Commands.MenuItems("file");
        Assert.Contains(file, i => i.Command == "file.new_song" && i.Enabled);
        Assert.Contains(file, i => i.Command == "song.render" && !i.Enabled);
        Assert.DoesNotContain(file, i => i.Command is "file.new" or "project.new");
        Assert.Contains(W.Commands.MenuItems("song"), i => i.Command == "song.play");
        Assert.Contains(W.Commands.MenuItems("view"), i => i.Command == "editor.mode.game");

        W.SetMode(EditorMode.Game);
        Assert.Contains(W.Commands.MenuItems("view"), i => i.Command == "editor.mode.music");
        Assert.True(ToolbarClass("music-tools", "hidden"));
        Assert.True(W.MenuBar.Document.GetElementById("menu-song").IsClassSet("hidden"));
    }

    [Fact]
    public void TheModeTogglesThumbMovesTowardsTheNewMode()
    {
        Assert.False(ToolbarClass("mode-thumb", "at-music"));
        Assert.False(ToolbarClass("mode-thumb", "to-right")); // placed at load, not animated

        W.SetMode(EditorMode.Music);
        Assert.True(ToolbarClass("mode-thumb", "at-music"));
        Assert.True(ToolbarClass("mode-thumb", "to-right"));

        W.SetMode(EditorMode.Video);
        Assert.True(ToolbarClass("mode-thumb", "at-video"));
        Assert.False(ToolbarClass("mode-thumb", "at-music"));
        Assert.True(ToolbarClass("mode-thumb", "to-right"));

        W.SetMode(EditorMode.Game);
        Assert.False(ToolbarClass("mode-thumb", "at-video"));
        Assert.True(ToolbarClass("mode-thumb", "to-left"));
        Assert.False(ToolbarClass("mode-thumb", "to-right"));
    }

    [Fact]
    public void TheToolbarTransportShowsTheSong()
    {
        var tab = OpenSong();
        Assert.False(ToolbarClass("song-play", "disabled"));
        Assert.False(ToolbarClass("song-pos", "empty"));
        Assert.Equal("001.1.000", W.Toolbar.Document.GetElementById("song-pos").InnerRml);
        Assert.Equal("120", W.Toolbar.Document.GetElementById("song-tempo").Value);
        Assert.Equal("4 / 4", W.Toolbar.Document.GetElementById("song-sig").InnerRml);
        Assert.False(ToolbarClass("song-loop", "active"));

        _editor.Key(Key.L); // the song tab's own key, before the music shortcuts
        _editor.Tick(2);
        Assert.True(tab.Document.Song.Loop.Enabled);
        Assert.True(ToolbarClass("song-loop", "active"));

        Assert.True(W.Commands.Execute("song.loop")); // the toolbar's command
        _editor.Tick(2);
        Assert.False(tab.Document.Song.Loop.Enabled);
        Assert.False(ToolbarClass("song-loop", "active"));

        tab.Document.SetTempo(96, "test");
        _editor.Tick(2);
        Assert.Equal("96", W.Toolbar.Document.GetElementById("song-tempo").Value);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void WithoutASongTheEmptyStateListsTheProjectsSongs()
    {
        var older = SongTab.CreateFile(_project.Abs("Content/Music/Older.msong"));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        var newer = SongTab.CreateFile(_project.Abs("Content/Music/Sub/Newer.msong"));

        W.SetMode(EditorMode.Music);
        _editor.Tick(3);
        Assert.Null(W.Session.ActiveTab);
        Assert.True(W.SongPanel.Visible);
        Assert.Equal(default, W.SongPanel.ArrangeRect);
        Assert.Equal([newer, older], W.SongPanel.RecentSongs);
        Assert.Contains(W.Commands.MenuItems("file"), i => i.Command == "song.recent:1" && i.Label == "Older");

        Assert.True(W.Commands.Execute("song.recent:1"));
        _editor.Tick(3);
        var tab = Assert.IsType<SongTab>(W.Session.ActiveTab);
        Assert.Equal(older, tab.FilePath);
        Assert.Equal(EditorMode.Music, W.Mode);
    }

    [Fact]
    public void ClosingTheLastSongStaysInMusicModeOnTheEmptyState()
    {
        var tab = OpenSong();
        W.Commands.Execute("file.close");
        _editor.Tick(3);
        Assert.DoesNotContain(tab, W.Session.Tabs);
        Assert.Equal(EditorMode.Music, W.Mode);
        Assert.Null(W.Session.ActiveTab);
        Assert.True(W.SongPanel.Visible);
        Assert.Contains(tab.FilePath!, W.SongPanel.RecentSongs);
    }

    [Fact]
    public void TheTransportShowsNothingActiveAfterTheLastSongCloses()
    {
        var tab = OpenSong();
        tab.Controller.ToggleLoop();
        _editor.Tick(2);
        Assert.True(ToolbarClass("song-loop", "active"));

        W.Session.Close(tab);
        _editor.Tick(2);
        Assert.False(ToolbarClass("song-loop", "active"));
        Assert.True(ToolbarClass("song-loop", "disabled"));
        Assert.True(ToolbarClass("song-pos", "empty"));
    }

    [Fact]
    public void ViewOutputTogglesTheMusicModesOutputOnItsOwn()
    {
        W.SetMode(EditorMode.Music);
        Assert.False(W.OutputPanel.Visible);
        Assert.Equal(default, W.Layout.Output);

        W.Commands.Execute("view.output");
        Assert.True(W.OutputPanel.Visible);
        Assert.True(W.Layout.Settings.MusicShowOutput);
        Assert.False(W.Layout.Settings.VideoShowOutput);
        Assert.True(W.Layout.Output.Height > 0);
        Assert.True(W.Layout.SplitterRect(Splitter.Bottom).Width > 0);
        Assert.Equal(default, W.Layout.SplitterRect(Splitter.Left));
        Assert.True(EditorLayout.Load(Path.Combine(_editor.Directory, "layout.json")).MusicShowOutput);

        W.SetMode(EditorMode.Video);
        Assert.False(W.OutputPanel.Visible); // video mode's own setting is still off
        W.SetMode(EditorMode.Game);
        Assert.True(W.OutputPanel.Visible);
    }

    [Fact]
    public void ClosingTheProjectAsksAboutSongsAndClosesThemButKeepsVideoTabs()
    {
        var song = OpenSong();
        song.Controller.ToggleLoop();
        Assert.True(song.IsDirty);
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);

        W.Commands.Execute("project.close");
        Assert.Equal("Close Project", W.Message.Current?.Title);
        Assert.Contains(song.DisplayName, W.Message.Current!.Message, StringComparison.Ordinal);
        W.Message.Answer(1); // Don't Save

        Assert.DoesNotContain(song, W.Session.Tabs);
        Assert.Same(video, Assert.Single(W.Session.Tabs));
        Assert.True(W.ProjectManager.Visible);
    }

    [Fact]
    public void ASongOfAnotherProjectIsRefusedWhileThisProjectsSongIsOpen()
    {
        OpenSong();
        using var other = new TestProject("Other");
        var foreign = SongTab.CreateFile(other.Abs("Content/Music/Foreign.msong"));
        Assert.Throws<InvalidOperationException>(() => W.Session.OpenSong(foreign));
    }

    [Fact]
    public void OpeningAProjectInMusicModeStaysInMusicMode()
    {
        using var other = new TestProject("Other");
        W.SetMode(EditorMode.Music);
        W.Commands.OpenProject(other.Root);
        TickUntil(() => W.Session.Scenes.Count == 1 && W.Session.ProjectRoot == other.Root);
        _editor.Tick(2);
        Assert.Equal(EditorMode.Music, W.Mode);
        Assert.Null(W.Session.ActiveTab);
        Assert.True(W.SongPanel.Visible);

        W.SetMode(EditorMode.Game); // the main scene opened behind the music editor
        Assert.Same(W.Session.Scenes[0], W.Session.ActiveTab);
    }

    [Fact]
    public void SongCommandsWithoutASongDoNothingQuietly()
    {
        W.SetMode(EditorMode.Music);
        var warnings = W.Output.Messages.Count(m => m.Text.Contains("Unknown command", StringComparison.Ordinal));
        foreach (var command in new[] { "song.play", "song.render", "song.signature", "song.snap", "song.quantize", "song.add_audio_track" })
            Assert.True(W.Commands.Execute(command), command);
        _editor.Key(Key.Space);
        _editor.Tick(2);
        Assert.Equal(warnings, W.Output.Messages.Count(m => m.Text.Contains("Unknown command", StringComparison.Ordinal)));
        Assert.False(W.Popup.Visible);
    }

    [Fact]
    public void TheToolbarMenusOpenUnderTheirButtons()
    {
        var tab = OpenSong();
        W.Commands.Execute("song.snap");
        Assert.True(W.Popup.Visible);
        W.Popup.Close();

        W.Commands.Execute("song.add_audio_track");
        Assert.Equal(2, tab.Document.Song.Tracks.Count);
        Assert.Equal(SongTrackKind.Audio, tab.SelectedTrack?.Kind);
    }

    [Theory]
    [InlineData(Key.N, EditorModifiers.Command, "file.new_song")]
    [InlineData(Key.O, EditorModifiers.Command, "file.open")]
    [InlineData(Key.S, EditorModifiers.Command | EditorModifiers.Shift, "file.save_as")]
    [InlineData(Key.Z, EditorModifiers.Command | EditorModifiers.Shift, "edit.redo")]
    [InlineData(Key.E, EditorModifiers.Command, "song.render")]
    [InlineData(Key.S, EditorModifiers.Command, "file.save")]
    [InlineData(Key.P, EditorModifiers.None, "song.piano_roll")]
    [InlineData(Key.X, EditorModifiers.None, "song.mixer")]
    [InlineData(Key.M, EditorModifiers.None, "song.mute_track")]
    [InlineData(Key.S, EditorModifiers.None, "song.solo_track")]
    [InlineData(Key.S, EditorModifiers.Shift, null)]
    [InlineData(Key.F1, EditorModifiers.None, "help.docs")]
    [InlineData(Key.W, EditorModifiers.None, null)]   // the gizmo keys are game-only
    [InlineData(Key.F5, EditorModifiers.None, null)]  // so is Play
    public void MusicShortcuts(Key key, EditorModifiers modifiers, string? command) =>
        Assert.Equal(command, EditorWorkspace.MusicShortcutFor(key, modifiers));
}

/// <summary>Music mode at start-up: it comes back, behind the Project Manager when there is one (songs need a project).</summary>
[Collection(nameof(SerialEditor))]
public sealed class MusicModeStartUpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheModeIsRestoredAtStartUp(bool projectManager)
    {
        var directory = Directory.CreateTempSubdirectory("mf-editor-mode").FullName;
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            new EditorSettings { LastMode = EditorMode.Music }.Save(settings);
            Assert.Equal(EditorMode.Music, EditorSettings.Load(settings).LastMode);

            using var editor = new HeadlessEditor(configure: o => o with { EditorSettingsPath = settings, ShowProjectManager = projectManager });
            editor.Tick(2);
            Assert.Equal(EditorMode.Music, editor.Workspace.Mode);
            Assert.Equal(projectManager, editor.Workspace.ProjectManager.Visible);
            Assert.Null(editor.Workspace.Session.ActiveTab);
            Assert.True(editor.Workspace.SongPanel.Visible);
            Assert.Empty(editor.Workspace.SongPanel.RecentSongs);
            Assert.Contains(editor.Workspace.Commands.MenuItems("file"), i => i.Command == "file.new_song" && !i.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
