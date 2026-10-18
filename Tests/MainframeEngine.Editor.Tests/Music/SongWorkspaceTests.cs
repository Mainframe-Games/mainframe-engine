using MainframeEngine.Editor.Music;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The song tab in the headless editor (music mode): the panel, the tab's keys before the editor's shortcuts, save and close.</summary>
[Collection(nameof(SerialEditor))]
public sealed class SongWorkspaceTests
{
    private static (HeadlessEditor Editor, SongTab Tab) Open()
    {
        var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Content", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        return (editor, Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab));
    }

    [Fact]
    public void SongTabShowsThePanelAndRestsTheScenePanels()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            Assert.Equal(EditorMode.Music, editor.Workspace.Mode);
            Assert.True(editor.Workspace.SongPanel.Visible);
            Assert.True(editor.Workspace.SongPanel.ArrangeRect.Width > 100);
            Assert.True(editor.Workspace.SongPanel.RollRect.Height > 50);
            Assert.Same(tab, editor.Workspace.SongView.Tab);
            Assert.Null(editor.Workspace.Session.Active);
            Assert.Empty(editor.RmlMessages);

            tab.SetPanel(SongBottomPanel.Mixer);
            editor.Tick(2);
            Assert.Equal(default, editor.Workspace.SongPanel.RollRect);

            editor.Workspace.Session.NewScene(); // a scene is game mode's
            editor.Tick(2);
            Assert.Equal(EditorMode.Game, editor.Workspace.Mode);
            Assert.False(editor.Workspace.SongPanel.Visible);
        }
    }

    [Fact]
    public void TabKeysComeFirstAndSaveAndUndoActOnTheSong()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            editor.Key(Key.L);
            Assert.True(tab.Document.Song.Loop.Enabled);
            Assert.True(tab.IsDirty);
            editor.Key(Key.Z, Key.ControlLeft);
            Assert.False(tab.Document.Song.Loop.Enabled);
            editor.Key(Key.Z, Key.ControlLeft, Key.ShiftLeft);
            Assert.True(tab.Document.Song.Loop.Enabled);

            editor.Key(Key.S, Key.ControlLeft);
            Assert.False(tab.IsDirty);
            Assert.True(SongFormat.Load(tab.FilePath!).Song.Loop.Enabled);
        }
    }

    [Fact]
    public void PAndXShowOneBottomPaneAndTheOpenOnesKeyHidesIt()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var w = editor.Workspace;
            Assert.Equal(SongBottomPanel.PianoRoll, tab.Panel);
            var withPane = w.SongPanel.ArrangeRect.Height;

            editor.Key(Key.X);
            editor.Tick(2);
            Assert.Equal(SongBottomPanel.Mixer, tab.Panel);
            Assert.Equal(SongBottomPanel.Mixer, w.Layout.Settings.MusicPane);
            Assert.Equal(default, w.SongPanel.RollRect);
            Assert.Contains(w.Commands.MenuItems("view"), i => i.Command == "song.mixer" && i.Css == "current");

            editor.Key(Key.X); // the open pane's key hides it: the tracks fill the view
            editor.Tick(2);
            Assert.Equal(SongBottomPanel.None, tab.Panel);
            Assert.Equal(default, w.SongPanel.RollRect);
            Assert.True(w.SongPanel.ArrangeRect.Height > withPane + 100);
            Assert.DoesNotContain(w.Commands.MenuItems("view"), i => i.Css == "current");
            Assert.Equal(SongBottomPanel.None, EditorLayout.Load(Path.Combine(editor.Directory, "layout.json")).MusicPane);

            editor.Key(Key.P);
            editor.Tick(2);
            Assert.Equal(SongBottomPanel.PianoRoll, tab.Panel);
            Assert.True(w.SongPanel.RollRect.Height > 50);
            editor.Key(Key.P);
            editor.Tick(2);
            Assert.Equal(SongBottomPanel.None, tab.Panel);

            // The pane is global: another song opens with none, and the Hide button's command works on it too.
            var other = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Content", "Music", "Other.msong"));
            w.Commands.OpenSong(other);
            editor.Tick(3);
            var second = Assert.IsType<SongTab>(w.Session.ActiveTab);
            Assert.NotSame(tab, second);
            Assert.Equal(SongBottomPanel.None, second.Panel);
            w.Commands.Execute("song.mixer");
            w.Commands.Execute("song.hide_pane");
            Assert.Equal(SongBottomPanel.None, second.Panel);
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void MAndSMuteAndSoloTheSelectedTrackAsOneUndoStepEach()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var track = tab.Document.Song.Tracks[0];
            tab.SelectClip(track, null);
            editor.Key(Key.M);
            Assert.True(track.Mute);
            editor.Key(Key.S);
            Assert.True(track.Solo);
            Assert.Equal(SongBottomPanel.PianoRoll, tab.Panel); // M no longer switches to the mixer
            editor.Key(Key.Z, Key.ControlLeft);
            Assert.False(track.Solo);
            Assert.True(track.Mute);
            editor.Key(Key.M);
            Assert.False(track.Mute);
            Assert.Contains(editor.Workspace.Commands.MenuItems("song"), i => i.Command == "song.solo_track" && i.Enabled);
        }
    }

    [Fact]
    public void ThePaneSplitterIsClampedAndTheHeightPersists()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var w = editor.Workspace;
            var panel = w.SongPanel;
            Assert.Equal(300, panel.RollRect.Height, 1.0);

            panel.DragPaneSplitter(0); // to the top: the tracks keep their minimum
            editor.Tick(2);
            Assert.Equal(w.Layout.MaxMusicPaneHeight, w.Layout.Settings.MusicPaneHeight);
            Assert.Equal(w.Layout.MaxMusicPaneHeight, panel.RollRect.Height, 1.0);
            Assert.True(panel.ArrangeRect.Height >= EditorLayout.MinMusicTracksHeight - 1);

            panel.DragPaneSplitter(100_000); // to the bottom: the pane keeps its minimum
            editor.Tick(2);
            Assert.Equal(EditorLayout.MinMusicPaneHeight, panel.RollRect.Height, 1.0);

            var bottom = panel.RollRect.Bottom;
            panel.DragPaneSplitter(bottom - 250 - EditorLayout.SplitterSize * 0.5f - EditorLayout.MusicPaneHeaderHeight);
            editor.Tick(2);
            Assert.Equal(250, w.Layout.Settings.MusicPaneHeight, 0.5);
            Assert.Equal(250, panel.RollRect.Height, 1.0);

            w.SaveLayout(); // the splitter's dragend
            Assert.Equal(250, EditorLayout.Load(Path.Combine(editor.Directory, "layout.json")).MusicPaneHeight, 0.5);
        }
    }

    [Fact]
    public void ClosingADirtySongAsksFirst()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            tab.Document.SetTempo(140);
            editor.Workspace.Commands.CloseTab(tab);
            Assert.True(editor.Workspace.Message.Visible);
            Assert.Contains(tab, editor.Workspace.Session.Tabs);
            editor.Workspace.Message.Answer(1); // Don't Save
            Assert.DoesNotContain(tab, editor.Workspace.Session.Tabs);
        }
    }
}
