using System.Numerics;
using MainframeEngine.Editor.Music;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// The song tab: opening, dirty state, saving and closing through the session; canvas gestures mapped to
/// <see cref="SongDocument"/> edits with one history entry each; snap and quantize math; the tab's keys.
/// </summary>
/// <remarks>
/// Geometry at the defaults (960 PPQ, 4/4): a bar is 75 dp in the arrangement (lanes start at y 22, 44 dp high); in the
/// piano roll a beat is 92 dp after the 52 dp keyboard and pitch 72's row spans y 162–174.
/// </remarks>
[Collection(nameof(SerialEditor))]
public sealed class SongTabTests : IDisposable
{
    private const float Lane0 = 40;
    private const float Pitch72 = 168;
    private const float Key = SongCanvasController.KeyboardWidth;
    private const float Beat = 92;

    private readonly SessionHost _host = new();
    private readonly SongTab _tab;

    public SongTabTests()
    {
        var path = SongTab.CreateFile(Path.Combine(_host.ProjectDirectory, "Content", "Music", "Theme.msong"));
        _tab = _host.Session.OpenSong(path);
    }

    public void Dispose() => _host.Dispose();

    private SongDocument Doc => _tab.Document;
    private SongCanvasController C => _tab.Controller;
    private SongTrack Lead => Doc.Song.Tracks[0];
    private int Entries => Doc.History.Actions.Count;

    private MidiClip NewClip(long start = 0, long length = 3840)
    {
        var clip = Doc.AddMidiClip(Lead, start, length);
        _tab.SelectClip(Lead, clip);
        return clip;
    }

    private void Gesture(SongArea area, Vector2 from, Vector2 to, EditorModifiers modifiers = EditorModifiers.None)
    {
        C.Press(area, from, modifiers);
        for (var i = 1; i <= 4; i++)
            C.Drag(Vector2.Lerp(from, to, i / 4f), modifiers);
        C.Release(to);
    }

    // ── Tab ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewSongOpensCleanWithOneBuiltInTrack()
    {
        Assert.Same(_tab, _host.Session.ActiveTab);
        Assert.Same(_tab, _host.Session.ActiveSong);
        Assert.False(_tab.IsDirty);
        Assert.Equal("Theme.msong", _tab.Title);
        var track = Assert.Single(Doc.Song.Tracks);
        Assert.True(track.Instrument?.IsZzfx);
        Assert.Same(_tab, _host.Session.OpenSong(_tab.FilePath!)); // already open: activated, not opened twice
        Assert.Single(_host.Session.Tabs);
        Assert.Throws<IOException>(() => SongTab.CreateFile(_tab.FilePath!));
    }

    [Fact]
    public void EditsMarkTheTabDirtyAndSavingClearsIt()
    {
        var changed = 0;
        _host.Session.ScenesChanged += () => changed++;
        Doc.AddTrack(SongTrackKind.Audio, "Rain");
        Assert.True(_tab.IsDirty);
        Assert.Equal("Theme.msong*", _tab.Title);
        Assert.True(_host.Session.HasUnsavedChanges);
        Assert.Contains(_tab, _host.Session.DirtyTabs);
        Assert.Equal(1, changed); // the tab strip follows the dirty flag, not every edit
        Doc.RenameTrack(Lead, "Melody");
        Assert.Equal(1, changed);

        _host.Session.SaveSong(_tab);
        Assert.False(_tab.IsDirty);
        Assert.False(_host.Session.HasUnsavedChanges);
        var reloaded = SongFormat.Load(_tab.FilePath!).Song;
        Assert.Equal(["Melody", "Rain"], reloaded.Tracks.Select(t => t.Name));
    }

    [Fact]
    public void SaveAsMovesTheTabToANewSongWithItsOwnUid()
    {
        var uid = Doc.Song.Uid;
        var copy = Path.Combine(_host.ProjectDirectory, "Content", "Music", "Copy");
        _host.Session.SaveSong(_tab, copy);
        Assert.EndsWith("Copy.msong", _tab.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(_tab.FilePath));
        Assert.NotEqual(uid, Doc.Song.Uid);
        Assert.False(_tab.IsDirty);
    }

    [Fact]
    public void ClosingAndDeactivatingStopTheTab()
    {
        var scene = _host.Session.NewScene();
        Assert.Same(scene, _host.Session.ActiveTab);
        Assert.Equal(SubViewportUpdateMode.Disabled, _tab.ArrangeViewport.UpdateMode);
        _host.Session.Activate(_tab);
        _host.Session.Close(_tab);
        Assert.DoesNotContain(_tab, _host.Session.Tabs);
        Assert.True(_tab.ArrangeViewport.IsFreed);
        Assert.True(_tab.RollViewport.IsFreed);
    }

