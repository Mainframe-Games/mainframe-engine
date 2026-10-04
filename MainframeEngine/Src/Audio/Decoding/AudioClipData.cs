namespace MainframeEngine.Audio;

/// <summary>
/// What a voice plays: either fully decoded samples (<see cref="AudioClipData"/>, shared by every voice playing it)
/// or a file decoded incrementally by the streaming thread (<see cref="AudioStreamSource"/>, one decoder per voice).
/// Immutable once created, so the audio thread can read it without synchronisation.
/// </summary>
internal abstract class AudioSource
{
    protected AudioSource(string path, int channels, int sampleRate, long frames)
    {
        Path = path;
        Channels = channels;
        SampleRate = sampleRate;
        Frames = frames;
    }

    /// <summary>Absolute file path (or a descriptive name for generated clips).</summary>
    public string Path { get; }

    /// <summary>1 or 2 (decoders downmix anything wider).</summary>
    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Length in frames; −1 when unknown (some streamed files).</summary>
    public long Frames { get; }

    public double DurationSeconds => Frames < 0 ? 0 : (double)Frames / SampleRate;
}

/// <summary>Decoded PCM in memory: interleaved float samples at the file's rate (resampled per voice).</summary>
internal sealed class AudioClipData : AudioSource
{
    private AudioClipData(string path, float[] samples, int channels, int sampleRate)
        : base(path, channels, sampleRate, samples.Length / channels)
    {
        Samples = samples;
    }

    /// <summary>Interleaved samples, <see cref="AudioSource.Channels"/> per frame.</summary>
    public float[] Samples { get; }

    /// <summary>Decodes the whole file at <paramref name="path"/>.</summary>
    public static AudioClipData Decode(string path)
    {
        using var decoder = AudioDecoder.Open(path);
        var channels = decoder.Channels;
        var samples = decoder.TotalFrames > 0
            ? new float[checked((int)Math.Min(decoder.TotalFrames * channels, Array.MaxLength))]
            : new float[channels * decoder.SampleRate];
        var count = 0;
        while (true)
        {
            if (count == samples.Length)
            {
                if (samples.Length >= Array.MaxLength)
                    break;
                Array.Resize(ref samples, (int)Math.Min((long)samples.Length * 2, Array.MaxLength));
            }

            var read = decoder.Read(samples.AsSpan(count));
            if (read == 0)
                break;
            count += read;
        }

        if (count != samples.Length)
            Array.Resize(ref samples, count - count % channels);
        return new AudioClipData(path, samples, channels, decoder.SampleRate);
    }

    /// <summary>Wraps generated samples (tests, tones). <paramref name="samples"/> is used as is, not copied.</summary>
    public static AudioClipData FromSamples(string name, float[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        if (samples.Length % channels != 0)
            throw new ArgumentException("Sample count must be a whole number of frames.", nameof(samples));
        return new AudioClipData(name, samples, channels, sampleRate);
    }
}

/// <summary>A file the streaming thread decodes while it plays (header probed once at load).</summary>
internal sealed class AudioStreamSource : AudioSource
{
    private AudioStreamSource(string path, string format, int channels, int sampleRate, long frames)
        : base(path, channels, sampleRate, frames)
    {
        Format = format;
    }

    public string Format { get; }

    /// <summary>Reads the header of <paramref name="path"/> (throws when it cannot be decoded).</summary>
    public static AudioStreamSource Probe(string path)
    {
        using var decoder = AudioDecoder.Open(path);
        return new AudioStreamSource(path, decoder.Format, decoder.Channels, decoder.SampleRate, decoder.TotalFrames);
    }
}
