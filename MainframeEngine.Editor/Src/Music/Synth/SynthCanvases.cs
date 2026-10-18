using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// An oscillator's wavetable in the synth window: up to 32 frames stacked in a pseudo-3D view (the frame at the current
/// position bright, in front) or the current frame alone (flat). Each frame is reduced to 128 points once per table.
/// Drawn into a sub-viewport only when the table, position, view or size changed.
/// </summary>
public sealed class WavetableCanvas : EditorCanvas
{
    private const int Points = 128;
    private const int MaxShown = 32;
    private static readonly ConditionalWeakTable<Wavetable, float[][]> Previews = [];

    public Wavetable? Table { get; set; }

    /// <summary>0…1 across the frames.</summary>
    public float FramePosition { get; set; }

    public bool Flat { get; set; }

    public Vector4 Color { get; set; } = Rgb(96, 165, 250);

    private static float[][] Preview(Wavetable table) => Previews.GetValue(table, static t =>
    {
        var frames = new float[t.Frames][];
        for (var f = 0; f < t.Frames; f++)
        {
            var samples = t.Frame(0, f);
            var step = samples.Length / Points;
            frames[f] = new float[Points];
            for (var i = 0; i < Points; i++)
                frames[f][i] = samples[i * step];
        }

        return frames;
    });

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, h, Rgb(21, 23, 28));
        if (Table is not { } table)
            return;
        var preview = Preview(table);
        var current = Math.Clamp((int)MathF.Round(FramePosition * (table.Frames - 1)), 0, table.Frames - 1);
        if (Flat || table.Frames == 1)
        {
            Line(4, h / 2, w - 4, h / 2, Rgb(52, 56, 66));
            Wave(preview[current], 4, h / 2, w - 8, h * 0.42f, Color, 1.6f);
            return;
        }

        // Stacked: frames from the back (top right) to the front (bottom left); the current one drawn last.
        var shown = Math.Min(MaxShown, table.Frames);
        var depthX = w * 0.18f;
        var depthY = h * 0.42f;
        var width = w - depthX - 8;
        var amp = h * 0.2f;
        for (var k = shown - 1; k >= 0; k--)
        {
            var f = shown == 1 ? 0 : (int)MathF.Round(k * (table.Frames - 1) / (float)(shown - 1));
            var t = shown == 1 ? 0 : k / (float)(shown - 1);
            Wave(preview[f], 4 + depthX * t, h - amp - 6 - depthY * t, width, amp, WithAlpha(Color, 0.16f + 0.1f * (1 - t)), 1);
        }

        var ct = table.Frames == 1 ? 0 : current / (float)(table.Frames - 1);
        Wave(preview[current], 4 + depthX * ct, h - amp - 6 - depthY * ct, width, amp, Color, 1.8f);
    }

    private void Wave(float[] points, float x, float mid, float width, float amp, Vector4 color, float thickness)
    {
        for (var i = 1; i < points.Length; i++)
            Line(x + width * (i - 1) / (points.Length - 1), mid - points[i - 1] * amp, x + width * i / (points.Length - 1), mid - points[i] * amp, color, thickness);
    }
}

/// <summary>The synth's two filters as magnitude curves (at middle C, so key tracking does not move them), log 20 Hz–20 kHz, ±24 dB.</summary>
public sealed class SynthFilterCanvas : EditorCanvas
{
    private const int Points = 200;
    private const double Rate = 48000;
    private readonly float[] _curve = new float[Points];

    public IReadOnlyDictionary<string, float>? Values { get; set; }

    public static readonly Vector4 F1Color = Rgb(251, 146, 60);
    public static readonly Vector4 F2Color = Rgb(34, 211, 238);

