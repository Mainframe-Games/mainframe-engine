using System.Text;
using System.Text.Json.Nodes;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>Parameters, curves, rule compilation and the <c>.mmusic</c> format.</summary>
public sealed class MusicLogicTests
{
    private static MusicParameterInfo Intensity(float seek = 0) =>
        new() { Name = "intensity", Kind = MusicParameterKind.Continuous, Min = 0, Max = 1, SeekSpeed = seek };

    private static MusicParameterInfo Area() =>
        new() { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat", "Boss"] };

    private static MusicParameterInfo Wave() =>
        new() { Name = "wave", Kind = MusicParameterKind.Discrete, Min = 1, Max = 5, Default = 1 };

    // --- parameters ------------------------------------------------------------------------------

    [Fact]
    public void ParametersClampRoundAndLookUpLabels()
    {
        var set = new MusicParameterSet([Intensity(), Area(), Wave()]);
        Assert.Equal(3, set.Count);
        Assert.Equal(1, set.IndexOf("area"));
        Assert.Equal(-1, set.IndexOf("missing"));
        Assert.Equal(1f, set.GetValue(2)); // default

        set.SetTarget(0, 7);
        Assert.Equal(1f, set.GetValue(0)); // clamped, no seek: jumps
        set.SetTarget(2, 3.6f);
        Assert.Equal(4f, set.GetValue(2)); // discrete rounds
        Assert.True(set.SetLabel("area", "Boss"));
        Assert.Equal(2f, set.GetValue(1));
        Assert.Equal("Boss", set.GetLabel(1));
        Assert.False(set.SetLabel("area", "Nope"));
        Assert.False(set.SetTarget("nope", 1));
        set.SetTarget(1, 9);
        Assert.Equal(2f, set.GetValue(1)); // labeled clamps to the last label
        set.SetTarget(0, float.NaN);
        Assert.Equal(1f, set.GetValue(0)); // NaN ignored

        set.Reset();
        Assert.Equal((0f, 0f, 1f), (set.GetValue(0), set.GetValue(1), set.GetValue(2)));
    }

    [Fact]
    public void ContinuousParametersSeekAtTheirSpeedOthersJump()
    {
        var set = new MusicParameterSet([Intensity(seek: 0.5f), Area()]);
        var version = set.Version;
        set.SetTarget(0, 1);
        Assert.Equal(0f, set.GetValue(0));
        Assert.Equal(1f, set.GetTarget(0));
        Assert.Equal(version, set.Version); // the value has not moved yet

        Assert.True(set.Advance(1.0));
        Assert.Equal(0.5f, set.GetValue(0), 5);
        Assert.True(set.Advance(5.0));
        Assert.Equal(1f, set.GetValue(0)); // lands exactly on the target
        Assert.False(set.Advance(1.0));
        Assert.True(set.Version > version);

        set.SetTarget(0, 0, immediate: true);
        Assert.Equal(0f, set.GetValue(0));
        set.SetTarget(1, 1);
        Assert.Equal(1f, set.GetValue(1)); // labeled ignores the seek
    }

    [Fact]
    public void RepeatedNamesKeepTheFirst()
    {
        var set = new MusicParameterSet([Intensity(), new MusicParameterInfo { Name = "intensity", Min = 5, Max = 9 }]);
        Assert.Equal(1, set.Count);
        Assert.Equal(1f, set.MaxOf(0));
    }

    // --- curves ----------------------------------------------------------------------------------

    [Fact]
    public void VolumeCurvesInterpolateInDecibelsAndHoldTheirEnds()
    {
        var linear = new MusicCurve(0, MusicCurveTarget.Volume, 0, MusicCurveShape.Linear, false, [new(0.5f, 0), new(0, -80)]); // unsorted on purpose
        Assert.Equal(-80f, linear.Evaluate(-1));
        Assert.Equal(-40f, linear.Evaluate(0.25f), 4);
        Assert.Equal(0f, linear.Evaluate(0.9f));

        var smooth = new MusicCurve(0, MusicCurveTarget.Volume, 0, MusicCurveShape.Smooth, false, [new(0, -80), new(1, 0)]);
        Assert.Equal(-40f, smooth.Evaluate(0.5f), 4); // cosine ease is symmetric
        Assert.True(smooth.Evaluate(0.1f) < linear.Evaluate(0.05f)); // slow start
    }

    [Fact]
    public void CutoffCurvesInterpolateOnALogScale()
    {
        var curve = new MusicCurve(0, MusicCurveTarget.Cutoff, 0, MusicCurveShape.Linear, false, [new(0, 200), new(1, 20000)]);
        Assert.Equal(2000f, curve.Evaluate(0.5f), 1); // the geometric mean
        Assert.Equal(200f, curve.Evaluate(0), 1);
    }

    [Fact]
    public void CurvesOverLabelsAreSteps()
    {
        var curve = new MusicCurve(0, MusicCurveTarget.Volume, 0, MusicCurveShape.Smooth, true, [new(0, -80), new(2, 0)]);
        Assert.Equal(-80f, curve.Evaluate(0));
        Assert.Equal(-80f, curve.Evaluate(1)); // no point at 1: holds the point below
        Assert.Equal(0f, curve.Evaluate(2));
    }

    [Fact]
    public void LayerCurvesCombine()
    {
        var plan = MusicPlan.Compile(new MusicManifest
        {
            Parameters = [Intensity(), Wave()],
            Layers =
            [
                new MusicManifestLayer { Id = "a", Filter = MusicLayerFilter.LowPass },
                new MusicManifestLayer { Id = "b", Filter = MusicLayerFilter.HighPass },
                new MusicManifestLayer { Id = "c" },
            ],
            Sections = [new MusicManifestSection { Id = "s", Bars = 1 }],
            Curves =
            [
                new MusicCurveInfo { Layer = "a", Target = MusicCurveTarget.Volume, Parameter = "intensity", Points = [new(0, -6), new(1, -6)] },
                new MusicCurveInfo { Layer = "a", Target = MusicCurveTarget.Volume, Parameter = "wave", Points = [new(1, -6), new(5, -6)] },
                new MusicCurveInfo { Layer = "a", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 500), new(1, 500)] },
                new MusicCurveInfo { Layer = "a", Target = MusicCurveTarget.Cutoff, Parameter = "wave", Points = [new(1, 900), new(5, 900)] },
                new MusicCurveInfo { Layer = "b", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 500), new(1, 500)] },
                new MusicCurveInfo { Layer = "b", Target = MusicCurveTarget.Cutoff, Parameter = "wave", Points = [new(1, 900), new(5, 900)] },
                new MusicCurveInfo { Layer = "c", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 1)] }, // no filter
            ],
        });
        Span<float> gain = stackalloc float[3];
        Span<float> cutoff = stackalloc float[3];
        plan.EvaluateLayers(plan.CreateParameters(), gain, cutoff);
        Assert.Equal(-12f, gain[0], 3); // gains multiply: dB add
        Assert.Equal(500f, cutoff[0], 1); // low-pass: the lowest
        Assert.Equal(900f, cutoff[1], 1); // high-pass: the highest
        Assert.Equal(0f, gain[2]);
        Assert.Equal(MusicCurve.MaxCutoffHz, cutoff[2]);
        Assert.Contains(plan.Problems, p => p.Contains("no filter", StringComparison.Ordinal));
    }

    // --- plan ------------------------------------------------------------------------------------

    [Fact]
    public void CompilingReportsWhatItCannotUseAndNeverThrows()
    {
        var plan = MusicPlan.Compile(new MusicManifest
        {
            Start = "outro",
            Parameters = [Area()],
            Layers = [.. Enumerable.Range(0, 10).Select(i => new MusicManifestLayer { Id = "l" + i })],
            Sections =
            [
                new MusicManifestSection { Id = "explore", Bars = 4, Loop = true },
                new MusicManifestSection { Id = "outro", Bars = 2, Kind = MusicSectionKind.Stinger },
                new MusicManifestSection { Id = "intro", Bars = 2, Then = "nowhere" },
                new MusicManifestSection { Id = "empty", Bars = 0 },
                new MusicManifestSection { Id = "explore", Bars = 1 },
            ],
            Curves = [new MusicCurveInfo { Layer = "ghost", Parameter = "area", Points = [new(0, 0)] }],
            Transitions =
            [
                new MusicTransitionInfo { From = ["intro", "missing"], To = "explore", When = When("area", "Combat") },
                new MusicTransitionInfo { From = ["*"], To = "outro", When = When("area", "Combat") },
                new MusicTransitionInfo { From = ["explore"], To = "intro", When = When("area", "Nope") },
                new MusicTransitionInfo { From = ["explore"], To = "intro", When = When("ghost", "Calm") },
                new MusicTransitionInfo { From = ["missing"], To = "intro" },
            ],
        });

        Assert.Equal(MusicPlan.MaxLayers, plan.Layers.Length);
        Assert.Equal(["explore", "outro", "intro"], plan.Sections.Select(s => s.Id));
        Assert.Equal(0, plan.StartSection); // the stinger cannot start: the first playable section
        Assert.True(plan.Sections[0].Loops);
        Assert.Equal(-1, plan.Sections[2].Then);
        Assert.Empty(plan.Curves);
        Assert.Equal(3, plan.Rules.Length); // the stinger destination and the source-less rule are dropped
        Assert.Equal([2], plan.Rules[0].From);
        Assert.True(plan.Rules[0].Automatic);
        Assert.False(plan.Rules[1].Automatic); // unknown label
        Assert.False(plan.Rules[2].Automatic); // unknown parameter
        string[] expected = ["over the limit", "no bars", "defined twice", "not a playable section", "does not exist", "not one of its labels", "no source"];
        foreach (var text in expected)
            Assert.Contains(plan.Problems, p => p.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public void RulesMatchAllAndAnyAndSkipTheCurrentSection()
    {
        var plan = MusicPlan.Compile(new MusicManifest
        {
            Parameters = [Intensity(), Area()],
            Sections = [new MusicManifestSection { Id = "a", Bars = 1, Loop = true }, new MusicManifestSection { Id = "b", Bars = 1, Loop = true }],
            Transitions =
            [
                new MusicTransitionInfo
                {
                    From = ["*"], To = "b",
                    When = new MusicConditionGroup
                    {
                        All = [new MusicComparison { Parameter = "intensity", Op = MusicCompare.GreaterOrEqual, Value = new MusicValue(0.5f) }],
                        Any =
                        [
                            new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue("Combat") },
                            new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue(2) },
                        ],
                    },
                },
            ],
        });
        var p = plan.CreateParameters();
        Assert.Null(plan.FirstMatchingRule(0, p));
        p.SetTarget(0, 0.5f);
        Assert.Null(plan.FirstMatchingRule(0, p)); // all holds, any does not
        p.SetLabel("area", "Boss");
        Assert.Same(plan.Rules[0], plan.FirstMatchingRule(0, p)); // the label index 2
        Assert.Null(plan.FirstMatchingRule(1, p)); // already in b
    }

    // --- manifest format -------------------------------------------------------------------------

    private const string Example = """
        {
          "format": 1,
          "uid": "mus_8c1f00000000",
          "song": "sng_41aa00000000",
          "sampleRate": 48000,
          "tempo": 120, "timeSignature": [4, 4],
          "start": "explore",
          "x-future": { "kept": true },
          "parameters": [ { "name": "intensity", "kind": "continuous", "min": 0, "max": 1, "default": 0, "seekSpeed": 0.5, "x-curve": 2 } ],
          "layers": [ { "id": "perc", "name": "Perc", "filter": "lowpass" } ],
          "sections": [
            {
              "id": "explore", "name": "Explore", "kind": "section", "bars": 16, "lengthFrames": 1536000, "loop": true,
              "markers": [ { "name": "drop", "frame": 768000, "exit": true } ],
              "stems": {
                "perc": { "body": { "uid": "aud_000000000001", "path": "Content/Music/Theme.stems/explore.perc.3f9a1c.ogg" },
                          "tail": { "uid": "aud_000000000002", "path": "Content/Music/Theme.stems/explore.perc.3f9a1c.tail.ogg", "frames": 61440 } },
                "pads": null
              }
            }
          ],
          "curves": [ { "layer": "perc", "target": "volume", "parameter": "intensity", "shape": "smooth", "points": [[0, -80], [0.5, 0]] } ],
          "transitions": [ { "from": ["explore"], "to": "explore", "when": { "all": [ { "parameter": "intensity", "op": ">=", "value": 0.5 } ] }, "quantize": "bars", "every": 2 } ],
          "bake": { "editor": "0.1.212", "inputs": { "explore/perc": "3f9a1c" } }
        }
        """;

    [Fact]
    public void TheManifestRoundTripsWithUnknownFieldsKept()
    {
        var read = MusicManifestFormat.Parse(Encoding.UTF8.GetBytes(Example), "Theme.mmusic");
        Assert.Null(read.Notice);
        var m = read.Manifest;
        Assert.Equal("mus_8c1f00000000", m.Uid);
        var section = Assert.Single(m.Sections);
        Assert.Equal(1536000, section.LengthFrames);
        Assert.True(Assert.Single(section.Markers).Exit);
        Assert.Equal(61440, section.Stems["perc"]!.Tail!.Frames);
        Assert.Null(section.Stems["pads"]);
        Assert.Equal(MusicLayerFilter.LowPass, m.Layers[0].Filter);
        Assert.Equal([new MusicCurvePoint(0, -80), new MusicCurvePoint(0.5f, 0)], m.Curves[0].Points);
        Assert.Equal(MusicCompare.GreaterOrEqual, m.Transitions[0].When!.All![0].Op);
        Assert.Equal(0.5f, m.Transitions[0].When!.All![0].Value.Number);
        Assert.Equal(2, m.Transitions[0].Every);

        var written = MusicManifestFormat.Write(m);
        var again = JsonNode.Parse(written)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Example), again), Encoding.UTF8.GetString(written));

        var plan = MusicPlan.Compile(m);
        Assert.Empty(plan.Problems);
        Assert.Equal([768000L], plan.Sections[0].ExitFrames);
    }

    [Fact]
    public void NewerManifestsLoadWithANoticeAndBadOnesThrowInvalidData()
    {
        var newer = MusicManifestFormat.Parse("""{ "format": 99, "sections": null, "parameters": [] }"""u8, "New.mmusic");
        Assert.NotNull(newer.Notice);
        Assert.Empty(newer.Manifest.Sections);
        Assert.Throws<InvalidDataException>(() => MusicManifestFormat.Parse("[1]"u8, "x"));
        Assert.Throws<InvalidDataException>(() => MusicManifestFormat.Parse("{ nope"u8, "x"));
        Assert.Throws<InvalidDataException>(() => MusicManifestFormat.Parse("""{ "sections": 3 }"""u8, "x"));
    }

    [Fact]
    public void ManifestsGetTheirOwnUidPrefix()
    {
        Assert.Equal(AssetUid.MusicPrefix, AssetUid.PrefixForExtension(MusicManifestFormat.Extension));
        Assert.StartsWith("mus_", AssetUid.Generate(AssetUid.MusicPrefix), StringComparison.Ordinal);
    }

    internal static MusicConditionGroup When(string parameter, string label) =>
        new() { All = [new MusicComparison { Parameter = parameter, Op = MusicCompare.Equal, Value = new MusicValue(label) }] };
}
