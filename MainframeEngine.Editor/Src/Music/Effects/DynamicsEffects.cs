namespace MainframeEngine.Editor.Music;

/// <summary>
/// A brickwall lookahead limiter: the signal is delayed 3 ms while the gain it needs (ceiling ÷ peak) is held over the
/// lookahead window and averaged over it, so the gain is already down when a peak arrives (no overshoot, no clipping
/// distortion); then it releases exponentially. A final clamp at the ceiling is the guarantee.
/// </summary>
public sealed class LimiterEffect : BuiltinEffect
{
    private const int Gain = 0, Ceiling = 1, Release = 2;
    private const float LookaheadSeconds = 0.003f;

    public static readonly BuiltinEffectInfo Definition = new("limiter", "Limiter", BuiltinCategory.Dynamics, "gauge",
        "Limiter — raises loudness and never lets a peak past the ceiling (3 ms lookahead)",
        [
            new("gain", "Gain", "dB", 0, 24, 0),
            new("ceiling", "Ceiling", "dB", -12, 0, -0.3f),
            new("release", "Release", "ms", 1, 1000, 50, ParamCurve.Log),
        ],
        sr => new LimiterEffect(sr), BuiltinGraph.GainReduction,
        [
            new("Default", new Dictionary<string, float>()),
            new("Master Loudness", new Dictionary<string, float> { ["gain"] = 6, ["ceiling"] = -1, ["release"] = 80 }),
            new("Peak Catcher", new Dictionary<string, float> { ["ceiling"] = -0.1f, ["release"] = 20 }),
        ]);

    private readonly int _window; // hold and average length (lookahead + 1)
    private readonly float[] _delayL;
    private readonly float[] _delayR;
    private readonly long[] _dequeIndex;
    private readonly float[] _dequeValue;
    private readonly float[] _box;
    private int _delayPos;
    private int _head, _tail, _count;
    private long _sample;
    private double _boxSum;
    private int _boxPos;
    private float _envelope = 1;
    private float _ceiling = 1, _releaseCoef;
    private LinearRamp _gain;

    private LimiterEffect(int sampleRate) : base(Definition, sampleRate, (int)MathF.Round(LookaheadSeconds * sampleRate))
    {
        _window = LatencyFrames + 1;
        _delayL = new float[LatencyFrames];
        _delayR = new float[LatencyFrames];
        _dequeIndex = new long[_window + 1];
        _dequeValue = new float[_window + 1];
        _box = new float[_window];
        ResetState();
    }

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _gain.SetTarget(Dsp.DbToGain(values[Gain]));
        _ceiling = Dsp.DbToGain(values[Ceiling]);
        _releaseCoef = Dsp.TimeCoefficient(values[Release] / 1000f, SampleRate);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var gainStep = _gain.Step(frames);
        var most = 1f;
        for (var f = 0; f < frames; f++)
        {
            var g = _gain.Current + gainStep * (f + 1);
            var l = stereo[2 * f] * g;
            var r = stereo[2 * f + 1] * g;
            var peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            var required = peak > _ceiling ? _ceiling / peak : 1f;

            // Sliding minimum of the required gain over the window (a monotonic deque of sample numbers).
            while (_count > 0 && _dequeValue[Prev(_tail)] >= required)
            {
                _tail = Prev(_tail);
                _count--;
            }

            _dequeIndex[_tail] = _sample;
            _dequeValue[_tail] = required;
            _tail = Next(_tail);
            _count++;
            if (_dequeIndex[_head] <= _sample - _window)
            {
                _head = Next(_head);
                _count--;
            }

            var held = _dequeValue[_head];
            // Release: rise slowly toward the held value; fall to it at once.
            _envelope = held < _envelope ? held : held + (_envelope - held) * _releaseCoef;

            // Average over the window: the gain ramps down across the lookahead instead of jumping.
            _boxSum += _envelope - _box[_boxPos];
            _box[_boxPos] = _envelope;
            _boxPos = _boxPos + 1 == _window ? 0 : _boxPos + 1;
            var gain = (float)(_boxSum / _window);
            most = MathF.Min(most, gain);

            // The lookahead delay.
            var dl = _delayL[_delayPos];
            var dr = _delayR[_delayPos];
            _delayL[_delayPos] = l;
            _delayR[_delayPos] = r;
            _delayPos = _delayPos + 1 == _delayL.Length ? 0 : _delayPos + 1;
            stereo[2 * f] = Math.Clamp(dl * gain, -_ceiling, _ceiling);
            stereo[2 * f + 1] = Math.Clamp(dr * gain, -_ceiling, _ceiling);
            _sample++;
        }

