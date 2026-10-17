using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.DspTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class EqAndDynamicsTests
{
    [Fact]
    public void AnEqBandBoostsItsFrequencyAndLeavesOthersAlone()
    {
        var values = new Dictionary<string, float> { ["peak2Freq"] = 1000, ["peak2Gain"] = 6, ["peak2Q"] = 1 };
        Assert.Equal(6, GainAt(Create("eq", values), 1000), 0.2);
        Assert.Equal(0, GainAt(Create("eq", values), 100), 0.2);
        Assert.Equal(0, GainAt(Create("eq"), 1000), 0.05); // flat by default
    }

    [Fact]
    public void ALowCutFallsAtItsSlope()
    {
        var values = new Dictionary<string, float> { ["lowCutOn"] = 1, ["lowCutFreq"] = 100, ["lowCutSlope"] = 2 }; // 24 dB/oct
        Assert.True(GainAt(Create("eq", values), 50) <= -20);
        Assert.Equal(-3, GainAt(Create("eq", values), 100), 0.3);
        Assert.Equal(0, GainAt(Create("eq", values), 1000), 0.2);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void TheEqCurveIsWhatPlays(double frequency)
    {
        var values = new Dictionary<string, float>
        {
            ["lowShelfGain"] = 4, ["peak2Freq"] = 900, ["peak2Gain"] = -5, ["peak2Q"] = 2, ["highShelfGain"] = -3, ["output"] = 1.5f,
        };
        var curve = EqEffect.ResponseDb(values, frequency, Rate);
        Assert.Equal(curve, GainAt(Create("eq", values), frequency), 0.15);
    }

    [Fact]
    public void EqParameterChangesGlideWithoutResettingTheFilter()
    {
        var effect = Create("eq");
        var input = Sine(1000, 0.25f, 0.5);
        Run(effect, input);
        effect.Apply(new Dictionary<string, float> { ["peak2Freq"] = 1000, ["peak2Gain"] = 12 }, false);
        var after = Run(effect, Sine(1000, 0.25f, 0.5));
        // Settled at the new gain; no sample jumps more than the sine itself can between frames.
        Assert.Equal(12, RmsDb(after, 0, 12000, 24000) - RmsDb(input, 0, 12000, 24000), 0.3);
        for (var f = 1; f < 2000; f++)
            Assert.True(Math.Abs(after[2 * f] - after[2 * (f - 1)]) < 0.15);
    }

    [Theory]
    [InlineData(-8, -17)] // 12 dB over a -20 dB threshold at 4:1 → 3 dB over
    [InlineData(-30, -30)] // under the threshold: untouched
    public void TheCompressorFollowsItsStaticCurve(float inputDb, float expectedDb)
    {
        var effect = Create("compressor", new Dictionary<string, float> { ["threshold"] = -20, ["ratio"] = 4, ["knee"] = 0, ["attack"] = 5, ["release"] = 200 });
        var output = Run(effect, Sine(1000, Dsp.DbToGain(inputDb), 1));
        Assert.Equal(expectedDb, PeakDb(output, Rate / 2, Rate), 0.5);
        Assert.Equal(inputDb - expectedDb, effect.TakeGainReductionDb(), 0.6);
        Assert.Equal(0, effect.TakeGainReductionDb()); // taken
    }

    [Fact]
    public void TheCompressorsKneeIsContinuous()
    {
        for (var x = -40f; x < 0; x += 0.25f)
        {
            var a = CompressorEffect.OutputDb(x, -20, 4, 10);
            var b = CompressorEffect.OutputDb(x + 0.25f, -20, 4, 10);
            Assert.InRange(b - a, 0.0624f, 0.2501f);
        }
    }

    [Fact]
    public void UtilityWidthZeroIsMonoAndInvertFlipsAChannel()
    {
        var input = new float[SongEngine.BlockFrames * 2 * 4];
        for (var f = 0; f < input.Length / 2; f++)
            (input[2 * f], input[2 * f + 1]) = (0.5f, -0.1f);
        var mono = Run(Create("utility", new Dictionary<string, float> { ["width"] = 0 }), (float[])input.Clone());
        Assert.Equal(mono[^2], mono[^1], 6);
        Assert.Equal(0.2f * MathF.Sqrt(0.5f) * MathF.Sqrt(2f), mono[^2], 4);
        var inverted = Run(Create("utility", new Dictionary<string, float> { ["invertLeft"] = 1 }), (float[])input.Clone());
        Assert.Equal(-0.5f, inverted[^2], 4);
        Assert.Equal(-0.1f, inverted[^1], 4);
    }
}
