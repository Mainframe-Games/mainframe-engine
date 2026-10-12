using System.Diagnostics;

namespace MainframeEngine.Audio;

/// <summary>
/// Receives a copy of every master-mix block for the gameplay recorder (live mode). <see cref="Write"/>
/// runs on the audio thread: one <see cref="SpscRing{T}.EnqueueBatch"/>, no locks, no allocation; a block
/// that does not fit whole is dropped and counted, and the recorder writes silence for it so the length still matches.
/// </summary>
internal sealed class AudioCaptureTap(int capacitySamples)
{
    private readonly SpscRing<float> _ring = new(capacitySamples);
    private long _firstBlock;
    private long _overflow;
    private int _channels;

    /// <summary>
    /// <see cref="Stopwatch"/> ticks when the first block was mixed; 0 before that. Read this before
    /// <see cref="Channels"/>: a non-zero value guarantees <see cref="Channels"/> is set. The stamp is taken when the
    /// block is mixed, so it is at most one device period plus output latency before that block is heard.
    /// </summary>
    public long FirstBlockTimestamp => Volatile.Read(ref _firstBlock);

    /// <summary>Interleaved channel count of the captured blocks; 0 until the first block.</summary>
    public int Channels => Volatile.Read(ref _channels);

    /// <summary>Samples dropped because the ring was full (whole blocks only); the writer pads these with silence.</summary>
    public long OverflowSamples => Interlocked.Read(ref _overflow);

    /// <summary>Audio thread.</summary>
    public void Write(ReadOnlySpan<float> buffer, int channels)
    {
        if (Volatile.Read(ref _firstBlock) == 0)
        {
            Volatile.Write(ref _channels, channels);
            Volatile.Write(ref _firstBlock, Stopwatch.GetTimestamp());
        }

        // All or nothing: a partial block would end mid-frame and swap L/R for everything after it.
        if (_ring.FreeSpace < buffer.Length)
        {
            Interlocked.Add(ref _overflow, buffer.Length);
            return;
        }

        _ring.EnqueueBatch(buffer);
    }

    /// <summary>Writer thread: moves up to <paramref name="into"/>.Length samples out of the ring.</summary>
    public int Drain(Span<float> into)
    {
        var n = 0;
        while (n < into.Length && _ring.TryDequeue(out var sample))
            into[n++] = sample;
        return n;
    }
}
