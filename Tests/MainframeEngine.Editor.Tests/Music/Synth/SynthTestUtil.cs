using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>Renders the wavetable synth directly (no song engine) for the synth tests.</summary>
internal static class SynthTestUtil
{
    public const int Rate = MusicTestUtil.Rate;
    private const int Block = SongEngine.BlockFrames;

    private static readonly TrackSnapshot Track = new("t", 0, 1);

    public static WavetableSynth Create(Dictionary<string, float>? values = null, Action<SynthPatch>? edit = null, int rate = Rate)
    {
        var patch = SynthPatch.Init();
        foreach (var (id, v) in values ?? [])
            patch.Params![id] = v;
        edit?.Invoke(patch);
        var synth = new WavetableSynth(rate);
        WavetableCache.WaitForPending();
        synth.Apply(InstrumentDescriptor.Wavetable(patch));
        WavetableCache.WaitForPending();
        synth.Apply(InstrumentDescriptor.Wavetable(patch));
        return synth;
    }

    /// <summary>Renders <paramref name="seconds"/>; <paramref name="notes"/> are (frame, pitch, velocity: 0 = off).</summary>
    public static float[] Render(WavetableSynth synth, double seconds, int rate = Rate, double tempo = 120, long startFrame = 0,
        params (long Frame, int Pitch, int Velocity)[] notes)
    {
        var frames = (int)(seconds * rate);
        var output = new float[frames * 2];
        var events = new List<NoteEvent>();
        for (var start = 0; start < frames; start += Block)
        {
            var n = Math.Min(Block, frames - start);
            events.Clear();
            foreach (var (frame, pitch, velocity) in notes)
            {
                if (frame >= start && frame < start + n)
                    events.Add(new NoteEvent((int)(frame - start), pitch, velocity, velocity > 0));
            }

            synth.Process(Track, events.ToArray(), output.AsSpan(start * 2, n * 2), new EffectContext(rate, tempo, true, startFrame + start));
        }

        return output;
    }

    public static float[] Note(WavetableSynth synth, int pitch, double seconds, double hold = double.MaxValue, int rate = Rate) =>
        Render(synth, seconds, rate, notes: hold >= seconds ? [(0, pitch, 100)] : [(0, pitch, 100), ((long)(hold * rate), pitch, 0)]);

    public static double Rms(ReadOnlySpan<float> stereo, int fromFrame, int toFrame)
    {
        double sum = 0;
        for (var f = fromFrame; f < toFrame; f++)
            sum += stereo[2 * f] * (double)stereo[2 * f];
        return Math.Sqrt(sum / Math.Max(1, toFrame - fromFrame));
    }

    public static double Db(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}

/// <summary>A temporary project folder as <see cref="AssetDatabase.Current"/> (restored on dispose; tests in <c>SerialEditor</c>).</summary>
internal sealed class TempProject : IDisposable
{
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public TempProject()
    {
        Root = Directory.CreateTempSubdirectory("mf-synth-").FullName;
        Directory.CreateDirectory(Path.Combine(Root, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(Root, recursive: true);
    }
}