        // Re-sum now and then: the running sum must not drift.
        if ((_sample & 0x3FFF) < frames)
        {
            _boxSum = 0;
            foreach (var v in _box)
                _boxSum += v;
        }
        _gain.End();
        ReportGainReduction(-Dsp.GainToDb(most));
    }

    private int Next(int i) => i + 1 == _dequeIndex.Length ? 0 : i + 1;

    private int Prev(int i) => i == 0 ? _dequeIndex.Length - 1 : i - 1;

    protected override void ResetState()
    {
        Array.Clear(_delayL);
        Array.Clear(_delayR);
        Array.Fill(_box, 1f);
        _boxSum = _window;
        _envelope = 1;
        _head = _tail = _count = 0;
        _sample = 0;
        _delayPos = _boxPos = 0;
    }
}

/// <summary>
/// A noise gate: opens when the louder channel's level passes the threshold, closes below threshold − hysteresis after
/// the hold time, fading to the range (the floor) with separate attack and release.
/// </summary>
public sealed class GateEffect : BuiltinEffect
{
    private const int Threshold = 0, Hysteresis = 1, Range = 2, Attack = 3, Hold = 4, Release = 5;

    public static readonly BuiltinEffectInfo Definition = new("gate", "Noise Gate", BuiltinCategory.Dynamics, "door",
        "Noise Gate — silences the signal between notes: threshold, hysteresis, range, attack, hold and release",
        [
            new("threshold", "Threshold", "dB", -80, 0, -40),
            new("hysteresis", "Hysteresis", "dB", 0, 12, 3),
            new("range", "Range", "dB", -80, 0, -80),
            new("attack", "Attack", "ms", 0.05f, 50, 0.5f, ParamCurve.Log),
            new("hold", "Hold", "ms", 0, 500, 20),
            new("release", "Release", "ms", 5, 2000, 100, ParamCurve.Log),
        ],
        sr => new GateEffect(sr), BuiltinGraph.GainReduction,
        [
            new("Default", new Dictionary<string, float>()),
            new("Tight Drums", new Dictionary<string, float> { ["threshold"] = -30, ["hold"] = 10, ["release"] = 40 }),
            new("Gentle Noise Floor", new Dictionary<string, float> { ["threshold"] = -55, ["range"] = -15, ["release"] = 250 }),
        ]);

    private readonly float _detectorRelease;
    private float _openDb, _closeDb, _floor, _attackCoef, _releaseCoef;
    private int _holdFrames;
    private float _level, _gain = 1;
    private bool _open;
    private int _holdLeft;

    private GateEffect(int sampleRate) : base(Definition, sampleRate) =>
        _detectorRelease = Dsp.TimeCoefficient(0.01f, sampleRate);

    protected override void OnParameters(ReadOnlySpan<float> values)
    {
        _openDb = values[Threshold];
        _closeDb = values[Threshold] - values[Hysteresis];
        _floor = Dsp.DbToGain(values[Range]);
        _attackCoef = Dsp.TimeCoefficient(values[Attack] / 1000f, SampleRate);
        _releaseCoef = Dsp.TimeCoefficient(values[Release] / 1000f, SampleRate);
        _holdFrames = (int)(values[Hold] / 1000f * SampleRate);
    }

    protected override void ProcessBlock(Span<float> stereo, in EffectContext context)
    {
        var frames = stereo.Length / 2;
        var least = 1f;
        var openGain = Dsp.DbToGain(_openDb);
        var closeGain = Dsp.DbToGain(_closeDb);
        for (var f = 0; f < frames; f++)
        {
            var l = stereo[2 * f];
            var r = stereo[2 * f + 1];
            var peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            _level = peak > _level ? peak : _level * _detectorRelease;
            if (_level >= openGain)
            {
                _open = true;
                _holdLeft = _holdFrames;
            }
            else if (_open && _level < closeGain)
            {
                if (_holdLeft > 0)
                    _holdLeft--;
                else
                    _open = false;
            }

            var target = _open ? 1f : _floor;
            _gain = target + (_gain - target) * (target > _gain ? _attackCoef : _releaseCoef);
            least = MathF.Min(least, _gain);
            stereo[2 * f] = l * _gain;
            stereo[2 * f + 1] = r * _gain;
        }

        Dsp.Flush(ref _level);
        ReportGainReduction(-Dsp.GainToDb(least));
    }

    protected override void ResetState()
    {
        _level = 0;
        _gain = 1;
        _open = false;
        _holdLeft = 0;
    }
}