    private float X(double hz) => (float)(ViewSize.X * Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000));

    private float Y(double db) => ViewSize.Y * 0.5f * (1 - (float)Math.Clamp(db, -30, 30) / 24);

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, h, Rgb(21, 23, 28));
        foreach (var hz in (ReadOnlySpan<double>)[100, 1000, 10000])
            Line(X(hz), 0, X(hz), h, Rgb(44, 48, 57));
        Line(0, Y(0), w, Y(0), Rgb(52, 56, 66));
        Curve(SynthParams.F1, F1Color);
        Curve(SynthParams.F2, F2Color);
    }

    private void Curve(in FilterParams p, Vector4 color)
    {
        if (SynthParams.Value(Values, p.On) < 0.5f)
            return;
        var type = (SynthFilterType)(int)SynthParams.Value(Values, p.Type);
        var cutoff = SynthParams.Value(Values, p.Cutoff);
        var reso = SynthParams.Value(Values, p.Reso);
        var w = ViewSize.X;
        for (var i = 0; i < Points; i++)
        {
            var hz = 20 * Math.Pow(1000, i / (double)(Points - 1));
            _curve[i] = Y(ResponseDb(type, cutoff, reso, hz, Rate));
        }

        for (var i = 1; i < Points; i++)
            Line(w * (i - 1) / (Points - 1), _curve[i - 1], w * i / (Points - 1), _curve[i], color, 1.8f);
    }

    /// <summary>An approximation of a synth filter's magnitude response in dB (for display).</summary>
    public static double ResponseDb(SynthFilterType type, double cutoff, double reso, double hz, double rate)
    {
        var q12 = 0.7071 + reso * reso * 24;
        double Biquad(BiquadShape shape, double q) => BiquadCoefficients.Design(shape, cutoff, q, 0, rate).MagnitudeDb(hz, rate);
        switch (type)
        {
            case SynthFilterType.Lp12: return Biquad(BiquadShape.LowPass, q12);
            case SynthFilterType.Hp12: return Biquad(BiquadShape.HighPass, q12);
            case SynthFilterType.Bp12: return Biquad(BiquadShape.BandPass, q12);
            case SynthFilterType.Notch: return Biquad(BiquadShape.Notch, q12);
            case SynthFilterType.Peak: return Biquad(BiquadShape.Peak, q12) + 6 * reso;
            case SynthFilterType.Lp24: return Biquad(BiquadShape.LowPass, 0.5412) + Biquad(BiquadShape.LowPass, q12);
            case SynthFilterType.Hp24: return Biquad(BiquadShape.HighPass, 0.5412) + Biquad(BiquadShape.HighPass, q12);
            case SynthFilterType.Bp24: return Biquad(BiquadShape.BandPass, q12) * 2;
            case SynthFilterType.Ladder: return Biquad(BiquadShape.LowPass, 0.5412) + Biquad(BiquadShape.LowPass, 0.5 + reso * 9);
            case SynthFilterType.CombPlus or SynthFilterType.CombMinus:
                {
                    var delay = rate / Math.Max(cutoff, 40);
                    var g = (type == SynthFilterType.CombPlus ? 1 : -1) * 0.98 * reso;
                    var w = 2 * Math.PI * hz / rate * delay;
                    var re = 1 - g * Math.Cos(w);
                    var im = g * Math.Sin(w);
                    return -10 * Math.Log10(re * re + im * im);
                }

            case SynthFilterType.Formant:
                {
                    // Three band-passes at the vowel the cutoff points to.
                    (double F1, double F2, double F3)[] vowels = [(800, 1150, 2900), (400, 1600, 2700), (350, 1700, 2700), (450, 800, 2830), (325, 700, 2530)];
                    var pos = Math.Clamp(Math.Log(cutoff / 20) / Math.Log(1000), 0, 1) * (vowels.Length - 1);
                    var i = Math.Min(vowels.Length - 2, (int)pos);
                    var t = pos - i;
                    var q = 2 + reso * 18;
                    double Band(double f, double gain) => gain * Math.Pow(10, BiquadCoefficients.Design(BiquadShape.BandPass, f, q, 0, rate).MagnitudeDb(hz, rate) / 20);
                    var sum = Band(vowels[i].F1 + (vowels[i + 1].F1 - vowels[i].F1) * t, 1.6) + Band(vowels[i].F2 + (vowels[i + 1].F2 - vowels[i].F2) * t, 1.1) +
                          Band(vowels[i].F3 + (vowels[i + 1].F3 - vowels[i].F3) * t, 0.8);
                    return 20 * Math.Log10(Math.Max(sum, 1e-6));
                }

            default:
                return 0;
        }
    }
}

/// <summary>
/// The synth window's modulation editor: an envelope (DAHDSR on a logarithmic time axis, with draggable handles at the
/// ends of attack, decay/sustain and release) or an LFO's cycle (built-in shapes, or a drawn shape with its points).
/// </summary>
public sealed class SynthModCanvas : EditorCanvas
{
    private const float HandleRadius = 5;
    private const int Points = 160;

    /// <summary>0–3: envelope 1–4; 4–7: LFO 1–4.</summary>
    public int Source { get; set; }

    public IReadOnlyDictionary<string, float>? Values { get; set; }

    /// <summary>The drawn shapes (LFO n's points), when drawn.</summary>
    public SynthLfoShape? Drawn { get; set; }

    public int SelectedPoint { get; set; } = -1;

    public Vector4 Color { get; set; } = Rgb(167, 139, 250);

    public bool IsEnvelope => Source < 4;

    // Envelope time axis: each segment's width grows with log(1 + 100 t), 30 s = a fifth of the view.
    private float SegmentWidth(float seconds) => ViewSize.X / 5 * MathF.Log(1 + MathF.Max(0, seconds) * 100) / MathF.Log(3001);

    /// <summary>The seconds a segment of <paramref name="width"/> dp stands for (the inverse of the time axis).</summary>
    public float SegmentSeconds(float width) => (MathF.Exp(Math.Max(0, width) / (ViewSize.X / 5) * MathF.Log(3001)) - 1) / 100;

