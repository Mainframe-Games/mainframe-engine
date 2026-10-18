namespace MainframeEngine.Editor.Music;

/// <summary>What an <see cref="EqEffect"/> band does.</summary>
public enum EqBandKind
{
    LowCut,
    LowShelf,
    Peak,
    HighShelf,
    HighCut,
}

/// <summary>One band of the <see cref="EqEffect"/>: its parameter ids (null where the kind has none) for the window's graph.</summary>
public sealed record EqBand(string Name, EqBandKind Kind, string OnId, string FreqId, string? GainId, string? QId, string? SlopeId);

/// <summary>
/// An eight-band channel EQ in the manner of Logic's: low cut, low shelf, four peaks, high shelf, high cut, output gain.
/// Cuts are Butterworth cascades at 6, 12, 24 or 48 dB/oct. Frequencies glide in the log domain and gains in dB over
/// ~20 ms; a band switched off glides to neutral before it stops (no click).
/// </summary>
public sealed class EqEffect : BuiltinEffect
{
    private const int SubBlock = 32;
    private const int MaxStages = 4;

    // Butterworth: the cascade's Qs for orders 2, 4 and 8.
    private static readonly double[] Butterworth2 = [0.70710678];
    private static readonly double[] Butterworth4 = [0.54119610, 1.30656296];
    private static readonly double[] Butterworth8 = [0.50979558, 0.60134489, 0.89997622, 2.56291545];

    private static readonly string[] Slopes = ["6 dB/oct", "12 dB/oct", "24 dB/oct", "48 dB/oct"];

    /// <summary>The bands in processing (and graph) order.</summary>
    public static readonly EqBand[] Bands =
    [
        new("Low Cut", EqBandKind.LowCut, "lowCutOn", "lowCutFreq", null, null, "lowCutSlope"),
        new("Low Shelf", EqBandKind.LowShelf, "lowShelfOn", "lowShelfFreq", "lowShelfGain", "lowShelfQ", null),
        new("Band 1", EqBandKind.Peak, "peak1On", "peak1Freq", "peak1Gain", "peak1Q", null),
        new("Band 2", EqBandKind.Peak, "peak2On", "peak2Freq", "peak2Gain", "peak2Q", null),
        new("Band 3", EqBandKind.Peak, "peak3On", "peak3Freq", "peak3Gain", "peak3Q", null),
        new("Band 4", EqBandKind.Peak, "peak4On", "peak4Freq", "peak4Gain", "peak4Q", null),
        new("High Shelf", EqBandKind.HighShelf, "highShelfOn", "highShelfFreq", "highShelfGain", "highShelfQ", null),
        new("High Cut", EqBandKind.HighCut, "highCutOn", "highCutFreq", null, null, "highCutSlope"),
    ];

    private static readonly BuiltinParam[] Parameters =
    [
        BuiltinParam.Toggle("lowCutOn", "On", false, "Low Cut"),
        new("lowCutFreq", "Freq", "Hz", 10, 2000, 80, ParamCurve.Log, Group: "Low Cut"),
        BuiltinParam.Choice("lowCutSlope", "Slope", Slopes, 2, "Low Cut"),
        BuiltinParam.Toggle("lowShelfOn", "On", true, "Low Shelf"),
        new("lowShelfFreq", "Freq", "Hz", 20, 2000, 100, ParamCurve.Log, Group: "Low Shelf"),
        new("lowShelfGain", "Gain", "dB", -24, 24, 0, Group: "Low Shelf"),
        new("lowShelfQ", "Q", "", 0.3f, 4, 0.71f, ParamCurve.Log, Group: "Low Shelf"),
        .. Peak(1, 200),
        .. Peak(2, 800),
        .. Peak(3, 2500),
        .. Peak(4, 6000),
        BuiltinParam.Toggle("highShelfOn", "On", true, "High Shelf"),
        new("highShelfFreq", "Freq", "Hz", 500, 20000, 8000, ParamCurve.Log, Group: "High Shelf"),
        new("highShelfGain", "Gain", "dB", -24, 24, 0, Group: "High Shelf"),
        new("highShelfQ", "Q", "", 0.3f, 4, 0.71f, ParamCurve.Log, Group: "High Shelf"),
        BuiltinParam.Toggle("highCutOn", "On", false, "High Cut"),
        new("highCutFreq", "Freq", "Hz", 1000, 22000, 16000, ParamCurve.Log, Group: "High Cut"),
        BuiltinParam.Choice("highCutSlope", "Slope", Slopes, 2, "High Cut"),
        new("output", "Output", "dB", -24, 24, 0, Group: "Output"),
    ];

