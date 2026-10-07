namespace MainframeEngine.Editor.Music;

/// <summary>A note event for one block: <see cref="Offset"/> frames into the block.</summary>
public readonly record struct NoteEvent(int Offset, int Pitch, int Velocity, bool On)
{
    /// <summary>Sort key: by offset, note-offs before note-ons at the same frame.</summary>
    internal long Key => ((long)Offset << 1) | (On ? 1L : 0L);
}

/// <summary>
/// A track's sound source on the render thread: the built-in <see cref="ZzfxInstrument"/> now, a plugin (through the
/// plugin host) later. Implementations must not allocate in <see cref="Process"/> or <see cref="AllNotesOff"/>.
/// </summary>
public interface IInstrument
{
    /// <summary>Output delay in frames (plugin delay compensation; 0 for built-ins).</summary>
    int LatencyFrames { get; }

    /// <summary>
    /// Renders one block: applies <paramref name="events"/> (sorted, offsets inside the block) at their frames and
    /// <b>adds</b> its stereo output into <paramref name="stereo"/> (interleaved, block frames × 2).
    /// </summary>
    void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo);

    /// <summary>Releases every sounding note quickly (stop, seek).</summary>
    void AllNotesOff();
}

/// <summary>A track or master insert on the render thread (plugin phase). Must not allocate in <see cref="Process"/>.</summary>
public interface IEffect
{
    int LatencyFrames { get; }

    /// <summary>Processes interleaved stereo in place.</summary>
    void Process(Span<float> stereo);

    /// <summary>Clears tails (stop, seek).</summary>
    void Reset();
}

/// <summary>
/// One ZzFX line rendered per MIDI pitch at the engine's rate (ZzFX's 44.1 kHz resampled once), cached per pitch.
/// Pitches are generated on the building (UI) thread through <see cref="Ensure"/>; the render thread only reads.
/// Shared by snapshots while the line is unchanged.
/// </summary>
public sealed class ZzfxVoiceBank
{
    private readonly float[]?[] _pitches = new float[]?[128];

    public ZzfxVoiceBank(ZzfxParameters parameters, int sampleRate)
    {
        Parameters = parameters;
        SampleRate = sampleRate;
    }

    public ZzfxParameters Parameters { get; }

    public int SampleRate { get; }

    /// <summary>The error from the last failed generation (sound longer than <see cref="Zzfx.MaxSeconds"/>), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>A4 = 440 Hz, equal temperament.</summary>
    public static double PitchToFrequency(int pitch) => 440.0 * Math.Pow(2, (pitch - 69) / 12.0);

    /// <summary>Generates <paramref name="pitch"/> if it is not cached yet (UI thread; allocates).</summary>
    public void Ensure(int pitch)
    {
        if ((uint)pitch >= 128 || Volatile.Read(ref _pitches[pitch]) is not null || Error is not null)
            return;
        float[] source;
        try
        {
            source = Zzfx.Generate(Parameters with { Frequency = (float)PitchToFrequency(pitch), Randomness = 0 });
        }
        catch (ArgumentException e)
        {
            Error = e.Message;
            return;
        }

        Volatile.Write(ref _pitches[pitch], Resample(source, Zzfx.SampleRate, SampleRate));
    }

    /// <summary>Render thread: the samples for <paramref name="pitch"/>, or null when not generated.</summary>
    public float[]? Get(int pitch) => (uint)pitch < 128 ? Volatile.Read(ref _pitches[pitch]) : null;

    /// <summary>Linear-interpolation resampling of mono samples (once per pitch, not per block).</summary>
    public static float[] Resample(float[] source, int fromRate, int toRate)
    {
        if (fromRate == toRate || source.Length == 0)
            return source;
        var ratio = (double)fromRate / toRate;
        var length = (int)Math.Ceiling(source.Length / ratio);
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * ratio;
            var i0 = (int)position;
            var t = (float)(position - i0);
            var a = i0 < source.Length ? source[i0] : 0f;
            var b = i0 + 1 < source.Length ? source[i0 + 1] : 0f;
            result[i] = a + (b - a) * t;
        }

        return result;
    }
}

