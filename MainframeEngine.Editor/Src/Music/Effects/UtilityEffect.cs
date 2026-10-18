namespace MainframeEngine.Editor.Music;

/// <summary>Gain, balance, stereo width (mid/side), per-channel phase invert and mono — the mixing toolbox.</summary>
public sealed class UtilityEffect : BuiltinEffect
{
    private const int Gain = 0, Pan = 1, Width = 2, InvertLeft = 3, InvertRight = 4, Mono = 5;

    public static readonly BuiltinEffectInfo Definition = new("utility", "Utility", BuiltinCategory.Utility, "adjustments",
        "Utility — gain, balance, stereo width, phase invert and mono",
        [
            new("gain", "Gain", "dB", -48, 24, 0),
            new("pan", "Balance", "pan", -100, 100, 0),
            new("width", "Width", "%", 0, 200, 100),
            BuiltinParam.Toggle("invertLeft", "Invert L", false, "Phase"),
            BuiltinParam.Toggle("invertRight", "Invert R", false, "Phase"),
            BuiltinParam.Toggle("mono", "Mono", false, "Phase"),
        ],
        sr => new UtilityEffect(sr));

    private LinearRamp _left;
    private LinearRamp _right;
    private LinearRamp _width;

    private UtilityEffect(int sampleRate) : base(Definition, sampleRate)
    {
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        var gain = Dsp.DbToGain(values[Gain]);
        AudioMath.PanGains(values[Pan] / 100f, out var l, out var r);
        _left.SetTarget(gain * l * (On(values[InvertLeft]) ? -1 : 1));
        _right.SetTarget(gain * r * (On(values[InvertRight]) ? -1 : 1));
        _width.SetTarget(On(values[Mono]) ? 0 : values[Width] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var ls = _left.Step(frames);
        var rs = _right.Step(frames);
        var ws = _width.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var w = _width.Current + ws * (f + 1);
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var mid = (l + r) * 0.5f;
            var side = (l - r) * 0.5f * w;
            stereo[2 * f] = (mid + side) * (_left.Current + ls * (f + 1));
            stereo[2 * f + 1] = (mid - side) * (_right.Current + rs * (f + 1));
        }

        _left.End();
        _right.End();
        _width.End();
    }

    protected override void ResetState()
    {
    }
}