    [Fact]
    public void MovedFilesAreFollowed()
    {
        var target = Path.Combine(_host.ProjectDirectory, "Content", "Music", "Renamed.msong");
        File.Move(_tab.FilePath!, target);
        _host.Session.FilesMoved(Path.Combine(_host.ProjectDirectory, "Content", "Music", "Theme.msong"), target);
        Assert.Equal(target, _tab.FilePath);
    }

    [Fact]
    public void UndoForgetsSelectionThatNoLongerExists()
    {
        var clip = NewClip();
        Assert.Same(clip, _tab.SelectedClip);
        Doc.History.Undo();
        Assert.Null(_tab.SelectedClip);
    }

    // ── Piano roll ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClickOnAnEmptyCellAddsANoteOfTheLastLengthOnTheGrid()
    {
        var clip = NewClip();
        var before = Entries;
        var at = new Vector2(Key + Beat / 2 + 5, Pitch72); // just after the second eighth: floors to 480
        C.Press(SongArea.PianoRoll, at);
        C.Release(at);
        var note = Assert.Single(clip.Notes);
        Assert.Equal((72, 480L, 240L), (note.Pitch, note.Start, note.Length));
        Assert.Equal(before + 1, Entries);
        Assert.Equal([note], _tab.SelectedNotes);
    }

    [Fact]
    public void DraggingANoteMovesItOnTheGridAsOneEntry()
    {
        var clip = NewClip();
        var note = Doc.AddNote(clip, 72, 0, 480);
        var before = Entries;
        Gesture(SongArea.PianoRoll, new Vector2(Key + 5, Pitch72), new Vector2(Key + 5 + Beat / 2 + 3, Pitch72 - 24));
        Assert.Equal((74, 480L), (note.Pitch, note.Start));
        Assert.Equal(before + 1, Entries);
        Doc.History.Undo();
        Assert.Equal((72, 0L), (note.Pitch, note.Start));
    }

