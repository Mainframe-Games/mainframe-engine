using System.Runtime.CompilerServices;

namespace MainframeEngine.Editor.Music;

/// <summary>Envelope times and curves for one control step (seconds; curves −1…1).</summary>
public readonly record struct EnvelopeShape(float Delay, float Attack, float Hold, float Decay, float Sustain, float Release,
    float AttackCurve, float DecayCurve, float ReleaseCurve);

/// <summary>
/// A DAHDSR envelope with curved segments, advanced once per control step. A note-off during attack or decay releases
/// from the current level; a retrigger restarts the attack from the current level.
/// </summary>
public struct SynthEnvelope
{
    private const float ZeroTime = 0.0006f; // the knobs' minimum (0.5 ms) counts as instant

    private enum Stage : byte
    {
        Idle,
        Delay,
        Attack,
        Hold,
        Decay,
        Sustain,
        Release,
    }

    private Stage _stage;
    private float _time;
    private float _from;

    public float Value { get; private set; }

    public readonly bool Active => _stage != Stage.Idle;

    public readonly bool Releasing => _stage == Stage.Release;

    /// <summary>Starts (or restarts) the envelope; a retrigger keeps the current level as the attack's start.</summary>
    public void Trigger()
    {
        _from = Value;
        _stage = Stage.Delay;
        _time = 0;
    }

    public void Release()
    {
        if (_stage is Stage.Idle or Stage.Release)
            return;
        _from = Value;
        _stage = Stage.Release;
        _time = 0;
    }

    public void Kill()
    {
        _stage = Stage.Idle;
        Value = 0;
        _time = 0;
    }

    /// <summary>The segment curve: progress 0…1 bent by <paramref name="curve"/> (0 linear; negative fast-then-slow).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Curve(float x, float curve)
    {
        var k = 6 * curve;
        if (MathF.Abs(k) < 1e-3f)
            return x;
        return (MathF.Exp(k * x) - 1) / (MathF.Exp(k) - 1);
    }

    /// <summary>Advances by <paramref name="dt"/> seconds; returns the new value.</summary>
    public float Advance(float dt, in EnvelopeShape s)
    {
        _time += dt;
        while (true)
        {
            switch (_stage)
            {
                case Stage.Idle:
                    Value = 0;
                    return 0;
                case Stage.Delay:
                    if (_time < s.Delay && s.Delay > ZeroTime)
                        return Value = _from;
                    Next(Stage.Attack, s.Delay);
                    continue;
                case Stage.Attack:
                    if (s.Attack > ZeroTime && _time < s.Attack)
                        return Value = _from + (1 - _from) * Curve(_time / s.Attack, s.AttackCurve);
                    Next(Stage.Hold, s.Attack);
                    continue;
                case Stage.Hold:
                    if (s.Hold > ZeroTime && _time < s.Hold)
                        return Value = 1;
                    Next(Stage.Decay, s.Hold);
                    continue;
                case Stage.Decay:
                    if (s.Decay > ZeroTime && _time < s.Decay)
                        return Value = 1 - (1 - s.Sustain) * Curve(_time / s.Decay, s.DecayCurve);
                    _stage = Stage.Sustain;
                    continue;
                case Stage.Sustain:
                    return Value = s.Sustain;
                default:
                    if (s.Release > ZeroTime && _time < s.Release)
                        return Value = _from * (1 - Curve(_time / s.Release, s.ReleaseCurve));
                    _stage = Stage.Idle;
                    _time = 0;
                    return Value = 0;
            }
        }
    }

    private void Next(Stage stage, float length)
    {
        _time = MathF.Max(0, _time - MathF.Max(0, length));
        _stage = stage;
        if (stage == Stage.Attack)
            return;
        _from = Value;
    }
}

