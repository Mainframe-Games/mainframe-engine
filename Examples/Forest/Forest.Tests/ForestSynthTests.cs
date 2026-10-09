using MainframeEngine;

namespace Forest.Tests;

/// <summary>The procedural sounds themselves: deterministic, seamless loops, calm levels, every surface and species.</summary>
public sealed class ForestSynthTests
{
    private static float Db(float linear) => 20f * MathF.Log10(MathF.Max(linear, 1e-9f));

    public static TheoryData<string> Loops => ["wind", "rustle", "brook", "falls"];

    private static (float[] Samples, int Channels) Loop(string name, ulong seed) => name switch
    {
        "wind" => (ForestSynth.WindRoar(seed), 2),
        "rustle" => (ForestSynth.LeafRustle(seed), 2),
        "brook" => (ForestSynth.Brook(seed), 1),
        _ => (ForestSynth.Falls(seed), 1),
    };

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsAreDeterministicPerSeed(string name)
    {
        var a = Loop(name, 7).Samples;
        var b = Loop(name, 7).Samples;
        var c = Loop(name, 8).Samples;
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsAreSeamless(string name)
    {
        // The jump from the last frame back to the first is no bigger than the signal's ordinary sample-to-sample steps.
        var (samples, channels) = Loop(name, 11);
        var frames = samples.Length / channels;
        for (var c = 0; c < channels; c++)
        {
            var largest = 0f;
            double sum = 0;
            for (var i = 1; i < frames; i++)
            {
                var step = MathF.Abs(samples[i * channels + c] - samples[(i - 1) * channels + c]);
                largest = MathF.Max(largest, step);
                sum += step;
            }

            var seam = MathF.Abs(samples[c] - samples[(frames - 1) * channels + c]);
            Assert.True(seam <= largest * 0.5f, $"{name} channel {c}: seam step {seam} vs largest {largest} (mean {sum / frames})");
        }
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsSitAtTheirLevelWithHeadroom(string name)
    {
        var (samples, _) = Loop(name, 3);
        var expected = name switch
        {
            "wind" => ForestSynth.WindRms,
            "rustle" => ForestSynth.RustleRms,
            "brook" => ForestSynth.BrookRms,
            _ => ForestSynth.FallsRms,
        };
        Assert.All(samples, s => Assert.True(float.IsFinite(s)));
        var rms = SignalTools.Rms(samples);
        var peak = SignalTools.Peak(samples);
        Assert.InRange(rms, expected * 0.7f, expected * 1.01f); // the peak limit may pull it down a little
        Assert.True(peak <= 0.7f + 1e-4f, $"{name}: peak {Db(peak):0.0} dBFS");
        Assert.True(peak / rms < 10f, $"{name}: crest factor {peak / rms}");
    }

    [Fact]
    public void TheWindHasGustsAndTheRightChannelIsItsOwn()
    {
        var wind = ForestSynth.WindRoar(5);
        var frames = wind.Length / 2;
        // RMS per second: the gusts make the loudest second at least twice the quietest.
        var window = ForestSynth.WindRate;
        float min = float.MaxValue, max = 0f;
        for (var start = 0; start + window <= frames; start += window)
        {
            double sum = 0;
            for (var i = start; i < start + window; i++)
                sum += wind[i * 2] * wind[i * 2];
            var rms = (float)Math.Sqrt(sum / window);
            min = MathF.Min(min, rms);
            max = MathF.Max(max, rms);
        }

        Assert.True(max > 2f * min, $"gusts: {min} … {max}");

        // Decorrelated channels: a wide bed, not a mono one.
        double lr = 0, ll = 0, rr = 0;
        for (var i = 0; i < frames; i++)
        {
            lr += wind[i * 2] * wind[i * 2 + 1];
            ll += wind[i * 2] * wind[i * 2];
            rr += wind[i * 2 + 1] * wind[i * 2 + 1];
        }

        Assert.InRange(lr / Math.Sqrt(ll * rr), -0.3, 0.3);
    }

    /// <summary>The share of <paramref name="mono"/>'s energy that passes a 4th-order low-pass (or high-pass) at <paramref name="hz"/>.</summary>
    private static float EnergyShare(ReadOnlySpan<float> mono, int rate, float hz, bool low)
    {
        var a = low ? Biquad.LowPass(hz, 0.707, rate) : Biquad.HighPass(hz, 0.707, rate);
        var b = low ? Biquad.LowPass(hz, 0.707, rate) : Biquad.HighPass(hz, 0.707, rate);
        double passed = 0, total = 0;
        foreach (var s in mono)
        {
            var y = b.Process(a.Process(s));
            passed += y * y;
            total += s * s;
        }

        return (float)(passed / total);
    }

    private static float[] Left(float[] stereo)
    {
        var left = new float[stereo.Length / 2];
        for (var i = 0; i < left.Length; i++)
            left[i] = stereo[i * 2];
        return left;
    }

    [Fact]
    public void EachLayerSitsInItsOwnBand()
    {
        // Wind is a low roar, leaves a high hiss, the brook mid-range, the falls broadband with a low body.
        var brook = ForestSynth.Brook(1);
        var falls = ForestSynth.Falls(1);
        var shares = new[]
        {
            EnergyShare(Left(ForestSynth.WindRoar(1)), ForestSynth.WindRate, 1000f, low: true),
            EnergyShare(Left(ForestSynth.LeafRustle(1)), ForestSynth.Rate, 1500f, low: false),
            EnergyShare(brook, ForestSynth.Rate, 250f, low: true),
            EnergyShare(brook, ForestSynth.Rate, 4000f, low: false),
            EnergyShare(falls, ForestSynth.Rate, 250f, low: true),
        };
        var text = string.Join(", ", shares);
        Assert.True(shares[0] > 0.65f, text);
        Assert.True(shares[1] > 0.8f, text);
        Assert.InRange(shares[2], 0.02f, 0.4f);
        Assert.InRange(shares[3], 0.005f, 0.3f);
        Assert.True(shares[4] > shares[2], text);
        Assert.True(shares[4] < 0.6f, text); // a roar, not a rumble
    }

    [Fact]
    public void TheBrookBubbles()
    {
        // Bubbles are transients: many 5 ms windows stand well above the loop's RMS.
        var brook = ForestSynth.Brook(9);
        var rms = SignalTools.Rms(brook);
        var window = ForestSynth.Rate / 200;
        var loud = 0;
        for (var start = 0; start + window <= brook.Length; start += window)
        {
            if (SignalTools.Rms(brook.AsSpan(start, window)) > 1.6f * rms)
                loud++;
        }

        Assert.True(loud > ForestSynth.BrookSeconds * 4, $"{loud} bubbly windows in {ForestSynth.BrookSeconds} s");
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void FootstepsAreShortDeterministicAndBelowTheirPeak(string surface)
    {
        var a = ForestSynth.Footstep(surface, 42);
        Assert.Equal(a, ForestSynth.Footstep(surface, 42));
        Assert.NotEqual(a, ForestSynth.Footstep(surface, 43));
        Assert.InRange(a.Length / (float)ForestSynth.Rate, 0.3f, 0.5f);
        var peak = SignalTools.Peak(a);
        Assert.InRange(peak, ForestSynth.FootstepPeak(surface) * 0.9f, ForestSynth.FootstepPeak(surface) + 1e-4f);
        Assert.True(SignalTools.Rms(a) > peak * 0.02f, $"{surface}: rms {SignalTools.Rms(a)}");
        Assert.Equal(0f, a[0]);
        Assert.Equal(0f, a[^1]);
    }

    public static TheoryData<string> Surfaces => [.. ForestSynth.FootstepSurfaces];

    [Fact]
    public void SurfacesSoundDifferent()
    {
        // The spectral centroid (zero crossings per second, a cheap proxy) separates soft and hard surfaces.
        float Brightness(string surface)
        {
            var s = ForestSynth.Footstep(surface, 1);
            var crossings = 0;
            for (var i = 1; i < s.Length; i++)
            {
                if ((s[i - 1] < 0) != (s[i] < 0))
                    crossings++;
            }

            return crossings / (s.Length / (float)ForestSynth.Rate);
        }

        Assert.True(Brightness("gravel") > 2f * Brightness("moss"));
        Assert.True(Brightness("leaves") > 2f * Brightness("mud"));
    }

    public static TheoryData<BirdSpecies> Species => [.. BirdSynth.AllSpecies];

    [Theory]
    [MemberData(nameof(Species))]
    public void BirdSongsAreDeterministicAndVaried(BirdSpecies species)
    {
        for (var v = 0; v < BirdSynth.Variants; v++)
        {
            var song = BirdSynth.Build(species, v, 1);
            Assert.Equal(song, BirdSynth.Build(species, v, 1));
            var seconds = song.Length / (float)BirdSynth.Rate;
            Assert.InRange(seconds, 0.03f, v < BirdSynth.SongVariants ? 3.5f : 0.8f);
            Assert.InRange(SignalTools.Peak(song), BirdSynth.Peak(species) - 1e-4f, BirdSynth.Peak(species) + 1e-4f);
        }

        Assert.NotEqual(BirdSynth.Build(species, 0, 1), BirdSynth.Build(species, 1, 1));
        Assert.NotEqual(BirdSynth.Build(species, 0, 1), BirdSynth.Build(species, 0, 2));
    }

    [Fact]
    public void BirdsSingInTheirRegister()
    {
        // Zero-crossing pitch of the loud part: songbirds whistle at 1.5–6 kHz.
        foreach (var species in new[] { BirdSpecies.Warbler, BirdSpecies.Chickadee, BirdSpecies.Finch, BirdSpecies.Thrush })
        {
            var song = BirdSynth.Build(species, 0, 3);
            var peak = SignalTools.Peak(song);
            int crossings = 0, loud = 0;
            for (var i = 1; i < song.Length; i++)
            {
                if (MathF.Abs(song[i]) < peak * 0.05f && MathF.Abs(song[i - 1]) < peak * 0.05f)
                    continue;
                loud++;
                if ((song[i - 1] < 0) != (song[i] < 0))
                    crossings++;
            }

            var hz = crossings / 2f / (loud / (float)BirdSynth.Rate);
            Assert.InRange(hz, 1500f, 6000f);
        }
    }

    [Fact]
    public void TheBankMapsSurfacesAndKeepsVariationsApart()
    {
        var bank = new ForestSoundBank(5);
        foreach (var surface in ForestSynth.FootstepSurfaces)
        {
            var set = bank.Footsteps(surface);
            Assert.Equal(ForestSoundBank.FootstepVariants, set.Length);
            Assert.All(set, s => Assert.Equal(ForestSoundBank.FootstepPitchRandomness, s.PitchRandomness));
            Assert.Equal(set.Length, set.Select(s => SignalTools.Rms(s.DecodedSamples.Span)).Distinct().Count());
        }

        Assert.Same(bank.Footsteps("leaves"), bank.Footsteps("leaf_litter"));
        Assert.Same(bank.Footsteps("dirt"), bank.Footsteps("path"));
        Assert.Same(bank.Footsteps("default"), bank.Footsteps("lava"));
        Assert.Same(bank.Footsteps("default"), bank.Footsteps(null));
        Assert.True(bank.Brook.Loop && bank.Falls.Loop && bank.WindRoar.Loop && bank.LeafRustle.Loop);
        Assert.Equal(2, bank.WindRoar.Channels);
        Assert.Equal(ForestSynth.WindSeconds, bank.WindRoar.Length, 3);
        foreach (var species in BirdSynth.AllSpecies)
            Assert.Equal(BirdSynth.Variants, bank.Birds(species).Length);
    }

    [Fact]
    public void RandomIsStableAcrossMachines()
    {
        // SplitMix64's published first outputs for seed 0 pin the generator (the schedules and sounds depend on it).
        var r = new ForestRandom(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, r.NextULong());
        Assert.Equal(0x6E789E6AA1B965F4UL, r.NextULong());
        var f = new ForestRandom(1);
        for (var i = 0; i < 1000; i++)
        {
            Assert.InRange(f.NextFloat(), 0f, 0.99999994f);
            Assert.InRange(f.Range(3, 7), 3, 6);
        }
    }
}
