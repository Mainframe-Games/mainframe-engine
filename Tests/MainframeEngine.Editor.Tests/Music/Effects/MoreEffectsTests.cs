using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.DspTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class MoreEffectsTests
{
    [Fact]
    public void TheLimiterNeverPassesItsCeiling()
    {
        var limiter = Create("limiter", new Dictionary<string, float> { ["gain"] = 12, ["ceiling"] = -1, ["release"] = 30 });
        var random = new Random(7);
        var signal = new float[Rate * 2];
        for (var i = 0; i < signal.Length; i++)
            signal[i] = (float)(random.NextDouble() * 2 - 1) * (i / 2 % 4800 < 200 ? 1f : 0.2f); // noise with loud bursts
        var output = Run(limiter, signal);
        var ceiling = Dsp.DbToGain(-1);
        Assert.All(output, v => Assert.True(Math.Abs(v) <= ceiling + 1e-6f));
        Assert.True(limiter.TakeGainReductionDb() > 6);
    }

    [Fact]
    public void TheLimiterDelaysByItsLatency()
    {
        var limiter = Create("limiter");
        Assert.Equal((int)Math.Round(0.003 * Rate), limiter.LatencyFrames);
        var output = Run(limiter, Impulse(0.05, 0));
        var peak = Enumerable.Range(0, output.Length / 2).MaxBy(f => Math.Abs(output[2 * f]));
        Assert.Equal(limiter.LatencyFrames, peak);
    }

    [Fact]
    public void TheGateDropsToItsRangeBelowTheThreshold()
    {
        var gate = Create("gate", new Dictionary<string, float> { ["threshold"] = -30, ["range"] = -40, ["release"] = 10, ["hold"] = 0 });
        var loud = Run(gate, Sine(500, Dsp.DbToGain(-10), 0.3));
        Assert.Equal(-10, PeakDb(loud, Rate / 10, (int)(0.3 * Rate)), 0.3);
        var quiet = Run(gate, Sine(500, Dsp.DbToGain(-50), 0.5));
        Assert.Equal(-90, PeakDb(quiet, Rate / 4, Rate / 2), 0.5);
    }

    [Theory]
    [InlineData(120, 2, 0, 1.0)] // 1/4
    [InlineData(120, 3, 1, 0.75)] // 1/8 dotted
    [InlineData(90, 3, 2, 1.0 / 3)] // 1/8 triplet
    public void TheFirstEchoLandsOnTheNoteValue(double tempo, int division, int feel, double beats)
    {
        var delay = Create("delay", new Dictionary<string, float> { ["division"] = division, ["feel"] = feel, ["mix"] = 100, ["feedback"] = 0, ["lowCut"] = 20, ["highCut"] = 20000 });
        var output = Run(delay, Impulse(1.5, 0), tempo);
        var expected = (int)Math.Round(60 / tempo * beats * Rate);
        var peak = Enumerable.Range(0, output.Length / 2).MaxBy(f => Math.Abs(output[2 * f]));
        Assert.InRange(peak, expected - 2, expected + 2);
        Assert.Contains(NoteDivision.Label(division, feel), DelayEffect.Describe(new Dictionary<string, float> { ["division"] = division, ["feel"] = feel }, tempo), StringComparison.Ordinal);
    }

    [Fact]
    public void PingPongAlternatesChannels()
    {
        var delay = Create("delay", new Dictionary<string, float> { ["division"] = 2, ["feel"] = 0, ["pingPong"] = 1, ["feedback"] = 80, ["mix"] = 100, ["lowCut"] = 20, ["highCut"] = 20000 });
        var output = Run(delay, Impulse(1.6, 0));
        var echo = Rate / 2; // 1/4 at 120 BPM
        Assert.True(Math.Abs(output[2 * echo]) > 4 * Math.Abs(output[2 * echo + 1]), "first echo on the left");
        Assert.True(Math.Abs(output[2 * 2 * echo + 1]) > 4 * Math.Abs(output[2 * 2 * echo]), "second echo on the right");
    }

    [Fact]
    public void TheReverbTailDecaysAndClearsOnReset()
    {
        var reverb = Create("reverb", new Dictionary<string, float> { ["mix"] = 100, ["preDelay"] = 0 });
        var output = Run(reverb, Impulse(3, 0));
        var early = RmsDb(output, 0, Rate / 10, Rate / 2);
        var late = RmsDb(output, 0, (int)(2.5 * Rate), 3 * Rate);
        Assert.True(early > -80, $"a tail: {early}");
        Assert.True(late < early - 20, $"decays: {early} → {late}");
        reverb.RequestReset();
        var silence = Run(reverb, new float[SongEngine.BlockFrames * 8]);
        Assert.All(silence, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ASyncedTremoloFollowsTheSongPosition()
    {
        var values = new Dictionary<string, float> { ["division"] = 2, ["depth"] = 100, ["shape"] = 2 }; // 1/4 square
        var a = Run(Create("tremolo", values), Constant(1f, 1), startFrame: 0);
        var b = Run(Create("tremolo", values), Constant(1f, 1), startFrame: Rate / 4); // half a cycle later
        // At 120 BPM a quarter is 0.5 s: one is up while the other is down.
        Assert.True(a[2 * (Rate / 8)] > 0.9f && b[2 * (Rate / 8)] < 0.1f);
        Assert.True(a[2 * (Rate / 2 + Rate / 8)] > 0.9f, "period 0.5 s");
    }

    [Fact]
    public void TheFilterCutsAboveItsCutoff()
    {
        var values = new Dictionary<string, float> { ["cutoff"] = 500, ["resonance"] = 0, ["slope"] = 1 };
        Assert.True(GainAt(Create("filter", values), 4000) < -30);
        Assert.Equal(0, GainAt(Create("filter", values), 100), 1);
    }

    [Fact]
    public void OneBitLeavesTwoLevelsAndDownsamplingHolds()
    {
        var output = Run(Create("bitcrusher", new Dictionary<string, float> { ["bits"] = 1, ["downsample"] = 4 }), Sine(100, 0.8f, 0.1));
        Assert.Equal(-1f, output.Min());
        Assert.All(output, v => Assert.Contains(v, new[] { -1f, 0f, 1f }));
        var last = -1; // the first held sample is frame 3
        for (var f = 1; f < output.Length / 2; f++)
        {
            if (output[2 * f] == output[2 * (f - 1)])
                continue;
            Assert.Equal(0, (f - last) % 4); // a new value only every 4th sample
            last = f;
        }
    }

    private static float[] Constant(float value, double seconds)
    {
        var result = new float[(int)(seconds * Rate) * 2];
        Array.Fill(result, value);
        return result;
    }
}
