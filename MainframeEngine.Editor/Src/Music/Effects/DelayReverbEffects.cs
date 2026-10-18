using System.Globalization;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// A tempo-synced stereo delay: note values (straight, dotted, triplet) or milliseconds, feedback through a low cut and
/// a high cut in the loop, ping-pong, wet width and mix. Time changes glide (a tape-like pitch bend, never a click).
/// </summary>
public sealed class DelayEffect : BuiltinEffect
{
    private const int Sync = 0, Division = 1, Feel = 2, Time = 3, Feedback = 4, PingPong = 5, LowCut = 6, HighCut = 7, Width = 8, Mix = 9;

    /// <summary>The longest delay (buffers are sized for it).</summary>
    public const float MaxSeconds = 4f;

    public static readonly BuiltinEffectInfo Definition = new("delay", "Beat Delay", BuiltinCategory.DelayReverb, "repeat",
        "Beat Delay — echoes locked to the tempo (or in ms), ping-pong, filtered feedback",
        [
            BuiltinParam.Toggle("sync", "Sync", true, "Time"),
            BuiltinParam.Choice("division", "Note", NoteDivision.Names, 3, "Time"),
            BuiltinParam.Choice("feel", "Feel", NoteDivision.Feels, 1, "Time"),
            new("time", "Time", "ms", 1, MaxSeconds * 1000, 375, ParamCurve.Log, Group: "Time"),
            new("feedback", "Feedback", "%", 0, 95, 35, Group: "Feedback"),
            BuiltinParam.Toggle("pingPong", "Ping-Pong", false, "Feedback"),
            new("lowCut", "Low Cut", "Hz", 20, 2000, 100, ParamCurve.Log, Group: "Feedback"),
            new("highCut", "High Cut", "Hz", 1000, 20000, 8000, ParamCurve.Log, Group: "Feedback"),
            new("width", "Width", "%", 0, 100, 100, Group: "Output"),
            new("mix", "Mix", "%", 0, 100, 30, Group: "Output"),
        ],
        sr => new DelayEffect(sr), BuiltinGraph.DelayReadout,
        [
            new("Dotted Eighth", new Dictionary<string, float>()),
            new("Quarter Ping-Pong", new Dictionary<string, float> { ["division"] = 2, ["feel"] = 0, ["pingPong"] = 1, ["feedback"] = 45 }),
            new("Slapback", new Dictionary<string, float> { ["sync"] = 0, ["time"] = 110, ["feedback"] = 5, ["mix"] = 25 }),
            new("Dub Wash", new Dictionary<string, float> { ["division"] = 2, ["feel"] = 1, ["feedback"] = 75, ["highCut"] = 2500, ["lowCut"] = 300, ["mix"] = 40 }),
        ]);

    private readonly DelayLine _left;
    private readonly DelayLine _right;
    private StereoBiquad _lowCut = new(), _highCut = new();
    private float _delayFrames = -1;
    private float _targetFrames;
    private float _glide;
    private float _feedback;
    private bool _pingPong;
    private bool _sync;
    private int _division, _feel;
    private float _timeMs;
    private LinearRamp _width, _mix;

    private DelayEffect(int sampleRate) : base(Definition, sampleRate)
    {
        _left = new DelayLine((int)(MaxSeconds * sampleRate) + 2);
        _right = new DelayLine((int)(MaxSeconds * sampleRate) + 2);
        _glide = Dsp.TimeCoefficient(0.05f, sampleRate);
    }

    /// <summary>The delay in milliseconds at <paramref name="tempo"/> for an insert's parameters (unclamped).</summary>
    public static double TimeMs(IReadOnlyDictionary<string, float>? values, double tempo) =>
        Definition.Value(values, "sync") >= 0.5f
            ? NoteDivision.ToBeats((int)Definition.Value(values, "division"), (int)Definition.Value(values, "feel")) * 60000.0 / tempo
            : Definition.Value(values, "time");

    /// <summary>The plugin window's readout: "1/8 dotted · 375.0 ms at 120 BPM" (or "clamped to 4 s").</summary>
    public static string Describe(IReadOnlyDictionary<string, float>? values, double tempo)
    {
        var c = CultureInfo.InvariantCulture;
        var ms = TimeMs(values, tempo);
        var clamped = ms > MaxSeconds * 1000 ? $" — clamped to {MaxSeconds.ToString("0", c)} s" : "";
        if (Definition.Value(values, "sync") < 0.5f)
            return ms.ToString("0.0", c) + " ms" + clamped;
        var note = NoteDivision.Label((int)Definition.Value(values, "division"), (int)Definition.Value(values, "feel"));
        return $"{note} · {ms.ToString("0.0", c)} ms at {tempo.ToString("0.##", c)} BPM{clamped}";
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _sync = On(values[Sync]);
        _division = (int)values[Division];
        _feel = (int)values[Feel];
        _timeMs = values[Time];
        _feedback = values[Feedback] / 100f;
        _pingPong = On(values[PingPong]);
        _lowCut.Coefficients = BiquadCoefficients.Design(BiquadShape.HighPass, values[LowCut], 0.7071, 0, SampleRate);
        _highCut.Coefficients = BiquadCoefficients.Design(BiquadShape.LowPass, values[HighCut], 0.7071, 0, SampleRate);
        _width.SetTarget(values[Width] / 100f);
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var ms = _sync ? NoteDivision.ToBeats(_division, _feel) * 60000.0 / context.Tempo : _timeMs;
        _targetFrames = (float)Math.Clamp(ms / 1000.0 * SampleRate, 1, _left.MaxDelay);
        if (_delayFrames < 0)
            _delayFrames = _targetFrames;
        var frames = stereo.Length / 2;
        var widthStep = _width.Step(frames);
        var mixStep = _mix.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            _delayFrames = _targetFrames + (_delayFrames - _targetFrames) * _glide;
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var wl = _left.Read(_delayFrames);
            var wr = _right.Read(_delayFrames);
            _lowCut.Process(ref wl, ref wr);
            _highCut.Process(ref wl, ref wr);
            if (_pingPong)
            {
                _left.Write((l + r) * 0.5f + wr * _feedback);
                _right.Write(wl * _feedback);
            }
            else
            {
                _left.Write(l + wl * _feedback);
                _right.Write(r + wr * _feedback);
            }

            var width = _width.Current + widthStep * (f + 1);
            var mid = (wl + wr) * 0.5f;
            var side = (wl - wr) * 0.5f * width;
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (mid + side - l) * mix;
            stereo[2 * f + 1] = r + (mid - side - r) * mix;
        }

