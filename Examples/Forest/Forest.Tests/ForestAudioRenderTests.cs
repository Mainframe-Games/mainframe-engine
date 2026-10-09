using System.Numerics;
using MainframeEngine;
using MainframeEngine.Audio;

namespace Forest.Tests;

/// <summary>
/// Renders each part of the soundscape for a few seconds through the engine's offline path (the manual null device,
/// the Forest's bus layout, the master mix) into <c>Examples/Forest/artifacts/audio/*.wav</c> (for listening) and
/// checks the levels numerically: nothing clips, everything is audible, and the mix stays calm.
/// </summary>
[Collection(AudioCollection.Name)]
public sealed class ForestAudioRenderTests(ITestOutputHelper output)
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

    /// <summary>Renders <paramref name="seconds"/> of a scene set up by <paramref name="setup"/>; returns (RMS, peak) in dBFS.</summary>
    private (float Rms, float Peak) Render(string name, float seconds, Action<AudioHarness> setup, Action<AudioHarness, int>? perTick = null)
    {
        using var h = new AudioHarness();
        setup(h);
        h.Run(2); // let the loops start
        h.Capture = new List<float>((int)(seconds * AudioHarness.Rate * 2) + 4096);
        h.RunSeconds(seconds, tick => perTick?.Invoke(h, tick));
        var samples = h.Capture.ToArray();
        WavWriter.Write(Path.Combine(OutputFolder, name + ".wav"), samples, 2, AudioHarness.Rate, WavSampleFormat.Pcm16);

        Assert.True(Array.TrueForAll(samples, float.IsFinite), "non-finite samples");
        var rms = Db(SignalTools.Rms(samples));
        var peak = Db(SignalTools.Peak(samples));
        output.WriteLine($"{name,-22} RMS {rms,6:0.0} dBFS   peak {peak,6:0.0} dBFS");
        return (rms, peak);
    }

    [Theory]
    [InlineData(0f, -44f, -28f)]
    [InlineData(1f, -38f, -22f)]
    public void TheAmbientBedIsQuietButThere(float wind, float minRms, float maxRms)
    {
        var (rms, peak) = Render($"ambience-wind-{wind:0}", 8f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 0));
            h.Scene.AddChild(new WorldEnvironment { WindStrength = wind });
            h.Scene.AddChild(new AmbienceAudio());
        });
        Assert.InRange(rms, minRms, maxRms);
        Assert.True(peak < -12f, $"peak {peak:0.0} dBFS");
    }

    [Fact]
    public void TheBrookBesideTheStreamSitsUnderTheBed()
    {
        var (rms, peak) = Render("brook-4m", 6f, h =>
        {
            h.AddListener(new Vector3(7, 1.6f, 0)); // 4 m from the east bank
            var river = h.AddRiver(drop: 1.5f);
            h.Scene.AddChild(new StreamAudio { River = river });
        });
        Assert.InRange(rms, -36f, -20f);
        Assert.True(peak < -8f, $"peak {peak:0.0} dBFS");

        // Walking away, it fades: 25 m out it is at least 10 dB quieter.
        var (far, _) = Render("brook-25m", 4f, h =>
        {
            h.AddListener(new Vector3(28, 1.6f, 0));
            var river = h.AddRiver(drop: 1.5f);
            h.Scene.AddChild(new StreamAudio { River = river });
        });
        Assert.True(far < rms - 10f, $"{far:0.0} vs {rms:0.0}");
    }

    [Fact]
    public void TheFallsAreTheLoudestWater()
    {
        var (rms, peak) = Render("falls-8m", 5f, h =>
        {
            h.AddListener(new Vector3(8, 1.6f, 0));
            h.Scene.AddChild(new StreamAudio { FallPositions = [new Vector3(0, 0, 0)] });
        });
        Assert.InRange(rms, -30f, -14f);
        Assert.True(peak < -6f, $"peak {peak:0.0} dBFS");
    }

    [Fact]
    public void BirdsAreDistantAndSparse()
    {
        var (rms, peak) = Render("birds", 16f, h =>
        {
            h.AddListener(new Vector3(0, 1.6f, 0));
            h.Scene.AddChild(new BirdSongs { MeanInterval = 2f, Seed = 7 });
        });
        Assert.InRange(rms, -60f, -30f);
        Assert.InRange(peak, -36f, -10f);
    }

    public static TheoryData<string> Surfaces => [.. ForestSynth.FootstepSurfaces];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void FootstepsAreClearButNotLoud(string surface)
    {
        FootstepAudio? steps = null;
        var (_, peak) = Render($"footsteps-{surface}", 3f, h =>
        {
            var player = h.Controller.AddPlayer(); // its camera is the listener, 1.63 m above the feet
            steps = new FootstepAudio { Name = "Steps" };
            player.AddChild(steps);
        }, (_, tick) =>
        {
            if (tick % 27 == 0) // ≈ 2.2 steps per second, a walk
                steps!.OnFootstep(surface);
        });
        Assert.InRange(steps!.Played, 7, 8); // and the landing step when the player drops onto the floor
        Assert.InRange(peak, -26f, -8f);
    }

    [Fact]
    public void TheWholeMixWalkingByTheStreamStaysCalm()
    {
        FirstPersonController? player = null;
        var (rms, peak) = Render("mix-walk", 12f, h =>
        {
            h.Scene.AddChild(new WorldEnvironment { WindStrength = 0.5f });
            var river = h.AddRiver(drop: 2f);
            player = h.Controller.AddPlayer(new Vector3(-7, 0, 20));
            var audio = ForestAudio.Attach(h.Scene, river, player, [new Vector3(0, -1.5f, -30)]);
            audio.Birds!.MeanInterval = 3f;
        }, (h, _) => h.Controller.Hold("move_forward"));
        Assert.True(player!.FootstepCount >= 30);
        Assert.InRange(rms, -36f, -18f);
        Assert.True(peak < -6f, $"peak {peak:0.0} dBFS"); // headroom: nothing near clipping
    }
}
