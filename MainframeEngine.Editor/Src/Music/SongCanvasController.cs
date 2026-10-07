using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Music;

/// <summary>What a mouse gesture on the song canvases is doing.</summary>
public enum SongGesture
{
    None,
    Seek,
    LoopNew,
    LoopMove,
    LoopStart,
    LoopEnd,
    ClipMove,
    ClipResizeLeft,
    ClipResizeRight,
    RollPending,   // pressed on an empty grid cell: a click adds a note, a drag becomes a rubber band
    RubberBand,
    NoteMove,
    NoteResize,
    Velocity,
    Keyboard,
}

/// <summary>
/// The song tab's canvas input: hit testing in ticks and pitches and the mouse and keyboard gestures of the arrangement
/// and the piano roll. Positions are local to each canvas, in dp. Every edit goes through the <see cref="SongDocument"/>
/// as one history entry per gesture: note and velocity drags and loop drags edit live with a merge key (ended on
/// release); clip moves, copies and resizes are previewed and committed on release.
/// </summary>
public sealed class SongCanvasController
{
    public const float RulerHeight = 22;
    public const float LoopBandHeight = 10;
    public const float LaneHeight = 44;
    public const float EdgeGrab = 6;
    public const float KeyboardWidth = 52;
    public const float RollRulerHeight = 18;
    public const float VelocityHeight = 56;
    public const float RowHeight = 12;
    public const float DragThreshold = 4;
    private const double AuditionSeconds = 0.25;

    private static readonly List<MidiNote> Clipboard = [];

    private readonly SongTab _tab;
    private readonly List<MidiNote> _velocityTargets = [];
    private Vector2 _press;
    private double _pressTick;
    private int _pressPitch;
    private long _origStart;
    private long _origLength;
    private int _origPitch;
    private SongTrack? _origTrack;
    private MidiNote? _anchor;
    private bool _moved;
    private SongTrack? _auditionTrack;
    private int _auditionPitch = -1;
    private double _auditionRelease = -1;
    private double _time;

