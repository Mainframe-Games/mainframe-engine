namespace MainframeEngine.Editor.Music;

/// <summary>The filter shapes of <see cref="BiquadCoefficients"/> (RBJ Audio EQ Cookbook).</summary>
public enum BiquadShape
{
    LowPass,
    HighPass,
    BandPass,
    Notch,
    Peak,
    LowShelf,
    HighShelf,
    AllPass,

    /// <summary>First-order low-pass (6 dB/oct; Q unused).</summary>
    LowPass1,

    /// <summary>First-order high-pass (6 dB/oct; Q unused).</summary>
    HighPass1,
}

/// <summary>
/// Normalised biquad coefficients (a0 = 1) in double precision (low frequencies at high sample rates stay stable). The
/// same coefficients drive the DSP and <see cref="MagnitudeDb"/>, so a plugin window's curve is exactly what plays.
/// </summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    /// <param name="shape">The filter type.</param>
    /// <param name="frequency">Cutoff or centre in Hz (clamped below Nyquist).</param>
    /// <param name="q">Resonance / bandwidth (shelves: slope-style Q; 0.7071 is Butterworth).</param>
    /// <param name="gainDb">Peak and shelf gain.</param>
    /// <param name="sampleRate">Hz.</param>
    public static BiquadCoefficients Design(BiquadShape shape, double frequency, double q, double gainDb, double sampleRate)
    {
        frequency = Math.Clamp(frequency, 1, sampleRate * 0.49);
        q = Math.Max(q, 0.01);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / (2 * q);
        var a = Math.Pow(10, gainDb / 40);
        double b0, b1, b2, a0, a1, a2;
        switch (shape)
        {
            case BiquadShape.LowPass:
                (b0, b1, b2) = ((1 - cos) / 2, 1 - cos, (1 - cos) / 2);
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadShape.HighPass:
                (b0, b1, b2) = ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2);
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadShape.BandPass:
                (b0, b1, b2) = (alpha, 0, -alpha);
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadShape.Notch:
                (b0, b1, b2) = (1, -2 * cos, 1);
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadShape.AllPass:
                (b0, b1, b2) = (1 - alpha, -2 * cos, 1 + alpha);
                (a0, a1, a2) = (1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadShape.Peak:
                (b0, b1, b2) = (1 + alpha * a, -2 * cos, 1 - alpha * a);
                (a0, a1, a2) = (1 + alpha / a, -2 * cos, 1 - alpha / a);
                break;
            case BiquadShape.LowShelf:
                {
                    var s = 2 * Math.Sqrt(a) * alpha;
                    (b0, b1, b2) = (a * (a + 1 - (a - 1) * cos + s), 2 * a * (a - 1 - (a + 1) * cos), a * (a + 1 - (a - 1) * cos - s));
                    (a0, a1, a2) = (a + 1 + (a - 1) * cos + s, -2 * (a - 1 + (a + 1) * cos), a + 1 + (a - 1) * cos - s);
                    break;
                }
            case BiquadShape.HighShelf:
                {
                    var s = 2 * Math.Sqrt(a) * alpha;
                    (b0, b1, b2) = (a * (a + 1 + (a - 1) * cos + s), -2 * a * (a - 1 + (a + 1) * cos), a * (a + 1 + (a - 1) * cos - s));
                    (a0, a1, a2) = (a + 1 - (a - 1) * cos + s, 2 * (a - 1 - (a + 1) * cos), a + 1 - (a - 1) * cos - s);
                    break;
                }
            case BiquadShape.LowPass1:
                {
                    var k = Math.Tan(w0 / 2);
                    (b0, b1, b2) = (k, k, 0);
                    (a0, a1, a2) = (k + 1, k - 1, 0);
                    break;
                }
            case BiquadShape.HighPass1:
                {
                    var k = Math.Tan(w0 / 2);
                    (b0, b1, b2) = (1, -1, 0);
                    (a0, a1, a2) = (k + 1, k - 1, 0);
                    break;
                }
            default:
                return Identity;
        }

        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>The magnitude response in dB at <paramref name="frequency"/> Hz.</summary>
    public double MagnitudeDb(double frequency, double sampleRate)
    {
        var w = 2 * Math.PI * frequency / sampleRate;
        var (c1, s1) = (Math.Cos(w), Math.Sin(w));
        var (c2, s2) = (Math.Cos(2 * w), Math.Sin(2 * w));
        // H(e^jw) = (b0 + b1 e^-jw + b2 e^-2jw) / (1 + a1 e^-jw + a2 e^-2jw)
        var nr = B0 + B1 * c1 + B2 * c2;
        var ni = -(B1 * s1 + B2 * s2);
        var dr = 1 + A1 * c1 + A2 * c2;
        var di = -(A1 * s1 + A2 * s2);
        var num = nr * nr + ni * ni;
        var den = dr * dr + di * di;
        return den <= 0 ? 0 : 10 * Math.Log10(Math.Max(num / den, 1e-20));
    }
}

/// <summary>A stereo biquad (transposed direct form II; denormals flushed per block).</summary>
public struct StereoBiquad
{
    private double _l1, _l2, _r1, _r2;

    public BiquadCoefficients Coefficients;

    public StereoBiquad() => Coefficients = BiquadCoefficients.Identity;

    public void Process(ref float left, ref float right)
    {
        var c = Coefficients;
        double x = left;
        var y = c.B0 * x + _l1;
        _l1 = c.B1 * x - c.A1 * y + _l2;
        _l2 = c.B2 * x - c.A2 * y;
        left = (float)y;
        x = right;
        y = c.B0 * x + _r1;
        _r1 = c.B1 * x - c.A1 * y + _r2;
        _r2 = c.B2 * x - c.A2 * y;
        right = (float)y;
    }

    public void Process(Span<float> stereo)
    {
        for (var f = 0; f < stereo.Length / 2; f++)
            Process(ref stereo[2 * f], ref stereo[2 * f + 1]);
        FlushState();
    }

    public void FlushState()
    {
        Dsp.Flush(ref _l1);
        Dsp.Flush(ref _l2);
        Dsp.Flush(ref _r1);
        Dsp.Flush(ref _r2);
    }

    public void Reset() => _l1 = _l2 = _r1 = _r2 = 0;
}