/// <summary>
/// The built-in instrument: 16 voices, each playing its pitch's ZzFX sound from the track's <see cref="ZzfxVoiceBank"/>
/// with velocity as gain (velocity / 127). A note-off releases its voice over <see cref="ReleaseSeconds"/> (sounds shorter
/// than the note end by themselves); when all 16 voices sound, a note-on steals the oldest.
/// </summary>
public sealed class ZzfxInstrument : IInstrument
{
    public const int VoiceCount = 16;

    /// <summary>Fade after a note-off.</summary>
    public const double ReleaseSeconds = 0.03;

    /// <summary>Fade on all-notes-off (stop, seek).</summary>
    public const double PanicSeconds = 0.005;

    private readonly Voice[] _voices = new Voice[VoiceCount];
    private readonly int _releaseFrames;
    private readonly int _panicFrames;
    private long _age;

    public ZzfxInstrument(int sampleRate)
    {
        _releaseFrames = Math.Max(1, (int)(ReleaseSeconds * sampleRate));
        _panicFrames = Math.Max(1, (int)(PanicSeconds * sampleRate));
    }

    public int LatencyFrames => 0;

    /// <summary>Voices currently sounding (tests, UI).</summary>
    public int ActiveVoices
    {
        get
        {
            var count = 0;
            foreach (ref readonly var v in _voices.AsSpan())
            {
                if (v.Samples is not null)
                    count++;
            }

            return count;
        }
    }

    public void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo)
    {
        var frames = stereo.Length / 2;
        var cursor = 0;
        foreach (var e in events)
        {
            var at = Math.Clamp(e.Offset, cursor, frames);
            Render(stereo, cursor, at);
            cursor = at;
            if (e.On)
                NoteOn(track.ZzfxBank, e.Pitch, e.Velocity);
            else
                NoteOff(e.Pitch);
        }

        Render(stereo, cursor, frames);
    }

    public void AllNotesOff()
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (v.Samples is null)
                continue;
            if (v.Release >= 0 && v.Release <= _panicFrames)
                continue; // already fading out at least as fast
            v.ReleaseScale = CurrentEnvelope(v);
            v.Release = _panicFrames;
            v.ReleaseTotal = _panicFrames;
        }
    }

    private static float CurrentEnvelope(in Voice v) => v.Release < 0 ? 1f : v.ReleaseScale * v.Release / v.ReleaseTotal;

    private void NoteOn(ZzfxVoiceBank? bank, int pitch, int velocity)
    {
        var samples = bank?.Get(pitch);
        if (samples is null || samples.Length == 0)
            return;
        var index = -1;
        for (var i = 0; i < _voices.Length; i++)
        {
            if (_voices[i].Samples is null)
            {
                index = i;
                break;
            }

            if (index < 0 || _voices[i].Age < _voices[index].Age)
                index = i;
        }

        _voices[index] = new Voice
        {
            Samples = samples,
            Pitch = pitch,
            Gain = Math.Clamp(velocity, 0, 127) / 127f,
            Release = -1,
            ReleaseScale = 1f,
            ReleaseTotal = 1,
            Age = ++_age,
        };
    }

    private void NoteOff(int pitch)
    {
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (v.Samples is not null && v.Pitch == pitch && v.Release < 0)
            {
                v.Release = _releaseFrames;
                v.ReleaseTotal = _releaseFrames;
                v.ReleaseScale = 1f;
            }
        }
    }

    private void Render(Span<float> stereo, int from, int to)
    {
        if (to <= from)
            return;
        for (var i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (v.Samples is not { } samples)
                continue;
            for (var f = from; f < to; f++)
            {
                if (v.Position >= samples.Length || v.Release == 0)
                {
                    v.Samples = null;
                    break;
                }

                var gain = v.Gain;
                if (v.Release > 0)
                {
                    gain *= v.ReleaseScale * v.Release / v.ReleaseTotal;
                    v.Release--;
                }

                var s = samples[v.Position++] * gain;
                stereo[2 * f] += s;
                stereo[2 * f + 1] += s;
            }
        }
    }

    private struct Voice
    {
        public float[]? Samples;
        public int Position;
        public int Pitch;
        public float Gain;
        public int Release; // frames left in the release fade; −1 = held
        public int ReleaseTotal;
        public float ReleaseScale; // envelope level when the current fade started
        public long Age;
    }
}
