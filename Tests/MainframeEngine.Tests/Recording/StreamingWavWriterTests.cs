using System.Buffers.Binary;

namespace MainframeEngine.Tests.Recording;

/// <summary>An IEEE-float WAV written in pieces whose RIFF sizes are patched at the end.</summary>
public sealed class StreamingWavWriterTests
{
    private static (int Channels, int Rate, int DataBytes, float[] Samples) Read(byte[] wav)
    {
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // WAVE_FORMAT_IEEE_FLOAT
        var channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        var data = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        var samples = new float[data / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(44 + i * 4));
        return (channels, rate, data, samples);
    }

    [Fact]
    public void PiecesAreConcatenatedAndTheHeaderIsPatched()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([0.1f, 0.2f]);
            w.Write([0.3f, 0.4f, 0.5f, 0.6f]);
            w.Finish(targetFrames: 3);
        }

        var (channels, rate, data, samples) = Read(ms.ToArray());
        Assert.Equal(2, channels);
        Assert.Equal(48_000, rate);
        Assert.Equal(24, data);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f], samples);
    }

    [Fact]
    public void FinishPadsShortAudioWithSilence()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([1f, 1f]);
            w.Finish(targetFrames: 3);
        }

        Assert.Equal([1f, 1f, 0f, 0f, 0f, 0f], Read(ms.ToArray()).Samples);
    }

    [Fact]
    public void FinishTruncatesLongAudio()
    {
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 2, 48_000))
        {
            w.Write([1f, 1f, 2f, 2f, 3f, 3f]);
            w.Finish(targetFrames: 2);
        }

        Assert.Equal([1f, 1f, 2f, 2f], Read(ms.ToArray()).Samples);
    }

    [Fact]
    public void WriteSilenceCountsFrames()
    {
        using var w = new StreamingWavWriter(new MemoryStream(), 2, 48_000);
        w.WriteSilence(100);
        Assert.Equal(100, w.Frames);
    }

    [Fact]
    public void SilenceWritesWholeFramesWhenTheChunkDoesNotDivideEvenly()
    {
        // 512 / 3 leaves 2 floats over, so each chunk must hold 170 whole frames, never 170.67.
        var ms = new MemoryStream();
        using (var w = new StreamingWavWriter(ms, 3, 48_000))
        {
            w.WriteSilence(1_000);
            w.Finish(targetFrames: 1_000);
        }

        var (channels, _, data, samples) = Read(ms.ToArray());
        Assert.Equal(3, channels);
        Assert.Equal(1_000 * 3 * 4, data);
        Assert.Equal(3_000, samples.Length);
        Assert.All(samples, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void SilenceTerminatesForChannelCountsWiderThanTheChunk()
    {
        using var w = new StreamingWavWriter(new MemoryStream(), 600, 48_000);
        w.WriteSilence(5);
        Assert.Equal(5, w.Frames);
    }

    private static (uint Riff, uint Data) HeaderSizes(long dataBytes)
    {
        var h = new byte[44];
        StreamingWavWriter.FillHeader(h, 2, 48_000, dataBytes);
        return (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)));
    }

    [Fact]
    public void SizesPastTwoGigabytesAreWrittenUnsigned()
    {
        // 3 GB is about 2.2 hours of 48 kHz stereo float: an int clamp here made ffmpeg stop reading at ~93 minutes.
        Assert.Equal((3_000_000_036u, 3_000_000_000u), HeaderSizes(3_000_000_000));
    }

    [Fact]
    public void TheLargestSizeThatFitsIsWrittenExactly()
    {
        Assert.Equal((uint.MaxValue, uint.MaxValue - 36), HeaderSizes(uint.MaxValue - 36));
    }

    [Fact]
    public void SizesThatDoNotFitAreMarkedUnknownSoReadersGoToTheEndOfTheFile()
    {
        Assert.Equal((uint.MaxValue, uint.MaxValue), HeaderSizes(uint.MaxValue - 35));
        Assert.Equal((uint.MaxValue, uint.MaxValue), HeaderSizes(5_000_000_000));
    }

    [Fact]
    public void WriteRejectsAPartialFrame()
    {
        using var w = new StreamingWavWriter(new MemoryStream(), 2, 48_000);
        Assert.Throws<ArgumentException>(() => w.Write([0.1f, 0.2f, 0.3f]));
        Assert.Equal(0, w.Frames);
    }
}
