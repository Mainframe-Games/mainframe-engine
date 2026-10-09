namespace Forest;

/// <summary>
/// A small deterministic random source (SplitMix64): the same seed gives the same sounds and the same bird schedule on
/// every machine. A value type with no allocation, so the per-frame schedulers can own one.
/// </summary>
public struct ForestRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextULong() >> 40) * (1f / (1 << 24));

    /// <summary>Uniform in [-1, 1).</summary>
    public float NextSigned() => NextFloat() * 2f - 1f;

    /// <summary>Uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public float Range(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>Uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public int Range(int min, int max) => max <= min ? min : min + (int)(NextULong() % (ulong)(max - min));

    /// <summary>Log-uniform in [<paramref name="min"/>, <paramref name="max"/>) (frequencies).</summary>
    public float LogRange(float min, float max) => min * MathF.Pow(max / min, NextFloat());

    /// <summary>An exponential interval with mean <paramref name="mean"/> (a Poisson process).</summary>
    public float Exponential(float mean) => -mean * MathF.Log(1f - NextFloat() * 0.999999f);
}

/// <summary>An RBJ biquad (the Audio EQ Cookbook), direct form I in doubles: low-pass, high-pass and band-pass.</summary>
public struct Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public static Biquad LowPass(double hz, double q, double rate)
    {
        var b = new Biquad();
        b.SetLowPass(hz, q, rate);
        return b;
    }

    public static Biquad HighPass(double hz, double q, double rate)
    {
        var b = new Biquad();
        b.SetHighPass(hz, q, rate);
        return b;
    }

    public static Biquad BandPass(double hz, double q, double rate)
    {
        var b = new Biquad();
        b.SetBandPass(hz, q, rate);
        return b;
    }

    public void SetLowPass(double hz, double q, double rate)
    {
        Prepare(hz, q, rate, out var cos, out var alpha);
        Set((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetHighPass(double hz, double q, double rate)
    {
        Prepare(hz, q, rate, out var cos, out var alpha);
        Set((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Band-pass with 0 dB peak gain.</summary>
    public void SetBandPass(double hz, double q, double rate)
    {
        Prepare(hz, q, rate, out var cos, out var alpha);
        Set(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Process(float x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return (float)y;
    }

    private static void Prepare(double hz, double q, double rate, out double cos, out double alpha)
    {
        var w = 2 * Math.PI * Math.Clamp(hz, 1, rate * 0.49) / rate;
        cos = Math.Cos(w);
        alpha = Math.Sin(w) / (2 * Math.Max(q, 1e-3));
    }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}

/// <summary>A one-pole low-pass (smoothing, brown noise, envelopes).</summary>
public struct OnePole(float hz, float rate)
{
    private readonly float _a = 1f - MathF.Exp(-2f * MathF.PI * hz / rate);
    private float _y;

    public float Process(float x) => _y += _a * (x - _y);
}

/// <summary>Helpers for whole buffers: seamless loops, levels and gain.</summary>
public static class SignalTools
{
    /// <summary>
    /// Folds a buffer of <paramref name="loopFrames"/> + <paramref name="fadeFrames"/> frames into a seamless loop of
    /// <paramref name="loopFrames"/> frames: the extra tail is crossfaded (equal power, for uncorrelated noise) into the
    /// start, so the last frame runs on into the first exactly as the signal ran on into its tail.
    /// </summary>
    public static float[] FoldLoop(float[] interleaved, int channels, int loopFrames, int fadeFrames)
    {
        var result = new float[loopFrames * channels];
        Array.Copy(interleaved, result, result.Length);
        for (var i = 0; i < fadeFrames; i++)
        {
            var t = (i + 0.5f) / fadeFrames;
            var fadeIn = MathF.Sin(t * MathF.PI / 2);
            var fadeOut = MathF.Cos(t * MathF.PI / 2);
            for (var c = 0; c < channels; c++)
                result[i * channels + c] = interleaved[i * channels + c] * fadeIn + interleaved[(loopFrames + i) * channels + c] * fadeOut;
        }

        return result;
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
            peak = MathF.Max(peak, MathF.Abs(s));
        return peak;
    }

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return 0f;
        double sum = 0;
        foreach (var s in samples)
            sum += (double)s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Scales <paramref name="samples"/> so their RMS is <paramref name="rms"/>, then limits the peak to <paramref name="maxPeak"/>.</summary>
    public static void NormalizeRms(Span<float> samples, float rms, float maxPeak = 0.9f)
    {
        var current = Rms(samples);
        if (current <= 0f)
            return;
        Scale(samples, rms / current);
        var peak = Peak(samples);
        if (peak > maxPeak)
            Scale(samples, maxPeak / peak);
    }

    /// <summary>Scales <paramref name="samples"/> so their peak is <paramref name="peak"/>.</summary>
    public static void NormalizePeak(Span<float> samples, float peak)
    {
        var current = Peak(samples);
        if (current > 0f)
            Scale(samples, peak / current);
    }

    public static void Scale(Span<float> samples, float gain)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }

    /// <summary>Linear fades over the first and last <paramref name="frames"/> frames (no clicks at the ends of a one-shot).</summary>
    public static void FadeEnds(Span<float> mono, int frames)
    {
        frames = Math.Min(frames, mono.Length / 2);
        for (var i = 0; i < frames; i++)
        {
            var g = (float)i / frames;
            mono[i] *= g;
            mono[^(i + 1)] *= g;
        }
    }
}
