namespace MainframeEngine.Editor.Music;

/// <summary>A note in engine frames (absolute song position at the engine's rate).</summary>
public readonly record struct NoteSpan(long StartFrame, long EndFrame, int Pitch, int Velocity);

/// <summary>An audio clip in engine frames, with its file's samples as stereo at the engine's rate.</summary>
public readonly record struct AudioClipSpan(long StartFrame, long EndFrame, long SourceOffsetFrames, float Gain, float[] Stereo);

/// <summary>
/// An immutable, render-ready copy of a <see cref="Song"/> at one sample rate: frames instead of ticks, notes sorted,
/// gains computed, ZzFX sounds and audio files prepared. Built on the UI thread by a <see cref="SongSnapshotBuilder"/>
/// and swapped into the <see cref="SongEngine"/> at a block boundary.
/// </summary>
public sealed class SongSnapshot
{
    internal SongSnapshot(int sampleRate, double tempo, int ppq, TrackSnapshot[] tracks, float masterGain, bool loopEnabled,
        long loopStartFrame, long loopEndFrame, long endFrame)
    {
        SampleRate = sampleRate;
        Tempo = tempo;
        Ppq = ppq;
        FramesPerTick = sampleRate * 60.0 / (tempo * ppq);
        Tracks = tracks;
        MasterGain = masterGain;
        LoopEnabled = loopEnabled;
        LoopStartFrame = loopStartFrame;
        LoopEndFrame = loopEndFrame;
        EndFrame = endFrame;
        foreach (var t in tracks)
            AnySolo |= t.Solo;
    }

    public int SampleRate { get; }
    public double Tempo { get; }
    public int Ppq { get; }
    public double FramesPerTick { get; }
    public IReadOnlyList<TrackSnapshot> TrackList => Tracks;
    internal TrackSnapshot[] Tracks { get; }
    public float MasterGain { get; }
    public bool AnySolo { get; }
    public bool LoopEnabled { get; }
    public long LoopStartFrame { get; }
    public long LoopEndFrame { get; }

    /// <summary>End of the last clip in frames.</summary>
    public long EndFrame { get; }

    public long TicksToFrames(double ticks) => (long)Math.Round(ticks * FramesPerTick);

    public double FramesToTicks(long frames) => frames / FramesPerTick;

    public TrackSnapshot? Find(string trackId) => Array.Find(Tracks, t => string.Equals(t.Id, trackId, StringComparison.Ordinal));
}

/// <summary>One track of a <see cref="SongSnapshot"/>.</summary>
public sealed class TrackSnapshot
{
    internal TrackSnapshot(string id, int slot, int slotGeneration)
    {
        Id = id;
        Slot = slot;
        SlotGeneration = slotGeneration;
    }

    public string Id { get; }

    /// <summary>The engine's per-track state slot (stable while the track exists).</summary>
    public int Slot { get; }

    public int SlotGeneration { get; }

    public float Gain { get; internal set; } = 1f;
    public float PanLeft { get; internal set; } = 1f;
    public float PanRight { get; internal set; } = 1f;
    public bool Mute { get; internal set; }
    public bool Solo { get; internal set; }
    public NoteSpan[] Notes { get; internal set; } = [];
    public AudioClipSpan[] AudioClips { get; internal set; } = [];
    public IInstrument? Instrument { get; internal set; }
    public ZzfxVoiceBank? ZzfxBank { get; internal set; }

    /// <summary>A problem the track shows ("missing plugin: …", "invalid ZzFX line: …"), or null.</summary>
    public string? Status { get; internal set; }

    /// <summary>Audible under the mute/solo rules: not muted, and soloed when any track is.</summary>
    public bool IsAudible(bool anySolo) => !Mute && (!anySolo || Solo);
}

/// <summary>
/// Builds <see cref="SongSnapshot"/>s for one engine (UI thread): assigns stable per-track slots, keeps each track's
/// <see cref="IInstrument"/> while its instrument is unchanged, caches ZzFX banks per line and decoded audio files per
/// file (so edits do not re-synthesise or re-decode).
/// </summary>
public sealed class SongSnapshotBuilder
{
    /// <summary>Tracks beyond this are not played (logged).</summary>
    public const int MaxTracks = 64;

