using System.Numerics;
using MainframeEngine;
using MainframeEngine.Audio;

namespace Neon.Tests;

/// <summary>
/// Renders the soundscape through the engine's offline path (the manual null device, Neon's bus layout, the master
/// mix) into <c>Examples/Neon/artifacts/audio/*.wav</c> for listening (<c>just neon-audio</c>), plus every raw sound into
/// <c>artifacts/audio/sounds/</c>, and checks the levels numerically: nothing clips, everything is audible, the mix
/// stays calm, and inside is duller than out.
/// </summary>
[Collection(AudioCollection.Name)]
public sealed class NeonAudioRenderTests(ITestOutputHelper output)
{
    private static string OutputFolder
    {
        get
        {
            var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "audio"));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static float Db(float linear) => 20f * MathF.Log10(MathF.Max(linear, 1e-9f));

    /// <summary>Renders <paramref name="seconds"/> of a scene set up by <paramref name="setup"/>; returns the samples and (RMS, peak) in dBFS.</summary>
    private (float[] Samples, float Rms, float Peak) Render(string name, float seconds, Action<AudioHarness> setup,
        Action<AudioHarness, int>? perTick = null, bool floor = true)
    {
        using var h = new AudioHarness(floor);
        setup(h);
        h.Run(2); // let the loops start
        h.Capture = new List<float>((int)(seconds * AudioHarness.Rate * 2) + 4096);
        h.RunSeconds(seconds, tick => perTick?.Invoke(h, tick));
        var samples = h.Capture.ToArray();
        WavWriter.Write(Path.Combine(OutputFolder, name + ".wav"), samples, 2, AudioHarness.Rate, WavSampleFormat.Pcm16);

        Assert.True(Array.TrueForAll(samples, float.IsFinite), "non-finite samples");
        var rms = Db(SignalTools.Rms(samples));
        var peak = Db(SignalTools.Peak(samples));
        output.WriteLine($"{name,-26} RMS {rms,6:0.0} dBFS   peak {peak,6:0.0} dBFS   > 2 kHz {HighShare(samples, 2000f),5:0.00}");
        return (samples, rms, peak);
    }

    /// <summary>The share of the left channel's energy above <paramref name="hz"/> (4th-order high-pass).</summary>
    private static float HighShare(float[] stereo, float hz)
    {
        var a = Biquad.HighPass(hz, 0.707, AudioHarness.Rate);
        var b = Biquad.HighPass(hz, 0.707, AudioHarness.Rate);
        double passed = 0, total = 0;
        for (var i = 0; i < stereo.Length; i += 2)
        {
            var y = b.Process(a.Process(stereo[i]));
            passed += y * y;
            total += stereo[i] * stereo[i];
        }

        return total > 0 ? (float)(passed / total) : 0f;
    }

    private static NeonAudio Quiet(AudioHarness h, Action<NeonAudio>? configure = null) =>
        h.AddAudio(configure: a =>
        {
            a.RainVolumeDb = -80f;
            a.CityVolumeDb = -80f;
            configure?.Invoke(a);
        });