/// <summary>
/// An LFO of the synth, advanced once per control step: built-in shapes or a drawn 1024-entry table, output 0…1, with
/// delay and rise (fade in after a note-on), smoothing, and an envelope mode that plays one cycle and holds.
/// </summary>
public struct SynthLfo
{
    private double _phase;
    private float _held;
    private float _previous;
    private float _smoothed;
    private float _age;
    private bool _finished;
    private uint _rng;

    public float Value { get; private set; }

    /// <summary>Note-on (retrigger and envelope modes restart at <paramref name="phase"/>; free mode keeps running).</summary>
    public void NoteOn(int mode, float phase, uint seed)
    {
        _age = 0;
        _rng = seed | 1;
        if (mode == 0)
            return;
        _phase = phase;
        _finished = false;
        _held = Random();
        _previous = _held;
    }

    /// <summary>Sets the phase from the song position (a synced global LFO while playing).</summary>
    public void Lock(double phase) => _phase = phase - Math.Floor(phase);

    /// <summary>
    /// Advances <paramref name="dt"/> seconds at <paramref name="hz"/>; returns the output 0…1. <paramref name="drawn"/> is
    /// the drawn shape's table (shape 7).
    /// </summary>
    public float Advance(float dt, float hz, int shape, int mode, float delay, float rise, float smooth, float[]? drawn)
    {
        _age += dt;
        if (!_finished)
        {
            var next = _phase + hz * dt;
            if (next >= 1)
            {
                if (mode == 2)
                {
                    next = 0.999999;
                    _finished = true;
                }
                else
                {
                    next -= Math.Floor(next);
                    _previous = _held;
                    _held = Random();
                }
            }

            _phase = next;
        }

        var p = (float)_phase;
        var v = shape switch
        {
            1 => p < 0.5f ? 2 * p : 2 - 2 * p,
            2 => p,
            3 => 1 - p,
            4 => p < 0.5f ? 1f : 0f,
            5 => _held,
            6 => _previous + (_held - _previous) * (0.5f - 0.5f * MathF.Cos(MathF.PI * p)),
            7 when drawn is { Length: > 0 } => drawn[Math.Min(drawn.Length - 1, (int)(p * drawn.Length))],
            _ => 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * p),
        };

        var fade = _age < delay ? 0 : rise <= 0.001f ? 1 : MathF.Min(1, (_age - delay) / rise);
        v *= fade;
        if (smooth > 0.001f)
        {
            var k = 1 - MathF.Exp(-dt / (smooth * 0.5f));
            _smoothed += (v - _smoothed) * k;
            v = _smoothed;
        }
        else
        {
            _smoothed = v;
        }

        return Value = v;
    }

    private float Random()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / (1 << 24));
    }
}

/// <summary>
/// One of a voice's two filters (stereo): state-variable LP/HP/BP 12 and 24, notch, peak; a 4-pole ladder with a tanh
/// per stage; feedback combs; a three-band vowel formant. Coefficients are set once per control step.
/// </summary>
public sealed class SynthFilter
{
    /// <summary>The comb's longest delay: 40 Hz at 192 kHz.</summary>
    private const int CombLength = 4800 + 4;

    private static readonly (float F1, float F2, float F3)[] Vowels = [(800, 1150, 2900), (400, 1600, 2700), (350, 1700, 2700), (450, 800, 2830), (325, 700, 2530)];

    // SVF / formant state: 3 sections × 2 channels × (ic1, ic2).
    private readonly float[] _s = new float[12];
    private readonly float[] _a1 = new float[3], _a2 = new float[3], _a3 = new float[3], _k = new float[3];
    private readonly float[] _ladder = new float[8];
    private float[]? _combL, _combR;
    private int _combWrite;
    private float _combDelay, _combFeedback, _dampL, _dampR;
    private float _g, _G, _ladderK;
    private SynthFilterType _type;
    private float _drive = 1, _driveNorm = 1;

