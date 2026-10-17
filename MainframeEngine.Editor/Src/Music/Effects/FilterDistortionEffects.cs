namespace MainframeEngine.Editor.Music;

/// <summary>
/// A resonant multimode filter (state-variable, 12 or 24 dB/oct) with input drive and an auto-filter LFO that sweeps the
/// cutoff by up to four octaves, synced to the song or free.
/// </summary>
public sealed class FilterEffect : BuiltinEffect
{
    private const int Type = 0, Cutoff = 1, Resonance = 2, Slope = 3, Drive = 4, LfoAmount = 5, LfoSync = 6, LfoDivision = 7, LfoRate = 8,
        LfoShapeIndex = 9, Mix = 10;

    private const int ControlRate = 16;

    public static readonly BuiltinEffectInfo Definition = new("filter", "Filter", BuiltinCategory.EqFilter, "filter",
        "Filter — resonant low/high/band-pass or notch with drive and a tempo-synced sweep",
        [
            BuiltinParam.Choice("type", "Type", ["Low-pass", "High-pass", "Band-pass", "Notch"]),
            new("cutoff", "Cutoff", "Hz", 20, 20000, 2000, ParamCurve.Log),
            new("resonance", "Resonance", "%", 0, 100, 20),
            BuiltinParam.Choice("slope", "Slope", ["12 dB/oct", "24 dB/oct"]),
            new("drive", "Drive", "dB", 0, 24, 0),
            new("lfoAmount", "Amount", "oct", 0, 4, 0, Group: "LFO"),
            BuiltinParam.Toggle("lfoSync", "Sync", true, "LFO"),
            BuiltinParam.Choice("lfoDivision", "Note", NoteDivision.Names, 2, "LFO"),
            new("lfoRate", "Rate", "Hz", 0.05f, 20, 1, ParamCurve.Log, Group: "LFO"),
            BuiltinParam.Choice("lfoShape", "Shape", Lfo.ShapeNames, 0, "LFO"),
            new("mix", "Mix", "%", 0, 100, 100),
        ],
        sr => new FilterEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Quarter Wobble", new Dictionary<string, float> { ["cutoff"] = 600, ["resonance"] = 55, ["slope"] = 1, ["lfoAmount"] = 2.5f }),
            new("Telephone Band", new Dictionary<string, float> { ["type"] = 2, ["cutoff"] = 1500, ["resonance"] = 30 }),
            new("Slow Rise", new Dictionary<string, float> { ["type"] = 1, ["cutoff"] = 200, ["lfoAmount"] = 3, ["lfoDivision"] = 8, ["lfoShape"] = 3 }),
        ]);

    private readonly Svf[] _stages = new Svf[4]; // L1, R1, L2, R2
    private Lfo _lfo;
    private BlockSmoother _logCutoff;
    private int _type;
    private bool _steep;
    private float _q, _drive, _amount;
    private double _cycleBeats, _hz;
    private LinearRamp _mix;

    private FilterEffect(int sampleRate) : base(Definition, sampleRate)
    {
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _type = (int)values[Type];
        _logCutoff.SetTarget(MathF.Log2(values[Cutoff]));
        _q = 0.7071f * MathF.Pow(28, values[Resonance] / 100f); // Butterworth … 20
        _steep = values[Slope] >= 0.5f;
        _drive = Dsp.DbToGain(values[Drive]);
        _amount = values[LfoAmount];
        _cycleBeats = values[LfoSync] >= 0.5f ? NoteDivision.ToBeats((int)values[LfoDivision], 0) : 0;
        _hz = values[LfoRate];
        _lfo.Shape = (LfoShape)(int)values[LfoShapeIndex];
        _mix.SetTarget(values[Mix] / 100f);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        _lfo.Begin(context, _cycleBeats, _hz);
        var frames = stereo.Length / 2;
        var mixStep = _mix.Step(frames);
        // Two cascaded resonant stages would ring twice as hard: the second is Butterworth-damped.
        var q2 = _steep ? MathF.Min(_q, 0.7071f) : 0;
        for (var f = 0; f < frames; f++)
        {
            var m = _lfo.Next();
            if (f % ControlRate == 0)
            {
                _logCutoff.Advance(ControlRate, SampleRate);
                var cutoff = MathF.Pow(2, _logCutoff.Value + _amount * m);
                _stages[0].Set(cutoff, _q, SampleRate);
                _stages[1].Set(cutoff, _q, SampleRate);
                if (_steep)
                {
                    _stages[2].Set(cutoff, q2, SampleRate);
                    _stages[3].Set(cutoff, q2, SampleRate);
                }
            }

            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var wl = Stage(ref _stages[0], Shape(l));
            var wr = Stage(ref _stages[1], Shape(r));
            if (_steep)
            {
                wl = Stage(ref _stages[2], wl);
                wr = Stage(ref _stages[3], wr);
            }

            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (wl - l) * mix;
            stereo[2 * f + 1] = r + (wr - r) * mix;
        }

        foreach (ref var stage in _stages.AsSpan())
            stage.Flush();
        _mix.End();
    }

    private float Shape(float x) => _drive == 1 ? x : MathF.Tanh(x * _drive) / MathF.Sqrt(_drive);

    private float Stage(ref Svf svf, float x)
    {
        var low = svf.Process(x, out var band, out var high);
        return _type switch
        {
            1 => high,
            2 => band,
            3 => low + high,
            _ => low,
        };
    }

    protected override void ResetState()
    {
        foreach (ref var stage in _stages.AsSpan())
            stage.Reset();
    }
}

