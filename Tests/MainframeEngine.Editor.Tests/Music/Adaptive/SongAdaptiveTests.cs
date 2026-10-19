using System.Text;
using System.Text.Json.Nodes;
using MainframeEngine.Audio.Music;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

public sealed class SongAdaptiveTests
{
    private const long Bar = 3840; // 960 PPQ, 4/4

    // The design doc's example (docs/design/future/adaptive-music.md), with fields this editor does not know.
    private const string Example = """
        {
          "format": 1,
          "uid": "sng_0123456789ab",
          "tempo": 120, "timeSignature": [4, 4], "ppq": 960,
          "tracks": [
            { "id": "t1", "name": "Pads",  "layer": "base",   "kind": "instrument", "clips": [] },
            { "id": "t2", "name": "Drums", "layer": "perc",   "kind": "instrument", "clips": [] },
            { "id": "t3", "name": "Brass", "layer": "combat", "kind": "instrument", "clips": [] }
          ],
          "adaptive": {
            "start": "intro",
            "output": "Assets/Music/Theme.mmusic",
            "tailSeconds": 4,
            "x-future": [1, 2],
            "parameters": [
              { "name": "intensity", "kind": "continuous", "min": 0, "max": 1, "default": 0, "seekSpeed": 0.5 },
              { "name": "area", "kind": "labeled", "labels": ["Calm", "Combat", "Boss"], "default": 0 },
              { "name": "wave", "kind": "discrete", "min": 1, "max": 5, "default": 1 }
            ],
            "sections": [
              { "id": "intro",   "name": "Intro",    "kind": "section",    "start": 0,      "end": 15360,  "loop": false, "then": "explore" },
              { "id": "explore", "name": "Explore",  "kind": "section",    "start": 15360,  "end": 76800,  "loop": true, "x-color-hint": "blue" },
              { "id": "combat",  "name": "Combat",   "kind": "section",    "start": 76800,  "end": 138240, "loop": true },
              { "id": "toboss",  "name": "To Boss",  "kind": "transition", "start": 138240, "end": 153600 },
              { "id": "boss",    "name": "Boss",     "kind": "section",    "start": 153600, "end": 215040, "loop": true },
              { "id": "victory", "name": "Victory",  "kind": "stinger",    "start": 215040, "end": 230400, "quantize": "beat", "pickupBeats": 1 },
              { "id": "outro",   "name": "Outro",    "kind": "section",    "start": 230400, "end": 261120, "loop": false }
            ],
            "markers": [
              { "id": "m1", "name": "drop", "tick": 107520, "exit": true }
            ],
            "layers": [
              { "id": "base",   "name": "Base",   "color": "#3b82f6" },
              { "id": "perc",   "name": "Perc",   "color": "#f59e0b" },
              { "id": "combat", "name": "Combat", "color": "#ef4444", "filter": "lowpass" }
            ],
            "curves": [
              { "layer": "perc",   "target": "volume", "parameter": "intensity", "shape": "smooth", "points": [[0, -80], [0.3, -80], [0.5, 0], [1, 0]] },
              { "layer": "combat", "target": "volume", "parameter": "intensity", "shape": "smooth", "points": [[0, -80], [0.5, -80], [0.8, 0], [1, 0]] },
              { "layer": "combat", "target": "cutoff", "parameter": "intensity", "shape": "linear", "points": [[0.5, 800], [1, 20000]] }
            ],
            "transitions": [
              { "from": ["explore"], "to": "combat", "when": { "all": [{ "parameter": "area", "op": "==", "value": "Combat" }] },
                "quantize": "bar", "fadeOutBeats": 1 },
              { "from": ["combat"], "to": "boss", "when": { "all": [{ "parameter": "area", "op": "==", "value": "Boss" }] },
                "quantize": "exitMarker", "segment": "toboss" },
              { "from": ["combat", "boss"], "to": "outro", "when": { "all": [{ "parameter": "area", "op": "==", "value": "Calm" }] },
                "quantize": "bars", "every": 4, "stinger": "victory", "fadeOutBeats": 2, "x-note": "kept" }
            ]
          }
        }
        """;

    private static Song Parse(string json) => SongFormat.Parse(Encoding.UTF8.GetBytes(json), "Theme.msong").Song;