    /// <summary>UI thread: allocates the comb lines (a program with a comb filter is published after this).</summary>
    public void EnsureComb()
    {
        if (_combL is not null)
            return;
        _combL = new float[CombLength];
        _combR = new float[CombLength];
    }

    public void Reset()
    {
        Array.Clear(_s);
        Array.Clear(_ladder);
        if (_combL is not null)
            Array.Clear(_combL);
        if (_combR is not null)
            Array.Clear(_combR);
        _dampL = _dampR = 0;
    }

    /// <summary>Sets type, cutoff (Hz), resonance (0…1) and drive (dB) for the next control step.</summary>
    public void Set(SynthFilterType type, float cutoff, float reso, float driveDb, float sampleRate)
    {
        if (type != _type)
        {
            _type = type;
            Reset();
        }

        _drive = driveDb > 0.05f ? Dsp.DbToGain(driveDb) : 1;
        _driveNorm = _drive > 1 ? 1 / MathF.Tanh(_drive) : 1;
        cutoff = Math.Clamp(cutoff, 10, sampleRate * 0.49f);
        reso = Math.Clamp(reso, 0, 1);
        switch (type)
        {
            case SynthFilterType.Lp24 or SynthFilterType.Hp24 or SynthFilterType.Bp24:
                Section(0, cutoff, 0.5412f, sampleRate);
                Section(1, cutoff, 0.7071f + reso * reso * 24, sampleRate);
                break;
            case SynthFilterType.Ladder:
                _g = MathF.Tan(MathF.PI * cutoff / sampleRate);
                _G = _g / (1 + _g);
                _ladderK = 4.2f * reso;
                break;
            case SynthFilterType.CombPlus or SynthFilterType.CombMinus:
                _combDelay = Math.Clamp(sampleRate / MathF.Max(cutoff, 40), 2, CombLength - 4);
                _combFeedback = (type == SynthFilterType.CombPlus ? 1 : -1) * 0.98f * reso;
                break;
            case SynthFilterType.Formant:
                {
                    var pos = Math.Clamp(MathF.Log(cutoff / 20) / MathF.Log(1000), 0, 1) * (Vowels.Length - 1);
                    var i = Math.Min(Vowels.Length - 2, (int)pos);
                    var t = pos - i;
                    var (a1, a2, a3) = Vowels[i];
                    var (b1, b2, b3) = Vowels[i + 1];
                    var q = 2 + reso * 18;
                    Section(0, a1 + (b1 - a1) * t, q, sampleRate);
                    Section(1, a2 + (b2 - a2) * t, q, sampleRate);
                    Section(2, a3 + (b3 - a3) * t, q, sampleRate);
                    break;
                }

            default:
                Section(0, cutoff, 0.7071f + reso * reso * 24, sampleRate);
                break;
        }
    }

    private void Section(int i, float cutoff, float q, float sampleRate)
    {
        var g = MathF.Tan(MathF.PI * Math.Clamp(cutoff, 10, sampleRate * 0.49f) / sampleRate);
        _k[i] = 1 / MathF.Max(q, 0.05f);
        _a1[i] = 1 / (1 + g * (g + _k[i]));
        _a2[i] = g * _a1[i];
        _a3[i] = g * _a2[i];
    }

    // One SVF section on one channel; returns low and writes band and high.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Svf(int section, int channel, float x, out float band, out float high)
    {
        var at = (section * 2 + channel) * 2;
        var ic1 = _s[at];
        var ic2 = _s[at + 1];
        var v3 = x - ic2;
        var v1 = _a1[section] * ic1 + _a2[section] * v3;
        var v2 = ic2 + _a2[section] * ic1 + _a3[section] * v3;
        _s[at] = 2 * v1 - ic1;
        _s[at + 1] = 2 * v2 - ic2;
        band = v1;
        high = x - _k[section] * v1 - v2;
        return v2;
    }

