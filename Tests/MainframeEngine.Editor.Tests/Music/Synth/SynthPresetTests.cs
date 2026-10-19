using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The synth's factory presets and preset resources.</summary>
[Collection(nameof(SerialEditor))]
public sealed class SynthPresetTests
{
    public static TheoryData<string, string> Factory()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in FactoryPresets.All)
            data.Add(p.Category, p.Name);
        return data;
    }

    [Fact]
    public void FactoryPresetsAreValidAndUnique()
    {
        Assert.True(FactoryPresets.All.Length >= 36);
        Assert.Equal(FactoryPresets.All.Length, FactoryPresets.All.Select(p => p.Category + "/" + p.Name).Distinct().Count());
        foreach (var preset in FactoryPresets.All)
        {
            Assert.Contains(preset.Category, FactoryPresets.Categories);
            var patch = preset.Make();
            foreach (var id in patch.Params!.Keys)
                Assert.True(SynthParams.Find(id) is not null, $"{preset.Name}: unknown parameter {id}");
            foreach (var table in patch.Tables!.Values)
                Assert.Contains(table.Source, FactoryWavetables.Sources);
            foreach (var route in patch.Matrix ?? [])
            {
                Assert.True(SynthParams.SourceIndex(route.Source) >= 0, $"{preset.Name}: source {route.Source}");
                Assert.True(SynthParams.Find(route.Target) is { } t && SynthParams.IsModTarget(t), $"{preset.Name}: target {route.Target}");
            }

            foreach (var fx in patch.Fx ?? [])
            {
                var info = BuiltinEffects.Find(fx.Builtin!);
                Assert.NotNull(info);
                foreach (var id in fx.Params!.Keys)
                    Assert.NotNull(info.Find(id));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Factory))]
    public void EveryFactoryPresetSounds(string category, string name)
    {
        var preset = FactoryPresets.All.Single(p => p.Category == category && p.Name == name);
        var synth = new WavetableSynth(SynthTestUtil.Rate);
        var patch = preset.Make();
        synth.Apply(InstrumentDescriptor.Wavetable(patch));
        WavetableCache.WaitForPending();
        synth.Apply(InstrumentDescriptor.Wavetable(patch));
        var output = SynthTestUtil.Render(synth, 1.5, notes: [(0, 57, 110), (SynthTestUtil.Rate, 57, 0)]);
        Assert.All(output, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) < 4, $"{name}: {v}"));
        Assert.True(SynthTestUtil.Rms(output, 0, SynthTestUtil.Rate) > 0.003, $"{name} is silent");
    }

    [Fact]
    public void APresetResourceRoundTripsAndIsListedByType()
    {
        using var project = new TempProject();
        var root = project.Root;
        var patch = FactoryPresets.All[0].Make();
        patch.Matrix = [new SynthModRoute { Source = "lfo2", Target = "a.wt_pos", Amount = 0.25f }];
        var path = Path.Combine(root, "Assets", SynthPresets.Folder, "Big Bass.mres");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var uid = SynthPresets.Save(patch, path, "Big Bass", "Bass");
        var entry = Assert.Single(SynthPresets.Project(root));
        Assert.Equal(("Big Bass", "Bass", uid), (entry.Name, entry.Category, entry.Id));
        Assert.Equal(SynthPresets.ToJson(patch), SynthPresets.ToJson(SynthPresets.Patch(entry)!));
        Assert.Contains(SynthPresets.All(root), p => p.Id == uid);
    }

    [Fact]
    public void LoadingAPresetIsOneUndoStepThatRestoresTheOldPatch()
    {
        var song = MusicTestUtil.NewSong();
        var track = MusicTestUtil.AddInstrumentTrack(song, "pad", InstrumentDescriptor.Wavetable());
        using var document = new SongDocument(song, "/tmp/x.msong");
        var old = track.Instrument!.Patch;
        var preset = FactoryPresets.All.Single(p => p.Name == "Warm Pad");
        document.SetSynthPatch(track, preset.Make(), "Preset: Warm Pad", "factory:Pad/Warm Pad");
        Assert.Equal("factory:Pad/Warm Pad", track.Instrument.Preset);
        Assert.Equal(5, track.Instrument.Patch!.Params!["a.unison"]);
        document.History.Undo();
        Assert.Same(old, track.Instrument.Patch);
        Assert.Null(track.Instrument.Preset);
    }
}
