using MainframeEngine.Audio.Dsp;

namespace MainframeEngine.Editor.Music;

/// <summary>Shared parameters and LFO setup of the modulation effects (sync to a note value or free in Hz).</summary>
internal static class ModulationParams
{
    public static BuiltinParam[] Rate(float hz, int division, float maxHz = 10) =>
    [
        BuiltinParam.Toggle("sync", "Sync", false, "Rate"),
        new("rate", "Rate", "Hz", 0.02f, maxHz, hz, ParamCurve.Log, Group: "Rate"),
        BuiltinParam.Choice("division", "Note", NoteDivision.Names, division, "Rate"),
    ];

    /// <summary>The LFO's cycle in beats when synced (indices of sync and division), else 0.</summary>
    public static double CycleBeats(ReadOnlySpan<float> values, int sync, int division) =>
        values[sync] >= 0.5f ? NoteDivision.ToBeats((int)values[division], 0) : 0;
}

/// <summary>A stereo chorus: one modulated delay tap per channel, the right LFO offset by the width (up to half a cycle).</summary>
public sealed class ChorusEffect : BuiltinEffect
{
    private const int Sync = 0, Rate = 1, Division = 2, Depth = 3, Delay = 4, Feedback = 5, Width = 6, Mix = 7;
    private const float MaxModMs = 6, MaxDelayMs = 40;

    public static readonly BuiltinEffectInfo Definition = new("chorus", "Chorus", BuiltinCategory.Modulation, "wave-sine",
        "Chorus — thickens and widens with slowly detuned copies",
        [
            .. ModulationParams.Rate(0.8f, 1),
            new("depth", "Depth", "%", 0, 100, 50),
            new("delay", "Delay", "ms", 2, MaxDelayMs, 12),
            new("feedback", "Feedback", "%", 0, 80, 0),
            new("width", "Width", "%", 0, 100, 100),
            new("mix", "Mix", "%", 0, 100, 50),
        ],
        sr => new ChorusEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Subtle Width", new Dictionary<string, float> { ["rate"] = 0.4f, ["depth"] = 25, ["mix"] = 35 }),
            new("Lush", new Dictionary<string, float> { ["rate"] = 1.2f, ["depth"] = 80, ["delay"] = 18, ["feedback"] = 20, ["mix"] = 50 }),
        ]);

    private readonly DelayLine _left;
    private readonly DelayLine _right;
    private Lfo _lfo;
    private double _cycleBeats, _hz;
    private float _baseFrames, _modFrames, _feedback, _offset;
    private LinearRamp _mix;

    private ChorusEffect(int sampleRate) : base(Definition, sampleRate)
    {
        var max = (int)((MaxDelayMs + MaxModMs) / 1000 * sampleRate) + 4;
        _left = new DelayLine(max);
        _right = new DelayLine(max);
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _cycleBeats = ModulationParams.CycleBeats(values, Sync, Division);
        _hz = values[Rate];
        _modFrames = values[Depth] / 100f * MaxModMs / 1000f * SampleRate;
        _baseFrames = values[Delay] / 1000f * SampleRate;
        _feedback = values[Feedback] / 100f;
        _offset = values[Width] / 100f * 0.5f;
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        _lfo.Begin(context, _cycleBeats, _hz);
        var frames = stereo.Length / 2;
        var mixStep = _mix.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var ml = _lfo.At();
            var mr = _lfo.At(_offset);
            _lfo.Advance();
            var wl = _left.Read(_baseFrames + _modFrames * (0.5f + 0.5f * ml));
            var wr = _right.Read(_baseFrames + _modFrames * (0.5f + 0.5f * mr));
            _left.Write(l + wl * _feedback);
            _right.Write(r + wr * _feedback);
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (wl - l) * mix;
            stereo[2 * f + 1] = r + (wr - r) * mix;
        }

        _mix.End();
    }

    protected override void ResetState()
    {
        _left.Clear();
        _right.Clear();
    }
}

/// <summary>A flanger: a short modulated delay with positive or negative feedback (the jet sweep).</summary>
public sealed class FlangerEffect : BuiltinEffect
{
    private const int Sync = 0, Rate = 1, Division = 2, Depth = 3, Delay = 4, Feedback = 5, Mix = 6;
    private const float MaxDelayMs = 10, MaxModMs = 8;

