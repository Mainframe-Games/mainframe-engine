using System.Diagnostics;

namespace MainframeEngine.Editor.Music;

/// <summary>Input quantize for MIDI recording (the transport's Q menu): snaps recorded note starts.</summary>
public enum MidiQuantize
{
    Off,
    Sixteenth,
    Eighth,
}

/// <summary>
/// A song tab's MIDI keyboard input (ADR 0148), on the UI thread. Live: notes go to the record-armed track (one at a
/// time), else the selected instrument track, and play its instrument through the engine's preview path (the engine's
/// command ring: no allocation per event, lock-free for the render thread); the sustain pedal (CC64) holds note-offs
/// while it is down; other controllers are ignored (the instrument API has none yet). Recording (<see cref="ToggleRecording"/>):
/// each message's timestamp is mapped to the transport position that was being <i>heard</i> when it was played (the
/// player's position minus the time since, minus <see cref="DeviceLatencySeconds"/>), so notes land where they were
/// played; the take is one new clip on the armed track — the recorded range, or the loop region with the loop on, every
/// pass adding to the same clip (overdub, committed at each wrap so earlier passes are heard) — and one undo entry.
/// </summary>
public sealed class SongMidiInput : IMidiInputSink
{
    /// <summary>The audio device's buffer, which the heard position does not include (an estimate: the engine's audio
    /// layer does not report it).</summary>
    public const double DefaultDeviceLatencySeconds = 0.010;

    private readonly SongDocument _document;
    private readonly SongPlayer _player;
    private readonly Func<SongTrack?> _selected;
    private readonly SongTrack?[] _sounding = new SongTrack?[128]; // the track each live pitch plays on
    private readonly bool[] _sustained = new bool[128];            // released while the pedal is down
    private bool _pedal;
    private Take? _take;
    private int _takes;

