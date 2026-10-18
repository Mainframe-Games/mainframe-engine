using BenchmarkDotNet.Attributes;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// The music editor's wavetable synth (docs/design/future/wavetable-synth.md, CPU budget): one 256-frame block at 48 kHz
/// of a patch with its notes held. A block is 5.33 ms of real time; the design's targets are shares of that.
/// </summary>
[MemoryDiagnoser]
public class SynthBenchmarks
{
    private const int Rate = 48000;
    private readonly float[] _block = new float[SongEngine.BlockFrames * 2];
    private static readonly string[] RackEffects = ["chorus", "delay", "reverb", "compressor"];
    private TrackSnapshot _track = null!;
    private WavetableSynth _synth = null!;
    private long _frame;

    [Params("init", "lead", "supersaw-pad", "worst-case")]
    public string Patch { get; set; } = "init";

    [GlobalSetup]
    public void Setup()
    {
        var (values, voices) = Patch switch
        {
            "lead" => (new Dictionary<string, float> { ["b.on"] = 1, ["a.unison"] = 4, ["b.unison"] = 4, ["f2.on"] = 1 }, 1),
            "supersaw-pad" => (new Dictionary<string, float> { ["a.unison"] = 7, ["b.on"] = 1, ["b.unison"] = 7 }, 8),
            "worst-case" => (new Dictionary<string, float>
            {
                ["a.unison"] = 16,
                ["b.on"] = 1,
                ["b.unison"] = 16,
                ["f2.on"] = 1,
                ["f2.type"] = (float)SynthFilterType.Ladder,
                ["voice.polyphony"] = 16,
                ["sub.on"] = 1,
                ["noise.on"] = 1,
            }, 16),
            _ => (new Dictionary<string, float>(), 1),
        };
        var patch = SynthPatch.Init();
        foreach (var (k, v) in values)
            patch.Params![k] = v;
        if (Patch is "lead" or "worst-case")
        {
            patch.Matrix =
            [
                new SynthModRoute { Source = "env2", Target = "f1.cutoff", Amount = 0.4f },
                new SynthModRoute { Source = "lfo1", Target = "a.wt_pos", Amount = 0.3f },
                new SynthModRoute { Source = "lfo2", Target = "b.fine", Amount = 0.1f },
                new SynthModRoute { Source = "velocity", Target = "env1.level", Amount = 0.3f },
            ];
        }

        if (Patch == "worst-case")
            patch.Fx = [.. RackEffects.Select(id => PluginInsert.ForBuiltin(BuiltinEffects.Find(id)!))];
        var song = new Song { Uid = "sng_000000000001" };
        song.Tracks.Add(new SongTrack { Id = "t", Name = "t", Kind = SongTrackKind.Instrument });
        _track = new SongSnapshotBuilder(Rate).Build(song).Find("t")!;
        _synth = new WavetableSynth(Rate);
        _synth.Apply(InstrumentDescriptor.Wavetable(patch));
        WavetableCache.WaitForPending();
        _synth.Apply(InstrumentDescriptor.Wavetable(patch));
        var notes = Enumerable.Range(0, voices).Select(i => new NoteEvent(i, 48 + i * 3, 100, true)).ToArray();
        _synth.Process(_track, notes, _block, new EffectContext(Rate, 120, true, 0));
    }

    [Benchmark]
    public void Block()
    {
        Array.Clear(_block);
        _frame += SongEngine.BlockFrames;
        _synth.Process(_track, default, _block, new EffectContext(Rate, 120, true, _frame));
    }
}