    [Fact]
    public void DraggingANotesRightEdgeResizesIt()
    {
        var clip = NewClip();
        var note = Doc.AddNote(clip, 72, 0, 960);
        var before = Entries;
        Gesture(SongArea.PianoRoll, new Vector2(Key + Beat - 2, Pitch72), new Vector2(Key + Beat - 2 + Beat / 2, Pitch72));
        Assert.Equal(1440, note.Length);
        Assert.Equal(1440, _tab.LastNoteLength);
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void RubberBandSelectsTheNotesItTouches()
    {
        var clip = NewClip();
        var a = Doc.AddNote(clip, 72, 0, 240);
        var b = Doc.AddNote(clip, 72, 960, 240);
        Doc.AddNote(clip, 60, 0, 240);
        var before = Entries;
        Gesture(SongArea.PianoRoll, new Vector2(Key + 1, Pitch72 - 10), new Vector2(Key + Beat * 2, Pitch72 + 2));
        Assert.Equal([a, b], _tab.SelectedNotes);
        Assert.Equal(before, Entries); // selecting is not an edit
    }

    [Fact]
    public void VelocityDragSetsTheVelocityAsOneEntry()
    {
        var clip = NewClip();
        var note = Doc.AddNote(clip, 72, 0, 240);
        var before = Entries;
        var top = C.GridBottom + 6;
        Gesture(SongArea.PianoRoll, new Vector2(Key + 1, top), new Vector2(Key + 1, top + 23));
        Assert.Equal(64, note.Velocity);
        Assert.Equal(before + 1, Entries);
    }

    // ── Arrangement ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DraggingAClipMovesItOnTheGridOnRelease()
    {
        _tab.Snap = SongSnap.Quarter;
        var clip = NewClip();
        var before = Entries;
        C.Press(SongArea.Arrangement, new Vector2(10, Lane0));
        C.Drag(new Vector2(88, Lane0));
        Assert.Equal(0, clip.Start); // previewed, not edited, while dragging
        Assert.Equal(3840, C.GhostStart);
        C.Release(new Vector2(88, Lane0));
        Assert.Equal(3840, clip.Start);
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void AltDragCopiesAClip()
    {
        _tab.Snap = SongSnap.Quarter;
        var clip = NewClip();
        Doc.AddNote(clip, 60, 0, 240);
        var before = Entries;
        Gesture(SongArea.Arrangement, new Vector2(10, Lane0), new Vector2(160, Lane0), EditorModifiers.Alt);
        Assert.Equal(2, Lead.Clips.Count);
        Assert.Equal(0, clip.Start);
        var copy = Assert.IsType<MidiClip>(Lead.Clips[1]);
        Assert.Equal(7680, copy.Start);
        Assert.NotSame(clip.Notes[0], copy.Notes[0]);
        Assert.Same(copy, _tab.SelectedClip);
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void DraggingAClipsRightEdgeResizesIt()
    {
        _tab.Snap = SongSnap.Quarter;
        var clip = NewClip();
        var before = Entries;
        Gesture(SongArea.Arrangement, new Vector2(73, Lane0), new Vector2(148, Lane0));
        Assert.Equal((0L, 7680L), (clip.Start, clip.Length));
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void DoubleClickOnAnEmptyLaneCreatesAOneBarClip()
    {
        var before = Entries;
        C.Press(SongArea.Arrangement, new Vector2(200, Lane0), doubleClick: true);
        C.Release(new Vector2(200, Lane0));
        var clip = Assert.IsType<MidiClip>(Assert.Single(Lead.Clips));
        Assert.Equal((7680L, 3840L), (clip.Start, clip.Length));
        Assert.Same(clip, _tab.SelectedClip);
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void CmdDDuplicatesTheSelectedClipAfterItself()
    {
        var clip = NewClip(0, 3840);
        _tab.Focus = SongArea.Arrangement;
        var before = Entries;
        Assert.True(C.Key(Silk.NET.Input.Key.D, EditorModifiers.Command));
        Assert.Equal(2, Lead.Clips.Count);
        Assert.Equal(3840, Lead.Clips[1].Start);
        Assert.Equal(before + 1, Entries);
        Assert.NotSame(clip, _tab.SelectedClip);
    }

    [Fact]
    public void DraggingTheLoopRegionMovesItAsOneEntry()
    {
        var loop = Doc.Song.Loop;
        Assert.Equal((0L, 15360L), (loop.Start, loop.End));
        var before = Entries;
        Gesture(SongArea.Arrangement, new Vector2(150, 5), new Vector2(225, 5));
        Assert.Equal((3840L, 19200L), (loop.Start, loop.End));
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void DraggingInAnEmptyRulerBandCreatesAnEnabledLoop()
    {
        Doc.SetLoop(false, 0, 3840);
        var before = Entries;
        Gesture(SongArea.Arrangement, new Vector2(150, 5), new Vector2(300, 5));
        Assert.True(Doc.Song.Loop.Enabled);
        Assert.Equal((7680L, 15360L), (Doc.Song.Loop.Start, Doc.Song.Loop.End));
        Assert.Equal(before + 1, Entries);
    }

    [Fact]
    public void DroppingASoundOnTheArrangementAddsAnAudioTrackAndClip()
    {
        C.AudioLength = _ => 1920;
        var clip = C.Drop("Content/Audio/rain.wav", new Vector2(80, Lane0));
        Assert.NotNull(clip);
        var track = Doc.Song.Tracks[1];
        Assert.Equal(SongTrackKind.Audio, track.Kind);
        Assert.Equal((4080L, 1920L), (clip!.Start, clip.Length));
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LTogglesTheLoopAndHomeAndSpaceAreHandled()
    {
        var before = Entries;
        Assert.True(C.Key(Silk.NET.Input.Key.L, EditorModifiers.None));
        Assert.True(Doc.Song.Loop.Enabled);
        Assert.Equal(before + 1, Entries);
        Assert.True(C.Key(Silk.NET.Input.Key.Home, EditorModifiers.None));
        Assert.True(C.Key(Silk.NET.Input.Key.Space, EditorModifiers.None));
        Assert.True(C.Key(Silk.NET.Input.Key.Space, EditorModifiers.None));
    }

    [Theory]
    [InlineData(Silk.NET.Input.Key.F5)]
    [InlineData(Silk.NET.Input.Key.F6)]
    [InlineData(Silk.NET.Input.Key.F7)]
    [InlineData(Silk.NET.Input.Key.F8)]
    [InlineData(Silk.NET.Input.Key.S)]
    public void PlayKeysAndOtherShortcutsAreLeftToTheEditor(Key key)
    {
        Assert.False(C.Key(key, EditorModifiers.None));
        Assert.False(C.Key(Silk.NET.Input.Key.S, EditorModifiers.Command)); // Save stays the editor's
    }

    [Fact]
    public void NoteKeysNudgeQuantizeCopyPasteAndDelete()
    {
        var clip = NewClip();
        var note = Doc.AddNote(clip, 60, 250, 240);
        _tab.SelectedNotes.Add(note);
        _tab.Focus = SongArea.PianoRoll;

        Assert.True(C.Key(Silk.NET.Input.Key.Q, EditorModifiers.None));
        Assert.Equal(240, note.Start);
        Assert.True(C.Key(Silk.NET.Input.Key.Right, EditorModifiers.None));
        Assert.Equal(480, note.Start);
        Assert.True(C.Key(Silk.NET.Input.Key.Up, EditorModifiers.Shift));
        Assert.Equal(72, note.Pitch);
        Assert.True(C.Key(Silk.NET.Input.Key.Down, EditorModifiers.None));
        Assert.Equal(71, note.Pitch);

        Assert.True(C.Key(Silk.NET.Input.Key.D, EditorModifiers.Command)); // duplicate after the selection
        Assert.Equal(2, clip.Notes.Count);
        Assert.Equal(720, clip.Notes[1].Start);
        Assert.Same(clip.Notes[1], Assert.Single(_tab.SelectedNotes));

        Assert.True(C.Key(Silk.NET.Input.Key.C, EditorModifiers.Command));
        Assert.True(C.Key(Silk.NET.Input.Key.V, EditorModifiers.Command)); // playhead at 0 is inside the clip
        Assert.Equal(3, clip.Notes.Count);
        Assert.Equal(0, clip.Notes[2].Start);

        var before = Entries;
        Assert.True(C.Key(Silk.NET.Input.Key.Delete, EditorModifiers.None));
        Assert.Equal(2, clip.Notes.Count);
        Assert.Empty(_tab.SelectedNotes);
        Assert.Equal(before + 1, Entries);
    }

    // ── Grid math ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SongSnap.Off, 1)]
    [InlineData(SongSnap.Quarter, 960)]
    [InlineData(SongSnap.Eighth, 480)]
    [InlineData(SongSnap.Sixteenth, 240)]
    [InlineData(SongSnap.ThirtySecond, 120)]
    [InlineData(SongSnap.QuarterTriplet, 640)]
    [InlineData(SongSnap.EighthTriplet, 320)]
    [InlineData(SongSnap.SixteenthTriplet, 160)]
    public void SnapSteps(SongSnap snap, long ticks) => Assert.Equal(ticks, SongGrid.Step(snap, 960));

    [Theory]
    [InlineData(0, 240, 0, 0, 0)]
    [InlineData(119, 240, 0, 0, 240)]
    [InlineData(120, 240, 240, 0, 240)]
    [InlineData(250, 240, 240, 240, 480)]
    [InlineData(-10, 240, 0, -240, 0)]
    [InlineData(-130, 240, -240, -240, 0)]
    [InlineData(7, 1, 7, 7, 7)]
    public void RoundFloorAndCeiling(long tick, long step, long round, long floor, long ceiling)
    {
        Assert.Equal(round, SongGrid.Round(tick, step));
        Assert.Equal(floor, SongGrid.Floor(tick, step));
        Assert.Equal(ceiling, SongGrid.Ceiling(tick, step));
    }

    [Theory]
    [InlineData(0, 4, 4, "001.1.000")]
    [InlineData(7680 + 960 + 12, 4, 4, "003.2.012")]
    [InlineData(1440 * 2 + 480, 6, 8, "002.2.000")]
    public void PositionsAreBarsBeatsTicks(long ticks, int beats, int unit, string expected)
    {
        var song = new Song { TimeSignature = [beats, unit] };
        Span<char> text = stackalloc char[24];
        var n = SongGrid.FormatPosition(text, ticks, song);
        Assert.Equal(expected, text[..n].ToString());
    }

    [Fact]
    public void QuantizeSnapsInSongTimeAsOneEntry()
    {
        var clip = NewClip(start: 100); // off the grid: notes snap to song time, not clip time
        var a = Doc.AddNote(clip, 60, 30, 240);
        var b = Doc.AddNote(clip, 62, 500, 240);
        var before = Entries;
        Doc.QuantizeNotes(clip, [a, b], 240);
        Assert.Equal((140L, 620L), (a.Start, b.Start));
        Assert.Equal(before + 1, Entries);
        Doc.History.Undo();
        Assert.Equal((30L, 500L), (a.Start, b.Start));
    }
}
