using System.Diagnostics;

namespace MainframeEngine.Audio;

/// <summary>
/// Receives a copy of every master-mix block for the gameplay recorder (live mode). <see cref="Write"/>
/// runs on the audio thread: one <see cref="SpscRing{T}.EnqueueBatch"/>, no locks, no allocation; what
/// does not fit is counted, and the recorder writes silence for it so the length still matches.
/// </summary>
internal sealed class AudioCaptureTap(int capacitySamples)
{
    private readonly SpscRing<float> _ring = new(capacitySamples);
    private long _firstBlock;
    private long _overflow;
    private int _channels;

    /// <summary><see cref="Stopwatch"/> ticks when the first block arrived; 0 before that.</summary>
    public long FirstBlockTimestamp => Volatile.Read(ref _firstBlock);

    public int Channels => Volatile.Read(ref _channels);

    public long OverflowSamples => Interlocked.Read(ref _overflow);

    /// <summary>Audio thread.</summary>
    public void Write(ReadOnlySpan<float> buffer, int channels)
    {
        if (Volatile.Read(ref _firstBlock) == 0)
        {
            Volatile.Write(ref _channels, channels);
            Volatile.Write(ref _firstBlock, Stopwatch.GetTimestamp());
        }

        var written = _ring.EnqueueBatch(buffer);
        if (written < buffer.Length)
            Interlocked.Add(ref _overflow, buffer.Length - written);
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