/// <summary>Analog-style saturation (tape, tube, soft clip) with bias, a tone tilt and mix, 2× oversampled.</summary>
public sealed class SaturationEffect : BuiltinEffect
{
    private const int Type = 0, Drive = 1, Bias = 2, Tone = 3, Mix = 4, Output = 5;

    public static readonly BuiltinEffectInfo Definition = new("saturation", "Saturation", BuiltinCategory.Distortion, "flame",
        "Saturation — warmth and density from tape, tube or soft-clip curves",
        [
            BuiltinParam.Choice("type", "Type", ["Tape", "Tube", "Soft Clip"]),
            new("drive", "Drive", "dB", 0, 36, 6),
            new("bias", "Asymmetry", "%", 0, 100, 0),
            new("tone", "Tone", "%", -100, 100, 0),
            new("mix", "Mix", "%", 0, 100, 100),
            new("output", "Output", "dB", -24, 24, 0),
        ],
        sr => new SaturationEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Warm Tape", new Dictionary<string, float> { ["drive"] = 9, ["tone"] = -20 }),
            new("Hot Tube", new Dictionary<string, float> { ["type"] = 1, ["drive"] = 18, ["bias"] = 30, ["output"] = -6 }),
        ]);

    private readonly Oversampler2x _oversampler;
    private StereoBiquad _toneLow = new(), _toneHigh = new(), _dcBlock = new();
    private Shaper _shaper;
    private readonly float[] _dry = new float[SongEngine.BlockFrames * 2];
    private LinearRamp _mix, _output;

    private SaturationEffect(int sampleRate) : base(Definition, sampleRate)
    {
        _oversampler = new Oversampler2x(sampleRate);
        _dcBlock.Coefficients = BiquadCoefficients.Design(BiquadShape.HighPass, 10, 0.7071, 0, sampleRate); // asymmetry's DC
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        var drive = Dsp.DbToGain(values[Drive]);
        _shaper = new Shaper((int)values[Type], drive, values[Bias] / 100f * 0.5f);
        var tilt = values[Tone] / 100f * 6f; // ±6 dB tilt around 1 kHz
        _toneLow.Coefficients = BiquadCoefficients.Design(BiquadShape.LowShelf, 1000, 0.5, -tilt, SampleRate);
        _toneHigh.Coefficients = BiquadCoefficients.Design(BiquadShape.HighShelf, 1000, 0.5, tilt, SampleRate);
        _mix.SetTarget(values[Mix] / 100f);
        _output.SetTarget(Dsp.DbToGain(values[Output]) / MathF.Sqrt(drive)); // roughly level-matched
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var dry = _dry.AsSpan(0, stereo.Length);
        stereo.CopyTo(dry);
        _oversampler.Process(stereo, ref _shaper);
        _dcBlock.Process(stereo);
        _toneLow.Process(stereo);
        _toneHigh.Process(stereo);
        var mixStep = _mix.Step(frames);
        var outStep = _output.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var mix = _mix.Current + mixStep * (f + 1);
            var gain = _output.Current + outStep * (f + 1);
            stereo[2 * f] = dry[2 * f] + (stereo[2 * f] * gain - dry[2 * f]) * mix;
            stereo[2 * f + 1] = dry[2 * f + 1] + (stereo[2 * f + 1] * gain - dry[2 * f + 1]) * mix;
        }

        _mix.End();
        _output.End();
    }

    protected override void ResetState()
    {
        _oversampler.Reset();
        _dcBlock.Reset();
        _toneLow.Reset();
        _toneHigh.Reset();
    }

    private readonly struct Shaper(int type, float drive, float bias) : IStereoShaper
    {
        private readonly float _offset = Curve(type, bias);

        public void Shape(ref float left, ref float right)
        {
            left = Curve(type, left * drive + bias) - _offset;
            right = Curve(type, right * drive + bias) - _offset;
        }

        private static float Curve(int type, float x) => type switch
        {
            1 => x >= 0 ? 1 - MathF.Exp(-x) : -(1 - MathF.Exp(x * 1.6f)) / 1.6f, // tube: softer on one side
            2 => MathF.Abs(x) >= 1 ? MathF.Sign(x) : 1.5f * x - 0.5f * x * x * x,
            _ => MathF.Tanh(x),
        };
    }
}

