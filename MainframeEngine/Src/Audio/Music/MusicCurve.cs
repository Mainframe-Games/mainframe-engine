namespace MainframeEngine.Audio.Music;

/// <summary>
/// A compiled <see cref="MusicCurveInfo"/>: a layer's volume (dB) or filter cutoff (Hz) over one parameter. Volume is
/// interpolated in dB, cutoff on a log-frequency scale; over a discrete or labeled parameter the curve is a step
/// function (each value holds the last point at or below it). Evaluation does not allocate.
/// </summary>
public sealed class MusicCurve
{
    /// <summary>The lowest cutoff a curve may ask for.</summary>
    public const float MinCutoffHz = 20f;

    /// <summary>The highest cutoff (and an open low-pass).</summary>
    public const float MaxCutoffHz = 20000f;

    private readonly float[] _x;
    private readonly float[] _y;

    public MusicCurve(int layer, MusicCurveTarget target, int parameter, MusicCurveShape shape, bool stepped, ReadOnlySpan<MusicCurvePoint> points)
    {
        if (points.Length == 0)
            throw new ArgumentException("A curve needs at least one point.", nameof(points));
        Layer = layer;
        Target = target;
        Parameter = parameter;
        Shape = shape;
        Stepped = stepped;
        var sorted = points.ToArray();
        Array.Sort(sorted, static (a, b) => a.X.CompareTo(b.X));
        _x = new float[sorted.Length];
        _y = new float[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            _x[i] = sorted[i].X;
            _y[i] = target == MusicCurveTarget.Cutoff
                ? MathF.Log(Math.Clamp(sorted[i].Y, MinCutoffHz, MaxCutoffHz)) // stored as ln(Hz): log interpolation
                : Math.Clamp(sorted[i].Y, AudioMath.SilenceDb, 24f);
        }
    }

    public int Layer { get; }

    public MusicCurveTarget Target { get; }

    public int Parameter { get; }

    public MusicCurveShape Shape { get; }

    /// <summary>Over a discrete or labeled parameter: a step function.</summary>
    public bool Stepped { get; }

    /// <summary>The curve's value at <paramref name="x"/>: dB for volume, Hz for cutoff.</summary>
    public float Evaluate(float x)
    {
        var y = EvaluateRaw(x);
        return Target == MusicCurveTarget.Cutoff ? MathF.Exp(y) : y;
    }

    private float EvaluateRaw(float x)
    {
        var last = _x.Length - 1;
        if (float.IsNaN(x) || x <= _x[0])
            return _y[0];
        if (x >= _x[last])
            return _y[last];
        var i = 1;
        while (_x[i] < x)
            i++;
        // _x[i - 1] < x <= _x[i]
        if (Stepped)
            return x == _x[i] ? _y[i] : _y[i - 1];
        var span = _x[i] - _x[i - 1];
        var t = span > 0 ? (x - _x[i - 1]) / span : 1f;
        if (Shape == MusicCurveShape.Smooth)
            t = (1f - MathF.Cos(MathF.PI * t)) * 0.5f;
        return _y[i - 1] + (_y[i] - _y[i - 1]) * t;
    }
}
