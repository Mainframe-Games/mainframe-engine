namespace MainframeEngine.Editor.Music;

/// <summary>
/// The song's sequencer and mixer, run in blocks of <see cref="BlockFrames"/> on one render thread (the live player's,
/// or the offline renderer's caller). Sample-accurate: each note's on and off land on their exact frame inside the
/// block; with a loop, the block is split at the loop end, notes still held get their note-off at the wrap, and the
/// sequence continues from the loop start in the same block. Stop and seek send all-notes-off.
/// </summary>
/// <remarks>
/// <para>Model edits arrive as immutable <see cref="SongSnapshot"/>s (<see cref="SetSnapshot"/>, any thread) swapped in
/// at the next block; transport and note previews are commands on a lock-free queue. Per-track state (event buffers,
/// held notes, gain ramps, meters) lives in preallocated slots, so a steady-state block allocates nothing.</para>
/// <para>Mixer per track: instrument or audio clips → (inserts, plugin phase) → volume × equal-power pan
/// (<see cref="AudioMath.PanGains"/>), ramped over the block → mute/solo → master volume. Peak meters accumulate per
/// track and for the master until the UI takes them (<see cref="TakeTrackPeak"/>).</para>
/// </remarks>
public sealed class SongEngine
{
    public const int BlockFrames = 256;

    /// <summary>Note events one track can receive per block (more are dropped and counted).</summary>
    public const int MaxEventsPerBlock = 256;

    /// <summary>Notes one track can hold at once (more are not tracked for note-off and counted).</summary>
    public const int MaxHeldNotes = 128;

    private const int CommandCapacity = 256;

    private readonly TrackRuntime[] _runtimes = new TrackRuntime[SongSnapshotBuilder.MaxTracks];
    private readonly Command[] _commands = new Command[CommandCapacity];
    private readonly Lock _producerGate = new();
    private long _commandHead;
    private long _commandTail;
    private SongSnapshot? _pending;
    private SongSnapshot? _current;
    private long _position;
    private bool _playing;
    private int _masterPeakBits;
    private long _blocks;
    private int _droppedEvents;

    public SongEngine(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
        for (var i = 0; i < _runtimes.Length; i++)
            _runtimes[i] = new TrackRuntime();
    }

    public int SampleRate { get; }

    /// <summary>The snapshot the render thread is playing (null before the first block after <see cref="SetSnapshot"/>).</summary>
    public SongSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Song position of the next block in frames (as of the last block).</summary>
    public long PositionFrames => Volatile.Read(ref _position);

    /// <summary>Song position in ticks (as of the last block).</summary>
    public double PositionTicks => Current is { } s ? s.FramesToTicks(PositionFrames) : 0;

    public bool IsPlaying => Volatile.Read(ref _playing);

    /// <summary>Blocks processed.</summary>
    public long Blocks => Volatile.Read(ref _blocks);