/// <summary>Distortion: overdrive, hard clip, fuzz or wavefolding, then a tone low-pass; 2× oversampled.</summary>
public sealed class DistortionEffect : BuiltinEffect
{
    private const int Type = 0, Drive = 1, Tone = 2, Mix = 3, Output = 4;

    public static readonly BuiltinEffectInfo Definition = new("distortion", "Distortion", BuiltinCategory.Distortion, "bolt",
        "Distortion — overdrive, hard clip, fuzz or wavefolder with a tone control",
        [
            BuiltinParam.Choice("type", "Type", ["Overdrive", "Hard Clip", "Fuzz", "Foldback"]),
            new("drive", "Drive", "dB", 0, 48, 18),
            new("tone", "Tone", "Hz", 500, 20000, 6000, ParamCurve.Log),
            new("mix", "Mix", "%", 0, 100, 100),
            new("output", "Output", "dB", -36, 12, -6),
        ],
        sr => new DistortionEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("Crunch", new Dictionary<string, float> { ["drive"] = 12, ["tone"] = 4000, ["output"] = -4 }),
            new("Fuzz Box", new Dictionary<string, float> { ["type"] = 2, ["drive"] = 30, ["tone"] = 3000, ["output"] = -10 }),
            new("Folded Synth", new Dictionary<string, float> { ["type"] = 3, ["drive"] = 14, ["tone"] = 9000, ["mix"] = 70 }),
        ]);

    private readonly Oversampler2x _oversampler;
    private StereoBiquad _tone = new(), _dcBlock = new();
    private Shaper _shaper;
    private readonly float[] _dry = new float[SongEngine.BlockFrames * 2];
    private LinearRamp _mix, _output;

    private DistortionEffect(int sampleRate) : base(Definition, sampleRate)
    {
        _oversampler = new Oversampler2x(sampleRate);
        _dcBlock.Coefficients = BiquadCoefficients.Design(BiquadShape.HighPass, 10, 0.7071, 0, sampleRate); // fuzz's DC
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _shaper = new Shaper((int)values[Type], Dsp.DbToGain(values[Drive]));
        _tone.Coefficients = BiquadCoefficients.Design(BiquadShape.LowPass, values[Tone], 0.7071, 0, SampleRate);
        _mix.SetTarget(values[Mix] / 100f);
        _output.SetTarget(Dsp.DbToGain(values[Output]));
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var dry = _dry.AsSpan(0, stereo.Length);
        stereo.CopyTo(dry);
        _oversampler.Process(stereo, ref _shaper);
        _dcBlock.Process(stereo);
        _tone.Process(stereo);
        var mixStep = _mix.Step(frames);
        var outStep = _output.Step(frames);
        for (var f = 0; f < frames; f++)
        {
            var mix = _mix.Current + mixStep * (f + 1);
            var gain = _output.Current + outStep * (f + 1);
            stereo[2 * f] = dry[2 * f] + (stereo[2 * f] * gain - dry[2 * f]) * mix;
            stereo[2 * f + 1] = dry[2 * f + 1] + (stereo[2 * f + 1] * gain - dry[2 * f + 1]) * mix;
        }

        _mix.End();
        _output.End();
    }

    protected override void ResetState()
    {
        _oversampler.Reset();
        _dcBlock.Reset();
        _tone.Reset();
    }

    private readonly struct Shaper(int type, float drive) : IStereoShaper
    {
        public void Shape(ref float left, ref float right)
        {
            left = Curve(left * drive);
            right = Curve(right * drive);
        }

        private float Curve(float x) => type switch
        {
            1 => Math.Clamp(x, -1f, 1f),
            2 => MathF.Sign(x + 0.1f) * (1 - MathF.Exp(-MathF.Abs(x + 0.1f) * 3)) - 0.26f, // asymmetric, like a transistor fuzz
            3 => Fold(x),
            _ => MathF.Tanh(x + 0.15f * x * x * MathF.Sign(x)) * 0.9f,
        };

        // A triangle fold: whatever goes past ±1 is reflected back.
        private static float Fold(float x)
        {
            var t = (x + 1) * 0.25f;
            t -= MathF.Floor(t);
            return MathF.Abs(t * 4 - 2) - 1;
        }
    }
}

