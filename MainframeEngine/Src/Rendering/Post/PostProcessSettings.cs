namespace MainframeEngine;

/// <summary>Which tonemap curve turns the HDR scene into display values (ADR 0124).</summary>
public enum Tonemapper
{
    /// <summary>The engine's curve: Stephen Hill's ACES fit, scaled by the project exposure (<c>rendering.exposure</c>).</summary>
    Engine,

    /// <summary>
    /// Godot 4.7's ACES (<c>tonemap_mode = ACES</c>): the same fit with Godot's 1.8 input bias and its output divided by
    /// the curve at <see cref="PostProcessSettings.TonemapWhite"/>, scaled by <see cref="PostProcessSettings.TonemapExposure"/>.
    /// </summary>
    GodotAces,
}

/// <summary>How glow is combined with the scene (Godot's <c>Environment.GlowBlendMode</c>, same ordinals).</summary>
public enum GlowBlendMode
{
    Additive,
    Screen,
    SoftLight,
    Replace,
    Mix,
}

/// <summary>
/// The tonemap, glow, auto exposure and light shafts of a world (Godot 4.7's <c>Environment</c> tonemap and glow properties, ADR 0124), set on its
/// <see cref="WorldEnvironment"/> and applied by the renderer's tonemap pass. <see cref="Default"/> is the engine's
/// behaviour before ADR 0124: its own curve and no glow.
/// </summary>
public readonly record struct PostProcessSettings
{
    public const int GlowLevelCount = 7;

    public static PostProcessSettings Default { get; } = new();

    public PostProcessSettings()
    {
    }

    public Tonemapper Tonemapper { get; init; } = Tonemapper.Engine;

    /// <summary>Exposure for <see cref="MainframeEngine.Tonemapper.GodotAces"/> (Godot's <c>tonemap_exposure</c>, default 1).</summary>
    public float TonemapExposure { get; init; } = 1f;

    /// <summary>White point for <see cref="MainframeEngine.Tonemapper.GodotAces"/> (Godot's <c>tonemap_white</c>; ACES uses at least 1).</summary>
    public float TonemapWhite { get; init; } = 1f;

    public bool GlowEnabled { get; init; }

    // Godot's glow_levels/1..7 defaults (4.7): 0, 0.8, 0.4, 0.1, 0, 0, 0.
    public float GlowLevel1 { get; init; }
    public float GlowLevel2 { get; init; } = 0.8f;
    public float GlowLevel3 { get; init; } = 0.4f;
    public float GlowLevel4 { get; init; } = 0.1f;
    public float GlowLevel5 { get; init; }
    public float GlowLevel6 { get; init; }
    public float GlowLevel7 { get; init; }

    /// <summary>Divide the levels by their sum (Godot's <c>glow_normalized</c>).</summary>
    public bool GlowNormalized { get; init; }

    public float GlowIntensity { get; init; } = 0.3f;

    /// <summary>Multiplies every blur pass (Godot's <c>glow_strength</c>): level k carries <c>strength^(k+1)</c>.</summary>
    public float GlowStrength { get; init; } = 1f;

    /// <summary>The glow share in <see cref="GlowBlendMode.Mix"/> (Godot's <c>glow_mix</c>).</summary>
    public float GlowMix { get; init; } = 0.05f;

    /// <summary>Minimum glow feedback below the threshold (Godot's <c>glow_bloom</c>).</summary>
    public float GlowBloom { get; init; }

    public GlowBlendMode GlowBlendMode { get; init; } = GlowBlendMode.Screen;

    public float GlowHdrThreshold { get; init; } = 1f;
    public float GlowHdrScale { get; init; } = 2f;
    public float GlowHdrLuminanceCap { get; init; } = 12f;

    /// <summary>
    /// Eye adaptation (ADR 0154): the scene's average log luminance, measured on the GPU each frame, adapts over time and
    /// sets the exposure to <see cref="AutoExposureScale"/> / luminance. The manual exposure (<c>rendering.exposure</c> or
    /// <see cref="TonemapExposure"/>) still multiplies it, as compensation.
    /// </summary>
    public bool AutoExposureEnabled { get; init; }

    /// <summary>The luminance an average scene is exposed to (middle grey; Godot's <c>auto_exposure_scale</c>, 0.4).</summary>
    public float AutoExposureScale { get; init; } = 0.4f;

    /// <summary>Adaptation rate per second (Godot's <c>auto_exposure_speed</c>, 0.5): each frame closes <c>1 − e^(−dt·speed)</c> of the gap.</summary>
    public float AutoExposureSpeed { get; init; } = 0.5f;

    /// <summary>The darkest average luminance adapted to: darker scenes stay darker (the most exposure is Scale / this).</summary>
    public float AutoExposureMinLuminance { get; init; } = 0.05f;

    /// <summary>The brightest average luminance adapted to: brighter scenes stay brighter (the least exposure is Scale / this).</summary>
    public float AutoExposureMaxLuminance { get; init; } = 2f;

    /// <summary>
    /// Screen-space light shafts (ADR 0160): sky pixels around the sun (the world's first <see cref="DirectionalLight3D"/>),
    /// blurred radially towards its screen position and added to the HDR scene before the tonemap, alongside glow. They
    /// need the sun on screen or near it, and fade out as it leaves.
    /// </summary>
    public bool LightShaftsEnabled { get; init; }

    /// <summary>Multiplies the shafts added to the scene (in scene radiance, before exposure).</summary>
    public float LightShaftsIntensity { get; init; } = 1f;

    /// <summary>
    /// The weight left after each sixteenth of a ray (GPU Gems 3's per-sample decay at 16 samples, independent of
    /// <see cref="LightShaftsSamples"/>): a shaft at the far end of its ray keeps <c>decay^16</c>. Lower is shorter.
    /// </summary>
    public float LightShaftsDecay { get; init; } = 0.96f;

    /// <summary>The fraction of the way to the sun each pixel's ray covers (GPU Gems 3's density, 0–1): longer shafts, coarser taps.</summary>
    public float LightShaftsDensity { get; init; } = 0.8f;

    /// <summary>Taps per blur pass (clamped to 4–64); the two passes give <c>samples²</c> effective taps per ray.</summary>
    public int LightShaftsSamples { get; init; } = 16;

    /// <summary>Taps per light-shaft blur pass: <see cref="LightShaftsSamples"/> clamped to [4, 64].</summary>
    public int LightShaftsTapsPerPass => Math.Clamp(LightShaftsSamples, 4, 64);

    /// <summary>
    /// The step and per-tap decay of light-shaft blur pass <paramref name="pass"/> (0: fine, 1: coarse). Each pixel's ray
    /// towards the sun is <see cref="LightShaftsDensity"/> of the way there, covered by <c>n²</c> effective taps for
    /// <c>n</c> = <see cref="LightShaftsTapsPerPass"/>: the fine pass steps <c>density / n²</c> with decay
    /// <c>decay^(16 / n²)</c>, the coarse pass <c>density / n</c> with <c>decay^(16 / n)</c>, so tap <c>i·n + j</c> of the
    /// combined ray weighs <c>decay^(16 (i·n + j) / n²)</c>.
    /// </summary>
    public (float Step, float TapDecay) LightShaftsPass(int pass)
    {
        var n = (float)LightShaftsTapsPerPass;
        var taps = pass == 0 ? n * n : n;
        var density = Math.Clamp(LightShaftsDensity, 0f, 1f);
        var decay = Math.Clamp(LightShaftsDecay, 0f, 1f);
        return (density / taps, MathF.Pow(decay, 16f / taps));
    }

    /// <summary>The fraction of the gap to the measured luminance that auto exposure closes over <paramref name="deltaTime"/> seconds.</summary>
    public float AutoExposureBlend(float deltaTime) =>
        Math.Clamp(1f - MathF.Exp(-MathF.Max(deltaTime, 0f) * MathF.Max(AutoExposureSpeed, 0f)), 0f, 1f);

    /// <summary>The exposure auto exposure applies for an adapted average <paramref name="luminance"/> (before compensation).</summary>
    public float AutoExposureFor(float luminance) =>
        AutoExposureScale / Math.Clamp(luminance, MathF.Max(AutoExposureMinLuminance, 1e-6f), MathF.Max(AutoExposureMaxLuminance, AutoExposureMinLuminance));

    /// <summary>The level weights the tonemap uses: as set, or divided by their sum when <see cref="GlowNormalized"/> (Godot's <c>_update_glow</c>).</summary>
    public void GetGlowWeights(Span<float> weights)
    {
        if (weights.Length < GlowLevelCount)
            throw new ArgumentException($"needs {GlowLevelCount} entries", nameof(weights));
        weights[0] = GlowLevel1;
        weights[1] = GlowLevel2;
        weights[2] = GlowLevel3;
        weights[3] = GlowLevel4;
        weights[4] = GlowLevel5;
        weights[5] = GlowLevel6;
        weights[6] = GlowLevel7;
        if (!GlowNormalized)
            return;
        var sum = 0f;
        for (var i = 0; i < GlowLevelCount; i++)
            sum += weights[i];
        if (sum > 0f)
            for (var i = 0; i < GlowLevelCount; i++)
                weights[i] /= sum;
    }

    /// <summary>The highest level with a weight above 0.01 (Godot's <c>max_glow_index</c>), or -1 when glow draws nothing.</summary>
    public int GlowMaxLevel
    {
        get
        {
            if (!GlowEnabled)
                return -1;
            Span<float> w = stackalloc float[GlowLevelCount];
            GetGlowWeights(w);
            for (var i = GlowLevelCount - 1; i >= 0; i--)
                if (w[i] > 0.01f)
                    return i;
            return -1;
        }
    }

    /// <summary>Godot's ACES <c>white_tonemapped</c>: the curve at <c>1.8 · max(1, white)</c>, which output is divided by.</summary>
    public float GodotAcesWhiteTonemapped
    {
        get
        {
            var white = MathF.Max(1f, TonemapWhite) * 1.8f;
            return (white * (white + 0.0245786f) - 0.000090537f) / (white * (0.983729f * white + 0.432951f) + 0.238081f);
        }
    }

    /// <summary>The white point the glow blend uses (Godot's <c>environment_get_white</c> for ACES; 1 for the engine curve).</summary>
    public float GlowWhite => Tonemapper == Tonemapper.GodotAces ? MathF.Max(1f, TonemapWhite) : 1f;
}
