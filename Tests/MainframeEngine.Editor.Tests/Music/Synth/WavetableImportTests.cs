using System.Buffers.Binary;
using System.Text;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>Reading <c>.wav</c> files as wavetables and importing them into a project.</summary>
[Collection(nameof(SerialEditor))]
public sealed class WavetableImportTests
{
    /// <summary>A RIFF/WAVE file: <paramref name="format"/> 1 (PCM) or 3 (float), interleaved samples in −1…1.</summary>
    internal static byte[] Wav(float[] samples, int channels = 1, int bits = 16, int format = 1, string? clm = null)
    {
        using var stream = new MemoryStream();
        var bytes = bits / 8;
        var data = new byte[samples.Length * bytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = data.AsSpan(i * bytes, bytes);
            var v = Math.Clamp(samples[i], -1f, 1f);
            switch (format, bits)
            {
                case (3, 32): BinaryPrimitives.WriteSingleLittleEndian(s, v); break;
                case (1, 8): s[0] = (byte)Math.Clamp(Math.Round(v * 127 + 128), 0, 255); break;
                case (1, 16): BinaryPrimitives.WriteInt16LittleEndian(s, (short)Math.Round(v * 32767)); break;
                case (1, 24):
                    var x = (int)Math.Round(v * 8388607);
                    s[0] = (byte)x;
                    s[1] = (byte)(x >> 8);
                    s[2] = (byte)(x >> 16);
                    break;
                default: BinaryPrimitives.WriteInt32LittleEndian(s, (int)Math.Round(v * 2147483647.0)); break;
            }
        }

        void Chunk(string id, byte[] body)
        {
            stream.Write(Encoding.ASCII.GetBytes(id));
            stream.Write(BitConverter.GetBytes(body.Length));
            stream.Write(body);
            if (body.Length % 2 == 1)
                stream.WriteByte(0);
        }

        stream.Write("RIFF"u8);
        stream.Write(new byte[4]);
        stream.Write("WAVE"u8);
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(44100 * channels * bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        Chunk("fmt ", fmt);
        if (clm is not null)
            Chunk("clm ", Encoding.ASCII.GetBytes(clm));
        Chunk("data", data);
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }

    internal static float[] Sines(int frames, int frameSize, int harmonic = 1) =>
        [.. Enumerable.Range(0, frames * frameSize).Select(i => (float)(0.8 * Math.Sin(2 * Math.PI * harmonic * (i % frameSize) / frameSize)))];

    [Fact]
    public void TheClmChunkGivesTheFrameSizeAndStereoIsMixedToMono()
    {
        var mono = Sines(3, 2048);
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
            (stereo[2 * i], stereo[2 * i + 1]) = (mono[i], mono[i] * 0.5f);
        var table = WavetableWav.Parse(Wav(stereo, channels: 2, clm: "<!>2048 10000000 wavetable (test)"));
        Assert.Equal(3, table.Frames.Count);
        Assert.Equal(2048, table.SourceFrameSize);
        Assert.Equal(0.75 * 0.8 * Math.Sin(2 * Math.PI * 100 / 2048), table.Frames[1][100], 3);
    }

    [Theory]
    [InlineData(1, 8, 0.02)]
    [InlineData(1, 16, 1e-4)]
    [InlineData(1, 24, 1e-6)]
    [InlineData(1, 32, 1e-6)]
    [InlineData(3, 32, 1e-6)]
    public void EverySampleFormatReads(int format, int bits, double tolerance)
    {
        var samples = Sines(1, 2048);
        var table = WavetableWav.Parse(Wav(samples, bits: bits, format: format));
        for (var i = 0; i < 2048; i += 97)
            Assert.Equal(samples[i], table.Frames[0][i], tolerance);
    }

    [Fact]
    public void FrameSizesFallBackAndOtherSizesAreResampled()
    {
        Assert.Equal(2, WavetableWav.Parse(Wav(Sines(2, 2048))).Frames.Count); // a multiple of 2048
        var single = WavetableWav.Parse(Wav(Sines(1, 1500)));
        Assert.Single(single.Frames);
        Assert.Equal(1500, single.SourceFrameSize);
        var four = WavetableWav.Parse(Wav(Sines(4, 1024, harmonic: 3)), frameSize: 1024);
        Assert.Equal(4, four.Frames.Count);
        for (var i = 0; i < 2048; i += 61)
            Assert.Equal(0.8 * Math.Sin(2 * Math.PI * 3 * i / 2048), four.Frames[2][i], 3); // the same cycle at 2048 samples

        var many = WavetableWav.Parse(Wav(Sines(300, 256)), frameSize: 256);
        Assert.Equal(Wavetable.MaxFrames, many.Frames.Count);
        Assert.Equal(44, many.DroppedFrames);
    }

    [Fact]
    public void MalformedFilesAreRefusedWithAReason()
    {
        Assert.Throws<InvalidDataException>(() => WavetableWav.Parse("hello"u8.ToArray()));
        var twelve = Wav(Sines(1, 2048));
        twelve[34] = 12; // the fmt chunk's bits per sample
        Assert.Contains("12-bit", Assert.Throws<InvalidDataException>(() => WavetableWav.Parse(twelve)).Message, StringComparison.Ordinal);
        var noData = Wav([]);
        Assert.Contains("no samples", Assert.Throws<InvalidDataException>(() => WavetableWav.Parse(noData)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => WavetableWav.Parse(Wav(new float[100_000]))); // too long for one cycle
    }

    [Fact]
    public void ImportCopiesAnOutsideFileIntoTheProjectAndTheSynthPlaysIt()
    {
        using var project = new TempProject();
        var outside = Path.Combine(project.Root, "outside.wav");
        File.WriteAllBytes(outside, Wav(Sines(2, 2048, harmonic: 5), clm: "<!>2048"));
        var uid = WavetableWav.Import(outside, AssetDatabase.Current);
        Assert.True(AssetUid.IsUid(uid));
        var path = AssetDatabase.Current.GetPath(uid)!;
        Assert.Equal("Assets/Wavetables/outside.wav", path);
        Assert.True(File.Exists(AssetDatabase.Current.ToAbsolutePath(path)));
        Assert.Equal("Assets/Wavetables/outside 2.wav", AssetDatabase.Current.GetPath(WavetableWav.Import(outside, AssetDatabase.Current)));

        var bad = Path.Combine(project.Root, "bad.wav");
        File.WriteAllText(bad, "not audio");
        Assert.Throws<InvalidDataException>(() => WavetableWav.Import(bad, AssetDatabase.Current));
        Assert.False(File.Exists(Path.Combine(AssetDatabase.Current.ContentDirectory, "Wavetables", "bad.wav")));

        var synth = SynthTestUtil.Create(new() { ["f1.on"] = 0 }, p => p.Tables!["a"] = new SynthTableRef { Source = uid });
        Assert.Null(synth.Status);
        var output = SynthTestUtil.Note(synth, 57, 0.3);
        Assert.True(SynthTestUtil.Rms(output, 4000, 12000) > 0.05);

        var missing = SynthTestUtil.Create(edit: p => p.Tables!["a"] = new SynthTableRef { Source = "Assets/Wavetables/nope.wav" });
        Assert.Equal("missing wavetable: Assets/Wavetables/nope.wav", missing.Status);
    }
}
