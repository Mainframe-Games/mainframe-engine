namespace MainframeEngine.Audio;

/// <summary>
/// Pulls 32-bit float PCM out of an encoded file, frame by frame. Output is interleaved with at most two channels:
/// sources with more are downmixed to stereo (ITU coefficients) so voices only ever deal with mono or stereo.
/// Used for whole-file decoding (memory streams) and incrementally by the streaming thread.
/// </summary>
/// <remarks>Implementations: <see cref="WavDecoder"/> (managed), <see cref="VorbisDecoder"/> (NVorbis),
/// <see cref="MiniAudioFileDecoder"/> (MP3/FLAC through SoundFlow's miniaudio codecs). Not thread-safe.</remarks>
internal abstract class AudioDecoder : IDisposable
{
    private float[] _scratch = [];

    /// <summary>Channels in the file.</summary>
    public int SourceChannels { get; protected set; }

    /// <summary>Channels <see cref="Read"/> produces: 1 or 2.</summary>
    public int Channels => Math.Min(SourceChannels, 2);

    public int SampleRate { get; protected set; }

    /// <summary>Length in frames, or −1 when the container does not say.</summary>
    public long TotalFrames { get; protected set; } = -1;

    /// <summary>Short format name for diagnostics ("wav", "ogg", …).</summary>
    public abstract string Format { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved <see cref="Channels"/>-channel samples; returns the
    /// number of samples written (a multiple of <see cref="Channels"/>), 0 at the end of the stream.
    /// </summary>
    public int Read(Span<float> destination)
    {
        var channels = Channels;
        var frames = destination.Length / channels;
        if (frames == 0)
            return 0;
        if (SourceChannels <= 2)
            return ReadSource(destination[..(frames * channels)]);

        var needed = frames * SourceChannels;
        if (_scratch.Length < needed)
            _scratch = new float[needed];
        var read = ReadSource(_scratch.AsSpan(0, needed)) / SourceChannels;
        DownmixToStereo(_scratch.AsSpan(0, read * SourceChannels), SourceChannels, destination);
        return read * 2;
    }

    /// <summary>Moves the read position to <paramref name="frame"/>; false when the source cannot seek.</summary>
    public abstract bool Seek(long frame);

    /// <summary>Reads interleaved <see cref="SourceChannels"/>-channel samples (a whole number of frames).</summary>
    protected abstract int ReadSource(Span<float> destination);

    public void Dispose()
    {
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Validates the format once the header has been read.</summary>
    protected void ValidateFormat(string path)
    {
        if (SourceChannels is < 1 or > 8)
            throw new InvalidDataException($"'{path}': unsupported channel count {SourceChannels} (1–8).");
        if (SampleRate is < 1000 or > 384_000)
            throw new InvalidDataException($"'{path}': unsupported sample rate {SampleRate} Hz.");
    }

    /// <summary>
    /// Folds 3–8 channel frames (L, R, C, LFE, Ls, Rs, Lside, Rside order) into stereo: centre and surrounds at
    /// −3 dB, LFE dropped, the sum scaled so a full-scale signal on every channel cannot exceed full scale.
    /// </summary>
    internal static void DownmixToStereo(ReadOnlySpan<float> source, int sourceChannels, Span<float> destination)
    {
        const float H = 0.70710677f;
        var frames = source.Length / sourceChannels;
        var norm = 1f / (1f + H * (sourceChannels >= 3 ? 1 : 0) + H * Math.Max(0, (sourceChannels - 4 + 1) / 2));
        for (var f = 0; f < frames; f++)
        {
            var s = source.Slice(f * sourceChannels, sourceChannels);
            float l = s[0], r = s[1];
            if (sourceChannels >= 3)
            {
                l += s[2] * H;
                r += s[2] * H;
            }

            for (var c = 4; c < sourceChannels; c++)
            {
                if ((c & 1) == 0)
                    l += s[c] * H;
                else
                    r += s[c] * H;
            }

            destination[2 * f] = l * norm;
            destination[2 * f + 1] = r * norm;
        }
    }

    /// <summary>Opens a decoder for <paramref name="path"/> by extension (wav, ogg, mp3, flac).</summary>
    public static AudioDecoder Open(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupportedExtension(path))
            throw new NotSupportedException($"'{path}': unsupported audio format '{extension}' (wav, ogg, mp3, flac).");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        try
        {
            return extension switch
            {
                ".wav" or ".wave" => new WavDecoder(stream, path),
                ".ogg" or ".oga" => new VorbisDecoder(stream, path),
                ".mp3" => new MiniAudioFileDecoder(stream, path, "mp3"),
                ".flac" => new MiniAudioFileDecoder(stream, path, "flac"),
                _ => throw new NotSupportedException($"'{path}': unsupported audio format '{extension}' (wav, ogg, mp3, flac)."),
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>True for the extensions <see cref="Open"/> understands.</summary>
    public static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".wave" or ".ogg" or ".oga" or ".mp3" or ".flac";
}
