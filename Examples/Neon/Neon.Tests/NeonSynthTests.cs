namespace Neon.Tests;

/// <summary>The synthesised sounds themselves: deterministic, seamless loops, calm levels, the right bands, every surface.</summary>
public sealed class NeonSynthTests
{
    private static float Db(float linear) => 20f * MathF.Log10(MathF.Max(linear, 1e-9f));

    /// <summary>Every loop: (samples, channels, rate, its RMS, its peak limit).</summary>
    private static (float[] Samples, int Channels, int Rate, float Rms, float MaxPeak) Loop(string name, ulong seed) => name switch
    {
        "rain-outdoor" => (NeonSynth.RainOutdoor(seed), 2, NeonSynth.Rate, NeonSynth.RainOutdoorRms, 0.6f),
        "rain-indoor" => (NeonSynth.RainIndoor(seed), 2, NeonSynth.Rate, NeonSynth.RainIndoorRms, 0.6f),
        "city" => (NeonSynth.CityDrone(seed), 2, NeonSynth.LowRate, NeonSynth.CityRms, 0.6f),
        "city-indoor" => (NeonSynth.CityDrone(seed, indoor: true), 2, NeonSynth.LowRate, NeonSynth.CityIndoorRms, 0.6f),
        "train" => (NeonSynth.TrainRumble(seed), 1, NeonSynth.LowRate, NeonSynth.TrainRms, 0.7f),
        "airship" => (NeonSynth.AirshipDrone(seed), 1, NeonSynth.LowRate, NeonSynth.AirshipRms, 0.7f),
        "shower" => (NeonSynth.Shower(seed), 1, NeonSynth.Rate, NeonSynth.ShowerRms, 0.7f),
        "tap" => (NeonSynth.Tap(seed), 1, NeonSynth.Rate, NeonSynth.TapRms, 0.7f),
        "fridge" => (NeonSynth.FridgeHum(seed), 1, NeonSynth.LowRate, NeonSynth.FridgeRms, 0.5f),
        "pc-fans" => (NeonSynth.PcFans(seed), 1, NeonSynth.Rate, NeonSynth.PcFansRms, 0.5f),
        "tv-static" => (NeonSynth.TvStatic(seed), 1, NeonSynth.Rate, NeonSynth.TvStaticRms, 0.6f),
        "tv-jingle" => (NeonMusic.Jingle(seed), 1, NeonMusic.Rate, NeonMusic.JingleRms, 0.5f),
        _ => throw new ArgumentException(name),
    };

