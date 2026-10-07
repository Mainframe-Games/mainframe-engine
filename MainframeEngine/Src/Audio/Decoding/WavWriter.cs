using System.Buffers.Binary;

namespace MainframeEngine.Audio;

/// <summary>Sample format written by <see cref="WavWriter"/>.</summary>
public enum WavSampleFormat
{
    /// <summary>16-bit signed PCM (every tool reads it); samples are clamped to ±1.</summary>
    Pcm16,

    /// <summary>32-bit IEEE float (exact).</summary>
    IeeeFloat,
}

/// <summary>Writes interleaved float samples as a canonical RIFF/WAVE file (the counterpart of the WAV decoder).</summary>
public static class WavWriter
{
    /// <summary>Writes <paramref name="interleaved"/> to <paramref name="path"/> (created or overwritten).</summary>
    public static void Write(string path, ReadOnlySpan<float> interleaved, int channels, int sampleRate,
        WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.Create(path);
        Write(stream, interleaved, channels, sampleRate, format);
    }

    /// <summary>Writes <paramref name="interleaved"/> to <paramref name="stream"/> at its current position.</summary>
    public static void Write(Stream stream, ReadOnlySpan<float> interleaved, int channels, int sampleRate,
        WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("Sample count must be a whole number of frames.", nameof(interleaved));

        var bytesPerSample = format == WavSampleFormat.IeeeFloat ? 4 : 2;
        var dataBytes = checked(interleaved.Length * bytesPerSample);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], checked(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(format == WavSampleFormat.IeeeFloat ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)(bytesPerSample * 8));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);

        Span<byte> buffer = stackalloc byte[4096];
        var used = 0;
        foreach (var sample in interleaved)
        {
            if (used + bytesPerSample > buffer.Length)
            {
                stream.Write(buffer[..used]);
                used = 0;
            }

            if (format == WavSampleFormat.IeeeFloat)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buffer[used..], sample);
            }
            else
            {
                // × 32768 like the decoder's ÷ 32768, so a round trip is within one LSB (+1 saturates to 32767)
                var value = float.IsNaN(sample) ? 0 : (int)MathF.Round(Math.Clamp(sample, -1f, 1f) * 32768f);
                BinaryPrimitives.WriteInt16LittleEndian(buffer[used..], (short)Math.Min(value, short.MaxValue));
            }

            used += bytesPerSample;
        }

        stream.Write(buffer[..used]);
    }
}
