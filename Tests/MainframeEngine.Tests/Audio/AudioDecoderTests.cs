using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Audio;

/// <summary>WAV (every PCM/float layout), OGG Vorbis (NVorbis), MP3 and FLAC (miniaudio) decoding, downmix, and loading.</summary>
[Collection(nameof(Scene.SerialResources))] // swaps AssetDatabase.Current (process-wide)
public sealed class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-audio-tests", Guid.NewGuid().ToString("N"));

    public AudioDecoderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheTestSineWavDecodes()
    {
        var clip = AudioClipData.Decode(AudioTestUtil.Asset("sine440_mono16_44k.wav"));
        Assert.Equal(1, clip.Channels);
        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(22050, clip.Frames);
        Assert.Equal(0.5, clip.DurationSeconds, 3);
        Assert.Equal(440f, AudioTestUtil.Frequency(clip.Samples, 1, 0, 44100), 0);
        Assert.Equal(0.5f, clip.Samples.Max(), 2);
    }

    [Theory]
    [InlineData(8, false, false)]
    [InlineData(16, false, false)]
    [InlineData(24, false, false)]
    [InlineData(32, false, false)]
    [InlineData(32, true, false)]
    [InlineData(64, true, false)]
    [InlineData(16, false, true)]
    [InlineData(32, true, true)]
    public void EveryWavLayoutDecodesToTheSameSignal(int bits, bool isFloat, bool extensible)
    {
        const int rate = 22050;
        var source = new float[2 * 1000];
        for (var i = 0; i < 1000; i++)
        {
            source[2 * i] = 0.5f * MathF.Sin(i * 0.05f);
            source[2 * i + 1] = -0.25f;
        }

        var path = Path.Combine(_dir, $"t{bits}{isFloat}{extensible}.wav");
        AudioTestUtil.WriteWav(path, source, 2, rate, bits, isFloat, extensible);
        var clip = AudioClipData.Decode(path);

        Assert.Equal(2, clip.Channels);
        Assert.Equal(rate, clip.SampleRate);
        Assert.Equal(1000, clip.Frames);
        var tolerance = bits == 8 ? 0.01f : 1e-4f;
        for (var i = 0; i < source.Length; i++)
            Assert.True(Math.Abs(source[i] - clip.Samples[i]) <= tolerance, $"sample {i}: {source[i]} vs {clip.Samples[i]}");
    }

    [Fact]
    public void WideFilesAreDownmixedToStereo()
    {
        // 5.1: only the centre carries signal → both stereo channels get it at −3 dB (then the anti-clip normalisation).
        var frames = 100;
        var source = new float[6 * frames];
        for (var i = 0; i < frames; i++)
            source[6 * i + 2] = 0.5f;
        var path = Path.Combine(_dir, "surround.wav");
        AudioTestUtil.WriteWav(path, source, 6, 48000, 16, isFloat: false);

        using var decoder = AudioDecoder.Open(path);
        Assert.Equal(6, decoder.SourceChannels);
        Assert.Equal(2, decoder.Channels);
        var output = new float[2 * frames];
        Assert.Equal(2 * frames, decoder.Read(output));
        var expected = 0.5f * 0.70710677f / (1f + 0.70710677f + 0.70710677f);
        Assert.Equal(expected, output[0], 3);
        Assert.Equal(output[0], output[1]);
    }

    [Fact]
    public void BadWavFilesFailWithAClearError()
    {
        var path = Path.Combine(_dir, "bad.wav");
        File.WriteAllBytes(path, "RIFF\0\0\0\0WAVEfmt "u8.ToArray());
        Assert.Throws<InvalidDataException>(() => AudioClipData.Decode(path));

        File.WriteAllText(path, "not audio at all");
        Assert.Throws<InvalidDataException>(() => AudioClipData.Decode(path));

        Assert.Throws<NotSupportedException>(() => AudioDecoder.Open(Path.Combine(_dir, "song.aiff")));
    }

    [Fact]
    public void OggVorbisDecodesThroughNVorbis()
    {
        var clip = AudioClipData.Decode(AudioTestUtil.Asset("stereo440_660.ogg"));
        AssertStereoTones(clip, 48000, lossy: true);
    }

    [Fact]
    public void Mp3DecodesThroughMiniaudio()
    {
        var clip = AudioClipData.Decode(AudioTestUtil.Asset("stereo440_660.mp3"));
        AssertStereoTones(clip, 48000, lossy: true);
    }

    [Fact]
    public void FlacDecodesThroughMiniaudio()
    {
        var clip = AudioClipData.Decode(AudioTestUtil.Asset("stereo440_660.flac"));
        AssertStereoTones(clip, 48000, lossy: false);
        Assert.Equal(24000, clip.Frames); // lossless: exact length
    }

    [Fact]
    public void StreamingDecodersSeekAndReadIncrementally()
    {
        foreach (var name in new[] { "sine440_mono16_44k.wav", "stereo440_660.ogg", "stereo440_660.flac" })
        {
            var whole = AudioClipData.Decode(AudioTestUtil.Asset(name));
            using var decoder = AudioDecoder.Open(AudioTestUtil.Asset(name));
            var channels = decoder.Channels;
            Assert.True(decoder.Seek(1000));
            var chunk = new float[64 * channels];
            Assert.Equal(chunk.Length, decoder.Read(chunk));
            var maxDiff = 0f;
            for (var i = 0; i < chunk.Length; i++)
                maxDiff = Math.Max(maxDiff, Math.Abs(whole.Samples[1000 * channels + i] - chunk[i]));
            Assert.True(maxDiff < 1e-3f, $"{name}: max difference {maxDiff}");
        }
    }

    [Fact]
    public void AudioStreamsLoadInMemoryOrStreamedAndReportErrors()
    {
        var memory = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Memory };
        Assert.True(memory.Preload());
        Assert.False(memory.IsStreamed);
        Assert.Equal(2, memory.Channels);
        Assert.Equal(48000, memory.SampleRate);
        Assert.Equal(0.5, memory.Length, 2);

        var streamed = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Stream };
        Assert.True(streamed.Preload());
        Assert.True(streamed.IsStreamed);

        var auto = new AudioStream { File = AudioTestUtil.Asset("sine440_mono16_44k.wav") }; // small: memory
        Assert.True(auto.Preload());
        Assert.False(auto.IsStreamed);

        // Two streams over one file share the decoded samples.
        var again = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Memory };
        Assert.True(again.Preload());
        Assert.Same(memory.GetSource(), again.GetSource());

        var missing = new AudioStream { File = Path.Combine(_dir, "missing.wav") };
        Assert.False(missing.Preload());
        Assert.Contains("not found", missing.LoadError, StringComparison.OrdinalIgnoreCase);
        Assert.False(missing.Preload()); // failure is remembered, not retried every play
        missing.File = AudioTestUtil.Asset("sine440_mono16_44k.wav");
        Assert.True(missing.Preload()); // changing the file retries
    }

    [Fact]
    public void ImportSettingsComeFromTheMetaSidecar()
    {
        var previous = AssetDatabase.Current;
        try
        {
            AssetDatabase.Current = new AssetDatabase(_dir);
            var file = Path.Combine(_dir, "loop.wav");
            File.Copy(AudioTestUtil.Asset("sine440_mono16_44k.wav"), file);
            File.WriteAllText(file + ".meta", """
                {"uid": "aud_0123456789ab", "importer": "audio",
                 "settings": {"loadMode": "Stream", "loop": true, "loopStart": 0.1, "loopEnd": 0.4}}
                """);
            var stream = AudioStream.Load("loop.wav");
            Assert.Equal(AudioLoadMode.Stream, stream.LoadMode);
            Assert.True(stream.Loop);
            Assert.Equal(0.1f, stream.LoopStart);
            Assert.Equal(0.4f, stream.LoopEnd);
            Assert.True(stream.Preload());
            Assert.True(stream.IsStreamed);

            // ResourceLoader imports sound files too (a resource's AudioStream property can point at a .wav).
            var loaded = ResourceLoader.Load<AudioStream>("loop.wav");
            Assert.True(loaded.Loop);
            Assert.Equal("aud_0123456789ab", loaded.Uid);
            Assert.True(loaded.Preload());
            loaded.Release();
        }
        finally
        {
            AssetDatabase.Current = previous;
        }
    }

    private static void AssertStereoTones(AudioClipData clip, int rate, bool lossy)
    {
        Assert.Equal(2, clip.Channels);
        Assert.Equal(rate, clip.SampleRate);
        Assert.InRange(clip.Frames, 23000, 25200); // 0.5 s (lossy codecs pad a little)
        var middle = clip.Samples.AsSpan(4000 * 2, 16000 * 2);
        var tolerance = lossy ? 0.02f : 0.005f; // low-bitrate test files: coding noise moves a few zero crossings
        Assert.InRange(AudioTestUtil.Frequency(middle, 2, 0, rate), 440f * (1 - tolerance), 440f * (1 + tolerance));
        Assert.InRange(AudioTestUtil.Frequency(middle, 2, 1, rate), 660f * (1 - tolerance), 660f * (1 + tolerance));
        Assert.InRange(AudioTestUtil.Rms(middle, 0), 0.32f, 0.38f); // 0.5 / √2
    }
}