    [Fact]
    public void TheBedsAreCalmInsideAndOpenOnTheBalcony()
    {
        var inside = Render("beds-inside", 8f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 0));
            h.AddAudio();
        });
        var balcony = Render("beds-balcony", 8f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 7f));
            h.AddAudio();
        });
        Render("beds-walk-out", 10f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 1f));
            h.AddAudio();
        }, (h, tick) => h.Scene.GetNode<Camera3D>("Listener").Position = new Vector3(0, 1.6f, 1f + 7f * tick / 600f));

        Assert.InRange(inside.Rms, -46f, -28f);
        Assert.InRange(balcony.Rms, -40f, -20f);
        Assert.True(balcony.Rms > inside.Rms + 2f, $"balcony {balcony.Rms:0.0} vs inside {inside.Rms:0.0}");
        Assert.True(HighShare(balcony.Samples, 2000f) > 3f * HighShare(inside.Samples, 2000f), "the glass takes the hiss away");
        Assert.True(balcony.Peak < -8f && inside.Peak < -14f, $"peaks {balcony.Peak:0.0} / {inside.Peak:0.0}");
    }

    public static TheoryData<string, NeonSound, float, string> Loops => new()
    {
        { "shower-2m", NeonSound.Shower, 2f, NeonAudio.BathroomBus },
        { "tap-1m", NeonSound.Tap, 1f, NeonAudio.BathroomBus },
        { "fridge-1m", NeonSound.FridgeHum, 1f, NeonAudio.FoleyBus },
        { "pc-fans-1m", NeonSound.PcFans, 1f, NeonAudio.FoleyBus },
        { "tv-static-3m", NeonSound.TvStatic, 3f, NeonAudio.TvBus },
        { "tv-jingle-3m", NeonSound.TvJingle, 3f, NeonAudio.TvBus },
        { "train-80m", NeonSound.TrainRumble, 80f, NeonAudio.AmbienceBus },
        { "airship-150m", NeonSound.AirshipDrone, 150f, NeonAudio.AmbienceBus },
    };

    [Theory]
    [MemberData(nameof(Loops))]
    public void EachLoopIsAudibleAndCalm(string name, NeonSound sound, float distance, string bus)
    {
        var (_, rms, peak) = Render(name, 6f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 7f)); // on the balcony: outdoor emitters unmuffled
            Quiet(h);
            var anchor = new Node3D { Name = "Anchor", Position = new Vector3(0, 1.6f, 7f - distance) };
            h.Scene.AddChild(anchor);
            NeonAudio.CreateLoop(sound, anchor, bus).Play();
        });
        Assert.InRange(rms, -52f, -18f);
        Assert.True(peak < -6f, $"{name}: peak {peak:0.0} dBFS");
    }

    [Fact]
    public void TheFoleyIsClearButNotLoud()
    {
        var sequence = new[]
        {
            NeonSound.DoorOpen, NeonSound.DoorClose, NeonSound.SliderOpen, NeonSound.SliderClose, NeonSound.SwitchClick,
            NeonSound.LockedBuzz, NeonSound.TvBlip, NeonSound.KeyClick, NeonSound.KeyClick, NeonSound.KeyClick, NeonSound.KeyClick,
        };
        var (_, _, peak) = Render("foley", 13f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 0));
            Quiet(h);
        }, (_, tick) =>
        {
            var index = tick < 420 ? tick / 60 : 7 + (tick - 420) / 9; // a second each, then fast typing
            if ((tick < 420 && tick % 60 == 0) || (tick >= 420 && (tick - 420) % 9 == 0 && index < sequence.Length + 30))
                NeonAudio.PlayAt(sequence[Math.Min(index, sequence.Length - 1)], new Vector3(1.5f, 1.2f, -1.5f));
        });
        Assert.InRange(peak, -24f, -6f);
    }

    [Fact]
    public void TheCityIsFarAwayAndDullerFromInside()
    {
        (float[] Samples, float Rms, float Peak) City(string name, float z) => Render(name, 12f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, z));
            var audio = Quiet(h);
            h.Run(1);
            audio.TriggerCityEvent(new Vector3(0, 1.6f, z));
        }, (h, tick) =>
        {
            var audio = h.Scene.GetNode<NeonAudio>("Audio");
            if (tick == 150)
                audio.TriggerCityEvent(new Vector3(0, 1.6f, z));
            if (tick == 300)
                audio.StartFlyBy(new Vector3(0, 1.6f, z));
            if (tick == 450)
                audio.TriggerCityEvent(new Vector3(0, 1.6f, z));
        });

        var outside = City("city-events-balcony", 7f);
        var inside = City("city-events-inside", 0f);
        Assert.InRange(outside.Peak, -40f, -6f);
        Assert.True(HighShare(inside.Samples, 1500f) < HighShare(outside.Samples, 1500f) * 0.5f, "muffled indoors");
    }

    public static TheoryData<string> Surfaces => [.. NeonSynth.FootstepSurfaces];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void FootstepsAreClearButNotLoud(string surface)
    {
        NeonFootstepAudio? steps = null;
        var (_, _, peak) = Render($"footsteps-{surface}", 3f, h =>
        {
            var player = h.Controller.AddPlayer();
            steps = new NeonFootstepAudio { Name = "Steps" };
            player.AddChild(steps);
        }, (_, tick) =>
        {
            if (tick % 27 == 0)
                steps!.OnFootstep(surface);
        });
        Assert.InRange(peak, -28f, -8f);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(0, 1f)]
    [InlineData(1, 0f)]
    [InlineData(1, 1f)]
    public void TheStereoPlaysAndMufflesThroughWalls(int song, float muffle)
    {
        var (samples, rms, peak) = Render($"music-{NeonMusic.Songs[song].Id}-muffle-{muffle:0}", 12f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 0));
            var audio = Quiet(h);
            h.Run(1);
            audio.Music!.StereoPosition = new Vector3(0, 1f, -2f);
            audio.Music.Play(song);
        }, (h, _) => h.Scene.GetNode<NeonAudio>("Audio").Music!.Muffle = muffle);
        Assert.InRange(rms, -40f, -14f);
        Assert.True(peak < -4f, $"peak {peak:0.0} dBFS");
        var high = HighShare(samples, 2000f);
        if (muffle >= 1f)
            Assert.True(high < 0.003f, $"muffled, but {high:0.0000} above 2 kHz");
        else
            Assert.True(high > 0.01f, $"open, but only {high:0.0000} above 2 kHz");
    }

    [Fact]
    public void TheWholeApartmentWalkingToTheBalconyStaysCalm()
    {
        FirstPersonController? player = null;
        var leg = 0;
        var (_, rms, peak) = Render("mix-walk-to-balcony", 14f, h =>
        {
            h.AddApartment();
            player = h.Controller.AddPlayer(new Vector3(0f, 0f, -2f), yawDegrees: 180f);
            var audio = h.AddAudio(cityEvents: true);
            h.Run(1);
            audio.Music!.Play(0);
            ((DoorBase)h.Scene.FindChild("Door_Balcony", owned: false)!).Toggle();
        }, (h, _) =>
        {
            // Past the stairs, to the slider and out (the apartment walk's route), at a stroll.
            ReadOnlySpan<Vector2> route = [new(0f, 4.45f), new(-0.95f, 4.6f), new(2.7f, 4.6f), new(2.7f, 7f)];
            var p = player!.GlobalPosition;
            while (leg < route.Length && Vector2.Distance(route[leg], new Vector2(p.X, p.Z)) < 0.35f)
                leg++;
            if (leg >= route.Length)
            {
                h.Controller.Release("move_forward");
                return;
            }

            var d = route[leg] - new Vector2(p.X, p.Z);
            ControllerHarness.Face(player, float.RadiansToDegrees(MathF.Atan2(-d.X, -d.Y)));
            h.Controller.Hold("move_forward", 0.6f);
        }, floor: false);
        Assert.True(player!.FootstepCount >= 12, $"{player.FootstepCount} steps");
        Assert.True(player.GlobalPosition.Z > 6f, $"short of the balcony: {player.GlobalPosition}");
        Assert.InRange(rms, -38f, -16f);
        Assert.True(peak < -4f, $"peak {peak:0.0} dBFS");
    }

    /// <summary>Every sound on its own (not through the mix), for listening: <c>artifacts/audio/sounds/*.wav</c>.</summary>
    [Fact]
    public void EverySoundIsWrittenForListening()
    {
        var folder = Path.Combine(OutputFolder, "sounds");
        Directory.CreateDirectory(folder);
        var bank = NeonSoundBank.Shared;
        foreach (var sound in NeonSoundBank.All)
        {
            var variants = bank.Variants(sound);
            for (var v = 0; v < variants.Length; v++)
            {
                var stream = variants[v];
                var samples = stream.DecodedSamples.ToArray();
                var file = variants.Length > 1 ? $"{sound}-{v}.wav" : $"{sound}.wav";
                WavWriter.Write(Path.Combine(folder, file), samples, stream.Channels, stream.SampleRate, WavSampleFormat.Pcm16);
                Assert.True(SignalTools.Peak(samples) < 0.75f, $"{file}: peak {SignalTools.Peak(samples)}");
            }
        }

        foreach (var surface in NeonSynth.FootstepSurfaces)
        {
            var set = bank.Footsteps(surface);
            var joined = set.SelectMany(s => s.DecodedSamples.ToArray().Concat(new float[NeonSynth.Rate / 4])).ToArray();
            WavWriter.Write(Path.Combine(folder, $"Footsteps-{surface}.wav"), joined, 1, NeonSynth.Rate, WavSampleFormat.Pcm16);
        }

        for (var song = 0; song < NeonMusic.Songs.Count; song++)
        {
            var stems = NeonMusic.RenderStems(song, NeonSoundBank.DefaultSeed);
            var mix = NeonMusic.Mix(stems);
            WavWriter.Write(Path.Combine(folder, $"Music-{NeonMusic.Songs[song].Id}.wav"), [.. mix, .. mix], 2, NeonMusic.Rate, WavSampleFormat.Pcm16);
        }

        output.WriteLine($"wrote {Directory.GetFiles(folder, "*.wav").Length} files to {folder}");
    }
}