    public static readonly BuiltinEffectInfo Definition = new("flanger", "Flanger", BuiltinCategory.Modulation, "ripple",
        "Flanger — a sweeping comb filter: short modulated delay with feedback",
        [
            .. ModulationParams.Rate(0.25f, 0),
            new("depth", "Depth", "%", 0, 100, 70),
            new("delay", "Delay", "ms", 0.1f, MaxDelayMs, 1, ParamCurve.Log),
            new("feedback", "Feedback", "%", -95, 95, 50),
            new("mix", "Mix", "%", 0, 100, 50),
        ],
        sr => new FlangerEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Jet", new Dictionary<string, float> { ["sync"] = 1, ["division"] = 7, ["feedback"] = 85, ["depth"] = 90 }),
            new("Metallic", new Dictionary<string, float> { ["rate"] = 2, ["feedback"] = -80, ["delay"] = 0.5f }),
        ]);

    private readonly DelayLine _left;
    private readonly DelayLine _right;
    private Lfo _lfo;
    private double _cycleBeats, _hz;
    private float _baseFrames, _modFrames, _feedback;
    private LinearRamp _mix;

    private FlangerEffect(int sampleRate) : base(Definition, sampleRate)
    {
        var max = (int)((MaxDelayMs + MaxModMs) / 1000 * sampleRate) + 4;
        _left = new DelayLine(max);
        _right = new DelayLine(max);
        _lfo.Shape = LfoShape.Triangle;
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _cycleBeats = ModulationParams.CycleBeats(values, Sync, Division);
        _hz = values[Rate];
        _modFrames = values[Depth] / 100f * MaxModMs / 1000f * SampleRate;
        _baseFrames = values[Delay] / 1000f * SampleRate;
        _feedback = values[Feedback] / 100f;
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        _lfo.Begin(context, _cycleBeats, _hz);
        var frames = stereo.Length / 2;
        var mixStep = _mix.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var m = 0.5f + 0.5f * _lfo.Next();
            var delay = 1 + _baseFrames + _modFrames * m;
            var wl = _left.Read(delay);
            var wr = _right.Read(delay);
            _left.Write(l + wl * _feedback);
            _right.Write(r + wr * _feedback);
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (wl - l) * mix;
            stereo[2 * f + 1] = r + (wr - r) * mix;
        }

        _mix.End();
    }

    protected override void ResetState()
    {
        _left.Clear();
        _right.Clear();
    }
}

/// <summary>A phaser: 4–12 all-pass stages swept by an LFO around a centre frequency, with feedback.</summary>
public sealed class PhaserEffect : BuiltinEffect
{
    private const int Sync = 0, Rate = 1, Division = 2, Depth = 3, Stages = 4, Center = 5, Feedback = 6, Mix = 7;
    private const int MaxSections = 6; // second-order sections: 12 first-order stages
    private const int ControlRate = 16;

    private static readonly string[] StageNames = ["4", "6", "8", "12"];
    private static readonly int[] SectionCounts = [2, 3, 4, 6];

    public static readonly BuiltinEffectInfo Definition = new("phaser", "Phaser", BuiltinCategory.Modulation, "wave-square",
        "Phaser — swirling notches from a swept chain of all-pass filters",
        [
            .. ModulationParams.Rate(0.5f, 1),
            new("depth", "Depth", "%", 0, 100, 70),
            BuiltinParam.Choice("stages", "Stages", StageNames, 1),
            new("center", "Center", "Hz", 100, 8000, 800, ParamCurve.Log),
            new("feedback", "Feedback", "%", -95, 95, 30),
            new("mix", "Mix", "%", 0, 100, 50),
        ],
        sr => new PhaserEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Slow Swirl", new Dictionary<string, float> { ["rate"] = 0.15f, ["stages"] = 3, ["feedback"] = 50 }),
            new("Quarter Pulse", new Dictionary<string, float> { ["sync"] = 1, ["division"] = 2, ["depth"] = 90, ["center"] = 1200 }),
        ]);

    private readonly Svf[] _left = new Svf[MaxSections];
    private readonly Svf[] _right = new Svf[MaxSections];
    private Lfo _lfo;
    private double _cycleBeats, _hz;
    private float _octaves, _center, _feedback, _lastL, _lastR;
    private int _sections;
    private LinearRamp _mix;

    private PhaserEffect(int sampleRate) : base(Definition, sampleRate)
    {
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _cycleBeats = ModulationParams.CycleBeats(values, Sync, Division);
        _hz = values[Rate];
        _octaves = values[Depth] / 100f * 2f;
        _sections = SectionCounts[(int)values[Stages]];
        _center = values[Center];
        _feedback = values[Feedback] / 100f;
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        _lfo.Begin(context, _cycleBeats, _hz);
        var frames = stereo.Length / 2;
        var mixStep = _mix.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var m = _lfo.Next();
            if (f % ControlRate == 0)
            {
                var freqL = _center * MathF.Pow(2, _octaves * m);
                var freqR = _center * MathF.Pow(2, -_octaves * m * 0.5f);
                for (var s = 0; s < _sections; s++)
                {
                    _left[s].Set(freqL, 0.5f, SampleRate);
                    _right[s].Set(freqR, 0.5f, SampleRate);
                }
            }

            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var wl = l + _lastL * _feedback;
            var wr = r + _lastR * _feedback;
            for (var s = 0; s < _sections; s++)
            {
                wl = _left[s].AllPass(wl);
                wr = _right[s].AllPass(wr);
            }

            (_lastL, _lastR) = (wl, wr);
            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (wl - l) * mix;
            stereo[2 * f + 1] = r + (wr - r) * mix;
        }

        for (var s = 0; s < MaxSections; s++)
        {
            _left[s].Flush();
            _right[s].Flush();
        }

        Dsp.Flush(ref _lastL);
        Dsp.Flush(ref _lastR);
        _mix.End();
    }

    protected override void ResetState()
    {
        for (var s = 0; s < MaxSections; s++)
        {
            _left[s].Reset();
            _right[s].Reset();
        }

        _lastL = _lastR = 0;
    }
}

