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

    // Producer state (streaming thread). The generation record is written under _generationSeq.
    private long _writePos;
    private int _generationSeq;
    private int _activeGeneration;
    private long _generationStart;
    private int _generationChannels;
    private int _closedGeneration;
    private long _closedEnd;
    private int _endGeneration;
    private int _errorGeneration;

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

    /// <summary>The generation whose samples are being written.</summary>
    public int ActiveGeneration => Volatile.Read(ref _activeGeneration);

    /// <summary>Set to a generation once its samples have all been written (non-looping end or error).</summary>
    public int EndGeneration => Volatile.Read(ref _endGeneration);

    /// <summary>Set to a generation whose file could not be opened or decoded (its voice ends without <c>Finished</c>).</summary>
    public int ErrorGeneration => Volatile.Read(ref _errorGeneration);

    /// <summary>
    /// Streaming thread: starts writing <paramref name="generation"/>. The previous generation is closed first at the
    /// current write position, so a consumer still reading it never runs into the new generation's samples. The
    /// generation record (active, start, channels, closed, closed end) is published under a sequence lock.
    /// </summary>
    public void BeginGeneration(int generation, int channels)
    {
        Interlocked.Increment(ref _generationSeq); // odd: writing
        _closedGeneration = _activeGeneration;
        _closedEnd = _writePos;
        _generationStart = _writePos;
        _generationChannels = channels;
        _activeGeneration = generation;
        Interlocked.Increment(ref _generationSeq); // even: published
    }

    /// <summary>Streaming thread: marks <paramref name="generation"/> complete (all its samples are written).</summary>
    public void EndOfGeneration(int generation) => Volatile.Write(ref _endGeneration, generation);

    /// <summary>Streaming thread: marks <paramref name="generation"/> failed, then complete.</summary>
    public void FailGeneration(int generation)
    {
        Volatile.Write(ref _errorGeneration, generation);
        EndOfGeneration(generation);
    }

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
    /// sample and returns its channel count; 0 while the producer has not started it yet (or moved past it).
    /// </summary>
    public int TrySync(int generation)
    {
        var before = Volatile.Read(ref _generationSeq);
        if ((before & 1) != 0)
            return 0; // being written: retry next block
        var active = _activeGeneration;
        var start = _generationStart;
        var channels = _generationChannels;
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _generationSeq) != before || active != generation)
            return 0;
        Volatile.Write(ref _readPos, start);
        return Math.Max(channels, 1);
    }

    /// <summary>
    /// Audio thread: the absolute sample position the consumer of <paramref name="generation"/> may read up to — the
    /// write position while it is the active generation, its closing position once the producer has moved on (never
    /// into the next generation's samples), else the read position (nothing to read). Taken once per block.
    /// </summary>
    public long ReadLimit(int generation)
    {
        var before = Volatile.Read(ref _generationSeq);
        if ((before & 1) == 0)
        {
            var active = _activeGeneration;
            var closed = _closedGeneration;
            var closedEnd = _closedEnd;
            var write = Volatile.Read(ref _writePos);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _generationSeq) == before)
            {
                if (active == generation)
                    return write;
                if (closed == generation)
                    return Math.Max(closedEnd, _readPos);
            }
        }

        return _readPos; // unknown or mid-switch: read nothing this block
    }

    /// <summary>Audio thread: samples available before <paramref name="limit"/> (from <see cref="ReadLimit"/>).</summary>
    public int Readable(long limit) => (int)Math.Max(0, limit - _readPos);

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
        return Volatile.Read(ref _writePos) - Math.Max(Volatile.Read(ref _readPos), Volatile.Read(ref _generationStart));
    }

    internal readonly record struct StreamRequest(
        int Generation,
        AudioStreamSource? Source,
        long StartFrame,
        bool Loop,
        long LoopStart,
        long LoopEnd);
}
