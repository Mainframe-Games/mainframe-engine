namespace MainframeEngine.Editor.Music;

/// <summary>
/// A feed-forward stereo-linked compressor: the louder channel's level in dB goes through a soft-knee gain computer and
/// the gain reduction is smoothed in the log domain with separate attack and release (Giannoulis, Massberg and Reiss,
/// "Digital Dynamic Range Compressor Design", 2012). Make-up gain (manual or automatic) and a dry/wet mix for parallel
/// compression.
/// </summary>
public sealed class CompressorEffect : BuiltinEffect
{
    private const int Threshold = 0, Ratio = 1, Attack = 2, Release = 3, Knee = 4, Makeup = 5, AutoMakeup = 6, Mix = 7;

    public static readonly BuiltinEffectInfo Definition = new("compressor", "Compressor", BuiltinCategory.Dynamics, "arrows-minimize",
        "Compressor — evens out levels: threshold, ratio, attack, release, knee and make-up",
        [
            new("threshold", "Threshold", "dB", -60, 0, -18),
            new("ratio", "Ratio", ":1", 1, 20, 4, ParamCurve.Log),
            new("attack", "Attack", "ms", 0.1f, 200, 10, ParamCurve.Log),
            new("release", "Release", "ms", 5, 2000, 120, ParamCurve.Log),
            new("knee", "Knee", "dB", 0, 24, 6),
            new("makeup", "Make-up", "dB", 0, 24, 0),
            BuiltinParam.Toggle("autoMakeup", "Auto Gain", false),
            new("mix", "Mix", "%", 0, 100, 100),
        ],
        sr => new CompressorEffect(sr), BuiltinGraph.GainReduction,
        [
            new("Default", new Dictionary<string, float>()),
            new("Gentle Glue", new Dictionary<string, float> { ["threshold"] = -12, ["ratio"] = 2, ["attack"] = 30, ["release"] = 200, ["knee"] = 10 }),
            new("Vocal Leveller", new Dictionary<string, float> { ["threshold"] = -20, ["ratio"] = 4, ["attack"] = 5, ["release"] = 80 }),
            new("Punchy Drums", new Dictionary<string, float> { ["threshold"] = -16, ["ratio"] = 6, ["attack"] = 25, ["release"] = 60, ["knee"] = 3 }),
            new("Parallel Smash", new Dictionary<string, float> { ["threshold"] = -35, ["ratio"] = 10, ["attack"] = 1, ["release"] = 100, ["autoMakeup"] = 1, ["mix"] = 40 }),
        ]);

    private float _threshold, _ratio, _knee, _attackCoef, _releaseCoef;
    private float _reductionDb; // smoothed gain reduction (≥ 0)
    private LinearRamp _makeup;
    private LinearRamp _mix;

    private CompressorEffect(int sampleRate) : base(Definition, sampleRate)
    {
    }

    /// <summary>The static curve: the output level in dB for a steady input level in dB (the window's transfer graph).</summary>
    public static float OutputDb(float inputDb, float threshold, float ratio, float knee)
    {
        var over = inputDb - threshold;
        if (knee > 0 && 2 * MathF.Abs(over) <= knee)
        {
            var x = over + knee / 2;
            return inputDb + (1 / ratio - 1) * x * x / (2 * knee);
        }

        return over > 0 ? threshold + over / ratio : inputDb;
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _threshold = values[Threshold];
        _ratio = Math.Max(1, values[Ratio]);
        _knee = values[Knee];
        _attackCoef = Dsp.TimeCoefficient(values[Attack] / 1000f, SampleRate);
        _releaseCoef = Dsp.TimeCoefficient(values[Release] / 1000f, SampleRate);
        var makeup = values[Makeup];
        if (On(values[AutoMakeup]))
            makeup += -OutputDb(0, _threshold, _ratio, _knee) * 0.5f; // half the reduction at 0 dBFS
        _makeup.SetTarget(Dsp.DbToGain(makeup));
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var makeupStep = _makeup.Step(frames);
        var mixStep = _mix.Step(frames);
        var reduction = _reductionDb;
        var most = 0f;
        for (var f = 0; f < frames; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var level = Dsp.GainToDb(MathF.Max(MathF.Abs(l), MathF.Abs(r)));
            var target = level - OutputDb(level, _threshold, _ratio, _knee); // ≥ 0
            var coef = target > reduction ? _attackCoef : _releaseCoef;
            reduction = target + (reduction - target) * coef;
            most = MathF.Max(most, reduction);
            var gain = Dsp.DbToGain(-reduction) * (_makeup.Current + makeupStep * (f + 1));
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (l * gain - l) * mix;
            stereo[2 * f + 1] = r + (r * gain - r) * mix;
        }

        Dsp.Flush(ref reduction);
        _reductionDb = reduction;
        _makeup.End();
        _mix.End();
        ReportGainReduction(most);
    }

    protected override void ResetState() => _reductionDb = 0;
}
