using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MainframeEngine.Editor.Music;

/// <summary>A <c>.wav</c> read as a wavetable: its frames (each <see cref="Wavetable.FrameSize"/>), and what was dropped.</summary>
public sealed record WavetableFile(List<float[]> Frames, int SourceFrameSize, int DroppedFrames);

/// <summary>
/// Reads <c>.wav</c> files as wavetables (a small RIFF reader: the engine's decoders resample and stream, which a table
/// import must not do): 8/16/24/32-bit PCM and 32-bit float, mixed to mono. The frame size comes from the
/// <c>clm </c> chunk (<c>&lt;!&gt;2048 …</c>), else the size the patch stores, else 2048 when the length is a multiple of
/// it, else the whole file is one single-cycle frame. Frames of another size are resampled to 2048 in the frequency
/// domain (so they stay band-limited); at most 256 are kept.
/// </summary>
public static class WavetableWav
{
    /// <summary>The frame sizes the table menu offers (0: automatic, −1: the whole file is one cycle).</summary>
    public static readonly int[] FrameSizes = [0, 256, 512, 1024, 2048, 4096, -1];

    /// <summary>A single-cycle file longer than this is refused (a sample, not a cycle).</summary>
    public const int MaxSingleCycle = 65536;

    /// <summary>Reads <paramref name="path"/>; throws <see cref="InvalidDataException"/> with a reason for files that are not usable.</summary>
    public static WavetableFile Read(string path, int frameSize = 0) => Parse(File.ReadAllBytes(path), frameSize);

    public static WavetableFile Parse(ReadOnlySpan<byte> data, int frameSize = 0)
    {
        var (samples, clm) = ReadSamples(data);
        if (samples.Length == 0)
            throw new InvalidDataException("the file has no samples");
        var size = frameSize switch
        {
            -1 => samples.Length,
            > 0 => frameSize,
            _ => clm ?? (samples.Length % Wavetable.FrameSize == 0 ? Wavetable.FrameSize : samples.Length),
        };
        if (size == samples.Length && size > MaxSingleCycle)
            throw new InvalidDataException($"{samples.Length} samples is too long for one cycle: choose a frame size");
        if (size < 8)
            throw new InvalidDataException($"a frame of {size} samples is too short");
        var count = samples.Length / size;
        if (count == 0)
            throw new InvalidDataException($"the file is shorter than one {size}-sample frame");
        var kept = Math.Min(count, Wavetable.MaxFrames);
        var frames = new List<float[]>(kept);
        for (var f = 0; f < kept; f++)
            frames.Add(Resample(samples.AsSpan(f * size, size)));
        return new WavetableFile(frames, size, count - kept);
    }

    /// <summary>One cycle of any length as <see cref="Wavetable.FrameSize"/> samples, band-limited (spectrum padded or truncated).</summary>
    public static float[] Resample(ReadOnlySpan<float> cycle)
    {
        var n = cycle.Length;
        if (n == Wavetable.FrameSize)
            return cycle.ToArray();
        const int N = Wavetable.FrameSize;
        var re = new double[N];
        var im = new double[N];
        var top = Math.Min(n / 2 - (n % 2 == 0 ? 1 : 0), N / 2 - 1);
        if ((n & (n - 1)) == 0)
        {
            // A power of two: one FFT.
            var sr = new double[n];
            var si = new double[n];
            for (var i = 0; i < n; i++)
                sr[i] = cycle[i];
            Fft.Transform(sr, si, inverse: false);
            for (var k = 1; k <= top; k++)
                Put(k, sr[k], si[k]);
        }
        else
        {
            // Any other length: the DFT of the harmonics kept.
            for (var k = 1; k <= top; k++)
            {
                double r = 0, ii = 0;
                for (var i = 0; i < n; i++)
                {
                    var a = -2 * Math.PI * k * i / n;
                    r += cycle[i] * Math.Cos(a);
                    ii += cycle[i] * Math.Sin(a);
                }

                Put(k, r, ii);
            }
        }

        Fft.Transform(re, im, inverse: true);
        return [.. re.Select(static v => (float)v)];

        void Put(int k, double r, double i)
        {
            var scale = (double)N / n;
            re[k] = r * scale;
            im[k] = i * scale;
            re[N - k] = r * scale;
            im[N - k] = -i * scale;
        }
    }