    private readonly Dictionary<string, SlotInfo> _slots = new(StringComparer.Ordinal);
    private readonly Stack<int> _freeSlots = new(Enumerable.Range(0, MaxTracks).Reverse());
    private readonly Dictionary<string, ZzfxVoiceBank> _banks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]?> _clips = new(StringComparer.Ordinal);
    private readonly Func<SongTrack, int, IInstrument?> _instrumentFactory;
    private readonly Func<string, AudioStream?> _clipLoader;
    private int _generation;

    /// <param name="sampleRate">The engine's rate.</param>
    /// <param name="instrumentFactory">
    /// Instrument for a track (UI thread, called when a track appears or its instrument changes kind); null uses
    /// <see cref="DefaultInstrument"/>.
    /// </param>
    /// <param name="clipLoader">Loads an audio clip's file (UID or project path); null uses <see cref="LoadClip"/>.</param>
    public SongSnapshotBuilder(int sampleRate, Func<SongTrack, int, IInstrument?>? instrumentFactory = null,
        Func<string, AudioStream?>? clipLoader = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
        _instrumentFactory = instrumentFactory ?? DefaultInstrument;
        _clipLoader = clipLoader ?? LoadClip;
    }

    public int SampleRate { get; }

    /// <summary>The built-in instruments: ZzFX for <c>builtin: zzfx</c>, nothing for plugins (no plugin host yet) or none.</summary>
    public static IInstrument? DefaultInstrument(SongTrack track, int sampleRate) =>
        track.Instrument is { IsZzfx: true } ? new ZzfxInstrument(sampleRate) : null;

    /// <summary>Loads a sound file (asset UID or project path; <c>.mres</c> audio resources such as ZzFX sounds too) into memory.</summary>
    public static AudioStream? LoadClip(string file)
    {
        var path = AssetUid.IsUid(file) ? AssetDatabase.Current.GetPath(file) : file;
        if (path is null)
            return null;
        if (path.EndsWith(".mres", StringComparison.OrdinalIgnoreCase))
            return ResourceLoader.Load<AudioStream>(path);
        var stream = AudioStream.Load(path);
        stream.LoadMode = AudioLoadMode.Memory;
        return stream;
    }

    /// <summary>Compiles <paramref name="song"/> (generating the ZzFX pitches its notes use and decoding its files).</summary>
    public SongSnapshot Build(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var framesPerTick = SampleRate * 60.0 / (song.Tempo * song.Ppq);
        long ToFrames(double ticks) => (long)Math.Round(ticks * framesPerTick);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<TrackSnapshot>(song.Tracks.Count);
        long end = 0;
        foreach (var track in song.Tracks)
        {
            if (!seen.Add(track.Id))
                continue;
            var instrumentKey = InstrumentKey(track);
            if (!_slots.TryGetValue(track.Id, out var slot) || slot.InstrumentKey != instrumentKey)
            {
                if (slot is null)
                {
                    if (!_freeSlots.TryPop(out var index))
                    {
                        Log.Warning($"[Music] More than {MaxTracks} tracks: '{track.Name}' is not played.");
                        continue;
                    }

                    slot = new SlotInfo { Index = index };
                    _slots[track.Id] = slot;
                }

                slot.Generation = ++_generation;
                slot.InstrumentKey = instrumentKey;
                slot.Instrument = track.Kind == SongTrackKind.Instrument ? _instrumentFactory(track, SampleRate) : null;
            }

            AudioMath.PanGains(track.Pan, out var left, out var right);
            var snapshot = new TrackSnapshot(track.Id, slot.Index, slot.Generation)
            {
                Gain = AudioMath.DbToLinear(track.VolumeDb),
                PanLeft = left,
                PanRight = right,
                Mute = track.Mute,
                Solo = track.Solo,
                Instrument = slot.Instrument,
            };

            if (track.Kind == SongTrackKind.Instrument)
                BuildInstrumentTrack(track, snapshot, ToFrames);
            else
                BuildAudioTrack(track, snapshot, ToFrames);
            foreach (var clip in track.Clips)
                end = Math.Max(end, ToFrames(clip.End));
            tracks.Add(snapshot);
        }

        // Tracks that are gone free their slots (a later track gets a new generation, so the engine resets the slot).
        foreach (var id in _slots.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _freeSlots.Push(_slots[id].Index);
            _slots.Remove(id);
        }

        var loop = song.Loop;
        var loopValid = loop.Enabled && loop.End > loop.Start;
        return new SongSnapshot(SampleRate, song.Tempo, song.Ppq, [.. tracks], AudioMath.DbToLinear(song.Master.VolumeDb), loopValid,
            ToFrames(loop.Start), ToFrames(loop.End), end);
    }

    /// <summary>Generates <paramref name="pitch"/> for a track's ZzFX instrument (before previewing a note).</summary>
    public void EnsurePitch(SongTrack track, int pitch)
    {
        if (BankFor(track, out _) is { } bank)
            bank.Ensure(pitch);
    }

    private void BuildInstrumentTrack(SongTrack track, TrackSnapshot snapshot, Func<double, long> toFrames)
    {
        var notes = new List<NoteSpan>();
        foreach (var clip in track.Clips)
        {
            if (clip is not MidiClip midi)
                continue;
            foreach (var note in midi.Notes)
            {
                if (note.Start < 0 || note.Start >= midi.Length || note.Length <= 0 || (uint)note.Pitch > 127)
                    continue; // outside the clip: not played
                var start = toFrames(midi.Start + note.Start);
                var endTick = midi.Start + Math.Min(note.Start + note.Length, midi.Length);
                var end = Math.Max(start + 1, toFrames(endTick));
                notes.Add(new NoteSpan(start, end, note.Pitch, Math.Clamp(note.Velocity, 1, 127)));
            }
        }

        notes.Sort(static (a, b) => a.StartFrame != b.StartFrame ? a.StartFrame.CompareTo(b.StartFrame) : a.Pitch.CompareTo(b.Pitch));
        snapshot.Notes = [.. notes];

        switch (track.Instrument)
        {
            case { IsZzfx: true }:
                var bank = BankFor(track, out var error);
                snapshot.ZzfxBank = bank;
                if (bank is not null)
                {
                    foreach (var n in snapshot.Notes)
                        bank.Ensure(n.Pitch);
                    error = bank.Error;
                }

                snapshot.Status = error is null ? null : "invalid ZzFX line: " + error;
                break;
            case { Plugin: not null } plugin when snapshot.Instrument is null:
                snapshot.Status = "missing plugin: " + plugin.DisplayName;
                break;
        }
    }

    private void BuildAudioTrack(SongTrack track, TrackSnapshot snapshot, Func<double, long> toFrames)
    {
        var clips = new List<AudioClipSpan>();
        foreach (var clip in track.Clips)
        {
            if (clip is not AudioClip audio || GetClipSamples(audio.File) is not { } stereo)
                continue;
            var start = toFrames(audio.Start);
            clips.Add(new AudioClipSpan(start, toFrames(audio.End), toFrames(audio.Offset), AudioMath.DbToLinear(audio.GainDb), stereo));
        }

        snapshot.AudioClips = [.. clips];
    }

    private float[]? GetClipSamples(string file)
    {
        if (_clips.TryGetValue(file, out var cached))
            return cached;
        float[]? result = null;
        try
        {
            if (_clipLoader(file) is { } stream && stream.Preload() && !stream.DecodedSamples.IsEmpty)
                result = ToStereo(stream.DecodedSamples.Span, stream.Channels, stream.SampleRate, SampleRate);
            else
                Log.Warning($"[Music] Audio clip '{file}' cannot be played (not found, or not a sound loaded into memory).");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                       FileNotFoundException or InvalidOperationException)
        {
            Log.Warning($"[Music] Audio clip '{file}' cannot be loaded: {e.Message}");
        }

        _clips[file] = result;
        return result;
    }

    /// <summary>Interleaved mono/stereo at <paramref name="fromRate"/> → stereo at <paramref name="toRate"/> (linear).</summary>
    public static float[] ToStereo(ReadOnlySpan<float> samples, int channels, int fromRate, int toRate)
    {
        channels = Math.Max(1, channels);
        var frames = samples.Length / channels;
        var ratio = (double)fromRate / toRate;
        var length = fromRate == toRate ? frames : (int)Math.Ceiling(frames / ratio);
        var result = new float[length * 2];
        for (var i = 0; i < length; i++)
        {
            var position = i * ratio;
            var i0 = (int)position;
            var t = (float)(position - i0);
            for (var c = 0; c < 2; c++)
            {
                var channel = Math.Min(c, channels - 1);
                var a = i0 < frames ? samples[i0 * channels + channel] : 0f;
                var b = i0 + 1 < frames ? samples[(i0 + 1) * channels + channel] : 0f;
                result[2 * i + c] = a + (b - a) * t;
            }
        }

        return result;
    }

    private ZzfxVoiceBank? BankFor(SongTrack track, out string? error)
    {
        error = null;
        if (track.Instrument is not { IsZzfx: true } instrument)
            return null;
        var line = instrument.Params ?? "";
        if (_banks.TryGetValue(line, out var bank))
            return bank;
        if (!ZzfxParameters.TryParse(line, out var parameters, out error))
            return null;
        bank = new ZzfxVoiceBank(parameters, SampleRate);
        _banks[line] = bank;
        return bank;
    }

    private static string InstrumentKey(SongTrack track) => track.Instrument switch
    {
        null => "none",
        { IsZzfx: true } => "zzfx",
        { Plugin: { } p } => "plugin:" + p.ClassId,
        { Builtin: { } b } => "builtin:" + b,
        _ => "none",
    };

    private sealed class SlotInfo
    {
        public int Index;
        public int Generation;
        public string InstrumentKey = "";
        public IInstrument? Instrument;
    }
}