    public static readonly BuiltinEffectInfo Definition = new("eq", "Channel EQ", BuiltinCategory.EqFilter, "adjustments-horizontal",
        "Channel EQ — low/high cut, shelves and four bands; drag the handles on its curve", Parameters, sr => new EqEffect(sr),
        BuiltinGraph.Eq,
        [
            new("Flat", new Dictionary<string, float>()),
            new("Clean Up Lows", new Dictionary<string, float> { ["lowCutOn"] = 1, ["lowCutFreq"] = 90, ["peak1Gain"] = -2.5f, ["peak1Freq"] = 300 }),
            new("Vocal Presence", new Dictionary<string, float> { ["lowCutOn"] = 1, ["lowCutFreq"] = 100, ["peak3Freq"] = 3000, ["peak3Gain"] = 3, ["highShelfGain"] = 2 }),
            new("Telephone", new Dictionary<string, float> { ["lowCutOn"] = 1, ["lowCutFreq"] = 400, ["highCutOn"] = 1, ["highCutFreq"] = 3000, ["peak2Freq"] = 1500, ["peak2Gain"] = 6 }),
            new("Air", new Dictionary<string, float> { ["highShelfFreq"] = 10000, ["highShelfGain"] = 4 }),
        ]);

    private static readonly int OutputIndex = Array.FindIndex(Parameters, p => p.Id == "output");

    private readonly BandState[] _bands;
    private BlockSmoother _output;

    private EqEffect(int sampleRate) : base(Definition, sampleRate)
    {
        _bands = new BandState[Bands.Length];
        for (var i = 0; i < _bands.Length; i++)
            _bands[i] = new BandState(Bands[i]);
    }

    private static BuiltinParam[] Peak(int n, float freq)
    {
        var group = "Band " + n;
        return
        [
            BuiltinParam.Toggle($"peak{n}On", "On", true, group),
            new($"peak{n}Freq", "Freq", "Hz", 20, 20000, freq, ParamCurve.Log, Group: group),
            new($"peak{n}Gain", "Gain", "dB", -24, 24, 0, Group: group),
            new($"peak{n}Q", "Q", "", 0.1f, 18, 1, ParamCurve.Log, Group: group),
        ];
    }

    /// <summary>The whole EQ's response in dB at <paramref name="frequency"/> for an insert's parameters (the window's curve).</summary>
    public static double ResponseDb(IReadOnlyDictionary<string, float>? values, double frequency, double sampleRate)
    {
        var total = (double)Definition.Value(values, "output");
        Span<BiquadCoefficients> stages = stackalloc BiquadCoefficients[MaxStages];
        foreach (var band in Bands)
        {
            if (Definition.Value(values, band.OnId) < 0.5f)
                continue;
            var count = Design(band, values, sampleRate, stages);
            for (var s = 0; s < count; s++)
                total += stages[s].MagnitudeDb(frequency, sampleRate);
        }

        return total;
    }

    /// <summary>One band's response in dB (0 when the band is off).</summary>
    public static double BandResponseDb(EqBand band, IReadOnlyDictionary<string, float>? values, double frequency, double sampleRate)
    {
        if (Definition.Value(values, band.OnId) < 0.5f)
            return 0;
        Span<BiquadCoefficients> stages = stackalloc BiquadCoefficients[MaxStages];
        var total = 0.0;
        var count = Design(band, values, sampleRate, stages);
        for (var s = 0; s < count; s++)
            total += stages[s].MagnitudeDb(frequency, sampleRate);
        return total;
    }

    private static int Design(EqBand band, IReadOnlyDictionary<string, float>? values, double sampleRate, Span<BiquadCoefficients> stages) =>
        Design(band.Kind, Definition.Value(values, band.FreqId), band.GainId is null ? 0 : Definition.Value(values, band.GainId),
            band.QId is null ? 0.7071f : Definition.Value(values, band.QId), band.SlopeId is null ? 0 : (int)Definition.Value(values, band.SlopeId),
            sampleRate, stages);

    // The band's biquad cascade; returns how many stages it uses.
    private static int Design(EqBandKind kind, double freq, double gain, double q, int slope, double sampleRate, Span<BiquadCoefficients> stages)
    {
        switch (kind)
        {
            case EqBandKind.LowShelf:
                stages[0] = BiquadCoefficients.Design(BiquadShape.LowShelf, freq, q, gain, sampleRate);
                return 1;
            case EqBandKind.HighShelf:
                stages[0] = BiquadCoefficients.Design(BiquadShape.HighShelf, freq, q, gain, sampleRate);
                return 1;
            case EqBandKind.Peak:
                stages[0] = BiquadCoefficients.Design(BiquadShape.Peak, freq, q, gain, sampleRate);
                return 1;
        }

        var high = kind == EqBandKind.LowCut; // a low cut is a high-pass
        if (slope <= 0)
        {
            stages[0] = BiquadCoefficients.Design(high ? BiquadShape.HighPass1 : BiquadShape.LowPass1, freq, 0.7071, 0, sampleRate);
            return 1;
        }

        var qs = slope switch
        {
            1 => Butterworth2,
            2 => Butterworth4,
            _ => Butterworth8,
        };
        var shape = high ? BiquadShape.HighPass : BiquadShape.LowPass;
        for (var i = 0; i < qs.Length; i++)
            stages[i] = BiquadCoefficients.Design(shape, freq, qs[i], 0, sampleRate);
        return qs.Length;
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        foreach (var band in _bands)
            band.SetTargets(values, SampleRate);
        _output.SetTarget(values[OutputIndex]);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        for (var start = 0; start < frames; start += SubBlock)
        {
            var count = Math.Min(SubBlock, frames - start);
            var chunk = stereo.Slice(start * 2, count * 2);
            foreach (var band in _bands)
                band.Process(chunk, SampleRate);
            var from = Dsp.DbToGain(_output.Value);
            _output.Advance(count, SampleRate);
            var to = Dsp.DbToGain(_output.Value);
            if (from == 1 && to == 1)
                continue;
            var step = (to - from) / count;
            for (var f = 0; f < count; f++)
            {
                var g = from + step * (f + 1);
                chunk[2 * f] *= g;
                chunk[2 * f + 1] *= g;
            }
        }
    }