    [Fact]
    public void TheDesignExampleRoundTripsWithUnknownFieldsKept()
    {
        var song = Parse(Example);
        var a = song.Adaptive!;
        Assert.Equal("intro", a.Start);
        Assert.Equal(7, a.Sections.Count);
        Assert.Equal(MusicSectionKind.Transition, a.FindSection("toboss")!.Kind);
        Assert.Equal(MusicQuantize.Beat, a.FindSection("victory")!.Quantize);
        Assert.Equal("perc", song.Tracks[1].Layer);
        Assert.Equal(MusicLayerFilter.LowPass, a.FindLayer("combat")!.Filter);
        Assert.Equal(["Calm", "Combat", "Boss"], a.FindParameter("area")!.Labels!);
        Assert.Equal("Combat", a.Transitions[0].When!.All![0].Value.Label);
        Assert.Equal(4, a.Transitions[2].Every);

        // Everything in the file comes back (the editor adds only default values such as "loop": false), unknown fields included.
        var written = SongFormat.Write(song);
        var expected = JsonNode.Parse(Example)!;
        var actual = JsonNode.Parse(written)!;
        Assert.True(Subset(expected["adaptive"], actual["adaptive"], "adaptive", out var where), where + "\n" + Encoding.UTF8.GetString(written));
        Assert.Equal("perc", (string?)actual["tracks"]![1]!["layer"]);
        Assert.Equal(Encoding.UTF8.GetString(written), Encoding.UTF8.GetString(SongFormat.Write(Parse(Encoding.UTF8.GetString(written)))));
    }

    // Every value of expected is in actual (objects may have more properties; arrays match element by element).
    private static bool Subset(JsonNode? expected, JsonNode? actual, string path, out string where)
    {
        where = path;
        switch (expected)
        {
            case JsonObject e when actual is JsonObject a:
                foreach (var (key, value) in e)
                {
                    if (!a.ContainsKey(key) || !Subset(value, a[key], path + "." + key, out where))
                    {
                        where = a.ContainsKey(key) ? where : path + "." + key + " (missing)";
                        return false;
                    }
                }

                return true;
            case JsonArray e when actual is JsonArray a && e.Count == a.Count:
                for (var i = 0; i < e.Count; i++)
                {
                    if (!Subset(e[i], a[i], $"{path}[{i}]", out where))
                        return false;
                }

                return true;
            default:
                return JsonNode.DeepEquals(expected, actual);
        }
    }

