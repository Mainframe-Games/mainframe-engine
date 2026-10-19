using System.Numerics;
using MainframeEngine.Audio.Music;
using MainframeEngine.Editor.Music;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

/// <summary>
/// The Sections lane under the ruler (docs/design/future/adaptive-music.md, "Sections lane and layers"): creating, moving
/// and resizing sections snapped to bars without overlaps, a click looping a section, markers, menus and renames; each
/// gesture one history entry.
/// </summary>
/// <remarks>Geometry: a bar is 75 dp; the ruler is 0–22, the Sections lane 22–44 (adaptive songs), the first track lane from 44.</remarks>
[Collection(nameof(SerialEditor))]
public sealed class SectionsLaneTests : IDisposable
{
    private const float Lane = 33; // the Sections lane's middle
    private const float BarDp = 75;
    private const long Bar = 3840;

    private readonly SessionHost _host = new();
    private readonly SongTab _tab;

    public SectionsLaneTests()
    {
        var path = SongTab.CreateFile(Path.Combine(_host.ProjectDirectory, "Assets", "Music", "Theme.msong"));
        _tab = _host.Session.OpenSong(path);
        Doc.EnableAdaptive();
    }

    public void Dispose() => _host.Dispose();

    private SongDocument Doc => _tab.Document;
    private SongCanvasController C => _tab.Controller;
    private SongAdaptive Adaptive => Doc.Song.Adaptive!;
    private int Entries => Doc.History.Actions.Count;

    private void Gesture(Vector2 from, Vector2 to, EditorModifiers modifiers = EditorModifiers.None)
    {
        C.Press(SongArea.Arrangement, from, modifiers);
        for (var i = 1; i <= 4; i++)
            C.Drag(Vector2.Lerp(from, to, i / 4f), modifiers);
        C.Release(to);
    }

    [Fact]
    public void TheLaneSitsUnderTheRulerOnlyForAdaptiveSongs()
    {
        Assert.Equal(SongCanvasController.RulerHeight + SongCanvasController.SectionsHeight, C.LaneY(0));
        Assert.Equal(-1, C.LaneAt(Lane));
        Assert.Equal(0, C.LaneAt(50));
        Doc.DisableAdaptive();
        Assert.Equal(SongCanvasController.RulerHeight, C.LaneY(0));
    }

    [Fact]
    public void DraggingAnEmptySpotMakesASectionSnappedToBarsAsOneEntry()
    {
        var entries = Entries;
        Gesture(new Vector2(BarDp * 1.3f, Lane), new Vector2(BarDp * 3.2f, Lane));
        var section = Assert.Single(Adaptive.Sections);
        Assert.Equal((Bar, 3 * Bar), (section.Start, section.End));
        Assert.Equal(entries + 1, Entries);
        Assert.Same(section, _tab.SelectedSection);
        Assert.Equal(section.Id, Adaptive.Start); // the first playable section starts the music

        // A click without a drag makes nothing.
        C.Press(SongArea.Arrangement, new Vector2(BarDp * 6.5f, Lane));
        C.Release(new Vector2(BarDp * 6.5f, Lane));
        Assert.Single(Adaptive.Sections);
    }

    [Fact]
    public void MovingAndResizingSnapToBarsAndStopAtNeighbours()
    {
        var a = Doc.AddSection("A", MusicSectionKind.Section, 0, 2 * Bar);
        var b = Doc.AddSection("B", MusicSectionKind.Section, 4 * Bar, 6 * Bar);
        var entries = Entries;

        // B's left edge dragged into A stops at A's end.
        Gesture(new Vector2(4 * BarDp + 2, Lane), new Vector2(BarDp * 0.6f, Lane));
        Assert.Equal((2 * Bar, 6 * Bar), (b.Start, b.End));
        Assert.Equal(entries + 1, Entries);

        // A's right edge: whole bars.
        Gesture(new Vector2(2 * BarDp - 2, Lane), new Vector2(BarDp * 1.4f, Lane));
        Assert.Equal((0L, Bar), (a.Start, a.End));

        // Moving B by three bars keeps its length.
        Gesture(new Vector2(BarDp * 3.5f, Lane), new Vector2(BarDp * 6.5f, Lane));
        Assert.Equal((5 * Bar, 9 * Bar), (b.Start, b.End));
        Doc.History.Undo();
        Assert.Equal((2 * Bar, 6 * Bar), (b.Start, b.End));
    }

