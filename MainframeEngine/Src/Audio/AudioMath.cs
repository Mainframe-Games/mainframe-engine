using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>How a positional sound's gain falls off with distance (<see cref="AudioPlayer3D.AttenuationModel"/>).</summary>
/// <remarks>
/// <c>d</c> is the full 3D distance, <c>U</c> the unit size (no attenuation within it), <c>M</c> the max distance,
/// <c>R</c> the rolloff factor. The range-based models fade over <c>[U, M]</c> (with <c>M = 100·U</c> when no
/// max distance is set). Beyond a set max distance the gain is always 0.
/// </remarks>
public enum AttenuationModel
{
    /// <summary>No distance attenuation (panning still applies).</summary>
    Disabled,

    /// <summary><c>U / (U + R·(d − U))</c> — OpenAL's inverse-distance law (−6 dB per doubling at R = 1).</summary>
    Inverse,

    /// <summary>The square of <see cref="Inverse"/> (−12 dB per doubling at R = 1).</summary>
    InverseSquare,

    /// <summary><c>1 − log(d/U) / log(M/U)</c>: loud near the source, reaching silence at the max distance.</summary>
    Logarithmic,

    /// <summary><c>1 − R·(d − U) / (M − U)</c>, clamped.</summary>
    Linear,

    /// <summary><c>(d / U)^−R</c> — OpenAL's exponent-distance law.</summary>
    Exponential,

    /// <summary>A user curve sampled evenly over <c>[0, M]</c> (<see cref="AudioPlayer3D.CustomAttenuationCurve"/>).</summary>
    Custom,
}

/// <summary>Decibel conversions and the positional-audio math shared by the audio nodes, server and tests.</summary>
public static class AudioMath
{
    /// <summary>Volumes at or below this are treated as silence (−80 dB is the inspector minimum).</summary>
    public const float SilenceDb = -80f;

    /// <summary>Linear gain for <paramref name="db"/> (0 at or below <see cref="SilenceDb"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DbToLinear(float db) => db <= SilenceDb || float.IsNaN(db) ? 0f : MathF.Pow(10f, db / 20f);

    /// <summary>Decibels for a linear gain (<see cref="SilenceDb"/> at or below −80 dB).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearToDb(float linear) => linear <= 0.0001f || float.IsNaN(linear) ? SilenceDb : 20f * MathF.Log10(linear);

    /// <summary>
    /// Distance gain in <c>[0, 1]</c>. <paramref name="maxDistance"/> ≤ 0 means unlimited (range-based models then
    /// fade over <c>[U, 100·U]</c>). <paramref name="customCurve"/> is used by <see cref="AttenuationModel.Custom"/>.
    /// </summary>
    public static float Attenuation(AttenuationModel model, float distance, float unitSize, float maxDistance,
        float rolloff = 1f, ReadOnlySpan<float> customCurve = default)
    {
        var d = float.IsFinite(distance) ? Math.Max(distance, 0f) : float.MaxValue;
        if (maxDistance > 0f && d > maxDistance)
            return 0f;

        var u = Math.Max(unitSize, 1e-4f);
        var r = Math.Max(rolloff, 0f);
        var range = maxDistance > u ? maxDistance : 100f * u;
        float gain;
        switch (model)
        {
            case AttenuationModel.Disabled:
                return 1f;
            case AttenuationModel.Inverse:
                gain = d <= u ? 1f : u / (u + r * (d - u));
                break;
            case AttenuationModel.InverseSquare:
                gain = d <= u ? 1f : u / (u + r * (d - u));
                gain *= gain;
                break;
            case AttenuationModel.Logarithmic:
                gain = d <= u ? 1f : 1f - MathF.Log(d / u) / MathF.Log(range / u);
                break;
            case AttenuationModel.Linear:
                gain = d <= u ? 1f : 1f - r * (d - u) / (range - u);
                break;
            case AttenuationModel.Exponential:
                gain = d <= u ? 1f : MathF.Pow(d / u, -r);
                break;
            case AttenuationModel.Custom:
                gain = SampleCurve(customCurve, d / range);
                break;
            default:
                return 1f;
        }

        return float.IsNaN(gain) ? 0f : Math.Clamp(gain, 0f, 1f);
    }