    /// <summary>Envelope handle positions (dp): the end of attack, of decay (at sustain) and of release; and where each segment starts.</summary>
    public (Vector2 Attack, Vector2 Decay, Vector2 Release, float AttackStart, float DecayStart, float ReleaseStart) EnvelopeHandles()
    {
        var e = SynthParams.Env[Source];
        var h = ViewSize.Y;
        var top = 8f;
        var bottom = h - 8;
        var x = 6 + SegmentWidth(Value(e.Delay));
        var attackStart = x;
        x += SegmentWidth(Value(e.Attack));
        var attack = new Vector2(x, top);
        x += SegmentWidth(Value(e.Hold));
        var decayStart = x;
        x += SegmentWidth(Value(e.Decay));
        var sustain = Value(e.Sustain);
        var decay = new Vector2(x, bottom - (bottom - top) * sustain);
        x += ViewSize.X / 10; // the sustain plateau
        var releaseStart = x;
        x += SegmentWidth(Value(e.Release));
        return (attack, decay, new Vector2(x, bottom), attackStart, decayStart, releaseStart);
    }

    private float Value(int index) => SynthParams.Value(Values, index);

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, h, Rgb(21, 23, 28));
        if (IsEnvelope)
            DrawEnvelope(h);
        else
            DrawLfo(w, h);
    }

    private void DrawEnvelope(float h)
    {
        var e = SynthParams.Env[Source];
        var top = 8f;
        var bottom = h - 8;
        var (attack, decay, release, attackStart, decayStart, releaseStart) = EnvelopeHandles();
        var line = Color;
        Line(6, bottom, attackStart, bottom, line, 1.6f);
        Segment(attackStart, bottom, attack.X, top, Value(e.AttackCurve), rising: true);
        Line(attack.X, top, decayStart, top, line, 1.6f);
        Segment(decayStart, top, decay.X, decay.Y, Value(e.DecayCurve), rising: false);
        Line(decay.X, decay.Y, releaseStart, decay.Y, line, 1.6f);
        Segment(releaseStart, decay.Y, release.X, bottom, Value(e.ReleaseCurve), rising: false);
        foreach (var (p, i) in (ReadOnlySpan<(Vector2, int)>)[(attack, 0), (decay, 1), (release, 2)])
            Dot(p, i == SelectedPoint);
    }

    // A curved segment as SynthEnvelope draws its values: progress bent by the curve.
    private void Segment(float x0, float y0, float x1, float y1, float curve, bool rising)
    {
        const int Steps = 24;
        var px = x0;
        var py = y0;
        for (var i = 1; i <= Steps; i++)
        {
            var t = i / (float)Steps;
            var s = SynthEnvelope.Curve(t, curve);
            var x = x0 + (x1 - x0) * t;
            var y = y0 + (y1 - y0) * s;
            Line(px, py, x, y, Color, 1.6f);
            (px, py) = (x, y);
        }
    }

    private void DrawLfo(float w, float h)
    {
        var n = Source - 4;
        var shape = (int)Value(SynthParams.Lfo[n].Shape);
        var drawn = shape == (int)SynthLfoWave.Drawn && Drawn is { Points.Count: > 0 } ? WavetableSynth.BakeShape(Drawn) : null;
        Line(0, h / 2, w, h / 2, Rgb(44, 48, 57));
        float? previous = null;
        for (var i = 0; i < Points; i++)
        {
            var p = i / (float)(Points - 1);
            var v = drawn is not null ? drawn[Math.Min(drawn.Length - 1, (int)(p * drawn.Length))] : ShapeValue(shape, p);
            var y = 6 + (h - 12) * (1 - v);
            var x = w * p;
            if (previous is { } py)
                Line(w * (i - 1) / (Points - 1), py, x, y, Color, 1.6f);
            previous = y;
        }

        if (drawn is null || Drawn is null)
            return;
        for (var i = 0; i < Drawn.Points.Count; i++)
            Dot(PointAt(i), i == SelectedPoint);
    }

    /// <summary>A drawn LFO point's position (dp).</summary>
    public Vector2 PointAt(int index)
    {
        var p = Drawn!.Points[index];
        return new Vector2(ViewSize.X * Math.Clamp(p[0], 0, 1), 6 + (ViewSize.Y - 12) * (1 - Math.Clamp(p[1], 0, 1)));
    }

    /// <summary>The LFO value (0–1) of a built-in shape at phase <paramref name="p"/> (random shapes as a fixed pattern).</summary>
    public static float ShapeValue(int shape, float p) => shape switch
    {
        1 => p < 0.5f ? 2 * p : 2 - 2 * p,
        2 => p,
        3 => 1 - p,
        4 => p < 0.5f ? 1 : 0,
        5 => (int)(p * 8) switch { 0 => 0.7f, 1 => 0.2f, 2 => 0.9f, 3 => 0.4f, 4 => 0.6f, 5 => 0.1f, 6 => 0.8f, _ => 0.3f },
        6 => 0.5f + 0.35f * MathF.Sin(2 * MathF.PI * p) * MathF.Cos(5 * MathF.PI * p),
        _ => 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * p),
    };

    private void Dot(Vector2 p, bool selected)
    {
        var r = (selected ? HandleRadius + 2 : HandleRadius) * PixelScale;
        DrawCircle(p * PixelScale, r, Color);
        DrawCircle(p * PixelScale, r * 0.45f, Rgb(21, 23, 28));
    }
}
