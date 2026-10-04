namespace MainframeEngine.Audio;

/// <summary>
/// The pipe between the streaming thread (producer: decodes) and one voice on the audio thread (consumer: plays)
/// for streamed sources. One per voice, created on first use and reused, so streaming a file allocates nothing on
/// the game or audio thread.
/// </summary>
/// <remarks>
/// <para><b>Requests</b> (game thread → streaming thread) are written under a sequence lock: the writer bumps
/// <c>_requestSeq</c> to odd, writes the fields, bumps it to even; the reader retries when it sees an odd or changed
/// sequence. Each request has a generation (<see cref="RequestGeneration"/>); a request without a source stops
/// streaming and closes the file.</para>
/// <para><b>Samples</b> flow through a single-producer / single-consumer float ring. Positions are absolute sample
/// counters; the producer owns the write position, the consumer the read position. When the producer starts a new
/// generation it records where that generation's samples begin (<see cref="GenerationStart"/>) and then publishes
/// <see cref="ActiveGeneration"/>; a consumer only reads once the active generation is its own, jumping its read
/// position to the start (discarding the previous generation's leftovers). The producer never writes over samples
/// the consumer has not passed, so a consumer still finishing the previous generation's block reads consistent data.</para>
/// </remarks>
internal sealed class AudioStreamChannel
{
    /// <summary>Ring capacity in samples: 32 768 stereo frames (≈0.7 s at 48 kHz).</summary>
    public const int RingSamples = 64 * 1024;

    private readonly float[] _ring = new float[RingSamples];
    private const long RingMask = RingSamples - 1;

    // Request (game thread writes, streaming thread reads).
    private int _requestSeq;
    private int _requestGeneration;
    private AudioStreamSource? _requestSource;
    private long _requestStartFrame;
    private bool _requestLoop;
    private long _requestLoopStart;
    private long _requestLoopEnd;

    // Producer state (streaming thread).
    private long _writePos;
    private int _activeGeneration;
    private long _generationStart;
    private int _generationChannels;
    private int _endGeneration;

    // Consumer state (audio thread).
    private long _readPos;

    public AudioStreamChannel(int voiceIndex)
    {
        VoiceIndex = voiceIndex;
    }

    public int VoiceIndex { get; }

    /// <summary>Game thread: set once the channel has been handed to the <see cref="AudioStreamer"/>.</summary>
    public bool Registered { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Game thread
    // ---------------------------------------------------------------------------------------------

    /// <summary>The newest requested generation.</summary>
    public int RequestGeneration => Volatile.Read(ref _requestGeneration);

    /// <summary>
    /// Game thread: asks the streaming thread to decode <paramref name="source"/> from <paramref name="startFrame"/>
    /// (or to stop when null). Returns the request's generation, which the voice's play command carries.
    /// </summary>
    public int Request(AudioStreamSource? source, long startFrame, bool loop, long loopStart, long loopEnd)
    {
        Interlocked.Increment(ref _requestSeq); // odd: writing
        _requestSource = source;
        _requestStartFrame = startFrame;
        _requestLoop = loop;
        _requestLoopStart = loopStart;
        _requestLoopEnd = loopEnd;
        var generation = _requestGeneration + 1;
        Volatile.Write(ref _requestGeneration, generation);
        Interlocked.Increment(ref _requestSeq); // even: done
        return generation;
    }

    // ---------------------------------------------------------------------------------------------
    // Streaming thread
    // ---------------------------------------------------------------------------------------------

    /// <summary>Streaming thread: reads a consistent request; false while the game thread is writing one.</summary>
    public bool TryReadRequest(out StreamRequest request)
    {
        var before = Volatile.Read(ref _requestSeq);
        if ((before & 1) != 0)
        {
            request = default;
            return false;
        }

        request = new StreamRequest(Volatile.Read(ref _requestGeneration), _requestSource, _requestStartFrame,
            _requestLoop, _requestLoopStart, _requestLoopEnd);
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _requestSeq) == before;
    }

    /// <summary>The generation whose samples are in the ring.</summary>
    public int ActiveGeneration => Volatile.Read(ref _activeGeneration);

    /// <summary>Sample position where <see cref="ActiveGeneration"/>'s data begins.</summary>
    public long GenerationStart => Volatile.Read(ref _generationStart);

    /// <summary>Channels of <see cref="ActiveGeneration"/>'s samples.</summary>
    public int GenerationChannels => Volatile.Read(ref _generationChannels);

    /// <summary>Set to a generation once its samples have all been written (non-looping end or error).</summary>
    public int EndGeneration => Volatile.Read(ref _endGeneration);

    /// <summary>Streaming thread: starts writing <paramref name="generation"/> (publishes it to the consumer).</summary>
    public void BeginGeneration(int generation, int channels)
    {
        Volatile.Write(ref _generationStart, _writePos);
        Volatile.Write(ref _generationChannels, channels);
        Volatile.Write(ref _activeGeneration, generation);
    }

    /// <summary>Streaming thread: marks <paramref name="generation"/> complete.</summary>
    public void EndOfGeneration(int generation) => Volatile.Write(ref _endGeneration, generation);

    /// <summary>Streaming thread: samples that can be written without overwriting unread data.</summary>
    public int WritableSamples => RingSamples - (int)(_writePos - Volatile.Read(ref _readPos));

    /// <summary>Streaming thread: copies <paramref name="samples"/> into the ring and publishes them.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var start = (int)(_writePos & RingMask);
        var first = Math.Min(samples.Length, RingSamples - start);
        samples[..first].CopyTo(_ring.AsSpan(start));
        samples[first..].CopyTo(_ring);
        Volatile.Write(ref _writePos, _writePos + samples.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Audio thread
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Audio thread: once <paramref name="generation"/> is the active one, moves the read position to its first
    /// sample and returns its channel count; 0 while the producer has not started it yet.
    /// </summary>
    public int TrySync(int generation)
    {
        if (ActiveGeneration != generation)
            return 0;
        var start = GenerationStart;
        var channels = GenerationChannels;
        if (ActiveGeneration != generation)
            return 0; // the producer moved on while we read; retry next block
        Volatile.Write(ref _readPos, start);
        return Math.Max(channels, 1);
    }

    /// <summary>Audio thread: samples available to read.</summary>
    public int ReadableSamples => (int)(Volatile.Read(ref _writePos) - _readPos);

    /// <summary>Audio thread: the sample <paramref name="offset"/> positions past the read position.</summary>
    public float Peek(int offset) => _ring[(_readPos + offset) & RingMask];

    /// <summary>Audio thread: consumes <paramref name="count"/> samples.</summary>
    public void Advance(int count) => Volatile.Write(ref _readPos, _readPos + count);

    /// <summary>
    /// Any thread (diagnostics, tests): samples of <paramref name="generation"/> decoded but not yet played, or −1 once
    /// that generation has been fully decoded.
    /// </summary>
    public long BufferedSamples(int generation)
    {
        if (ActiveGeneration != generation)
            return 0;
        if (EndGeneration == generation)
            return -1;
        return Volatile.Read(ref _writePos) - Math.Max(Volatile.Read(ref _readPos), GenerationStart);
    }

    internal readonly record struct StreamRequest(
        int Generation,
        AudioStreamSource? Source,
        long StartFrame,
        bool Loop,
        long LoopStart,
        long LoopEnd);
}