    [Fact]
    public void AClickLoopsTheSectionAndADoubleClickRenamesIt()
    {
        var section = Doc.AddSection("Explore", MusicSectionKind.Section, 2 * Bar, 4 * Bar);
        C.Press(SongArea.Arrangement, new Vector2(BarDp * 3, Lane));
        C.Release(new Vector2(BarDp * 3, Lane));
        Assert.True(Doc.Song.Loop.Enabled);
        Assert.Equal((2 * Bar, 4 * Bar), (Doc.Song.Loop.Start, Doc.Song.Loop.End));

        SongSection? renamed = null;
        C.RequestRenameSection = s => renamed = s;
        C.Press(SongArea.Arrangement, new Vector2(BarDp * 3, Lane), doubleClick: true);
        C.Release(new Vector2(BarDp * 3, Lane));
        Assert.Same(section, renamed);
    }

    [Fact]
    public void MarkersMoveByDragAndAltClickMakesThemExits()
    {
        Doc.AddSection("Explore", MusicSectionKind.Section, 0, 4 * Bar);
        var marker = Doc.AddMarker("drop", 2 * Bar);
        var entries = Entries;
        Gesture(new Vector2(2 * BarDp, Lane), new Vector2(3 * BarDp, Lane));
        Assert.Equal(3 * Bar, marker.Tick);
        Assert.Equal(entries + 1, Entries);

        C.Press(SongArea.Arrangement, new Vector2(3 * BarDp, Lane), EditorModifiers.Alt);
        C.Release(new Vector2(3 * BarDp, Lane));
        Assert.True(marker.Exit);
    }

    [Fact]
    public void ARightClickInTheLaneAsksForItsMenu()
    {
        var section = Doc.AddSection("Explore", MusicSectionKind.Section, 0, 4 * Bar);
        (SongSection? Section, SongMarker? Marker, long Tick)? asked = null;
        C.RequestSectionMenu = (s, m, tick, _) => asked = (s, m, tick);
        Assert.True(C.ContextMenu(new Vector2(BarDp * 2, Lane)));
        Assert.Same(section, asked!.Value.Section);
        Assert.Equal(2 * Bar, asked.Value.Tick);
        Assert.False(C.ContextMenu(new Vector2(BarDp * 2, 60))); // a track lane: not this menu
    }

    [Fact]
    public void ShiftSpaceIsAdaptivePlayAndTheMusicKeysAreBound()
    {
        Assert.Equal("music.adaptive", EditorWorkspace.MusicShortcutFor(Key.A, EditorModifiers.None));
        Assert.Equal("song.fire_stinger", EditorWorkspace.MusicShortcutFor(Key.T, EditorModifiers.None));
        Assert.Equal("song.bake", EditorWorkspace.MusicShortcutFor(Key.B, EditorModifiers.Command));
        Assert.True(C.Key(Key.Space, EditorModifiers.Shift)); // handled (no playable section: nothing starts)
        Assert.False(_tab.Player.IsAdaptive);
    }
}

/// <summary>The Adaptive tab, the layer chips and the toolbar's Bake button in the headless editor.</summary>
[Collection(nameof(SerialEditor))]
public sealed class AdaptivePanelTests
{
    private static (HeadlessEditor Editor, SongTab Tab) Open()
    {
        var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Assets", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        return (editor, Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab));
    }

    private static string Adaptive(HeadlessEditor editor) => editor.Workspace.SongPanel.Document.GetElementById("song-adaptive").InnerRml;

