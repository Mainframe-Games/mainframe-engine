using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// A 32-bit float WAV written as the recording runs (<see cref="MainframeEngine.Audio.WavWriter"/> needs the
/// whole buffer up front). The header is written with zero sizes and patched by <see cref="Finish"/>,
/// which also pads or truncates to the exact length the video needs, so A/V lengths always match.
/// </summary>
internal sealed class StreamingWavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private const int SilenceFloats = 512;
    private readonly Stream _stream;
    private readonly int _channels;
    private readonly int _sampleRate;

    public StreamingWavWriter(Stream stream, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        _stream = stream;
        _channels = channels;
        _sampleRate = sampleRate;
        WriteHeader(0);
    }

    /// <summary>Audio frames (one sample per channel) written so far.</summary>
    public long Frames { get; private set; }

    /// <summary>Appends whole frames. A partial frame would shift every later channel, so it is rejected.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException("Sample count must be a whole number of frames.", nameof(interleaved));

        if (BitConverter.IsLittleEndian)
        {
            _stream.Write(MemoryMarshal.AsBytes(interleaved));
        }
        else
        {
            Span<byte> one = stackalloc byte[4];
            foreach (var s in interleaved)
            {
                BinaryPrimitives.WriteSingleLittleEndian(one, s);
                _stream.Write(one);
            }
        }

        Frames += interleaved.Length / _channels;
    }

    /// <summary>
    /// Appends <paramref name="frames"/> frames of silence. The chunk is the fixed 512 floats (stereo-sized)
    /// unless the channel count is wider, so every chunk holds at least one whole frame for any channel count.
    /// </summary>
    public void WriteSilence(long frames)
    {
        Span<float> zeros = stackalloc float[Math.Max(SilenceFloats, _channels)];
        zeros.Clear();
        var perChunk = zeros.Length / _channels;
        while (frames > 0)
        {
            var n = (int)Math.Min(frames, perChunk);
            Write(zeros[..(n * _channels)]);
            frames -= n;
        }
    }

    /// <summary>Makes the data exactly <paramref name="targetFrames"/> long and writes the real sizes.</summary>
    public void Finish(long targetFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetFrames);

        if (Frames < targetFrames)
            WriteSilence(targetFrames - Frames);
        else if (Frames > targetFrames)
        {
            _stream.SetLength(HeaderBytes + targetFrames * _channels * 4);
            Frames = targetFrames;
        }

        _stream.Flush();
        _stream.Position = 0;
        WriteHeader(Frames * _channels * 4);
        _stream.Seek(0, SeekOrigin.End);
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();

    private void WriteHeader(long dataBytes)
    {
        Span<byte> h = stackalloc byte[HeaderBytes];
        FillHeader(h, _channels, _sampleRate, dataBytes);
        _stream.Write(h);
    }

    /// <summary>
    /// The 44-byte header. RIFF sizes are unsigned 32-bit: up to 4 GiB of data (about 3.1 hours of 48 kHz stereo float)
    /// they are exact; past that both are written as <c>0xFFFFFFFF</c>, the conventional "unknown length", and ffmpeg
    /// then reads the data to the end of the file. (A clamp to a smaller value would be honoured and cut the audio.)
    /// </summary>
    internal static void FillHeader(Span<byte> h, int channels, int sampleRate, long dataBytes)
    {
        var fits = 36 + dataBytes <= uint.MaxValue;
        var riffSize = fits ? (uint)(36 + dataBytes) : uint.MaxValue;
        var dataSize = fits ? (uint)dataBytes : uint.MaxValue;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], riffSize);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 3); // IEEE float
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * channels * 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)(channels * 4));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 32);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataSize);
    }
}