    public SongCanvasController(SongTab tab)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
    }

    /// <summary>The arrangement canvas size in dp (set by the <see cref="SongView"/> each frame).</summary>
    public Vector2 ArrangeSize { get; set; } = new(800, 300);

    /// <summary>The piano-roll canvas size in dp.</summary>
    public Vector2 RollSize { get; set; } = new(800, 300);

    public SongGesture Gesture { get; private set; }

    /// <summary>A clip drag's preview: where the clip (or its copy) would land.</summary>
    public SongClip? GhostClip { get; private set; }

    public long GhostStart { get; private set; }

    public long GhostLength { get; private set; }

    public SongTrack? GhostTrack { get; private set; }

    /// <summary>Alt-drag: the clip is copied on release.</summary>
    public bool GhostCopy { get; private set; }

    /// <summary>The rubber band in piano-roll coordinates (dp) while <see cref="Gesture"/> is <see cref="SongGesture.RubberBand"/>.</summary>
    public (Vector2 From, Vector2 To) RubberBand => (_press, _last);

    /// <summary>Asks for an audio file for a new clip on <c>track</c> at <c>tick</c> (double-click on an audio lane).</summary>
    public Action<SongTrack, long>? RequestAudioClip { get; set; }

    /// <summary>The length of an audio file in ticks (null: unknown); used for dropped and picked files.</summary>
    public Func<string, long?>? AudioLength { get; set; }

    private Vector2 _last;

    private SongDocument Doc => _tab.Document;

    private Song Song => _tab.Document.Song;

    private long Step => SongGrid.Step(_tab.Snap, Song.Ppq);

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────────────────

    public double ArrangeTickAt(float x) => _tab.ArrangeScrollTick + x / _tab.ArrangePixelsPerTick;

    public float ArrangeX(double tick) => (float)((tick - _tab.ArrangeScrollTick) * _tab.ArrangePixelsPerTick);

    /// <summary>The lane index at <paramref name="y"/> (may be out of range; −1 above the lanes).</summary>
    public int LaneAt(float y) => y < RulerHeight ? -1 : (int)MathF.Floor((y - RulerHeight + _tab.ArrangeScrollY) / LaneHeight);

    public float LaneY(int lane) => RulerHeight + lane * LaneHeight - _tab.ArrangeScrollY;

    /// <summary>The clip-relative tick at piano-roll x.</summary>
    public double RollTickAt(float x) => _tab.RollScrollTick + (x - KeyboardWidth) / _tab.RollPixelsPerTick;

    public float RollX(double relativeTick) => KeyboardWidth + (float)((relativeTick - _tab.RollScrollTick) * _tab.RollPixelsPerTick);

    public int PitchAt(float y) => 127 - (int)MathF.Floor((y - RollRulerHeight + _tab.RollScrollY) / RowHeight);

    /// <summary>The top of <paramref name="pitch"/>'s row.</summary>
    public float PitchY(int pitch) => RollRulerHeight + (127 - pitch) * RowHeight - _tab.RollScrollY;

    /// <summary>The bottom of the note grid (the velocity lane starts here).</summary>
    public float GridBottom => RollSize.Y - VelocityHeight;

    /// <summary>A clip-relative tick snapped so the song-absolute position is on the grid.</summary>
    public long SnapRelative(MidiClip clip, double relative, bool floor = false)
    {
        var absolute = (long)Math.Round(clip.Start + relative);
        return (floor ? SongGrid.Floor(absolute, Step) : SongGrid.Round(absolute, Step)) - clip.Start;
    }

    public static SongClip? ClipAt(SongTrack track, double tick)
    {
        for (var i = track.Clips.Count - 1; i >= 0; i--)
            if (tick >= track.Clips[i].Start && tick < track.Clips[i].End)
                return track.Clips[i];
        return null;
    }

    public MidiNote? NoteAt(MidiClip clip, Vector2 p)
    {
        var pitch = PitchAt(p.Y);
        for (var i = clip.Notes.Count - 1; i >= 0; i--)
        {
            var n = clip.Notes[i];
            if (n.Pitch == pitch && p.X >= RollX(n.Start) && p.X < MathF.Max(RollX(n.Start + n.Length), RollX(n.Start) + 3))
                return n;
        }

        return null;
    }

    private bool CanEdit => !Doc.IsReadOnly;

    // ── Mouse ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A mouse button went down at <paramref name="p"/> in <paramref name="area"/> (left button only).</summary>
    public void Press(SongArea area, Vector2 p, EditorModifiers modifiers = EditorModifiers.None, bool doubleClick = false)
    {
        CancelGesture();
        _press = p;
        _last = p;
        _moved = false;
        _tab.Focus = area;
        if (area == SongArea.Arrangement)
            PressArrangement(p, modifiers, doubleClick);
        else if (area == SongArea.PianoRoll)
            PressRoll(p, modifiers);
    }

    /// <summary>The mouse moved to <paramref name="p"/> (in the area the gesture started in) with the button held.</summary>
    public void Drag(Vector2 p, EditorModifiers modifiers = EditorModifiers.None)
    {
        _last = p;
        if (Vector2.Distance(p, _press) > DragThreshold)
            _moved = true;
        switch (Gesture)
        {
            case SongGesture.Seek:
                _tab.Player.Seek(Math.Max(0, SnapTick(ArrangeTickAt(p.X))));
                break;
            case SongGesture.LoopNew or SongGesture.LoopMove or SongGesture.LoopStart or SongGesture.LoopEnd:
                DragLoop(p);
                break;
            case SongGesture.ClipMove or SongGesture.ClipResizeLeft or SongGesture.ClipResizeRight:
                DragClip(p, modifiers);
                break;
            case SongGesture.RollPending when _moved:
                Gesture = SongGesture.RubberBand;
                UpdateRubberBand(modifiers);
                break;
            case SongGesture.RubberBand:
                UpdateRubberBand(modifiers);
                break;
            case SongGesture.NoteMove:
                DragNotes(p);
                break;
            case SongGesture.NoteResize:
                ResizeNotes(p);
                break;
            case SongGesture.Velocity:
                Doc.SetNoteVelocity(_velocityTargets, VelocityAt(p.Y), "song-velocity");
                break;
            case SongGesture.Keyboard:
                var pitch = Math.Clamp(PitchAt(p.Y), 0, 127);
                if (pitch != _auditionPitch && _tab.SelectedTrack is { } track)
                    Audition(track, pitch, hold: true);
                break;
        }
    }

    /// <summary>The button went up: the gesture is committed (one history entry).</summary>
    public void Release(Vector2 p)
    {
        _last = p;
        var gesture = Gesture;
        Gesture = SongGesture.None;
        switch (gesture)
        {
            case SongGesture.ClipMove or SongGesture.ClipResizeLeft or SongGesture.ClipResizeRight:
                CommitClip(gesture);
                break;
            case SongGesture.RollPending:
                AddNoteAt(_press);
                break;
            case SongGesture.Keyboard or SongGesture.NoteMove or SongGesture.NoteResize:
                StopAudition();
                break;
        }

        GhostClip = null;
        GhostTrack = null;
        Doc.History.EndMerge();
        _tab.Touch();
    }

    /// <summary>Drops any gesture in progress (tab deactivated, dialog opened).</summary>
    public void CancelGesture()
    {
        if (Gesture == SongGesture.None)
            return;
        Gesture = SongGesture.None;
        GhostClip = null;
        GhostTrack = null;
        Doc.History.EndMerge();
        StopAudition();
    }

    /// <summary>Wheel: scroll; Shift (or a horizontal wheel) scrolls in time; Cmd/Ctrl zooms time around the mouse.</summary>
    public void Wheel(SongArea area, Vector2 p, Vector2 delta, EditorModifiers modifiers = EditorModifiers.None)
    {
        var zoom = (modifiers & EditorModifiers.Command) != 0;
        var horizontal = (modifiers & EditorModifiers.Shift) != 0 ? delta.Y + delta.X : delta.X;
        if (area == SongArea.Arrangement)
        {
            if (zoom)
            {
                var anchor = ArrangeTickAt(p.X);
                _tab.ArrangePixelsPerTick = Math.Clamp(_tab.ArrangePixelsPerTick * Math.Pow(1.15, delta.Y), 4.0 / Song.Ppq / 16, 400.0 / Song.Ppq);
                _tab.ArrangeScrollTick = Math.Max(0, anchor - p.X / _tab.ArrangePixelsPerTick);
            }
            else if (horizontal != 0 || (modifiers & EditorModifiers.Shift) != 0)
            {
                _tab.ArrangeScrollTick = Math.Max(0, _tab.ArrangeScrollTick - horizontal * 40 / _tab.ArrangePixelsPerTick);
            }
            else
            {
                var max = MathF.Max(0, Song.Tracks.Count * LaneHeight - (ArrangeSize.Y - RulerHeight));
                _tab.ArrangeScrollY = Math.Clamp(_tab.ArrangeScrollY - delta.Y * LaneHeight * 0.5f, 0, max);
            }
        }
        else if (area == SongArea.PianoRoll)
        {
            if (zoom)
            {
                var anchor = RollTickAt(p.X);
                _tab.RollPixelsPerTick = Math.Clamp(_tab.RollPixelsPerTick * Math.Pow(1.15, delta.Y), 4.0 / Song.Ppq, 2000.0 / Song.Ppq);
                _tab.RollScrollTick = anchor - (p.X - KeyboardWidth) / _tab.RollPixelsPerTick;
            }
            else if (horizontal != 0 || (modifiers & EditorModifiers.Shift) != 0)
            {
                _tab.RollScrollTick -= horizontal * 40 / _tab.RollPixelsPerTick;
            }
            else
            {
                var max = MathF.Max(0, 128 * RowHeight - (GridBottom - RollRulerHeight));
                _tab.RollScrollY = Math.Clamp(_tab.RollScrollY - delta.Y * RowHeight * 2, 0, max);
            }
        }
    }

    /// <summary>A file dragged from the FileSystem panel was dropped on the arrangement at <paramref name="p"/>.</summary>
    public AudioClip? Drop(string projectFile, Vector2 p)
    {
        if (!CanEdit)
            return null;
        var lane = LaneAt(p.Y);
        var track = lane >= 0 && lane < Song.Tracks.Count && Song.Tracks[lane].Kind == SongTrackKind.Audio ? Song.Tracks[lane] : null;
        track ??= Doc.AddTrack(SongTrackKind.Audio, Path.GetFileNameWithoutExtension(projectFile));
        var clip = AddAudioClip(track, projectFile, Math.Max(0, SnapTick(ArrangeTickAt(p.X), floor: true)));
        Doc.History.EndMerge();
        return clip;
    }

    /// <summary>Adds an audio clip of the file's length (one bar when unknown) and selects it.</summary>
    public AudioClip AddAudioClip(SongTrack track, string projectFile, long start)
    {
        var length = AudioLength?.Invoke(projectFile) ?? Song.TicksPerBar;
        var clip = Doc.AddAudioClip(track, projectFile, start, Math.Max(1, length));
        _tab.SelectClip(track, clip);
        return clip;
    }

    // ── Arrangement ──────────────────────────────────────────────────────────────────────────────────────────────

    private void PressArrangement(Vector2 p, EditorModifiers modifiers, bool doubleClick)
    {
        var tick = ArrangeTickAt(p.X);
        if (p.Y < RulerHeight)
        {
            if (p.Y < LoopBandHeight + 2 && CanEdit)
            {
                var loop = Song.Loop;
                var xs = ArrangeX(loop.Start);
                var xe = ArrangeX(loop.End);
                _origStart = loop.Start;
                _origLength = loop.End - loop.Start;
                _pressTick = tick;
                Gesture = MathF.Abs(p.X - xs) <= EdgeGrab ? SongGesture.LoopStart
                    : MathF.Abs(p.X - xe) <= EdgeGrab ? SongGesture.LoopEnd
                    : p.X > xs && p.X < xe ? SongGesture.LoopMove
                    : SongGesture.LoopNew;
                if (Gesture == SongGesture.LoopNew)
                    _origStart = Math.Max(0, SnapTick(tick, floor: true));
            }
            else
            {
                Gesture = SongGesture.Seek;
                _tab.Player.Seek(Math.Max(0, SnapTick(tick)));
            }

            return;
        }

        var lane = LaneAt(p.Y);
        if (lane < 0 || lane >= Song.Tracks.Count)
        {
            _tab.SelectClip(null, null);
            return;
        }

        var track = Song.Tracks[lane];
        _tab.SelectTrack(track);
        var clip = ClipAt(track, tick);
        if (clip is null)
        {
            _tab.SelectClip(track, null);
            if (!doubleClick || !CanEdit)
                return;
            var start = Math.Max(0, SongGrid.Floor((long)tick, Song.TicksPerBar));
            if (track.Kind == SongTrackKind.Instrument)
            {
                var created = Doc.AddMidiClip(track, start, Song.TicksPerBar);
                _tab.SelectClip(track, created);
                _tab.SetPanel(SongBottomPanel.PianoRoll);
            }
            else
            {
                RequestAudioClip?.Invoke(track, start);
            }

            return;
        }

        _tab.SelectClip(track, clip);
        if (doubleClick && clip is MidiClip)
        {
            _tab.SetPanel(SongBottomPanel.PianoRoll);
            return;
        }

        if (!CanEdit)
            return;
        var left = ArrangeX(clip.Start);
        var right = ArrangeX(clip.End);
        var wide = right - left > EdgeGrab * 3;
        Gesture = wide && right - p.X <= EdgeGrab ? SongGesture.ClipResizeRight
            : wide && p.X - left <= EdgeGrab ? SongGesture.ClipResizeLeft
            : SongGesture.ClipMove;
        _pressTick = tick;
        _origStart = clip.Start;
        _origLength = clip.Length;
        _origTrack = track;
        GhostClip = clip;
        GhostStart = clip.Start;
        GhostLength = clip.Length;
        GhostTrack = track;
        GhostCopy = Gesture == SongGesture.ClipMove && (modifiers & EditorModifiers.Alt) != 0;
    }

    private void DragLoop(Vector2 p)
    {
        var loop = Song.Loop;
        var delta = ArrangeTickAt(p.X) - _pressTick;
        long start, end;
        switch (Gesture)
        {
            case SongGesture.LoopStart:
                start = Math.Max(0, SnapTick(_origStart + delta));
                end = loop.End;
                break;
            case SongGesture.LoopEnd:
                start = loop.Start;
                end = SnapTick(_origStart + _origLength + delta);
                break;
            case SongGesture.LoopMove:
                start = Math.Max(0, SnapTick(_origStart + delta));
                end = start + _origLength;
                break;
            default: // LoopNew: from the press to the mouse, either way
                var at = Math.Max(0, SnapTick(ArrangeTickAt(p.X)));
                if (!_moved)
                    return;
                (start, end) = at >= _origStart ? (_origStart, at) : (at, _origStart);
                break;
        }

        if (end <= start)
            end = start + Math.Max(1, Step);
        Doc.SetLoop(Gesture == SongGesture.LoopNew || loop.Enabled, start, end, "song-loop");
    }

    private void DragClip(Vector2 p, EditorModifiers modifiers)
    {
        if (GhostClip is not { } clip || _origTrack is null)
            return;
        var delta = ArrangeTickAt(p.X) - _pressTick;
        var step = Step;
        switch (Gesture)
        {
            case SongGesture.ClipMove:
                GhostStart = Math.Max(0, SnapTick(_origStart + delta));
                var lane = LaneAt(p.Y);
                GhostTrack = lane >= 0 && lane < Song.Tracks.Count && Song.Tracks[lane].Kind == _origTrack.Kind ? Song.Tracks[lane] : GhostTrack;
                GhostCopy = (modifiers & EditorModifiers.Alt) != 0 || GhostCopy;
                break;
            case SongGesture.ClipResizeRight:
                var end = SnapTick(_origStart + _origLength + delta);
                GhostStart = _origStart;
                GhostLength = Math.Max(Math.Max(1, step), end - _origStart);
                break;
            case SongGesture.ClipResizeLeft:
                var originalEnd = _origStart + _origLength;
                var start = Math.Clamp(SnapTick(_origStart + delta), 0, originalEnd - Math.Max(1, step));
                if (clip is AudioClip audio)
                    start = Math.Max(start, _origStart - audio.Offset); // cannot reveal audio before the file's start
                GhostStart = start;
                GhostLength = originalEnd - start;
                break;
        }
    }

    private void CommitClip(SongGesture gesture)
    {
        if (GhostClip is not { } clip || _origTrack is not { } track || !_moved)
            return;
        if (gesture == SongGesture.ClipMove)
        {
            var target = GhostTrack ?? track;
            if (GhostCopy)
            {
                var copy = clip.Clone();
                copy.Start = GhostStart;
                Doc.AddClip(target, copy);
                _tab.SelectClip(target, copy);
            }
            else if (GhostStart != clip.Start || !ReferenceEquals(target, track))
            {
                Doc.MoveClip(track, clip, GhostStart, ReferenceEquals(target, track) ? null : target);
                _tab.SelectClip(target, clip);
            }
        }
        else if (GhostStart != clip.Start || GhostLength != clip.Length)
        {
            Doc.ResizeClip(clip, GhostStart, GhostLength);
        }
    }

    /// <summary>Duplicates the selected clip right after itself (Cmd/Ctrl+D in the arrangement).</summary>
    public SongClip? DuplicateClip()
    {
        if (!CanEdit || _tab.SelectedClip is not { } clip || _tab.TrackOf(clip) is not { } track)
            return null;
        var copy = clip.Clone();
        copy.Start = clip.End;
        Doc.AddClip(track, copy);
        _tab.SelectClip(track, copy);
        return copy;
    }

    // ── Piano roll ───────────────────────────────────────────────────────────────────────────────────────────────

    private void PressRoll(Vector2 p, EditorModifiers modifiers)
    {
        if (_tab.RollClip is not { } clip || _tab.SelectedTrack is not { } track)
            return;
        if (p.Y < RollRulerHeight)
        {
            Gesture = SongGesture.Seek;
            _tab.Player.Seek(Math.Max(0, clip.Start + SnapRelative(clip, RollTickAt(p.X))));
            return;
        }

        if (p.X < KeyboardWidth && p.Y < GridBottom)
        {
            Gesture = SongGesture.Keyboard;
            Audition(track, Math.Clamp(PitchAt(p.Y), 0, 127), hold: true);
            return;
        }

        if (p.Y >= GridBottom)
        {
            if (!CanEdit || VelocityNoteAt(clip, p.X) is not { } target)
                return;
            _velocityTargets.Clear();
            if (_tab.SelectedNotes.Contains(target))
                _velocityTargets.AddRange(_tab.SelectedNotes);
            else
                _velocityTargets.Add(target);
            Gesture = SongGesture.Velocity;
            Doc.SetNoteVelocity(_velocityTargets, VelocityAt(p.Y), "song-velocity");
            return;
        }

        var shift = (modifiers & EditorModifiers.Shift) != 0;
        var note = NoteAt(clip, p);
        if (note is null)
        {
            if (!shift)
                _tab.SelectedNotes.Clear();
            Gesture = SongGesture.RollPending;
            _tab.Touch();
            return;
        }

        if (shift)
        {
            if (!_tab.SelectedNotes.Remove(note))
                _tab.SelectedNotes.Add(note);
            _tab.Touch();
            if (!_tab.SelectedNotes.Contains(note))
                return;
        }
        else if (!_tab.SelectedNotes.Contains(note))
        {
            _tab.SelectedNotes.Clear();
            _tab.SelectedNotes.Add(note);
            _tab.Touch();
        }

        _tab.LastNoteLength = note.Length;
        Audition(track, note.Pitch, hold: true, note.Velocity);
        if (!CanEdit)
            return;
        _anchor = note;
        _origStart = note.Start;
        _origLength = note.Length;
        _origPitch = note.Pitch;
        _pressTick = RollTickAt(p.X);
        _pressPitch = PitchAt(p.Y);
        var right = RollX(note.Start + note.Length);
        Gesture = right - p.X <= 5 && right - RollX(note.Start) > 10 ? SongGesture.NoteResize : SongGesture.NoteMove;
    }

    private void DragNotes(Vector2 p)
    {
        if (_anchor is not { } anchor || _tab.RollClip is not { } clip)
            return;
        var target = SnapRelative(clip, _origStart + (RollTickAt(p.X) - _pressTick));
        var deltaTicks = target - _origStart - (anchor.Start - _origStart);
        var deltaPitch = PitchAt(p.Y) - _pressPitch - (anchor.Pitch - _origPitch);
        if (deltaTicks == 0 && deltaPitch == 0)
            return;
        Doc.MoveNotes(_tab.SelectedNotes, deltaTicks, deltaPitch, "song-notes-move");
        if (deltaPitch != 0 && _tab.SelectedTrack is { } track)
            Audition(track, anchor.Pitch, hold: true, anchor.Velocity);
    }

    private void ResizeNotes(Vector2 p)
    {
        if (_anchor is not { } anchor || _tab.RollClip is not { } clip)
            return;
        var end = SnapRelative(clip, _origStart + _origLength + (RollTickAt(p.X) - _pressTick));
        var length = Math.Max(Math.Max(1, Step), end - anchor.Start);
        var delta = length - anchor.Length;
        if (delta == 0)
            return;
        Doc.ResizeNotes(_tab.SelectedNotes, delta, "song-notes-resize");
        _tab.LastNoteLength = anchor.Length;
    }

    private void UpdateRubberBand(EditorModifiers modifiers)
    {
        if (_tab.RollClip is not { } clip)
            return;
        var minX = MathF.Min(_press.X, _last.X);
        var maxX = MathF.Max(_press.X, _last.X);
        var minY = MathF.Min(_press.Y, _last.Y);
        var maxY = MathF.Max(_press.Y, _last.Y);
        if ((modifiers & EditorModifiers.Shift) == 0)
            _tab.SelectedNotes.Clear();
        foreach (var n in clip.Notes)
        {
            var x0 = RollX(n.Start);
            var x1 = RollX(n.Start + n.Length);
            var y0 = PitchY(n.Pitch);
            if (x1 >= minX && x0 <= maxX && y0 + RowHeight >= minY && y0 <= maxY && !_tab.SelectedNotes.Contains(n))
                _tab.SelectedNotes.Add(n);
        }

        _tab.Touch();
    }

    private void AddNoteAt(Vector2 p)
    {
        if (!CanEdit || _tab.RollClip is not { } clip || _tab.SelectedTrack is not { } track)
            return;
        var pitch = Math.Clamp(PitchAt(p.Y), 0, 127);
        var start = Math.Max(0, SnapRelative(clip, RollTickAt(p.X), floor: true));
        var note = Doc.AddNote(clip, pitch, start, Math.Max(1, _tab.LastNoteLength));
        _tab.SelectedNotes.Clear();
        _tab.SelectedNotes.Add(note);
        _tab.Touch();
        Audition(track, pitch, hold: false, note.Velocity);
    }

    private MidiNote? VelocityNoteAt(MidiClip clip, float x)
    {
        MidiNote? best = null;
        foreach (var n in clip.Notes)
        {
            var x0 = RollX(n.Start);
            if (x < x0 - 3 || x > x0 + MathF.Max(6, RollX(n.Start + n.Length) - x0))
                continue;
            if (best is null || (_tab.SelectedNotes.Contains(n) && !_tab.SelectedNotes.Contains(best)) || n.Start > best.Start)
                best = n;
        }

        return best;
    }

    /// <summary>The velocity a y in the velocity lane stands for (top 127, bottom 1).</summary>
    public int VelocityAt(float y)
    {
        var top = GridBottom + 6;
        var height = VelocityHeight - 10;
        return Math.Clamp((int)MathF.Round(127 * (1 - (y - top) / height)), 1, 127);
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The song tab's keys: Space play/stop, L loop, Home to start; in the piano roll Del, Cmd/Ctrl+C/V/D, arrows (nudge
    /// by the snap; Shift: an octave), Q quantize, Esc deselect; in the arrangement Del and Cmd/Ctrl+D. False when the key
    /// is not one of them (the editor's own shortcuts then apply).
    /// </summary>
    public bool Key(Key key, EditorModifiers modifiers)
    {
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        if (command)
        {
            switch (key)
            {
                case Silk.NET.Input.Key.C when _tab.Focus == SongArea.PianoRoll:
                    CopyNotes();
                    return true;
                case Silk.NET.Input.Key.V when _tab.Focus == SongArea.PianoRoll:
                    PasteNotes();
                    return true;
                case Silk.NET.Input.Key.D:
                    Duplicate();
                    return true;
                default:
                    return false;
            }
        }

        switch (key)
        {
            case Silk.NET.Input.Key.Space:
                _tab.Player.TogglePlay();
                return true;
            case Silk.NET.Input.Key.L:
                ToggleLoop();
                return true;
            case Silk.NET.Input.Key.Home:
                _tab.Player.Seek(0);
                _tab.ArrangeScrollTick = 0;
                return true;
            case Silk.NET.Input.Key.Delete or Silk.NET.Input.Key.Backspace:
                Delete();
                return true;
            case Silk.NET.Input.Key.Escape when _tab.SelectedNotes.Count > 0:
                _tab.SelectedNotes.Clear();
                _tab.Touch();
                return true;
            case Silk.NET.Input.Key.Q when _tab.Focus == SongArea.PianoRoll:
                Quantize();
                return true;
            case Silk.NET.Input.Key.Left or Silk.NET.Input.Key.Right when _tab.Focus == SongArea.PianoRoll && _tab.SelectedNotes.Count > 0:
                if (CanEdit)
                    Doc.MoveNotes(_tab.SelectedNotes, (key == Silk.NET.Input.Key.Left ? -1 : 1) * SongGrid.NudgeStep(_tab.Snap, Song.Ppq), 0);
                return true;
            case Silk.NET.Input.Key.Up or Silk.NET.Input.Key.Down when _tab.Focus == SongArea.PianoRoll && _tab.SelectedNotes.Count > 0:
                if (CanEdit)
                    Doc.MoveNotes(_tab.SelectedNotes, 0, (key == Silk.NET.Input.Key.Up ? 1 : -1) * (shift ? 12 : 1));
                return true;
            default:
                return false;
        }
    }

    public void ToggleLoop()
    {
        if (!CanEdit)
            return;
        var loop = Song.Loop;
        var end = loop.End > loop.Start ? loop.End : loop.Start + Song.TicksPerBar * 4;
        Doc.SetLoop(!loop.Enabled, loop.Start, end);
    }

    /// <summary>Del: the selected notes (piano roll) or the selected clip (arrangement).</summary>
    public void Delete()
    {
        if (!CanEdit)
            return;
        if (_tab.Focus == SongArea.PianoRoll && _tab.RollClip is { } clip && _tab.SelectedNotes.Count > 0)
        {
            Doc.RemoveNotes(clip, [.. _tab.SelectedNotes]);
            _tab.SelectedNotes.Clear();
            _tab.Touch();
        }
        else if (_tab.Focus == SongArea.Arrangement && _tab.SelectedClip is { } selected && _tab.TrackOf(selected) is { } track)
        {
            Doc.RemoveClip(track, selected);
            _tab.SelectClip(track, null);
        }
    }

    /// <summary>Cmd/Ctrl+D: the selected notes after themselves (piano roll), else the selected clip after itself.</summary>
    public void Duplicate()
    {
        if (_tab.Focus == SongArea.PianoRoll && _tab.SelectedNotes.Count > 0)
        {
            var source = new List<MidiNote>(_tab.SelectedNotes.Count);
            CopyInto(source);
            var (start, end) = Span(source);
            PasteAt(source, start + Math.Max(1, SongGrid.Ceiling(end - start, SongGrid.NudgeStep(_tab.Snap, Song.Ppq))));
        }
        else
        {
            DuplicateClip();
        }
    }

    public void CopyNotes()
    {
        if (_tab.SelectedNotes.Count == 0)
            return;
        Clipboard.Clear();
        CopyInto(Clipboard);
    }

    private void CopyInto(List<MidiNote> target)
    {
        foreach (var n in _tab.SelectedNotes)
            target.Add(n.Clone());
    }

    /// <summary>Cmd/Ctrl+V: the copied notes at the playhead when it is inside the clip, else after the copied notes.</summary>
    public void PasteNotes()
    {
        if (Clipboard.Count == 0 || _tab.RollClip is not { } clip)
            return;
        var playhead = (long)_tab.Player.PositionTicks - clip.Start;
        var (start, end) = Span(Clipboard);
        var at = playhead >= 0 && playhead < clip.Length
            ? SnapRelative(clip, playhead, floor: true)
            : start + Math.Max(1, SongGrid.Ceiling(end - start, SongGrid.NudgeStep(_tab.Snap, Song.Ppq)));
        PasteAt(Clipboard, at);
    }

    private void PasteAt(List<MidiNote> source, long at)
    {
        if (!CanEdit || _tab.RollClip is not { } clip || source.Count == 0)
            return;
        var (start, _) = Span(source);
        var notes = new List<MidiNote>(source.Count);
        foreach (var n in source)
        {
            var copy = n.Clone();
            copy.Start = Math.Max(0, at + n.Start - start);
            notes.Add(copy);
        }

        Doc.AddNotes(clip, notes);
        _tab.SelectedNotes.Clear();
        _tab.SelectedNotes.AddRange(notes);
        _tab.Touch();
    }

    private static (long Start, long End) Span(List<MidiNote> notes)
    {
        long start = long.MaxValue, end = long.MinValue;
        foreach (var n in notes)
        {
            start = Math.Min(start, n.Start);
            end = Math.Max(end, n.Start + n.Length);
        }

        return (start, end);
    }

    /// <summary>Q: snaps the selected notes (or all of the clip's when none is selected) to the grid.</summary>
    public void Quantize()
    {
        if (!CanEdit || _tab.RollClip is not { } clip)
            return;
        var step = SongGrid.NudgeStep(_tab.Snap, Song.Ppq);
        Doc.QuantizeNotes(clip, _tab.SelectedNotes.Count > 0 ? [.. _tab.SelectedNotes] : [.. clip.Notes], step);
    }

    // ── Audition ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void Audition(SongTrack track, int pitch, bool hold, int velocity = 100)
    {
        StopAudition();
        if (track.Kind != SongTrackKind.Instrument)
            return;
        _tab.Player.PreviewNote(track, pitch, velocity);
        _auditionTrack = track;
        _auditionPitch = pitch;
        _auditionRelease = hold ? -1 : _time + AuditionSeconds;
    }

    /// <summary>Plays <paramref name="pitch"/> on the track's instrument briefly (the mixer's audition button).</summary>
    public void AuditionBriefly(SongTrack track, int pitch = 60) => Audition(track, pitch, hold: false);

    private void StopAudition()
    {
        if (_auditionTrack is { } track && _auditionPitch >= 0)
            _tab.Player.ReleaseNote(track, _auditionPitch);
        _auditionTrack = null;
        _auditionPitch = -1;
        _auditionRelease = -1;
    }

    /// <summary>Every frame: ends short auditions.</summary>
    public void Tick(double deltaTime)
    {
        _time += deltaTime;
        if (_auditionRelease >= 0 && _time >= _auditionRelease)
            StopAudition();
    }

    private long SnapTick(double tick, bool floor = false)
    {
        var t = (long)Math.Round(tick);
        return floor ? SongGrid.Floor(t, Step) : SongGrid.Round(t, Step);
    }
}
