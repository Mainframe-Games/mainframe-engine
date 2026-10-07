using System.Text.Json;
using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Audio;

/// <summary>
/// The ZzFX port against the reference implementation, the line format, <see cref="ZzfxStream"/> (invalidation, limits,
/// <c>.mres</c> round trip), per-play <see cref="AudioStream.PitchRandomness"/> and <see cref="WavWriter"/>.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class ZzfxTests
{
    // ---------------------------------------------------------------------------------------------
    // Synthesis
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string> ReferenceCases()
    {
        var data = new TheoryData<string>();
        using var json = JsonDocument.Parse(File.ReadAllBytes(AudioTestUtil.Asset("zzfx-reference.json")));
        foreach (var c in json.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void GenerateMatchesZzfxReferenceSamples(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(AudioTestUtil.Asset("zzfx-reference.json")));
        var stride = json.RootElement.GetProperty("stride").GetInt32();
        var c = json.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

        var parameters = ZzfxParameters.Default;
        var index = 0;
        foreach (var value in c.GetProperty("params").EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number)
                parameters = parameters.With(index, value.GetSingle());
            index++;
        }

        var samples = Zzfx.Generate(parameters);
        Assert.Equal(c.GetProperty("count").GetInt32(), samples.Length);
        var i = 0;
        foreach (var expected in c.GetProperty("samples").EnumerateArray())
        {
            var want = expected.GetDouble() * Zzfx.MasterVolume;
            var got = samples[i * stride];
            Assert.True(Math.Abs(got - want) <= 1e-5, $"{name}: sample {i * stride} is {got}, ZzFX gives {want}");
            i++;
        }
    }

    [Fact]
    public void GenerateIgnoresRandomnessAndIsDeterministic()
    {
        var a = Zzfx.Generate(ZzfxParameters.Default);
        var b = Zzfx.Generate(ZzfxParameters.Default with { Randomness = 0.5f });
        Assert.Equal(a, b);
        Assert.Equal((int)(0.1 * Zzfx.SampleRate + 9), a.Length); // release 0.1 s + the 9-sample minimum attack
    }

    [Fact]
    public void GenerateRejectsSoundsLongerThanMaxSeconds()
    {
        var p = ZzfxParameters.Default with { Sustain = 9f, Release = 2f };
        var e = Assert.Throws<ArgumentException>(() => Zzfx.Generate(p));
        Assert.Contains("limit is 10 s", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFiniteParametersTakeZzfxDefaults()
    {
        var samples = Zzfx.Generate(ZzfxParameters.Default with { Frequency = float.NaN, Volume = float.PositiveInfinity });
        Assert.Equal(Zzfx.Generate(ZzfxParameters.Default), samples);
    }

    [Fact]
    public void PresetsStayWithinMaxSecondsAndMutateKeepsTheShape()
    {
        var random = new Random(1234);
        Func<Random, ZzfxParameters>[] presets =
        [
            ZzfxPresets.Pickup, ZzfxPresets.Coin, ZzfxPresets.Laser, ZzfxPresets.Explosion, ZzfxPresets.Hit, ZzfxPresets.Jump,
            ZzfxPresets.Blip, ZzfxPresets.PowerUp, ZzfxPresets.Randomize,
        ];
        foreach (var preset in presets)
        {
            for (var n = 0; n < 50; n++)
            {
                var p = preset(random);
                Assert.InRange(Zzfx.Duration(p), 0.0001, Zzfx.MaxSeconds);
                Assert.True(ZzfxParameters.TryParse(p.ToLine(), out var parsed, out _));
                Assert.Equal(p, parsed);

                var mutated = ZzfxPresets.Mutate(p, random);
                Assert.Equal(p.Shape, mutated.Shape);
                for (var i = 0; i < ZzfxParameters.Count; i++)
                {
                    if (p[i] == ZzfxParameters.DefaultAt(i))
                        Assert.Equal(p[i], mutated[i]); // defaults stay defaults
                    else
                        Assert.InRange(Math.Abs(mutated[i]), Math.Abs(p[i]) * 0.89f, Math.Abs(p[i]) * 1.11f);
                }
            }
        }

        Assert.NotEmpty(Zzfx.Generate(ZzfxPresets.Blip(random)));
    }

    // ---------------------------------------------------------------------------------------------
    // Line format
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])")]
    [InlineData("zzfx(...[.5,0,330,,,.2,5,.4])")]
    [InlineData("zzfx(...[,,,,,,4])")]
    [InlineData("zzfx(...[])")]
    [InlineData("zzfx(...[,,-.5,,,,,,,,,,,,,,,,,,-1000])")]
    public void LinesRoundTrip(string line)
    {
        Assert.True(ZzfxParameters.TryParse(line, out var p, out var error), error);
        Assert.Equal(line, p.ToLine());
    }

    [Fact]
    public void ParseAcceptsEveryWrapperAndFillsDefaults()
    {
        var expected = ZzfxParameters.Default with { Volume = 0.5f, Frequency = 925f, Attack = 0.04f };
        foreach (var text in new[]
                 {
                     "zzfx(...[.5,,925,.04])", "zzfx(.5,,925,.04)", "[.5,,925,.04]", ".5,,925,.04", " zzfx( ...[ 0.5 , , 925 , 0.04 ] ); ",
                     "ZZFX(...[.5,,925,.04,,])",
                 })
        {
            Assert.True(ZzfxParameters.TryParse(text, out var p, out var error), $"{text}: {error}");
            Assert.Equal(expected, p);
        }

        Assert.True(ZzfxParameters.TryParse("zzfx()", out var defaults, out _));
        Assert.Equal(ZzfxParameters.Default, defaults);
        Assert.True(ZzfxParameters.TryParse("[,,,,,,3.5]", out var shape, out _));
        Assert.Equal(ZzfxShape.Noise, shape.Shape); // ZzFX's shape > 3 test
        Assert.Equal("zzfx(...[,,,,,,4])", shape.ToLine());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("zzfx[1,2]", "parentheses")]
    [InlineData("[1,2", "brackets")]
    [InlineData("[1,abc]", "Value 2 ('abc') is not a finite number")]
    [InlineData("[1,,NaN]", "Value 3 ('NaN') is not a finite number")]
    [InlineData("[Infinity]", "Value 1 ('Infinity') is not a finite number")]
    [InlineData("[1e39]", "Value 1 ('1e39') is not a finite number")]
    [InlineData("[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]", "Too many values")]
    public void ParseRejectsInvalidLines(string text, string message)
    {
        Assert.False(ZzfxParameters.TryParse(text, out var p, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Equal(ZzfxParameters.Default, p);
    }

    // ---------------------------------------------------------------------------------------------
    // ZzfxStream
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StreamDefaultsAreZzfxDefaultsAndMapToParameters()
    {
        var stream = new ZzfxStream();
        Assert.Equal(ZzfxParameters.Default, stream.Parameters);
        Assert.Equal(0.05f, stream.PitchRandomness);
        Assert.Equal(0f, new AudioStream().PitchRandomness);

        var coin = ZzfxStream.FromLine("zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])", "Coin");
        Assert.Equal(925f, coin.Frequency);
        Assert.Equal(0.6f, coin.ReleaseTime);
        Assert.Equal(ZzfxShape.Triangle, coin.Shape);
        Assert.Equal("zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])", coin.ToLine());
        Assert.Throws<FormatException>(() => ZzfxStream.FromLine("[x]"));
    }

    [Fact]
    public void ParameterChangesInvalidateTheSourceButNotPlayingVoices()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new ZzfxStream { Sustain = 0.5f };
        Assert.True(stream.Preload());
        Assert.Equal(Zzfx.SampleRate, stream.SampleRate);
        Assert.Equal(1, stream.Channels);
        var first = stream.GetSource();
        Assert.Same(first, stream.GetSource()); // cached

        var voice = server.PlayOneShot(stream);
        Assert.True(voice.IsValid);
        stream.Frequency = 440f;
        Assert.False(stream.IsLoaded);
        var second = stream.GetSource();
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.NotEqual(((AudioClipData)first!).Samples, ((AudioClipData)second!).Samples);
        Assert.True(server.IsPlaying(voice)); // the voice keeps the clip it started with
        AudioTestUtil.Render(server, 4800);
        Assert.True(server.IsPlaying(voice));

        stream.Frequency = 440f; // same value: kept
        Assert.Same(second, stream.GetSource());
    }

    [Fact]
    public void OverLongSoundIsALoadErrorAndSilent()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new ZzfxStream { ResourceName = "Drone", Sustain = 12f };
        Assert.False(stream.Preload());
        Assert.Contains("limit is 10 s", stream.LoadError, StringComparison.Ordinal);
        Assert.False(server.PlayOneShot(stream).IsValid);

        stream.Sustain = 1f;
        Assert.True(stream.Preload());
        Assert.Null(stream.LoadError);
    }

    [Fact]
    public void StreamRoundTripsThroughMresStoringOnlyNonDefaults()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-zzfx-tests", Guid.NewGuid().ToString("N"));
        var previous = AssetDatabase.Current;
        Directory.CreateDirectory(Path.Combine(project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(project);
        ResourceLoader.ClearCache();
        try
        {
            var coin = ZzfxStream.FromLine("zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])", "Coin");
            var path = Path.Combine(project, AssetDatabase.ContentFolder, "Coin.mres");
            var uid = ResourceSaver.Save(coin, path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"type\": \"ZzfxStream\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Frequency\": 925", json, StringComparison.Ordinal);
            Assert.Contains("\"Shape\": \"Triangle\"", json, StringComparison.Ordinal);
            foreach (var omitted in new[] { "Volume", "PitchRandomness", "SustainVolume", "Filter", "File", "LoadMode" })
                Assert.DoesNotContain($"\"{omitted}\"", json, StringComparison.Ordinal);

            ResourceLoader.ClearCache();
            var loaded = ResourceLoader.Load<ZzfxStream>(uid);
            Assert.Equal(coin.Parameters, loaded.Parameters);
            Assert.Equal("Coin", loaded.ResourceName);
            Assert.Equal(Zzfx.Generate(coin.Parameters), ((AudioClipData)loaded.GetSource()!).Samples);
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            try
            {
                Directory.Delete(project, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PitchRandomness
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PitchRandomnessVariesEveryPlayWithinRange()
    {
        using var server = AudioTestUtil.CreateServer(randomSeed: 12345);
        var stream = AudioTestUtil.Sine(440f, 0.5f);
        stream.PitchRandomness = 0.2f;
        var pitches = new List<float>();
        for (var i = 0; i < 20; i++)
        {
            var voice = server.PlayOneShot(stream, pitchScale: 1.5f);
            pitches.Add(server.GetVoicePitch(voice) / 1.5f);
            server.Process(AudioTestUtil.Frame);
            Assert.Equal(pitches[^1] * 1.5f, server.GetVoicePitch(voice), 5); // stays put frame to frame
            server.Stop(voice);
        }

        Assert.All(pitches, p => Assert.InRange(p, 0.8f - 1e-5f, 1.2f + 1e-5f));
        Assert.True(pitches.Distinct().Count() > 15);
        Assert.True(pitches.Min() < 0.95f && pitches.Max() > 1.05f);

        // Players too, and the same seed gives the same sequence.
        var tree = new SceneTree();
        using var seeded = AudioTestUtil.CreateServer(tree, randomSeed: 12345);
        var player = new AudioPlayer { Stream = stream, PitchScale = 1.5f, MaxPolyphony = 1 };
        tree.Root.AddChild(player);
        player.Play();
        tree.Tick(AudioTestUtil.Frame);
        var playerPitch = Enumerable.Range(0, seeded.Stats.TotalVoices)
            .Select(i => seeded.GetVoicePitch(new AudioVoiceHandle(i, 1)))
            .Single(pitch => pitch != 0f); // the fresh server's only voice
        Assert.Equal(pitches[0] * 1.5f, playerPitch, 4);
        tree.Shutdown();
    }

    [Fact]
    public void ZeroPitchRandomnessLeavesPitchExact()
    {
        using var server = AudioTestUtil.CreateServer(randomSeed: 7);
        var stream = AudioTestUtil.Sine(440f, 0.5f);
        for (var i = 0; i < 10; i++)
        {
            var voice = server.PlayOneShot(stream, pitchScale: 1.25f);
            Assert.Equal(1.25f, server.GetVoicePitch(voice));
            server.Stop(voice);
        }
    }

    [Fact]
    public void PlayingAZzfxOneShotEveryFrameDoesNotAllocateAfterSynthesis()
    {
        using var server = AudioTestUtil.CreateServer(randomSeed: 99);
        var stream = new ZzfxStream { Sustain = 0.02f, ReleaseTime = 0.02f };
        var buffer = new float[800 * 2];

        void Frame()
        {
            server.PlayOneShot(stream, bus: "SFX");
            server.Process(AudioTestUtil.Frame);
            server.RenderNullDevice(800, buffer);
        }

        for (var i = 0; i < 240; i++) // warm-up: synthesis, JIT, voice pool
            Frame();
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Frame();
        });

        Assert.Equal(0, allocated);
        Assert.False(server.Stats.Faulted);
    }

    // ---------------------------------------------------------------------------------------------
    // WavWriter
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 1)]
    [InlineData(WavSampleFormat.Pcm16, 2)]
    [InlineData(WavSampleFormat.IeeeFloat, 1)]
    [InlineData(WavSampleFormat.IeeeFloat, 2)]
    public void WavWriterRoundTripsThroughTheDecoder(WavSampleFormat format, int channels)
    {
        var samples = new float[5000 * channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.01f) * 0.9f;
        samples[0] = 1f;
        samples[channels] = -1f;
        var path = Path.Combine(Path.GetTempPath(), $"mf-wavwriter-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.Write(path, samples, channels, 44100, format);
            var clip = AudioClipData.Decode(path);
            Assert.Equal(channels, clip.Channels);
            Assert.Equal(44100, clip.SampleRate);
            Assert.Equal(samples.Length, clip.Samples.Length);
            if (format == WavSampleFormat.IeeeFloat)
            {
                Assert.Equal(samples, clip.Samples);
            }
            else
            {
                for (var i = 0; i < samples.Length; i++)
                    Assert.True(Math.Abs(samples[i] - clip.Samples[i]) <= 1f / 32768f + 1e-7f, $"sample {i}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