    public void Process(ref float left, ref float right)
    {
        if (_drive > 1)
        {
            left = MathF.Tanh(left * _drive) * _driveNorm;
            right = MathF.Tanh(right * _drive) * _driveNorm;
        }

        left = Channel(0, left);
        right = Channel(1, right);
    }

    private float Channel(int c, float x)
    {
        switch (_type)
        {
            case SynthFilterType.Lp12:
                return Svf(0, c, x, out _, out _);
            case SynthFilterType.Hp12:
                Svf(0, c, x, out _, out var h12);
                return h12;
            case SynthFilterType.Bp12:
                Svf(0, c, x, out var b12, out _);
                return b12;
            case SynthFilterType.Notch:
                {
                    Svf(0, c, x, out var b, out _);
                    return x - _k[0] * b;
                }

            case SynthFilterType.Peak:
                {
                    var l = Svf(0, c, x, out _, out var h);
                    return l - h;
                }

            case SynthFilterType.Lp24:
                return Svf(1, c, Svf(0, c, x, out _, out _), out _, out _);
            case SynthFilterType.Hp24:
                {
                    Svf(0, c, x, out _, out var h1);
                    Svf(1, c, h1, out _, out var h2);
                    return h2;
                }

            case SynthFilterType.Bp24:
                {
                    Svf(0, c, x, out var b1, out _);
                    Svf(1, c, b1, out var b2, out _);
                    return b2;
                }

            case SynthFilterType.Ladder:
                return Ladder(c, x);
            case SynthFilterType.CombPlus or SynthFilterType.CombMinus:
                return Comb(c, x);
            case SynthFilterType.Formant:
                {
                    Svf(0, c, x, out var f1, out _);
                    Svf(1, c, x, out var f2, out _);
                    Svf(2, c, x, out var f3, out _);
                    return 1.6f * f1 + 1.1f * f2 + 0.8f * f3;
                }

            default:
                return x;
        }
    }

    // A zero-delay-feedback 4-pole ladder (Zavalishin): the feedback is solved linearly, then saturated.
    private float Ladder(int c, float x)
    {
        var s = _ladder.AsSpan(c * 4, 4);
        var G = _G;
        var inv = 1 / (1 + _g);
        var sum = (G * G * G * s[0] + G * G * s[1] + G * s[2] + s[3]) * inv;
        var g4 = G * G * G * G;
        var u = MathF.Tanh((x - _ladderK * sum) / (1 + _ladderK * g4));
        for (var i = 0; i < 4; i++)
        {
            var v = (u - s[i]) * G;
            var y = v + s[i];
            s[i] = Math.Clamp(y + v, -8, 8);
            u = y;
        }

        return u;
    }

    private float Comb(int c, float x)
    {
        var line = c == 0 ? _combL : _combR;
        if (line is null)
            return x;
        var position = _combWrite - _combDelay;
        if (position < 0)
            position += CombLength;
        var i = (int)position;
        var t = position - i;
        var a = line[i];
        var b = line[i + 1 == CombLength ? 0 : i + 1];
        var delayed = a + (b - a) * t;
        ref var damp = ref c == 0 ? ref _dampL : ref _dampR;
        damp += (delayed - damp) * 0.7f;
        var y = Math.Clamp(x + _combFeedback * damp, -8, 8);
        Dsp.Flush(ref y);
        line[_combWrite] = y;
        if (c == 1)
            _combWrite = _combWrite + 1 == CombLength ? 0 : _combWrite + 1;
        return y;
    }

    /// <summary>Zeroes denormal states (once per control step).</summary>
    public void Flush()
    {
        for (var i = 0; i < _s.Length; i++)
            Dsp.Flush(ref _s[i]);
        for (var i = 0; i < _ladder.Length; i++)
            Dsp.Flush(ref _ladder[i]);
        Dsp.Flush(ref _dampL);
        Dsp.Flush(ref _dampR);
    }
}

