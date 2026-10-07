using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// A stream whose samples a producer pushes while it plays (Godot's <c>AudioStreamGenerator</c>): procedural audio,
/// emulators, the editor's song engine. Play it like any stream, then get the voice's
/// <see cref="AudioStreamGeneratorPlayback"/> from <see cref="AudioServer.GetGeneratorPlayback"/> and keep it fed with
/// <see cref="AudioStreamGeneratorPlayback.PushFrames"/>.
/// </summary>
/// <remarks>
/// Every play gets its own ring of <see cref="BufferSeconds"/> × <see cref="MixRate"/> stereo frames (allocated when the
/// voice starts, never afterwards). The voice resamples from <see cref="MixRate"/> to the device rate like any other
/// source (pitch applies). It never ends on its own; when the ring runs dry it plays silence and counts an underrun.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "An AudioStream (Godot's name, ADR 0010); not a System.IO.Stream.")]
[EditorIcon("music")]
public sealed class AudioStreamGenerator : AudioStream
{
    private float _bufferSeconds = 0.5f;
    private float _mixRate = 44100f;

    /// <summary>Length of each play's ring in seconds (Godot's <c>buffer_length</c>, default 0.5 s).</summary>
    [Export(Range = "0.01,10,0.01")]
    public float BufferSeconds
    {
        get => _bufferSeconds;
        set
        {
            var clamped = float.IsFinite(value) ? Math.Clamp(value, 0.01f, 10f) : 0.5f;
            if (_bufferSeconds == clamped)
                return;
            _bufferSeconds = clamped;
            Invalidate();
        }
    }

    /// <summary>Sample rate of the pushed frames in Hz (default 44 100; set it to <see cref="AudioServer.SampleRate"/> to skip resampling).</summary>
    [Export(Range = "8000,192000,1")]
    public float MixRate
    {
        get => _mixRate;
        set
        {
            var clamped = float.IsFinite(value) ? Math.Clamp(value, 8000f, 192000f) : 44100f;
            if (_mixRate == clamped)
                return;
            _mixRate = clamped;
            Invalidate();
        }
    }

    /// <summary>Always 2: frames are interleaved stereo.</summary>
    public static int ChannelCount => 2;

    /// <summary>Ring capacity in frames for one play.</summary>
    public int BufferFrames => Math.Max(64, (int)Math.Ceiling(_bufferSeconds * _mixRate));

    private protected override AudioSource? CreateSource() =>
        new AudioGeneratorTemplate(string.IsNullOrEmpty(ResourceName) ? "Generator" : ResourceName, (int)_mixRate, BufferFrames);
}

/// <summary>
/// One play of an <see cref="AudioStreamGenerator"/>: a lock-free single-producer / single-consumer ring of interleaved
/// stereo frames. Push from one thread at a time (any thread); the audio thread consumes. Nothing allocates after the
/// voice started.
/// </summary>
public sealed class AudioStreamGeneratorPlayback
{
    private readonly float[] _samples;
    private readonly long _mask; // over samples (power of two)
    private readonly int _capacityFrames;
    private PaddedIndex _read; // samples consumed (audio thread)
    private PaddedIndex _write; // samples produced (producer)
    private PaddedIndex _clearTo; // producer: everything before this sample index is to be discarded
    private int _underruns;
    private long _framesConsumed;

