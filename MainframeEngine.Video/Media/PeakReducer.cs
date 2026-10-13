using System.Buffers.Binary;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Streams 16-bit mono PCM and reduces it to min/max <see cref="Peak"/>s of a fixed bucket size
/// without ever holding the sample stream. Feed it chunks in arrival order; call
/// <see cref="Finish"/> once at the end.
/// </summary>
public sealed class PeakReducer
{
    private readonly int _samplesPerBucket;
    private readonly List<Peak> _peaks = [];

    private short _min = short.MaxValue;
    private short _max = short.MinValue;
    private int _inBucket;
    private byte _pendingByte;
    private bool _hasPendingByte;

    public PeakReducer(int samplesPerBucket)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(samplesPerBucket, 0);
        _samplesPerBucket = samplesPerBucket;
    }

    /// <summary>Total samples consumed so far, across both Add methods.</summary>
    public long SamplesSeen { get; private set; }

    public void AddSamples(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            if (s < _min)
            {
                _min = s;
            }

            if (s > _max)
            {
                _max = s;
            }

            if (++_inBucket == _samplesPerBucket)
            {
                FlushBucket();
            }
        }

        SamplesSeen += samples.Length;
    }

    /// <summary>
    /// Decodes little-endian s16 bytes. A trailing odd byte is held until the next call, since
    /// a pipe read can split a sample.
    /// </summary>
    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        if (_hasPendingByte && bytes.Length > 0)
        {
            Span<byte> pair = [_pendingByte, bytes[0]];
            AddSamples([BinaryPrimitives.ReadInt16LittleEndian(pair)]);
            _hasPendingByte = false;
            bytes = bytes[1..];
        }

        var whole = bytes.Length & ~1;
        for (var i = 0; i < whole; i += 2)
        {
            // One sample at a time keeps this endian-independent; the JIT handles the loop fine.
            AddSamples([BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i, 2))]);
        }

        if (whole < bytes.Length)
        {
            _pendingByte = bytes[^1];
            _hasPendingByte = true;
        }
    }

    /// <summary>Closes any partial bucket and returns every peak so far.</summary>
    public IReadOnlyList<Peak> Finish()
    {
        if (_inBucket > 0)
        {
            FlushBucket();
        }

        return _peaks;
    }

    private void FlushBucket()
    {
        _peaks.Add(new Peak(_min, _max));
        _min = short.MaxValue;
        _max = short.MinValue;
        _inBucket = 0;
    }
}