/// <summary>Bit depth and sample-rate reduction (lo-fi), with mix.</summary>
public sealed class BitcrusherEffect : BuiltinEffect
{
    private const int Bits = 0, Downsample = 1, Mix = 2;

    public static readonly BuiltinEffectInfo Definition = new("bitcrusher", "Bitcrusher", BuiltinCategory.Distortion, "wand",
        "Bitcrusher — fewer bits and a lower sample rate for lo-fi grit",
        [
            new("bits", "Bits", "bits", 1, 24, 8),
            new("downsample", "Downsample", "×", 1, 64, 4, ParamCurve.Log),
            new("mix", "Mix", "%", 0, 100, 100),
        ],
        sr => new BitcrusherEffect(sr), BuiltinGraph.None,
        [
            new("Default", new Dictionary<string, float>()),
            new("8-bit Console", new Dictionary<string, float> { ["bits"] = 8, ["downsample"] = 3 }),
            new("Crushed", new Dictionary<string, float> { ["bits"] = 4, ["downsample"] = 12, ["mix"] = 60 }),
        ]);

    private float _step, _factor, _counter, _heldL, _heldR;
    private LinearRamp _mix;

    private BitcrusherEffect(int sampleRate) : base(Definition, sampleRate)
    {
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _step = 2f / MathF.Pow(2, values[Bits]);
        _factor = values[Downsample];
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
            _counter += 1;
            if (_counter >= _factor)
            {
                _counter -= _factor;
                _heldL = MathF.Round(l / _step) * _step;
                _heldR = MathF.Round(r / _step) * _step;
            }

            var mix = _mix.Current + mixStep * (f + 1);
            stereo[2 * f] = l + (_heldL - l) * mix;
            stereo[2 * f + 1] = r + (_heldR - r) * mix;
        }

        _mix.End();
    }

    protected override void ResetState() => _counter = _heldL = _heldR = 0;
}