    /// <summary>Note events dropped because a track received more than <see cref="MaxEventsPerBlock"/> in one block.</summary>
    public int DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Any thread: the song to play from the next block on.</summary>
    public void SetSnapshot(SongSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SampleRate != SampleRate)
            throw new ArgumentException($"The snapshot is for {snapshot.SampleRate} Hz; the engine runs at {SampleRate} Hz.", nameof(snapshot));
        Volatile.Write(ref _pending, snapshot);
    }

    public void Play() => Enqueue(new Command(CommandType.Play, 0, 0, 0, 0));

    /// <summary>Stops (all notes off); the position stays where it is.</summary>
    public void Stop() => Enqueue(new Command(CommandType.Stop, 0, 0, 0, 0));

    /// <summary>Moves the position to <paramref name="frame"/> (all notes off).</summary>
    public void SeekFrames(long frame) => Enqueue(new Command(CommandType.Seek, Math.Max(0, frame), 0, 0, 0));

    /// <summary>Starts (velocity &gt; 0) or releases a note on a track's instrument outside the sequence (auditioning).</summary>
    public void PreviewNote(TrackSnapshot track, int pitch, int velocity)
    {
        ArgumentNullException.ThrowIfNull(track);
        Enqueue(new Command(CommandType.Note, track.SlotGeneration, track.Slot, pitch, velocity));
    }

    /// <summary>UI thread: the highest peak of a track's output since the last call (linear), then resets it.</summary>
    public float TakeTrackPeak(string trackId)
    {
        var track = Current?.Find(trackId);
        return track is null ? 0f : TakePeak(ref _runtimes[track.Slot].PeakBits);
    }

    /// <summary>UI thread: the highest master peak since the last call (linear), then resets it.</summary>
    public float TakeMasterPeak() => TakePeak(ref _masterPeakBits);

    /// <summary>Render thread: produces the next <c>output.Length / 2</c> (at most <see cref="BlockFrames"/>) stereo frames.</summary>
    public void Process(Span<float> output)
    {
        var frames = output.Length / 2;
        if (frames > BlockFrames)
            throw new ArgumentException($"At most {BlockFrames} frames per block.", nameof(output));
        output.Clear();
        ApplySnapshot();
        var song = _current;
        if (song is not null)
        {
            foreach (var track in song.Tracks)
            {
                var rt = _runtimes[track.Slot];
                rt.EventCount = 0;
                rt.Buffer.AsSpan(0, frames * 2).Clear();
            }
        }

        DrainCommands(song);
        if (song is null)
        {
            Volatile.Write(ref _blocks, _blocks + 1);
            return;
        }

        if (_playing)
            Sequence(song, frames);

        var master = song.MasterGain;
        var masterPeak = 0f;
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            var buffer = rt.Buffer.AsSpan(0, frames * 2);
            if (rt.EventCount > 1)
                SortEvents(rt.Events.AsSpan(0, rt.EventCount));
            track.Instrument?.Process(track, rt.Events.AsSpan(0, rt.EventCount), buffer);

            var audible = track.IsAudible(song.AnySolo);
            var targetL = audible ? track.Gain * track.PanLeft : 0f;
            var targetR = audible ? track.Gain * track.PanRight : 0f;
            if (float.IsNaN(rt.GainL))
                (rt.GainL, rt.GainR) = (targetL, targetR);
            var stepL = (targetL - rt.GainL) / frames;
            var stepR = (targetR - rt.GainR) / frames;
            var peak = 0f;
            for (var f = 0; f < frames; f++)
            {
                var gl = rt.GainL + stepL * (f + 1);
                var gr = rt.GainR + stepR * (f + 1);
                var l = buffer[2 * f] * gl;
                var r = buffer[2 * f + 1] * gr;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                output[2 * f] += l;
                output[2 * f + 1] += r;
            }

            (rt.GainL, rt.GainR) = (targetL, targetR);
            AccumulatePeak(ref rt.PeakBits, peak);
        }

        for (var i = 0; i < output.Length; i++)
        {
            var v = output[i] * master;
            output[i] = v;
            masterPeak = Math.Max(masterPeak, Math.Abs(v));
        }

        AccumulatePeak(ref _masterPeakBits, masterPeak);
        Volatile.Write(ref _blocks, _blocks + 1);
    }

    // --- sequencing ------------------------------------------------------------------------------

    private void Sequence(SongSnapshot song, int frames)
    {
        var position = _position;
        var offset = 0;
        while (offset < frames)
        {
            if (song.LoopEnabled && position == song.LoopEndFrame)
            {
                // The wrap: notes still held end here; the sequence goes on from the loop start.
                ReleaseHeld(song, offset);
                position = song.LoopStartFrame;
            }

            var remaining = frames - offset;
            var segment = song.LoopEnabled && position < song.LoopEndFrame
                ? (int)Math.Min(remaining, song.LoopEndFrame - position)
                : remaining;
            var from = position;
            var to = position + segment;
            foreach (var track in song.Tracks)
            {
                var rt = _runtimes[track.Slot];
                NoteOffs(rt, from, to, offset);
                NoteOns(rt, track.Notes, from, to, offset);
                MixClips(rt, track.AudioClips, from, to, offset);
            }

            position = to;
            offset += segment;
        }

        Volatile.Write(ref _position, position);
    }

    private void NoteOffs(TrackRuntime rt, long from, long to, int offset)
    {
        for (var i = rt.HeldCount - 1; i >= 0; i--)
        {
            ref var held = ref rt.Held[i];
            if (held.EndFrame >= to)
                continue;
            AddEvent(rt, new NoteEvent(offset + (int)Math.Max(0, held.EndFrame - from), held.Pitch, 0, false));
            rt.Held[i] = rt.Held[--rt.HeldCount];
        }
    }

    private void NoteOns(TrackRuntime rt, NoteSpan[] notes, long from, long to, int offset)
    {
        for (var i = FirstAtOrAfter(notes, from); i < notes.Length && notes[i].StartFrame < to; i++)
        {
            var note = notes[i];
            AddEvent(rt, new NoteEvent(offset + (int)(note.StartFrame - from), note.Pitch, note.Velocity, true));
            if (note.EndFrame < to)
            {
                AddEvent(rt, new NoteEvent(offset + (int)(note.EndFrame - from), note.Pitch, 0, false));
            }
            else if (rt.HeldCount < MaxHeldNotes)
            {
                rt.Held[rt.HeldCount++] = new HeldNote(note.Pitch, note.EndFrame);
            }
        }
    }

    private static void MixClips(TrackRuntime rt, AudioClipSpan[] clips, long from, long to, int offset)
    {
        foreach (var clip in clips)
        {
            var start = Math.Max(from, clip.StartFrame);
            var end = Math.Min(to, clip.EndFrame);
            if (end <= start)
                continue;
            var samples = clip.Stereo;
            var source = start - clip.StartFrame + clip.SourceOffsetFrames;
            var available = samples.Length / 2 - source;
            if (available <= 0)
                continue;
            end = Math.Min(end, start + available);
            var buffer = rt.Buffer;
            var o = offset + (int)(start - from);
            for (var f = start; f < end; f++, o++, source++)
            {
                buffer[2 * o] += samples[2 * source] * clip.Gain;
                buffer[2 * o + 1] += samples[2 * source + 1] * clip.Gain;
            }
        }
    }

    private void ReleaseHeld(SongSnapshot song, int offset)
    {
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            for (var i = 0; i < rt.HeldCount; i++)
                AddEvent(rt, new NoteEvent(offset, rt.Held[i].Pitch, 0, false));
            rt.HeldCount = 0;
        }
    }

    private void AllNotesOff(SongSnapshot? song)
    {
        if (song is null)
            return;
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            rt.HeldCount = 0;
            rt.EventCount = 0;
            track.Instrument?.AllNotesOff();
        }
    }

    private void AddEvent(TrackRuntime rt, NoteEvent e)
    {
        if (rt.EventCount < MaxEventsPerBlock)
            rt.Events[rt.EventCount++] = e;
        else
            Interlocked.Increment(ref _droppedEvents);
    }

    private static int FirstAtOrAfter(NoteSpan[] notes, long frame)
    {
        int lo = 0, hi = notes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (notes[mid].StartFrame < frame)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    // Stable insertion sort by offset, note-offs first at equal offsets (small arrays, no allocation).
    private static void SortEvents(Span<NoteEvent> events)
    {
        for (var i = 1; i < events.Length; i++)
        {
            var e = events[i];
            var j = i - 1;
            while (j >= 0 && events[j].Key > e.Key)
            {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = e;
        }
    }

    // --- snapshots and commands ------------------------------------------------------------------

    private void ApplySnapshot()
    {
        var next = Interlocked.Exchange(ref _pending, null);
        if (next is null)
            return;
        var previous = _current;
        if (previous is not null && previous.FramesPerTick != next.FramesPerTick)
        {
            // Tempo change: keep the musical position; held notes end now.
            _position = (long)Math.Round(previous.FramesToTicks(_position) * next.FramesPerTick);
            AllNotesOff(previous);
        }

        foreach (var track in next.Tracks)
        {
            var rt = _runtimes[track.Slot];
            if (rt.Generation != track.SlotGeneration)
                rt.Reset(track.SlotGeneration);
        }

        Volatile.Write(ref _current, next);
        Volatile.Write(ref _position, _position);
    }

    private void Enqueue(in Command command)
    {
        lock (_producerGate)
        {
            var tail = _commandTail;
            if (tail - Volatile.Read(ref _commandHead) >= CommandCapacity)
            {
                Log.Warning("[Music] Song engine command queue full; command dropped.");
                return;
            }

            _commands[tail % CommandCapacity] = command;
            Volatile.Write(ref _commandTail, tail + 1);
        }
    }

    private void DrainCommands(SongSnapshot? song)
    {
        var head = _commandHead;
        var tail = Volatile.Read(ref _commandTail);
        for (; head < tail; head++)
        {
            var c = _commands[head % CommandCapacity];
            switch (c.Type)
            {
                case CommandType.Play:
                    _playing = true;
                    break;
                case CommandType.Stop:
                    _playing = false;
                    AllNotesOff(song);
                    break;
                case CommandType.Seek:
                    AllNotesOff(song);
                    _position = c.Value;
                    break;
                case CommandType.Note when song is not null && (uint)c.Slot < (uint)_runtimes.Length:
                    var rt = _runtimes[c.Slot];
                    if (rt.Generation == c.Value)
                        AddEvent(rt, new NoteEvent(0, c.Pitch, c.Velocity, c.Velocity > 0));
                    break;
            }
        }

        Volatile.Write(ref _commandHead, head);
        Volatile.Write(ref _playing, _playing);
        Volatile.Write(ref _position, _position);
    }

    private static void AccumulatePeak(ref int bits, float peak)
    {
        while (true)
        {
            var current = Volatile.Read(ref bits);
            if (BitConverter.Int32BitsToSingle(current) >= peak)
                return;
            if (Interlocked.CompareExchange(ref bits, BitConverter.SingleToInt32Bits(peak), current) == current)
                return;
        }
    }

    private static float TakePeak(ref int bits) => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref bits, 0));

    private enum CommandType : byte
    {
        Play,
        Stop,
        Seek,
        Note,
    }

    private readonly record struct Command(CommandType Type, long Value, int Slot, int Pitch, int Velocity);

    private readonly record struct HeldNote(int Pitch, long EndFrame);

    private sealed class TrackRuntime
    {
        public readonly NoteEvent[] Events = new NoteEvent[MaxEventsPerBlock];
        public readonly HeldNote[] Held = new HeldNote[MaxHeldNotes];
        public readonly float[] Buffer = new float[BlockFrames * 2];
        public int Generation = -1;
        public int EventCount;
        public int HeldCount;
        public float GainL = float.NaN;
        public float GainR = float.NaN;
        public int PeakBits;

        public void Reset(int generation)
        {
            Generation = generation;
            EventCount = 0;
            HeldCount = 0;
            GainL = GainR = float.NaN;
            PeakBits = 0;
        }
    }
}
