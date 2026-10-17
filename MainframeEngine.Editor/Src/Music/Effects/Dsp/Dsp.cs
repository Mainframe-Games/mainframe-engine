namespace MainframeEngine.Editor.Music;

/// <summary>Small helpers shared by the built-in effects (render thread, no allocation).</summary>
public static class Dsp
{
    /// <summary>Below this, recursive state is flushed to zero (.NET cannot portably enable flush-to-zero).</summary>
    public const float DenormalThreshold = 1e-15f;

    public static float DbToGain(float db) => MathF.Pow(10f, db / 20f);

    public static float GainToDb(float gain) => gain > 1e-10f ? 20f * MathF.Log10(gain) : -200f;

    /// <summary>Zeroes a value that has decayed into the denormal range (recursive filter and feedback state).</summary>
    public static void Flush(ref float value)
    {
        if (MathF.Abs(value) < DenormalThreshold)
            value = 0;
    }

    public static void Flush(ref double value)
    {
        if (Math.Abs(value) < DenormalThreshold)
            value = 0;
    }

    /// <summary>One-pole coefficient for a time constant of <paramref name="seconds"/> per sample (0 = instant).</summary>
    public static float TimeCoefficient(float seconds, float sampleRate) =>
        seconds <= 0 ? 0f : MathF.Exp(-1f / (seconds * sampleRate));
}

/// <summary>
/// A per-block smoother for parameters that drive coefficients (frequencies, gains): moves its value toward a target with
/// a ~20 ms time constant once per call, and reports when it has settled so coefficients are recomputed only while moving.
/// </summary>
public struct BlockSmoother
{
    private float _target;
    private bool _started;

    public float Value { get; private set; }

    public readonly bool Settled => Value == _target;

    /// <summary>The first target is taken at once (no glide from zero when an effect starts).</summary>
    public void SetTarget(float target)
    {
        _target = target;
        if (_started)
            return;
        _started = true;
        Value = target;
    }

    /// <summary>Advances by <paramref name="frames"/>; returns whether the value changed.</summary>
    public bool Advance(int frames, float sampleRate, float seconds = 0.02f)
    {
        if (Value == _target)
            return false;
        var k = MathF.Exp(-frames / (seconds * sampleRate));
        var next = _target + (Value - _target) * k;
        Value = MathF.Abs(next - _target) <= 1e-4f * MathF.Max(1f, MathF.Abs(_target)) ? _target : next;
        return true;
    }

    /// <summary>Jumps to the target.</summary>
    public void Snap() => Value = _target;
}

/// <summary>A per-sample linear ramp from the last block's value to the new target over one block (gains, mixes).</summary>
public struct LinearRamp
{
    private float _current;
    private float _target;
    private bool _started;

    public void SetTarget(float target)
    {
        _target = target;
        if (_started)
            return;
        _started = true;
        _current = target;
    }

    /// <summary>The per-frame step for a block of <paramref name="frames"/>; call <see cref="End"/> after it.</summary>
    public readonly float Step(int frames) => frames > 0 ? (_target - _current) / frames : 0;

    public readonly float Current => _current;

    public readonly float Target => _target;

    public void End() => _current = _target;
}