    public static TheoryData<string> Loops =>
        ["rain-outdoor", "rain-indoor", "city", "city-indoor", "train", "airship", "shower", "tap", "fridge", "pc-fans", "tv-static", "tv-jingle"];

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsAreDeterministicPerSeed(string name)
    {
        var a = Loop(name, 7).Samples;
        Assert.Equal(a, Loop(name, 7).Samples);
        Assert.NotEqual(a, Loop(name, 8).Samples);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsAreSeamless(string name)
    {
        // The jump from the last frame back to the first is no bigger than the signal's ordinary sample-to-sample steps.
        var (samples, channels, _, _, _) = Loop(name, 11);
        var frames = samples.Length / channels;
        for (var c = 0; c < channels; c++)
        {
            var steps = new float[frames - 1];
            for (var i = 1; i < frames; i++)
                steps[i - 1] = MathF.Abs(samples[i * channels + c] - samples[(i - 1) * channels + c]);
            Array.Sort(steps);
            var typical = steps[(int)(steps.Length * 0.99)]; // the 99th percentile step
            var seam = MathF.Abs(samples[c] - samples[(frames - 1) * channels + c]);
            Assert.True(seam <= typical, $"{name} channel {c}: seam step {seam} vs 99th percentile {typical}");
        }
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopsSitAtTheirLevelWithHeadroom(string name)
    {
        var (samples, _, _, expected, maxPeak) = Loop(name, 3);
        Assert.All(samples, s => Assert.True(float.IsFinite(s)));
        var rms = SignalTools.Rms(samples);
        var peak = SignalTools.Peak(samples);
        Assert.InRange(rms, expected * 0.7f, expected * 1.01f); // the peak limit may pull it down a little
        Assert.True(peak <= maxPeak + 1e-4f, $"{name}: peak {Db(peak):0.0} dBFS");
        Assert.True(peak / rms < 10f, $"{name}: crest factor {peak / rms}");
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void LoopLengthsAreWholeLoops(string name)
    {
        var (samples, channels, rate, _, _) = Loop(name, 1);
        var seconds = samples.Length / channels / (float)rate;
        Assert.InRange(seconds, 5.9f, 20.01f);
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

    [Fact]
    public void InsideIsDullerThanOutside()
    {
        // Through the glass the rain loses its hiss and the city its traffic wash: the indoor beds live below ≈ 700 Hz.
        var rainOut = EnergyShare(SignalTools.Left(NeonSynth.RainOutdoor(1)), NeonSynth.Rate, 1500f, low: false);
        var rainIn = EnergyShare(SignalTools.Left(NeonSynth.RainIndoor(1)), NeonSynth.Rate, 1500f, low: false);
        var cityOut = EnergyShare(SignalTools.Left(NeonSynth.CityDrone(1)), NeonSynth.LowRate, 400f, low: true);
        var cityIn = EnergyShare(SignalTools.Left(NeonSynth.CityDrone(1, indoor: true)), NeonSynth.LowRate, 400f, low: true);
        var text = $"rain > 1.5 kHz {rainOut:0.00} out / {rainIn:0.00} in; city < 400 Hz {cityOut:0.00} out / {cityIn:0.00} in";
        Assert.True(rainOut > 0.35f, text);
        Assert.True(rainIn < rainOut * 0.5f, text);
        Assert.True(cityIn > 0.9f, text);
        Assert.True(cityOut < cityIn, text);
    }

    [Fact]
    public void TheIndoorRainPattersAndDrips()
    {
        // Ticks on the glass are transients: many 5 ms windows stand well above the bed's RMS.
        var left = SignalTools.Left(NeonSynth.RainIndoor(9));
        var rms = SignalTools.Rms(left);
        const int window = NeonSynth.Rate / 200;
        var loud = 0;
        for (var start = 0; start + window <= left.Length; start += window)
            if (SignalTools.Peak(left.AsSpan(start, window)) > 3f * rms)
                loud++;
        Assert.True(loud > NeonSynth.RainIndoorSeconds * 3, $"{loud} pattering windows in {NeonSynth.RainIndoorSeconds} s");
    }

    [Fact]
    public void EachMachineSitsInItsBand()
    {
        var fridge = NeonSynth.FridgeHum(2);
        var fans = NeonSynth.PcFans(2);
        var shower = NeonSynth.Shower(2);
        var tap = NeonSynth.Tap(2);
        var airship = NeonSynth.AirshipDrone(2);
        var train = NeonSynth.TrainRumble(2);
        var shares = new[]
        {
            EnergyShare(fridge, NeonSynth.LowRate, 400f, low: true),
            EnergyShare(fans, NeonSynth.Rate, 5000f, low: false),
            EnergyShare(shower, NeonSynth.Rate, 1500f, low: false),
            EnergyShare(tap, NeonSynth.Rate, 400f, low: true),
            EnergyShare(airship, NeonSynth.LowRate, 200f, low: true),
            EnergyShare(train, NeonSynth.LowRate, 300f, low: true),
        };
        var text = string.Join(", ", shares.Select(s => s.ToString("0.000")));
        Assert.True(shares[0] > 0.8f, "fridge is a hum: " + text);
        Assert.InRange(shares[1], 0.001f, 0.2f); // a faint whine above the fans
        Assert.True(shares[2] > 0.5f, "the shower is a bright spray: " + text);
        Assert.True(shares[3] < 0.2f, "the tap is mid-range: " + text);
        Assert.True(shares[4] > 0.6f, "the airship throbs low: " + text);
        Assert.True(shares[5] > 0.4f, "the train rumbles: " + text);
    }

    [Fact]
    public void TheTrainClatters()
    {
        // Four clacks a second (a car's two bogies): the 10 ms windows around them stand above the rumble.
        var train = NeonSynth.TrainRumble(4);
        var hp = Biquad.HighPass(600, 0.7, NeonSynth.LowRate);
        var bright = train.Select(hp.Process).ToArray();
        var rms = SignalTools.Rms(bright);
        const int window = NeonSynth.LowRate / 100;
        var loud = 0;
        for (var start = 0; start + window <= bright.Length; start += window)
            if (SignalTools.Rms(bright.AsSpan(start, window)) > 2f * rms)
                loud++;
        Assert.InRange(loud, NeonSynth.TrainSeconds * 3, NeonSynth.TrainSeconds * 12);
    }

    // ── One-shots ─────────────────────────────────────────────────────────────────────────────────

    private static (float[] Samples, float Peak) OneShot(string name, int variant, ulong seed) => name switch
    {
        "siren" => (NeonSynth.Siren(variant, seed), NeonSynth.SirenPeak),
        "horn" => (NeonSynth.Horn(variant, seed), NeonSynth.HornPeak),
        "pa-chime" => (NeonSynth.PaChime(variant, seed), NeonSynth.PaChimePeak),
        "flying-car" => (NeonSynth.FlyingCar(seed), NeonSynth.FlyingCarPeak),
        "door-open" => (NeonSynth.DoorOpen(seed), NeonSynth.DoorOpenPeak),
        "door-close" => (NeonSynth.DoorClose(seed), NeonSynth.DoorClosePeak),
        "slider-open" => (NeonSynth.Slider(false, seed), NeonSynth.SliderPeak),
        "slider-close" => (NeonSynth.Slider(true, seed), NeonSynth.SliderPeak),
        "switch" => (NeonSynth.SwitchClick(seed), NeonSynth.SwitchClickPeak),
        "locked-buzz" => (NeonSynth.LockedBuzz(seed), NeonSynth.LockedBuzzPeak),
        "tv-blip" => (NeonSynth.TvBlip(seed), NeonSynth.TvBlipPeak),
        _ => throw new ArgumentException(name),
    };

    public static TheoryData<string, int> OneShots
    {
        get
        {
            var data = new TheoryData<string, int>();
            for (var v = 0; v < NeonSynth.SirenVariants; v++)
                data.Add("siren", v);
            for (var v = 0; v < NeonSynth.HornVariants; v++)
                data.Add("horn", v);
            for (var v = 0; v < NeonSynth.PaChimeVariants; v++)
                data.Add("pa-chime", v);
            foreach (var name in new[] { "flying-car", "door-open", "door-close", "slider-open", "slider-close", "switch", "locked-buzz", "tv-blip" })
                data.Add(name, 0);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OneShots))]
    public void OneShotsAreDeterministicAtTheirPeakAndFadeOut(string name, int variant)
    {
        var (a, peak) = OneShot(name, variant, 21);
        Assert.Equal(a, OneShot(name, variant, 21).Samples);
        Assert.All(a, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(peak, SignalTools.Peak(a), 3);
        Assert.Equal(0f, a[0]);
        Assert.Equal(0f, a[^1]);
        Assert.True(SignalTools.Rms(a) > peak * 0.01f, $"{name}: almost silent");
    }

    [Fact]
    public void KeyClicksAreShortAndVaried()
    {
        var clicks = Enumerable.Range(0, NeonSynth.KeyClickVariants).Select(v => NeonSynth.KeyClick(v, 5)).ToArray();
        Assert.All(clicks, c => Assert.InRange(c.Length / (float)NeonSynth.Rate, 0.05f, 0.15f));
        Assert.All(clicks, c => Assert.InRange(SignalTools.Peak(c), NeonSynth.KeyClickPeak * 0.84f, NeonSynth.KeyClickPeak + 1e-4f));
        Assert.Equal(clicks.Length, clicks.Select(c => SignalTools.Rms(c)).Distinct().Count());
    }

    [Fact]
    public void TheLockSaysNoHighThenLow()
    {
        // The two tones after the buzz: the first (0.48 s) is higher than the second (0.68 s), by zero crossings.
        var buzz = NeonSynth.LockedBuzz(1);
        float Pitch(float from, float to)
        {
            var crossings = 0;
            for (var i = (int)(from * NeonSynth.Rate) + 1; i < (int)(to * NeonSynth.Rate); i++)
                if ((buzz[i - 1] < 0) != (buzz[i] < 0))
                    crossings++;
            return crossings / 2f / (to - from);
        }

        var first = Pitch(0.5f, 0.6f);
        var second = Pitch(0.72f, 0.86f);
        Assert.InRange(first, 900f, 1100f);
        Assert.InRange(second, 600f, 720f);
    }

    [Fact]
    public void SirensWail()
    {
        // The wail's pitch moves: zero-crossing rates over quarter seconds spread by more than 40 %.
        var siren = NeonSynth.Siren(0, 3);
        var rates = new List<float>();
        const int window = NeonSynth.Rate / 4;
        for (var start = 2 * NeonSynth.Rate; start + window <= 5 * NeonSynth.Rate; start += window)
        {
            var crossings = 0;
            for (var i = start + 1; i < start + window; i++)
                if ((siren[i - 1] < 0) != (siren[i] < 0))
                    crossings++;
            rates.Add(crossings);
        }

        Assert.True(rates.Max() > rates.Min() * 1.4f, string.Join(", ", rates));
    }

    // ── Footsteps ─────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> Surfaces => [.. NeonSynth.FootstepSurfaces];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void FootstepsAreShortDeterministicAndBelowTheirPeak(string surface)
    {
        var a = NeonSynth.Footstep(surface, 42);
        Assert.Equal(a, NeonSynth.Footstep(surface, 42));
        Assert.NotEqual(a, NeonSynth.Footstep(surface, 43));
        Assert.InRange(a.Length / (float)NeonSynth.Rate, 0.3f, 0.5f);
        var peak = SignalTools.Peak(a);
        Assert.InRange(peak, NeonSynth.FootstepPeak(surface) * 0.9f, NeonSynth.FootstepPeak(surface) + 1e-4f);
        Assert.True(SignalTools.Rms(a) > peak * 0.02f, $"{surface}: rms {SignalTools.Rms(a)}");
        Assert.Equal(0f, a[0]);
        Assert.Equal(0f, a[^1]);
    }

    [Fact]
    public void SurfacesSoundDifferent()
    {
        // Zero crossings per second (a cheap spectral centroid) separate the soft rug from the hard floors, and the grate
        // rings on longer than concrete.
        float Brightness(string surface)
        {
            var s = NeonSynth.Footstep(surface, 1);
            var crossings = 0;
            for (var i = 1; i < s.Length; i++)
                if ((s[i - 1] < 0) != (s[i] < 0))
                    crossings++;
            return crossings / (s.Length / (float)NeonSynth.Rate);
        }

        float Tail(string surface)
        {
            var s = NeonSynth.Footstep(surface, 1);
            return SignalTools.Rms(s.AsSpan((int)(0.15f * NeonSynth.Rate), (int)(0.15f * NeonSynth.Rate))) / SignalTools.Peak(s);
        }

        Assert.True(Brightness("tile") > 2f * Brightness("rug"), $"tile {Brightness("tile")}, rug {Brightness("rug")}");
        Assert.True(Brightness("concrete") > 1.5f * Brightness("rug"));
        Assert.True(Tail("metal") > 2f * Tail("concrete"), $"metal {Tail("metal")}, concrete {Tail("concrete")}");
    }

    // ── The bank ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBankHasEverySoundAndMapsSurfaces()
    {
        var bank = new NeonSoundBank(5);
        foreach (var sound in NeonSoundBank.All)
        {
            var set = bank.Variants(sound);
            Assert.NotEmpty(set);
            Assert.All(set, s => Assert.Equal(NeonSoundBank.IsLoop(sound), s.Loop));
            Assert.Same(set[0], bank[sound]);
        }

        Assert.Equal(NeonSynth.KeyClickVariants, bank.Variants(NeonSound.KeyClick).Length);
        Assert.Equal(NeonSynth.HornVariants, bank.Variants(NeonSound.Horn).Length);
        Assert.Equal(NeonSynth.SirenVariants, bank.Variants(NeonSound.Siren).Length);
        Assert.Equal(2, bank[NeonSound.RainOutdoor].Channels);
        Assert.Equal(1, bank[NeonSound.FlyingCar].Channels); // positional, for Doppler
        Assert.Equal(1, bank[NeonSound.TrainRumble].Channels);
        foreach (var surface in NeonSynth.FootstepSurfaces)
        {
            var set = bank.Footsteps(surface);
            Assert.Equal(NeonSoundBank.FootstepVariants, set.Length);
            Assert.All(set, s => Assert.Equal(NeonSoundBank.FootstepPitchRandomness, s.PitchRandomness));
            Assert.Equal(set.Length, set.Select(s => SignalTools.Rms(s.DecodedSamples.Span)).Distinct().Count());
        }

        Assert.Same(bank.Footsteps("metal"), bank.Footsteps("grate"));
        Assert.Same(bank.Footsteps("rug"), bank.Footsteps("carpet"));
        Assert.Same(bank.Footsteps("default"), bank.Footsteps("lava"));
        Assert.Same(bank.Footsteps("default"), bank.Footsteps(null));
        Assert.Equal(NeonAudio.BathroomBus, NeonSoundBank.DefaultBus(NeonSound.Shower));
        Assert.Equal(NeonAudio.TvBus, NeonSoundBank.DefaultBus(NeonSound.TvJingle));
        Assert.Equal(NeonAudio.RainBus, NeonSoundBank.DefaultBus(NeonSound.RainIndoor));
    }

    [Fact]
    public void RandomIsStableAcrossMachines()
    {
        // SplitMix64's published first outputs for seed 0 pin the generator.
        var r = new NeonRandom(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, r.NextULong());
        Assert.Equal(0x6E789E6AA1B965F4UL, r.NextULong());
    }
}