        _lowCut.FlushState();
        _highCut.FlushState();
        _width.End();
        _mix.End();
    }

    protected override void ResetState()
    {
        _left.Clear();
        _right.Clear();
        _lowCut.Reset();
        _highCut.Reset();
    }
}

/// <summary>An algorithmic reverb (Freeverb) with pre-delay, room size, damping, width, a low cut and a high cut on the tail.</summary>
public sealed class ReverbEffect : BuiltinEffect
{
    private const int PreDelay = 0, Size = 1, Damping = 2, Width = 3, LowCut = 4, HighCut = 5, Mix = 6;
    private const float MaxPreDelayMs = 250;

    public static readonly BuiltinEffectInfo Definition = new("reverb", "Reverb", BuiltinCategory.DelayReverb, "sparkles",
        "Reverb — a room or hall around the sound: pre-delay, size, damping and tone of the tail",
        [
            new("preDelay", "Pre-Delay", "ms", 0, MaxPreDelayMs, 20),
            new("size", "Size", "%", 0, 100, 60),
            new("damping", "Damping", "%", 0, 100, 50),
            new("width", "Width", "%", 0, 100, 100),
            new("lowCut", "Low Cut", "Hz", 20, 1000, 120, ParamCurve.Log),
            new("highCut", "High Cut", "Hz", 1000, 20000, 12000, ParamCurve.Log),
            new("mix", "Mix", "%", 0, 100, 25),
        ],
        sr => new ReverbEffect(sr), BuiltinGraph.None,
        [
            new("Room", new Dictionary<string, float>()),
            new("Small Room", new Dictionary<string, float> { ["preDelay"] = 5, ["size"] = 30, ["damping"] = 60, ["mix"] = 20 }),
            new("Large Hall", new Dictionary<string, float> { ["preDelay"] = 40, ["size"] = 90, ["damping"] = 35, ["mix"] = 30 }),
            new("Dark Plate", new Dictionary<string, float> { ["preDelay"] = 10, ["size"] = 70, ["damping"] = 80, ["highCut"] = 5000, ["mix"] = 25 }),
        ]);

    private readonly Freeverb _reverb;
    private readonly DelayLine _preL;
    private readonly DelayLine _preR;
    private StereoBiquad _lowCut = new(), _highCut = new();
    private float _preFrames;
    private LinearRamp _mix;

    private ReverbEffect(int sampleRate) : base(Definition, sampleRate)
    {
        _reverb = new Freeverb(sampleRate);
        _preL = new DelayLine((int)(MaxPreDelayMs / 1000 * sampleRate) + 2);
        _preR = new DelayLine((int)(MaxPreDelayMs / 1000 * sampleRate) + 2);
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _preFrames = values[PreDelay] / 1000f * SampleRate;
        _reverb.Set(values[Size] / 100f, values[Damping] / 100f, values[Width] / 100f);
        _lowCut.Coefficients = BiquadCoefficients.Design(BiquadShape.HighPass, values[LowCut], 0.7071, 0, SampleRate);
        _highCut.Coefficients = BiquadCoefficients.Design(BiquadShape.LowPass, values[HighCut], 0.7071, 0, SampleRate);
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var mixStep = _mix.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            _preL.Write(l);
            _preR.Write(r);
            float inL = l, inR = r;
            if (_preFrames >= 1)
            {
                inL = _preL.Read(_preFrames);
                inR = _preR.Read(_preFrames);
            }

            _reverb.Process(inL, inR, out var wl, out var wr);
            _lowCut.Process(ref wl, ref wr);
            _highCut.Process(ref wl, ref wr);
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (wl - l) * mix;
            stereo[2 * f + 1] = r + (wr - r) * mix;
        }

        _lowCut.FlushState();
        _highCut.FlushState();
        _mix.End();
    }

    protected override void ResetState()
    {
        _reverb.Clear();
        _preL.Clear();
        _preR.Clear();
        _lowCut.Reset();
        _highCut.Reset();
    }
}