/// <summary>A voice's stereo noise: white, pink (Kellet), brown, crackle or key-tracked sample-and-hold, with a colour tilt.</summary>
public struct SynthNoise
{
    private uint _rng;
    private float _p0L, _p1L, _p2L, _p3L, _p4L, _p5L, _p6L;
    private float _p0R, _p1R, _p2R, _p3R, _p4R, _p5R, _p6R;
    private float _brownL, _brownR, _holdL, _holdR, _hold, _tiltL, _tiltR;

    public void Seed(uint seed) => _rng = seed | 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float White()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (int)_rng * (1f / int.MaxValue);
    }

    /// <summary>One stereo sample; <paramref name="holdStep"/> is the S&amp;H rate as a fraction of the sample rate.</summary>
    public void Next(int type, float color, float holdStep, out float left, out float right)
    {
        var wl = White();
        var wr = White();
        switch (type)
        {
            case 1:
                left = Pink(wl, ref _p0L, ref _p1L, ref _p2L, ref _p3L, ref _p4L, ref _p5L, ref _p6L);
                right = Pink(wr, ref _p0R, ref _p1R, ref _p2R, ref _p3R, ref _p4R, ref _p5R, ref _p6R);
                break;
            case 2:
                _brownL = Math.Clamp(_brownL * 0.998f + wl * 0.06f, -1, 1);
                _brownR = Math.Clamp(_brownR * 0.998f + wr * 0.06f, -1, 1);
                left = _brownL * 2.5f;
                right = _brownR * 2.5f;
                break;
            case 3:
                left = MathF.Abs(wl) > 0.995f ? MathF.Sign(wl) : 0;
                right = MathF.Abs(wr) > 0.995f ? MathF.Sign(wr) : 0;
                break;
            case 4:
                _hold += holdStep;
                if (_hold >= 1)
                {
                    _hold -= MathF.Floor(_hold);
                    _holdL = wl;
                    _holdR = wr;
                }

                left = _holdL;
                right = _holdR;
                break;
            default:
                left = wl;
                right = wr;
                break;
        }

        if (MathF.Abs(color) > 0.01f)
        {
            // Tilt: a one-pole low-pass; positive colour adds what it removes (brighter), negative keeps it (darker).
            _tiltL += (left - _tiltL) * 0.15f;
            _tiltR += (right - _tiltR) * 0.15f;
            left = color > 0 ? left + color * (left - _tiltL) : left + (-color) * (_tiltL - left);
            right = color > 0 ? right + color * (right - _tiltR) : right + (-color) * (_tiltR - right);
        }
    }

    private static float Pink(float w, ref float b0, ref float b1, ref float b2, ref float b3, ref float b4, ref float b5, ref float b6)
    {
        b0 = 0.99886f * b0 + w * 0.0555179f;
        b1 = 0.99332f * b1 + w * 0.0750759f;
        b2 = 0.96900f * b2 + w * 0.1538520f;
        b3 = 0.86650f * b3 + w * 0.3104856f;
        b4 = 0.55000f * b4 + w * 0.5329522f;
        b5 = -0.7616f * b5 - w * 0.0168980f;
        var pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362f;
        b6 = w * 0.115926f;
        return pink * 0.11f;
    }

    public void Flush()
    {
        Dsp.Flush(ref _p0L); Dsp.Flush(ref _p1L); Dsp.Flush(ref _p2L); Dsp.Flush(ref _p3L); Dsp.Flush(ref _p4L); Dsp.Flush(ref _p5L);
        Dsp.Flush(ref _p0R); Dsp.Flush(ref _p1R); Dsp.Flush(ref _p2R); Dsp.Flush(ref _p3R); Dsp.Flush(ref _p4R); Dsp.Flush(ref _p5R);
        Dsp.Flush(ref _brownL); Dsp.Flush(ref _brownR); Dsp.Flush(ref _tiltL); Dsp.Flush(ref _tiltR);
    }
}