    /// <summary>Linear interpolation over evenly spaced samples at <paramref name="t"/> in [0, 1] (1 when empty).</summary>
    public static float SampleCurve(ReadOnlySpan<float> curve, float t)
    {
        if (curve.IsEmpty)
            return 1f;
        if (curve.Length == 1)
            return curve[0];
        var x = Math.Clamp(t, 0f, 1f) * (curve.Length - 1);
        var i = Math.Min((int)x, curve.Length - 2);
        return curve[i] + (curve[i + 1] - curve[i]) * (x - i);
    }

    /// <summary>
    /// Projects a world-space emitter into listener space (the listener looks down its −Z) and returns the full 3D
    /// distance and the left/right pan in <c>[−1, 1]</c>. The emitter is flattened onto the horizontal listener plane
    /// (<c>(x, −z)</c>, front = +y) for the pan direction only; elevation does not pan but does count toward distance.
    /// The listener's scale is ignored.
    /// </summary>
    public static void ProjectToListener(in Transform3D listener, Vector3 emitterPosition, float panningStrength,
        out float distance, out float pan)
    {
        var rel = emitterPosition - listener.Origin;
        distance = rel.Length();

        var x = Vector3.Dot(rel, NormalizeOr(listener.Basis.X, Vector3.UnitX));
        var z = Vector3.Dot(rel, NormalizeOr(listener.Basis.Z, Vector3.UnitZ));
        var flatLength = MathF.Sqrt(x * x + z * z);
        pan = flatLength > 1e-5f ? Math.Clamp(x / flatLength * panningStrength, -1f, 1f) : 0f;
    }

    /// <summary>
    /// Stereo gains for <paramref name="pan"/> in <c>[−1, 1]</c>: a sine/cosine law normalized so the centre plays at
    /// unity on both channels and a hard pan plays at unity on one (<c>min(1, √2·cos a)</c>, <c>min(1, √2·sin a)</c>,
    /// <c>a = (pan + 1)·π/4</c>). No channel is ever boosted above the source level.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PanGains(float pan, out float left, out float right)
    {
        var a = ((float.IsNaN(pan) ? 0f : Math.Clamp(pan, -1f, 1f)) + 1f) * (MathF.PI / 4f);
        left = Math.Min(1f, MathF.Sqrt(2f) * MathF.Cos(a));
        right = Math.Min(1f, MathF.Sqrt(2f) * MathF.Sin(a));
    }

    /// <summary>
    /// Doppler pitch factor for an emitter and listener moving with the given world velocities
    /// (<c>(c + v_listener→emitter) / (c − v_emitter→listener)</c>, clamped to [0.5, 2]). <paramref name="scale"/>
    /// exaggerates (&gt; 1) or softens (&lt; 1) the effect.
    /// </summary>
    public static float DopplerFactor(Vector3 listenerPosition, Vector3 listenerVelocity, Vector3 emitterPosition,
        Vector3 emitterVelocity, float speedOfSound, float scale = 1f)
    {
        var toEmitter = emitterPosition - listenerPosition;
        var length = toEmitter.Length();
        if (length < 1e-5f || speedOfSound <= 0f || scale <= 0f)
            return 1f;
        var dir = toEmitter / length;
        var c = speedOfSound;
        var listenerTowards = Math.Clamp(Vector3.Dot(listenerVelocity, dir) * scale, -0.5f * c, 0.5f * c);
        var emitterTowards = Math.Clamp(Vector3.Dot(emitterVelocity, -dir) * scale, -0.5f * c, 0.5f * c);
        return Math.Clamp((c + listenerTowards) / (c - emitterTowards), 0.5f, 2f);
    }

    /// <summary>One-pole low-pass coefficient for <paramref name="cutoffHz"/> (1 = filter open).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LowPassCoefficient(float cutoffHz, int sampleRate)
    {
        if (cutoffHz <= 0f || cutoffHz >= sampleRate * 0.45f)
            return 1f;
        return 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / sampleRate);
    }

    private static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
    {
        var lengthSquared = v.LengthSquared();
        return lengthSquared > 1e-12f ? v / MathF.Sqrt(lengthSquared) : fallback;
    }
}
