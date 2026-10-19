namespace MainframeEngine.Audio.Dsp;

/// <summary>
/// A state-variable filter in Zavalishin's topology-preserving form (as in Andrew Simper's "SvfLinearTrapOptimised2"):
/// stable while its cutoff moves every few samples, so LFO-swept filters and phasers stay clean. One channel.
/// </summary>
/// <remarks>
/// Shared by the editor's built-in effects and the adaptive-music runtime's layer filter, so a layer sounds the same in
/// the editor's live preview and in a game (docs/design/future/adaptive-music.md, Curve targets). Allocation free.
/// </remarks>
public struct Svf
{
    /// <summary>Q of a 12 dB/oct Butterworth response (no resonant peak).</summary>
    public const float ButterworthQ = 0.70710677f;

    private float _ic1, _ic2, _a1, _a2, _a3, _k;

    /// <summary>Sets cutoff (Hz) and Q.</summary>
    public void Set(float frequency, float q, float sampleRate)
    {
        var g = MathF.Tan(MathF.PI * Math.Clamp(frequency, 5f, sampleRate * 0.49f) / sampleRate);
        _k = 1f / MathF.Max(q, 0.05f);
        _a1 = 1f / (1f + g * (g + _k));
        _a2 = g * _a1;
        _a3 = g * _a2;
    }

    /// <summary>Takes <paramref name="other"/>'s cutoff and Q, keeping this filter's state (one <see cref="Set"/> for several channels).</summary>
    public void CopyCoefficients(in Svf other) => (_a1, _a2, _a3, _k) = (other._a1, other._a2, other._a3, other._k);

    /// <summary>One sample; returns low-pass and writes band-pass and high-pass.</summary>
    public float Process(float input, out float bandPass, out float highPass)
    {
        var v3 = input - _ic2;
        var v1 = _a1 * _ic1 + _a2 * v3;
        var v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2 * v1 - _ic1;
        _ic2 = 2 * v2 - _ic2;
        bandPass = v1;
        highPass = input - _k * v1 - v2;
        return v2;
    }

    /// <summary>The all-pass output (phasers).</summary>
    public float AllPass(float input)
    {
        var low = Process(input, out var band, out var high);
        return low - _k * band + high;
    }

    /// <summary>Flushes denormal state to zero (call once per block in feedback-heavy chains).</summary>
    public void Flush()
    {
        FlushDenormal(ref _ic1);
        FlushDenormal(ref _ic2);
    }

    public void Reset() => _ic1 = _ic2 = 0;

    private static void FlushDenormal(ref float value)
    {
        if (MathF.Abs(value) < 1e-15f)
            value = 0;
    }
}