    internal AudioStreamGeneratorPlayback(int capacityFrames, int sampleRate)
    {
        _capacityFrames = capacityFrames;
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(capacityFrames * 2));
        _samples = new float[size];
        _mask = size - 1;
        SampleRate = sampleRate;
    }

    /// <summary>Rate of the pushed frames (the stream's <see cref="AudioStreamGenerator.MixRate"/>).</summary>
    public int SampleRate { get; }

    /// <summary>Ring size in frames.</summary>
    public int CapacityFrames => _capacityFrames;

    /// <summary>Frames that can be pushed now (Godot's <c>get_frames_available</c>).</summary>
    public int FramesAvailable =>
        Math.Max(0, _capacityFrames - (int)((Volatile.Read(ref _write.Value) - Volatile.Read(ref _read.Value)) / 2));

    /// <summary>Frames pushed but not yet played.</summary>
    public int FramesQueued => (int)((Volatile.Read(ref _write.Value) - Volatile.Read(ref _read.Value)) / 2);

    /// <summary>Audio blocks that found the ring empty (or short) after the first frame was pushed (Godot's skips).</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Frames the voice has played from the ring (underrun silence not included).</summary>
    public long FramesConsumed => Volatile.Read(ref _framesConsumed);

    /// <summary>
    /// Queues <paramref name="interleavedStereo"/> (L, R, L, R…). All or nothing: false (nothing queued) when it holds
    /// more frames than <see cref="FramesAvailable"/> or an odd number of samples.
    /// </summary>
    public bool PushFrames(ReadOnlySpan<float> interleavedStereo)
    {
        if ((interleavedStereo.Length & 1) != 0)
            return false;
        var frames = interleavedStereo.Length / 2;
        if (frames > FramesAvailable)
            return false;
        var write = _write.Value;
        var start = (int)(write & _mask);
        var first = Math.Min(interleavedStereo.Length, _samples.Length - start);
        interleavedStereo[..first].CopyTo(_samples.AsSpan(start));
        interleavedStereo[first..].CopyTo(_samples);
        Volatile.Write(ref _write.Value, write + interleavedStereo.Length);
        return true;
    }

    /// <summary>Queues one frame; false when the ring is full.</summary>
    public bool PushFrame(float left, float right)
    {
        if (FramesAvailable < 1)
            return false;
        var write = _write.Value;
        _samples[write & _mask] = left;
        _samples[(write + 1) & _mask] = right;
        Volatile.Write(ref _write.Value, write + 2);
        return true;
    }

    /// <summary>Producer: drops everything queued so far (the audio thread discards it on its next block).</summary>
    public void ClearBuffer() => Volatile.Write(ref _clearTo.Value, _write.Value);

    // --- audio thread -----------------------------------------------------------------------------

    /// <summary>Consumer: reads one frame; false when empty.</summary>
    internal bool TryRead(out float left, out float right)
    {
        var read = _read.Value;
        var clearTo = Volatile.Read(ref _clearTo.Value);
        if (clearTo > read)
            read = clearTo;
        if (Volatile.Read(ref _write.Value) - read < 2)
        {
            if (read != _read.Value)
                Volatile.Write(ref _read.Value, read);
            left = right = 0;
            return false;
        }

        left = _samples[read & _mask];
        right = _samples[(read + 1) & _mask];
        Volatile.Write(ref _read.Value, read + 2);
        Volatile.Write(ref _framesConsumed, _framesConsumed + 1);
        return true;
    }

    /// <summary>True once anything was ever pushed (an empty ring before that is not an underrun).</summary>
    internal bool Started => Volatile.Read(ref _write.Value) > 0;

    internal void CountUnderrun() => Interlocked.Increment(ref _underruns);
}

/// <summary>The cached source of an <see cref="AudioStreamGenerator"/>: the shape of the ring each play creates.</summary>
internal sealed class AudioGeneratorTemplate(string name, int sampleRate, int capacityFrames)
    : AudioSource(name, 2, sampleRate, -1)
{
    public int CapacityFrames { get; } = capacityFrames;

    public AudioGeneratorSource CreatePlay() => new(Path, SampleRate, new AudioStreamGeneratorPlayback(CapacityFrames, SampleRate));
}

/// <summary>One voice's generator source: its own <see cref="AudioStreamGeneratorPlayback"/> ring.</summary>
internal sealed class AudioGeneratorSource(string name, int sampleRate, AudioStreamGeneratorPlayback playback)
    : AudioSource(name, 2, sampleRate, -1)
{
    public AudioStreamGeneratorPlayback Playback { get; } = playback;
}