    protected override void ResetState()
    {
        foreach (var band in _bands)
            band.Reset();
    }

    private sealed class BandState(EqBand band)
    {
        private readonly int _on = Definition.IndexOf(band.OnId);
        private readonly int _freq = Definition.IndexOf(band.FreqId);
        private readonly int _gain = band.GainId is null ? -1 : Definition.IndexOf(band.GainId);
        private readonly int _q = band.QId is null ? -1 : Definition.IndexOf(band.QId);
        private readonly int _slope = band.SlopeId is null ? -1 : Definition.IndexOf(band.SlopeId);
        private readonly StereoBiquad[] _stages = [new(), new(), new(), new()];
        private BlockSmoother _logFreq;
        private BlockSmoother _gainDb;
        private BlockSmoother _logQ;
        private int _stageCount;
        private int _slopeValue = -1;
        private bool _enabled;
        private bool _active;
        private bool _dirty = true;
        private bool _first = true;

        public void SetTargets(ReadOnlySpan<float> values, int sampleRate)
        {
            _enabled = values[_on] >= 0.5f;
            var freq = values[_freq];
            var gain = _gain >= 0 ? values[_gain] : 0f;
            if (!_enabled)
            {
                // Off: glide to neutral, then stop processing.
                if (band.Kind == EqBandKind.LowCut)
                    freq = 5;
                else if (band.Kind == EqBandKind.HighCut)
                    freq = sampleRate * 0.45f;
                gain = 0;
            }

            if (_first)
            {
                // The first parameters are taken at once (the smoothers' first targets).
                _first = false;
                _active = _enabled;
            }
            else if (!_active && _enabled)
            {
                // Coming back on from fully off: start from neutral too.
                _active = true;
                _logFreq.SetTarget(MathF.Log2(band.Kind == EqBandKind.LowCut ? 5 : band.Kind == EqBandKind.HighCut ? sampleRate * 0.45f : freq));
                _logFreq.Snap();
                _gainDb.SetTarget(0);
                _gainDb.Snap();
                ResetFilters();
            }

            _logFreq.SetTarget(MathF.Log2(freq));
            _gainDb.SetTarget(gain);
            _logQ.SetTarget(MathF.Log2(_q >= 0 ? values[_q] : 0.7071f));
            var slope = _slope >= 0 ? (int)values[_slope] : 0;
            if (slope != _slopeValue)
            {
                _slopeValue = slope;
                _dirty = true;
            }
        }

        public void Process(Span<float> chunk, int sampleRate)
        {
            if (!_active)
                return;
            var frames = chunk.Length / 2;
            _dirty |= _logFreq.Advance(frames, sampleRate);
            _dirty |= _gainDb.Advance(frames, sampleRate);
            _dirty |= _logQ.Advance(frames, sampleRate);
            if (!_enabled && _logFreq.Settled && _gainDb.Settled)
            {
                // Neutral and off: stop until switched on again.
                _active = false;
                return;
            }

            if (_dirty)
            {
                _dirty = false;
                Span<BiquadCoefficients> coefficients = stackalloc BiquadCoefficients[MaxStages];
                var count = Design(band.Kind, MathF.Pow(2, _logFreq.Value), _gainDb.Value, MathF.Pow(2, _logQ.Value), _slopeValue, sampleRate, coefficients);
                if (count != _stageCount)
                {
                    for (var s = _stageCount; s < count; s++)
                        _stages[s].Reset();
                    _stageCount = count;
                }

                for (var s = 0; s < count; s++)
                    _stages[s].Coefficients = coefficients[s];
            }

            for (var s = 0; s < _stageCount; s++)
                _stages[s].Process(chunk);
        }

        public void Reset() => ResetFilters();

        private void ResetFilters()
        {
            foreach (ref var stage in _stages.AsSpan())
                stage.Reset();
            _dirty = true;
        }
    }
}
