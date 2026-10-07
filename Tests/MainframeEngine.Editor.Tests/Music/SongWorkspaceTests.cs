using MainframeEngine.Editor.Music;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The song tab in the headless editor: the panel, the tab's keys before the editor's shortcuts, save and close.</summary>
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
            Assert.True(editor.Workspace.SongPanel.Visible);
            Assert.True(editor.Workspace.SongPanel.ArrangeRect.Width > 100);
            Assert.True(editor.Workspace.SongPanel.RollRect.Height > 50);
            Assert.Same(tab, editor.Workspace.SongView.Tab);
            Assert.Null(editor.Workspace.Session.Active);
            Assert.Empty(editor.RmlMessages);

            tab.SetPanel(SongBottomPanel.Mixer);
            editor.Tick(2);
            Assert.Equal(default, editor.Workspace.SongPanel.RollRect);

            editor.Workspace.Session.NewScene();
            editor.Tick(2);
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