/// <summary>Tremolo (volume) or auto-pan (balance) from a synced or free LFO, its shape and the right channel's phase offset.</summary>
public sealed class TremoloEffect : BuiltinEffect
{
    private const int Mode = 0, Sync = 1, Division = 2, Feel = 3, Rate = 4, Depth = 5, Shape = 6, Phase = 7;

    public static readonly BuiltinEffectInfo Definition = new("tremolo", "Tremolo", BuiltinCategory.Modulation, "wave-sine",
        "Tremolo / Auto-pan — rhythmic volume or panning, locked to the tempo",
        [
            BuiltinParam.Choice("mode", "Mode", ["Tremolo", "Auto-pan"]),
            BuiltinParam.Toggle("sync", "Sync", true, "Rate"),
            BuiltinParam.Choice("division", "Note", NoteDivision.Names, 3, "Rate"),
            BuiltinParam.Choice("feel", "Feel", NoteDivision.Feels, 0, "Rate"),
            new("rate", "Rate", "Hz", 0.1f, 20, 4, ParamCurve.Log, Group: "Rate"),
            new("depth", "Depth", "%", 0, 100, 50),
            BuiltinParam.Choice("shape", "Shape", Lfo.ShapeNames),
            new("phase", "Stereo Phase", "°", 0, 180, 0),
        ],
        sr => new TremoloEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Eighth Chop", new Dictionary<string, float> { ["shape"] = 2, ["depth"] = 100 }),
            new("Slow Auto-pan", new Dictionary<string, float> { ["mode"] = 1, ["division"] = 0, ["depth"] = 70 }),
        ]);

    private readonly float _smooth;
    private Lfo _lfo;
    private double _cycleBeats, _hz, _phase;
    private bool _pan;
    private float _depth, _valueL, _valueR;

    private TremoloEffect(int sampleRate) : base(Definition, sampleRate) => _smooth = Dsp.TimeCoefficient(0.002f, sampleRate);

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _pan = values[Mode] >= 0.5f;
        _cycleBeats = values[Sync] >= 0.5f ? NoteDivision.ToBeats((int)values[Division], (int)values[Feel]) : 0;
        _hz = values[Rate];
        _depth = values[Depth] / 100f;
        _lfo.Shape = (LfoShape)(int)values[Shape];
        _phase = values[Phase] / 360.0;
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        _lfo.Begin(context, _cycleBeats, _hz);
        var frames = stereo.Length / 2;
        for (var f = 0; f < frames; f++)
        {
            // Both channels read the LFO at their phase; a 2 ms smoother takes the click out of square and random shapes.
            var left = _lfo.At();
            var right = _lfo.At(_phase);
            _lfo.Advance();
            _valueL = left + (_valueL - left) * _smooth;
            _valueR = right + (_valueR - right) * _smooth;
            float gl, gr;
            if (_pan)
            {
                AudioMath.PanGains(_valueL * _depth, out gl, out gr);
            }
            else
            {
                gl = 1 - _depth * (0.5f - 0.5f * _valueL);
                gr = 1 - _depth * (0.5f - 0.5f * _valueR);
            }

            stereo[2 * f] *= gl;
            stereo[2 * f + 1] *= gr;
        }
    }

    protected override void ResetState() => _lfo.Reset();
}
