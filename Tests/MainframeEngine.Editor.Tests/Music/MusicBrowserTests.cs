using MainframeEngine.Editor.Music;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>Music mode's Browser (songs and plugins) and the mixer's insert slots, in the headless editor.</summary>
[Collection(nameof(SerialEditor))]
public sealed class MusicBrowserTests
{
    private static (HeadlessEditor Editor, SongTab Tab) Open()
    {
        var editor = new HeadlessEditor();
        var music = Path.Combine(editor.Directory, "Project", "Assets", "Music");
        SongTab.CreateFile(Path.Combine(music, "Other.msong"));
        var path = SongTab.CreateFile(Path.Combine(music, "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        return (editor, Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab));
    }

    private static int Row(SongBrowser browser, string id)
    {
        var index = browser.Ids.ToList().IndexOf(id);
        Assert.True(index >= 0, $"no row '{id}'");
        return index;
    }

    private static string Html(HeadlessEditor editor, string id) => editor.Workspace.SongPanel.Document.GetElementById(id).InnerRml;

    [Fact]
    public void FTogglesTheBrowserWhichListsTheSongsAndEveryPlugin()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var browser = editor.Workspace.SongPanel.Browser;
            Assert.False(editor.Workspace.Layout.Settings.MusicShowBrowser);
            editor.Key(Key.F);
            editor.Tick(2);
            Assert.True(editor.Workspace.Layout.Settings.MusicShowBrowser);
            Assert.False(editor.Workspace.SongPanel.Document.GetElementById("song-browser").IsClassSet("hidden"));
            Assert.Empty(editor.RmlMessages);

            var theme = Row(browser, "song:" + tab.FilePath);
            Assert.True(browser.Rows[theme].Current);
            Row(browser, "song:" + Path.Combine(Path.GetDirectoryName(tab.FilePath)!, "Other.msong"));
            foreach (var info in BuiltinEffects.All)
                Row(browser, "fx:" + info.Id);
            Assert.Contains("Channel EQ", Html(editor, "song-browser-list"), StringComparison.Ordinal);

            browser.Search = "comp";
            editor.Tick(1);
            Assert.Contains("fx:compressor", browser.Ids);
            Assert.DoesNotContain("fx:eq", browser.Ids);
            Assert.DoesNotContain(browser.Ids, id => id.StartsWith("song:", StringComparison.Ordinal));
            browser.Search = "";
            editor.Tick(1);

            browser.Click(Row(browser, "fx:Dynamics")); // collapses, saved with the layout
            editor.Tick(1);
            Assert.DoesNotContain("fx:compressor", browser.Ids);
            Assert.Contains("fx:Dynamics", editor.Workspace.Layout.Settings.MusicBrowserCollapsed!);

            browser.Click(Row(browser, "song:" + Path.Combine(Path.GetDirectoryName(tab.FilePath)!, "Other.msong")));
            editor.Tick(2);
            Assert.EndsWith("Other.msong", editor.Workspace.Session.ActiveSong!.FilePath, StringComparison.Ordinal);

            editor.Key(Key.F);
            editor.Tick(1);
            Assert.False(editor.Workspace.Layout.Settings.MusicShowBrowser);
        }
    }

    [Fact]
    public void ADoubleClickAddsToTheSelectedTrackAndOpensTheWindow()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var browser = editor.Workspace.SongPanel.Browser;
            editor.Workspace.Commands.Execute("music.browser");
            editor.Tick(2);
            var track = tab.Document.Song.Tracks[0];
            tab.SelectClip(track, null);
            browser.DoubleClick(Row(browser, "fx:reverb"));
            Assert.Equal("reverb", Assert.Single(track.Inserts).Builtin);
            Assert.Same(track.Inserts[0], editor.Workspace.PluginWindow.Insert);
            tab.Document.History.Undo();
            Assert.Empty(track.Inserts);
        }
    }

    [Fact]
    public void DroppingAPluginOnAHeaderStripSlotOrTheMasterAppliesIt()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var browser = editor.Workspace.SongPanel.Browser;
            var song = tab.Document.Song;
            var track = song.Tracks[0];
            Assert.True(browser.Drop("track:0", "fx:eq"));
            Assert.True(browser.Drop("strip:0", "fx:compressor"));
            Assert.Equal(["eq", "compressor"], track.Inserts.Select(i => i.Builtin));
            Assert.True(browser.Drop("slot:0:0", "fx:filter")); // replaces the EQ
            Assert.Equal(["filter", "compressor"], track.Inserts.Select(i => i.Builtin));
            Assert.True(browser.Drop("master", "fx:limiter"));
            Assert.True(browser.Drop("mslot:0", "fx:utility"));
            Assert.Equal("utility", Assert.Single(song.Master.Inserts).Builtin);
            Assert.False(browser.Drop("slot:0:9", "fx:eq"));
            Assert.False(browser.Drop("track:0", "fx:no-such-effect"));
            Assert.False(browser.Drop("strip:0"));      // nothing dragged

        }
    }

    [Fact]
    public void TheMixerShowsASlotPerInsertWithItsState()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            editor.Workspace.SetMusicPane(SongBottomPanel.Mixer);
            var track = tab.Document.Song.Tracks[0];
            tab.Document.AddBuiltinInsert(track, "eq");
            var delay = tab.Document.AddBuiltinInsert(track, "delay");
            tab.Document.SetInsertBypass(delay, true);
            tab.Document.AddBuiltinInsert(null, "limiter");
            track.Inserts.Add(new PluginInsert { Builtin = "from-the-future" });
            tab.Document.SetTrackMute(track, false); // a version bump after the direct add
            editor.Tick(3);

            var mixer = Html(editor, "song-mixer");
            Assert.Contains("Channel EQ", mixer, StringComparison.Ordinal);
            Assert.Contains("Beat Delay", mixer, StringComparison.Ordinal);
            Assert.Contains("Limiter", mixer, StringComparison.Ordinal);
            Assert.Contains("Unknown effect (from-the-future)", mixer, StringComparison.Ordinal);
            Assert.Contains("bypassed", mixer, StringComparison.Ordinal);
            Assert.Contains("missing", mixer, StringComparison.Ordinal);
            Assert.Empty(editor.RmlMessages);
        }
    }
}