    [Fact]
    public void TheAdaptiveTabOffersMakeAdaptiveThenListsTheSong()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            editor.Workspace.Commands.Execute("music.adaptive");
            editor.Tick(2);
            Assert.True(editor.Workspace.Layout.Settings is { MusicShowBrowser: true, MusicAdaptiveTab: true });
            Assert.Contains("Make Adaptive", Adaptive(editor), StringComparison.Ordinal);

            editor.Workspace.Commands.Execute("song.make_adaptive");
            SongAdaptiveDemo.Build(tab.Document);
            editor.Tick(3);
            var html = Adaptive(editor);
            foreach (var expected in (string[])["Parameters", "intensity", "Perc", "low-pass", "when area = Combat", "next bar", "section end", "Hit", "None: the song is ready to bake"])
                Assert.Contains(expected, html, StringComparison.Ordinal);
            Assert.Empty(editor.RmlMessages);

            // F switches to the Browser; again hides it; A brings the Adaptive tab back.
            editor.Workspace.Commands.Execute("music.browser");
            Assert.True(editor.Workspace.Layout.Settings is { MusicShowBrowser: true, MusicAdaptiveTab: false });
            editor.Workspace.Commands.Execute("music.browser");
            Assert.False(editor.Workspace.Layout.Settings.MusicShowBrowser);
            editor.Workspace.Commands.Execute("music.adaptive");
            Assert.True(editor.Workspace.Layout.Settings is { MusicShowBrowser: true, MusicAdaptiveTab: true });
        }
    }

    [Fact]
    public void TheRuleThatWouldFireNowIsHighlighted()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            SongAdaptiveDemo.Build(tab.Document);
            editor.Workspace.ShowMusicRightTab(adaptive: true);
            tab.SelectSection(tab.Document.Song.Adaptive!.FindSection("s2")); // Explore
            editor.Tick(3);
            var document = editor.Workspace.SongPanel.Document;
            Assert.False(document.GetElementById("ad-rule-0").IsClassSet("firing")); // area is Calm

            tab.Auditions["area"] = 1; // Combat
            tab.Touch();
            editor.Tick(2);
            Assert.True(document.GetElementById("ad-rule-0").IsClassSet("firing"));
        }
    }

    [Fact]
    public void SteadyFramesWithTheAdaptiveTabAllocateNothing()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            tab.SelectClip(null, null);
            SongAdaptiveDemo.Build(tab.Document);
            tab.SelectSection(tab.Document.Song.Adaptive!.Sections[1]);
            editor.Workspace.ShowMusicRightTab(adaptive: true);
            editor.Workspace.SetMusicPane(SongBottomPanel.Mixer);
            editor.Tick(2);
            tab.Player.Engine.Process(new float[SongEngine.BlockFrames * 2]); // the snapshot (and its plan) in place, as with an audio device
            editor.Tick(30);
            var before = GC.GetAllocatedBytesForCurrentThread();
            editor.Tick(100);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            // The arrangement's Sections lane: drawn every frame.
            var canvas = tab.Arrangement;
            canvas.ViewSize = new Vector2(1000, 400);
            for (var i = 0; i < 5; i++)
                canvas.RunDraw();
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
                canvas.RunDraw();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void TrackHeadersShowTheirLayerAndTheToolbarBakes()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var panel = editor.Workspace.SongPanel.Document;
            editor.Tick(2);
            Assert.Equal("Render", editor.Workspace.Toolbar.Document.GetElementById("song-render-label").InnerRml);

            SongAdaptiveDemo.Build(tab.Document);
            editor.Tick(3);
            var chips = panel.AsElement().QuerySelectorAll(".track .layer-chip");
            Assert.Equal(["Main", "Perc", "Combat"], chips.Select(c => c.InnerRml));
            Assert.Equal("Bake", editor.Workspace.Toolbar.Document.GetElementById("song-render-label").InnerRml);
            Assert.Equal("song.bake", editor.Workspace.Toolbar.Document.GetElementById("song-render").GetAttribute("data-command"));
            Assert.Empty(editor.RmlMessages);
        }
    }
}