    // RIFF/WAVE: fmt (PCM 1 or float 3, or WAVE_FORMAT_EXTENSIBLE with those subformats), data, optional clm.
    private static (float[] Samples, int? Clm) ReadSamples(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("not a RIFF/WAVE file");
        int format = 0, channels = 0, bits = 0;
        ReadOnlySpan<byte> pcm = default;
        var hasData = false;
        int? clm = null;
        var at = 12;
        while (at + 8 <= data.Length)
        {
            var id = data.Slice(at, 4);
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]), (uint)(data.Length - at - 8));
            var body = data.Slice(at + 8, length);
            if (id.SequenceEqual("fmt "u8) && body.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // the subformat GUID's first two bytes
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = body;
                hasData = true;
            }
            else if (id.SequenceEqual("clm "u8))
            {
                clm = ParseClm(body);
            }

            at += 8 + length + (length & 1);
        }

        if (channels <= 0 || bits == 0)
            throw new InvalidDataException("no fmt chunk");
        if (!hasData)
            throw new InvalidDataException("no data chunk");
        var bytes = bits / 8;
        if (bits % 8 != 0 || (!(format == 1 && bytes is 1 or 2 or 3 or 4) && !(format == 3 && bytes == 4)))
            throw new InvalidDataException($"unsupported sample format ({(format == 3 ? "float" : "PCM")} {bits}-bit)");
        var frames = pcm.Length / (bytes * channels);
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var s = pcm.Slice((f * channels + c) * bytes, bytes);
                sum += format == 3
                    ? BinaryPrimitives.ReadSingleLittleEndian(s)
                    : bytes switch
                    {
                        1 => (s[0] - 128) / 128.0,
                        2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0,
                        3 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608.0,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0,
                    };
            }

            var v = (float)(sum / channels);
            samples[f] = float.IsFinite(v) ? v : 0;
        }

        return (samples, clm);
    }

    // "<!>2048 10000000 wavetable (…)": the frame size after the marker; the flags after it are ignored.
    private static int? ParseClm(ReadOnlySpan<byte> body)
    {
        var text = Encoding.ASCII.GetString(body);
        var marker = text.IndexOf("<!>", StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var digits = new string([.. text[(marker + 3)..].TakeWhile(char.IsAsciiDigit)]);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 8 ? size : null;
    }

    /// <summary>
    /// Imports <paramref name="file"/> as a wavetable of the project: checks that it reads, copies a file from outside
    /// <c>Assets/</c> into <c>Assets/Wavetables/</c> (a free name), gives it a UID and returns that UID (the patch's
    /// table source). Throws <see cref="InvalidDataException"/> for files that are not tables.
    /// </summary>
    public static string Import(string file, AssetDatabase database, int frameSize = 0)
    {
        ArgumentNullException.ThrowIfNull(database);
        var parsed = Read(file, frameSize); // refuse before copying anything
        var content = Path.GetFullPath(database.ContentDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(content, StringComparison.Ordinal))
        {
            var folder = Path.Combine(database.ContentDirectory, "Wavetables");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(full);
            var target = Path.Combine(folder, name + ".wav");
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(folder, $"{name} {n}.wav");
            File.Copy(full, target);
            full = target;
        }

        if (parsed.DroppedFrames > 0)
            Log.Warning($"[Music] {Path.GetFileName(full)}: {parsed.DroppedFrames} frames past the first {Wavetable.MaxFrames} are not used.");
        var meta = database.ReadOrCreateMeta(full, create: true)!;
        database.Register(meta.Uid, database.ToProjectPath(full));
        return meta.Uid;
    }
}
