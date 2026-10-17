using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.DspTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class BuiltinEffectsTests
{
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var info in BuiltinEffects.All)
            data.Add(info.Id);
        return data;
    }

    [Fact]
    public void TheRegistryIsConsistent()
    {
        Assert.NotEmpty(BuiltinEffects.All);
        Assert.Equal(BuiltinEffects.All.Count, BuiltinEffects.All.Select(i => i.Id).Distinct().Count());
        foreach (var info in BuiltinEffects.All)
        {
            Assert.Same(info, BuiltinEffects.Find(info.Id));
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.Tooltip));
            IconAtlas.AssertExists(info.Icon, info.Id);
            Assert.Equal(info.Params.Length, info.Params.Select(p => p.Id).Distinct().Count());
            foreach (var p in info.Params)
            {
                Assert.True(p.Min <= p.Default && p.Default <= p.Max, $"{info.Id}.{p.Id} default out of range");
                Assert.False(p.Curve == ParamCurve.Log && p.Min <= 0, $"{info.Id}.{p.Id} log with min <= 0");
                Assert.False(p.Curve == ParamCurve.Choice && p.Choices is not { Length: > 0 }, $"{info.Id}.{p.Id} choice without choices");
                Assert.False(string.IsNullOrEmpty(p.Format(p.Default)));
                foreach (var n in new[] { 0f, 0.25f, 0.5f, 1f })
                {
                    var value = p.FromNormalized(n);
                    if (p.Curve is ParamCurve.Linear or ParamCurve.Log)
                        Assert.Equal(n, p.ToNormalized(value), 3);
                }
            }

            foreach (var preset in info.Presets ?? [])
            {
                foreach (var id in preset.Values.Keys)
                    Assert.NotNull(info.Find(id));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void EveryEffectRunsAtCommonRatesWithFiniteOutput(string id)
    {
        foreach (var rate in new[] { 44100, 48000, 96000 })
        {
            var effect = Create(id, sampleRate: rate);
            var output = Run(effect, Sine(220, 0.5f, 0.3, rate), sampleRate: rate);
            Assert.All(output, v => Assert.True(float.IsFinite(v)));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void SilenceAfterABurstNeverProducesNaNsOrSubnormals(string id)
    {
        var info = BuiltinEffects.Find(id)!;
        var effect = Create(id, info.Presets is { Length: > 1 } presets ? new Dictionary<string, float>(presets[^1].Values) : null);
        Run(effect, Sine(440, 0.9f, 0.2));
        var silence = Run(effect, new float[Rate * 2 * 20]); // 20 s
        foreach (var v in silence.AsSpan(silence.Length - Rate * 2))
        {
            Assert.True(float.IsFinite(v));
            Assert.False(float.IsSubnormal(v), $"{id} output a subnormal");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ProcessingAllocatesNothingAcrossParameterChangesAndBypass(string id)
    {
        var info = BuiltinEffects.Find(id)!;
        var effect = info.Create(Rate);
        var block = new float[SongEngine.BlockFrames * 2];
        var a = info.Defaults();
        var b = info.Defaults();
        foreach (var p in info.Params)
            b[p.Id] = p.FromNormalized(0.7f);
        var context = new EffectContext(Rate, 120, true, 0);
        for (var i = 0; i < 200; i++) // warm up every path
        {
            effect.Apply(i % 2 == 0 ? a : b, i % 50 > 40);
            block.AsSpan().Fill(0.1f);
            effect.Process(block, context);
        }

        var allocated = long.MaxValue;
        for (var attempt = 0; attempt < 3 && allocated != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                if (i % 100 == 0)
                    effect.Apply(i % 200 == 0 ? a : b, i % 300 == 0); // UI-thread work, here on the same thread
                block.AsSpan().Fill(0.1f);
                effect.Process(block, context with { PositionFrames = i * SongEngine.BlockFrames });
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void BypassFadesToTheDrySignal(string id)
    {
        var effect = Create(id);
        effect.Apply(null, true);
        var input = Sine(330, 0.3f, 0.2);
        var output = Run(effect, (float[])input.Clone());
        var latency = effect.LatencyFrames;
        for (var f = latency + 2000; f < input.Length / 2; f++)
            Assert.Equal(input[2 * (f - latency)], output[2 * f], 5);
    }

    [Fact]
    public void ParameterFormattingReadsLikeAMixer()
    {
        Assert.Equal("2.40 kHz", new BuiltinParam("f", "F", "Hz", 20, 20000, 2400, ParamCurve.Log).Format(2400));
        Assert.Equal("80 Hz", new BuiltinParam("f", "F", "Hz", 20, 20000, 80, ParamCurve.Log).Format(80));
        Assert.Equal("-6.0 dB", new BuiltinParam("g", "G", "dB", -24, 24, 0).Format(-6));
        Assert.Equal("4.0 : 1", new BuiltinParam("r", "R", ":1", 1, 20, 4).Format(4));
        Assert.Equal("30 L", new BuiltinParam("p", "P", "pan", -100, 100, 0).Format(-30));
        Assert.Equal("On", BuiltinParam.Toggle("t", "T", true).Format(1));
        Assert.Equal("b", BuiltinParam.Choice("c", "C", ["a", "b"]).Format(1));
    }
}
