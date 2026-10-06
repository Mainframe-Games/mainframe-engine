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
/// The tonemap and glow of a world (Godot 4.7's <c>Environment</c> tonemap and glow properties, ADR 0124), set on its
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