    [Fact]
    public void ALinearSongWritesNoAdaptiveFields()
    {
        var song = MusicTestUtil.NewSong();
        MusicTestUtil.AddInstrumentTrack(song, "t1");
        var json = Encoding.UTF8.GetString(SongFormat.Write(song));
        Assert.DoesNotContain("adaptive", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layer\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesignExampleValidatesWithoutErrors()
    {
        var song = Parse(Example);
        var problems = SongAdaptiveValidator.Validate(song);
        Assert.True(SongAdaptiveValidator.CanBake(problems), string.Join("\n", problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void EachValidationRule()
    {
        var song = Parse(Example);
        var a = song.Adaptive!;
        a.FindSection("intro")!.End = 15000;                         // not whole bars
        a.FindSection("outro")!.Start = 220000;                       // overlaps victory (and is not on a bar line)
        a.Sections.Add(new SongSection { Id = "lost", Name = "Lost", Start = 300000, End = 303360, Loop = true });
        a.FindSection("victory")!.End = 215040 + 5 * Bar;             // a long stinger
        a.Markers.Add(new SongMarker { Id = "m2", Name = "nowhere", Tick = 999999 });
        a.Markers.Add(new SongMarker { Id = "m3", Name = "plain", Tick = 30720, Tail = true }); // a tail point that is not an exit
        a.Curves.Add(new MusicCurveInfo { Layer = "ghost", Parameter = "intensity", Points = [new(0, 0)] });
        a.Curves.Add(new MusicCurveInfo { Layer = "base", Target = MusicCurveTarget.Cutoff, Parameter = "speed", Points = [] });
        a.Transitions.Add(new MusicTransitionInfo { From = ["explore"], To = "combat", When = a.Transitions[0].When!.Clone() }); // shadowed
        a.Transitions.Add(new MusicTransitionInfo
        {
            From = ["nope"],
            To = "victory",
            Segment = "boss",
            Stinger = "intro",
            When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "area", Value = new MusicValue("Lava") }] },
        });
        song.Tracks[0].Layer = "ghost";
        song.Master.Inserts.Add(new PluginInsert { Plugin = new PluginDescriptor { Name = "Glue" } });
        var drums = song.Tracks[1];
        drums.Clips.Add(new MidiClip { Start = 15360, Length = 2 * 61440, Notes = [new MidiNote { Start = 61000, Length = 2000 }] }); // crosses explore → combat

        var problems = SongAdaptiveValidator.Validate(song);
        Assert.False(SongAdaptiveValidator.CanBake(problems));
        string[] errors = ["does not start and end on bar lines", "overlap"];
        string[] warnings =
        [
            "longer than 4 bars", "outside every section", "layer 'ghost'", "no filter", "parameter 'speed'", "no points",
            "never fires: transition 1", "comes from 'nope'", "not a playable section", "not a transition section", "not a stinger",
            "not one of its labels", "never reached", "Master inserts", "crosses the end of 'Explore'", "Track 'Pads' names layer 'ghost'",
            "Tail point 'plain' is not an exit marker",
        ];
        foreach (var text in errors)
            Assert.Contains(problems, p => p.Severity == AdaptiveProblemSeverity.Error && p.Message.Contains(text, StringComparison.Ordinal));
        foreach (var text in warnings)
            Assert.Contains(problems, p => p.Severity == AdaptiveProblemSeverity.Warning && p.Message.Contains(text, StringComparison.Ordinal));

        a.Start = "victory";
        Assert.Contains(SongAdaptiveValidator.Validate(song), p => p.Severity == AdaptiveProblemSeverity.Error && p.Message.Contains("start section", StringComparison.Ordinal));
        for (var i = 0; i < 6; i++)
            a.Layers.Add(new SongLayer { Id = "x" + i });
        Assert.Contains(SongAdaptiveValidator.Validate(song), p => p.Message.Contains("the most is 8", StringComparison.Ordinal));
    }

    [Fact]
    public void TheManifestHasFramesAndTheMainLayerAndCompilesCleanly()
    {
        var song = Parse(Example);
        MusicTestUtil.AddInstrumentTrack(song, "t4"); // no layer: Main
        var manifest = SongAdaptiveCompiler.ToManifest(song, 48000);
        Assert.Equal(["main", "base", "perc", "combat"], manifest.Layers.Select(l => l.Id));
        var explore = manifest.Sections.Single(s => s.Id == "explore");
        Assert.Equal((16, 16 * 96000L), (explore.Bars, explore.LengthFrames));
        var combat = manifest.Sections.Single(s => s.Id == "combat");
        var drop = Assert.Single(combat.Markers);
        Assert.Equal((8 * 96000L, true), (drop.Frame, drop.Exit)); // tick 107 520 = 8 bars into combat
        Assert.Equal("sng_0123456789ab", manifest.Song);

        var plan = SongAdaptiveCompiler.Compile(song, 48000);
        Assert.Empty(plan.Problems);
        Assert.Equal(plan.FindSection("intro"), plan.StartSection);
        Assert.Equal(3, plan.Rules.Length);
        Assert.Equal(SongLayer.MainId, SongAdaptiveCompiler.LayerOf(song, song.Tracks[3]));
    }

    [Fact]
    public void EveryAdaptiveEditUndoesAndRedoesExactly()
    {
        var song = MusicTestUtil.NewSong();
        var path = Path.Combine(Path.GetTempPath(), "Adaptive.msong");
        using var doc = new SongDocument(song, path);
        var track = doc.AddTrack(SongTrackKind.Instrument, "Drums");
        var states = new List<byte[]> { SongFormat.Write(song) };
        void Step(Action edit)
        {
            edit();
            states.Add(SongFormat.Write(song));
        }

        SongSection? explore = null;
        SongLayer? perc = null;
        MusicParameterInfo? intensity = null;
        MusicCurveInfo? curve = null;
        MusicTransitionInfo? rule = null;
        SongMarker? marker = null;
        Step(() => doc.EnableAdaptive());
        Step(() => explore = doc.AddSection("Explore", MusicSectionKind.Section, 100, 4 * Bar - 300));
        Step(() => doc.AddSection("Combat", MusicSectionKind.Section, 4 * Bar, 8 * Bar));
        Step(() => doc.SetSectionRange(explore!, 0, 6 * Bar));
        Step(() => doc.RenameSection(explore!, "Wander"));
        Step(() => doc.SetSectionLoop(explore!, false));
        Step(() => doc.SetSectionThen(explore!, "s2"));
        Step(() => marker = doc.AddMarker("drop", 2 * Bar, exit: true));
        Step(() => doc.MoveMarker(marker!, 3 * Bar));
        Step(() => doc.SetMarkerTail(marker!, true));
        Step(() => doc.SetMarkerExit(marker!, false));
        Step(() => perc = doc.AddLayer("Perc", "#f59e0b"));
        Step(() => doc.SetLayerFilter(perc!, MusicLayerFilter.LowPass));
        Step(() => doc.SetTrackLayer(track, perc!.Id));
        Step(() => intensity = doc.AddParameter(new MusicParameterInfo { Name = "intensity", Max = 1 }));
        Step(() => doc.EditParameter(intensity!, p => p.SeekSpeed = 0.25f));
        Step(() => doc.SetParameterDefault(intensity!, 0.3f));
        Step(() => curve = doc.AddCurve(new MusicCurveInfo { Layer = perc!.Id, Parameter = "intensity", Points = [new(1, 0), new(0, -80)] }));
        Step(() => doc.SetCurvePoints(curve!, [new(0, -80), new(0.5f, -6), new(1, 0)]));
        Step(() => doc.SetCurveShape(curve!, MusicCurveShape.Smooth));
        Step(() => rule = doc.AddTransition(new MusicTransitionInfo { From = ["s1"], To = "s2", When = new MusicConditionGroup() }));
        Step(() => doc.EditTransition(rule!, t => (t.Quantize, t.Every) = (MusicQuantize.Bars, 0)));
        Step(() => doc.AddTransition(new MusicTransitionInfo { From = ["*"], To = "s1" }, index: 0));
        Step(() => doc.MoveTransition(rule!, 0));
        Step(() => doc.RemoveLayer(perc!));
        Step(() => doc.RemoveSection(explore!));
        Step(() => doc.DisableAdaptive());

        Assert.Equal(states.Count - 1 + 1, doc.History.Actions.Count); // + the track
        for (var i = states.Count - 1; i > 0; i--)
        {
            Assert.True(doc.History.Undo());
            Assert.Equal(Encoding.UTF8.GetString(states[i - 1]), Encoding.UTF8.GetString(SongFormat.Write(song)));
        }

        for (var i = 1; i < states.Count; i++)
        {
            Assert.True(doc.History.Redo());
            Assert.Equal(Encoding.UTF8.GetString(states[i]), Encoding.UTF8.GetString(SongFormat.Write(song)));
        }
    }

    [Fact]
    public void SectionsSnapToBarsAndTheFirstBecomesTheStart()
    {
        using var doc = new SongDocument(MusicTestUtil.NewSong(), Path.Combine(Path.GetTempPath(), "Snap.msong"));
        doc.EnableAdaptive();
        var stinger = doc.AddSection("Hit", MusicSectionKind.Stinger, 0, 10);
        Assert.Null(doc.Adaptive.Start); // a stinger cannot start
        var s = doc.AddSection("", MusicSectionKind.Section, 2000, 2100);
        Assert.Equal((Bar, 2 * Bar), (s.Start, s.End)); // nearest bar, at least one bar
        Assert.Equal(s.Id, doc.Adaptive.Start);
        Assert.Equal((0L, Bar), (stinger.Start, stinger.End));
        Assert.True(s.Loop);
        Assert.False(stinger.Loop);
        Assert.NotEqual(stinger.Id, s.Id);

        doc.History.Undo();
        Assert.Null(doc.Adaptive.Start);
        doc.RemoveSection(stinger);
        Assert.Empty(doc.Adaptive.Sections);
        Assert.Throws<ArgumentException>(() => doc.RemoveSection(stinger));
        Assert.Throws<ArgumentException>(() => doc.SetTrackLayer(doc.AddTrack(SongTrackKind.Instrument), "ghost"));
        Assert.NotEqual(SongLayer.MainId, doc.AddLayer("").Id);
    }

    [Fact]
    public void DragsMergeIntoOneEntry()
    {
        using var doc = new SongDocument(MusicTestUtil.NewSong(), Path.Combine(Path.GetTempPath(), "Drag.msong"));
        doc.EnableAdaptive();
        var s = doc.AddSection("A", MusicSectionKind.Section, 0, Bar);
        var layer = doc.AddLayer("L");
        var curve = doc.AddCurve(new MusicCurveInfo { Layer = layer.Id, Parameter = "p", Points = [new(0, 0)] });
        var count = doc.History.Actions.Count;
        for (var i = 1; i <= 4; i++)
            doc.SetSectionRange(s, 0, (i + 1) * Bar, mergeKey: "edge");
        doc.History.EndMerge();
        for (var i = 1; i <= 4; i++)
            doc.SetCurvePoints(curve, [new(0, -i)], mergeKey: "point");
        doc.History.EndMerge();
        Assert.Equal(count + 2, doc.History.Actions.Count);
        Assert.Equal(5 * Bar, s.End);
        doc.History.Undo();
        Assert.Equal(new MusicCurvePoint(0, 0), Assert.Single(curve.Points));
        doc.History.Undo();
        Assert.Equal(Bar, s.End);
    }

    [Fact]
    public void AdaptiveEditsAreRefusedWithoutTheBlockAndOnReadOnlySongs()
    {
        using var doc = new SongDocument(MusicTestUtil.NewSong(), Path.Combine(Path.GetTempPath(), "None.msong"));
        Assert.Throws<InvalidOperationException>(() => doc.AddSection("A", MusicSectionKind.Section, 0, Bar));
        using var readOnly = new SongDocument(MusicTestUtil.NewSong(), Path.Combine(Path.GetTempPath(), "Ro.msong"), readOnly: true);
        Assert.Throws<InvalidOperationException>(() => readOnly.EnableAdaptive());
    }
}