    public SongMidiInput(SongDocument document, SongPlayer player, Func<SongTrack?> selectedTrack)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _selected = selectedTrack ?? throw new ArgumentNullException(nameof(selectedTrack));
    }

    /// <summary>The record-armed track's id, or null.</summary>
    public string? ArmedTrackId { get; private set; }

    public SongTrack? ArmedTrack => ArmedTrackId is { } id ? _document.Song.Tracks.Find(t => t.Id == id && t.Kind == SongTrackKind.Instrument) : null;

    /// <summary>Where live notes go: the armed track, else the selected instrument track.</summary>
    public SongTrack? Target => ArmedTrack ?? (_selected() is { Kind: SongTrackKind.Instrument } track && _document.Song.Tracks.Contains(track) ? track : null);

    public bool IsRecording => _take is not null;

    public MidiQuantize Quantize { get; set; }

    /// <summary>See <see cref="DefaultDeviceLatencySeconds"/>.</summary>
    public double DeviceLatencySeconds { get; set; } = DefaultDeviceLatencySeconds;

    /// <summary>Bumped when the arm or record state changed (the panel redraws).</summary>
    public int Version { get; private set; }

    /// <summary>The clock MIDI timestamps are on (tests replace it).</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>The heard position in ticks (tests replace it; default: <see cref="SongPlayer.PositionTicks"/>).</summary>
    internal Func<double>? Position { get; set; }

    /// <summary>Arms <paramref name="track"/> for recording (disarming any other), or disarms it.</summary>
    public void ToggleArm(SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != SongTrackKind.Instrument)
            return;
        if (IsRecording)
            StopRecording();
        ArmedTrackId = ArmedTrackId == track.Id ? null : track.Id;
        Version++;
    }

    public void OnMidi(in MidiInputEvent midiEvent)
    {
        if (midiEvent.IsNoteOn)
            NoteOn(midiEvent.Data1, midiEvent.Data2, midiEvent.Timestamp);
        else if (midiEvent.IsNoteOff)
            NoteOff(midiEvent.Data1, midiEvent.Timestamp);
        else if (midiEvent.IsControlChange && midiEvent.Data1 == 64)
            Pedal(midiEvent.Data2 >= 64, midiEvent.Timestamp);
    }

    /// <summary>Record button / R: starts a take (and playback) or ends it. Returns why it could not start, or null.</summary>
    public string? ToggleRecording()
    {
        if (IsRecording)
        {
            StopRecording();
            return null;
        }

        return StartRecording();
    }

    /// <summary>Starts a take on the armed track (arming the selected instrument track when none is). Null on success.</summary>
    public string? StartRecording()
    {
        if (IsRecording)
            return null;
        if (_document.IsReadOnly)
            return "The song is read-only.";
        if (ArmedTrack is null)
        {
            if (_selected() is not { Kind: SongTrackKind.Instrument } selected)
                return "Arm an instrument track to record (the record button on its header).";
            ArmedTrackId = selected.Id;
        }

        if (!_player.IsPlaying)
            _player.Play();
        var loop = _document.Song.Loop;
        var pos = Heard(Clock());
        _take = new Take
        {
            Track = ArmedTrack!,
            Key = $"record:{++_takes}",
            Loop = loop.Enabled && loop.End > loop.Start,
            LoopStart = loop.Start,
            LoopEnd = loop.End,
            StartTick = pos,
            LastTick = pos,
        };
        Version++;
        return null;
    }

    /// <summary>Ends the take: open notes end now, the clip is written (one undo entry).</summary>
    public void StopRecording()
    {
        if (_take is not { } take)
            return;
        var now = Clock();
        var tick = Heard(now);
        for (var p = 0; p < 128; p++)
            take.End(p, tick, this);
        take.LastTick = Math.Max(take.LastTick, tick);
        Commit(take);
        _document.History.EndMerge();
        _take = null;
        Version++;
    }

    /// <summary>UI thread, every frame: ends the take when playback stopped; commits each loop pass (overdub).</summary>
    public void Update()
    {
        if (_take is not { } take)
            return;
        if (_player.IsPlaying)
            take.SawPlaying = true;
        else if (take.SawPlaying)
        {
            StopRecording();
            return;
        }

        var tick = Heard(Clock());
        if (take.Loop && tick < take.LastTick - (take.LoopEnd - take.LoopStart) / 2.0)
            Commit(take); // wrapped: the passes so far become audible
        take.LastTick = tick;
    }

    /// <summary>Releases every live note and ends a take (the tab was deactivated or closed).</summary>
    public void ReleaseAll()
    {
        StopRecording();
        var now = Clock();
        _pedal = false;
        for (var p = 0; p < 128; p++)
            if (_sounding[p] is not null)
                Release(p, now);
    }

    /// <summary>The transport position heard at <paramref name="timestamp"/> (Stopwatch ticks), in song ticks.</summary>
    public double Heard(long timestamp)
    {
        var song = _document.Song;
        var position = Position?.Invoke() ?? _player.PositionTicks;
        var ticksPerSecond = song.Tempo * song.Ppq / 60.0;
        var ago = Math.Max(0, (Clock() - timestamp) / (double)Stopwatch.Frequency);
        var tick = position - (ago + DeviceLatencySeconds) * ticksPerSecond;
        if (_take is { Loop: true } take && position >= take.LoopStart && tick < take.LoopStart)
            tick += take.LoopEnd - take.LoopStart; // played just before the wrap: the loop's end
        return Math.Max(0, tick);
    }

    private void NoteOn(int pitch, int velocity, long timestamp)
    {
        if (Target is not { } track)
            return;
        if (_sounding[pitch] is not null)
            Release(pitch, timestamp); // struck again while held or sustained
        _sounding[pitch] = track;
        _player.PreviewNote(track, pitch, velocity);
        if (_take is { } take && ReferenceEquals(take.Track, track))
            take.Begin(pitch, velocity, Heard(timestamp));
    }

    private void NoteOff(int pitch, long timestamp)
    {
        if (_sounding[pitch] is null)
            return;
        if (_pedal)
            _sustained[pitch] = true;
        else
            Release(pitch, timestamp);
    }

    private void Pedal(bool down, long timestamp)
    {
        _pedal = down;
        if (down)
            return;
        for (var p = 0; p < 128; p++)
            if (_sustained[p])
                Release(p, timestamp);
    }

    private void Release(int pitch, long timestamp)
    {
        if (_sounding[pitch] is { } track)
            _player.ReleaseNote(track, pitch);
        _sounding[pitch] = null;
        _sustained[pitch] = false;
        _take?.End(pitch, Heard(timestamp), this);
    }

    private long Step => Quantize switch
    {
        MidiQuantize.Sixteenth => Math.Max(1, _document.Song.Ppq / 4),
        MidiQuantize.Eighth => Math.Max(1, _document.Song.Ppq / 2),
        _ => 1,
    };

    // Writes the take so far: one clip, the same history entry every time (merge key).
    private void Commit(Take take)
    {
        if (take.Notes.Count == 0 || !_document.Song.Tracks.Contains(take.Track))
            return;
        long start, end;
        if (take.Loop)
        {
            start = take.LoopStart;
            end = take.LoopEnd;
        }
        else
        {
            var bar = Math.Max(1, _document.Song.TicksPerBar);
            var first = Math.Min(take.StartTick, take.Notes.Min(n => n.Start));
            var last = Math.Max(take.LastTick, take.Notes.Max(n => n.Start + n.Length));
            start = (long)Math.Floor(first / bar) * bar;
            end = Math.Max(start + bar, (long)Math.Ceiling(last / bar) * bar);
        }

        var span = end - start;
        var notes = new List<MidiNote>(take.Notes.Count);
        foreach (var n in take.Notes)
        {
            var at = Math.Clamp(n.Start - start, 0, span - 1);
            notes.Add(new MidiNote { Pitch = n.Pitch, Start = at, Length = Math.Clamp(n.Length, 1, span - at), Velocity = n.Velocity });
        }

        _document.CommitRecording(take.Track, take.Clip, start, span, notes, take.Key);
    }

    private sealed class Take
    {
        public required SongTrack Track { get; init; }
        public required string Key { get; init; }
        public required bool Loop { get; init; }
        public required long LoopStart { get; init; }
        public required long LoopEnd { get; init; }
        public required double StartTick { get; init; }
        public double LastTick { get; set; }
        public bool SawPlaying { get; set; }
        public MidiClip Clip { get; } = new();

        // Absolute ticks.
        public List<MidiNote> Notes { get; } = [];

        private readonly double[] _openStart = new double[128];
        private readonly int[] _openVelocity = new int[128]; // 0: not open

        public void Begin(int pitch, int velocity, double tick)
        {
            _openStart[pitch] = tick;
            _openVelocity[pitch] = Math.Clamp(velocity, 1, 127);
        }

        public void End(int pitch, double tick, SongMidiInput owner)
        {
            if (_openVelocity[pitch] == 0)
                return;
            var start = _openStart[pitch];
            var length = tick - start;
            if (length <= 0 && Loop)
                length = LoopEnd - start; // released after the wrap: held to the loop's end
            var step = owner.Step;
            var at = (long)Math.Round(start / step) * step;
            if (Loop && at >= LoopEnd)
                at = LoopStart; // quantized onto the wrap: the loop's first step
            Notes.Add(new MidiNote { Pitch = pitch, Start = at, Length = Math.Max(1, (long)Math.Round(length)), Velocity = _openVelocity[pitch] });
            _openVelocity[pitch] = 0;
        }
    }
}
