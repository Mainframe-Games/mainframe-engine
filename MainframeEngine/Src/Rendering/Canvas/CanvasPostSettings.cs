using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The 2D canvas's post-processing for one frame (ADR 0185), copied from the root viewport's <see cref="CanvasEnvironment"/>
/// by the canvas server. The canvas layers up to <see cref="MaxLayer"/> are drawn into their own target, then glow, the
/// colour adjustments, vignette, chromatic aberration and grain run over it in gamma space (the canvas layer's space), and
/// the layers above <see cref="MaxLayer"/> draw on top untouched. <see cref="Enabled"/> false (the default) skips all of it.
/// </summary>
public readonly record struct CanvasPostSettings
{
    public CanvasPostSettings()
    {
    }

    public bool Enabled { get; init; }

    /// <summary>Canvas layers up to this number are post-processed (Godot's <c>background_canvas_max_layer</c>).</summary>
    public int MaxLayer { get; init; }

    /// <summary>Glow added to the canvas; 0 skips the glow passes.</summary>
    public float GlowIntensity { get; init; }

    /// <summary>Brightness (gamma-space luma of the source) where glow starts.</summary>
    public float GlowThreshold { get; init; } = 0.8f;

    /// <summary>Width of the soft knee below <see cref="GlowThreshold"/>, as a fraction of it (0 = hard cut).</summary>
    public float GlowSoftKnee { get; init; } = 0.5f;

    /// <summary>Weight of each wider glow level (0..1): higher spreads the glow further.</summary>
    public float GlowSpread { get; init; } = 0.7f;

    /// <summary>Godot's <c>adjustment_brightness</c>: multiplies the colour.</summary>
    public float Brightness { get; init; } = 1f;

    /// <summary>Godot's <c>adjustment_contrast</c>: around mid grey.</summary>
    public float Contrast { get; init; } = 1f;

    /// <summary>Godot's <c>adjustment_saturation</c>: 0 is grey, 1 unchanged.</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>Raises the shadows (0 = none), per channel.</summary>
    public Vector3 Lift { get; init; }

    /// <summary>Midtone power, per channel (1 = none; above 1 brightens).</summary>
    public Vector3 Gamma { get; init; } = Vector3.One;

    /// <summary>Scales the highlights, per channel (1 = none).</summary>
    public Vector3 Gain { get; init; } = Vector3.One;

    /// <summary>Multiplies the colour by <c>rgb</c> with strength <c>a</c> (alpha 0 = no tint).</summary>
    public Vector4 Tint { get; init; }

    /// <summary>How much the vignette colour covers the edges (0 = none).</summary>
    public float VignetteStrength { get; init; }

    /// <summary>Distance from the centre (0..~0.7 of the screen height) where the vignette starts.</summary>
    public float VignetteRadius { get; init; } = 0.45f;

    /// <summary>Distance over which the vignette fades in.</summary>
    public float VignetteSoftness { get; init; } = 0.45f;

    /// <summary>The vignette's colour (straight, gamma space).</summary>
    public Vector3 VignetteColor { get; init; }

    /// <summary>Film grain: the largest offset added to a channel (0 = none).</summary>
    public float GrainAmount { get; init; }

    /// <summary>Grain cell size in target pixels.</summary>
    public float GrainSize { get; init; } = 1.5f;

    /// <summary>Red and blue are sampled this far apart toward the edges (UV units at the corner; 0 = none).</summary>
    public float ChromaticAberration { get; init; }

    /// <summary>The default: no post-processing.</summary>
    public static CanvasPostSettings Disabled => new();
}

/// <summary>
/// The colour maths of <c>CanvasPost.vk.frag.slang</c> on the CPU (tests compare rendered pixels against it). Works on
/// straight (not premultiplied) gamma-space colour.
/// </summary>
public static class CanvasPostMath
{
    private static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Adjustments, lift/gamma/gain and tint, in that order (the shader's <c>grade</c>).</summary>
    public static Vector3 Grade(Vector3 rgb, in CanvasPostSettings s)
    {
        rgb *= s.Brightness;
        rgb = Vector3.Lerp(new Vector3(0.5f), rgb, s.Contrast);
        var luma = Vector3.Dot(rgb, Luma);
        rgb = Vector3.Lerp(new Vector3(luma), rgb, s.Saturation);
        rgb = Vector3.Max(rgb * s.Gain + s.Lift * (Vector3.One - rgb), Vector3.Zero);
        rgb = new Vector3(MathF.Pow(rgb.X, 1f / s.Gamma.X), MathF.Pow(rgb.Y, 1f / s.Gamma.Y), MathF.Pow(rgb.Z, 1f / s.Gamma.Z));
        rgb *= Vector3.Lerp(Vector3.One, new Vector3(s.Tint.X, s.Tint.Y, s.Tint.Z), s.Tint.W);
        return rgb;
    }

    /// <summary>
    /// The vignette's cover at <paramref name="uv"/> (0..1 over the target) for a target of <paramref name="aspect"/>
    /// (width / height): 0 in the middle, up to <see cref="CanvasPostSettings.VignetteStrength"/> at the corners.
    /// </summary>
    public static float Vignette(Vector2 uv, float aspect, in CanvasPostSettings s)
    {
        var d = (uv - new Vector2(0.5f)) * new Vector2(aspect, 1f);
        var t = SmoothStep(s.VignetteRadius, s.VignetteRadius + MathF.Max(s.VignetteSoftness, 1e-4f), d.Length());
        return t * s.VignetteStrength;
    }

    /// <summary>The glow bright-pass weight of a colour (soft knee below the threshold, linear above it).</summary>
    public static float BrightPass(Vector3 rgb, float threshold, float softKnee)
    {
        var luma = Vector3.Dot(rgb, Luma);
        var knee = MathF.Max(threshold * softKnee, 1e-4f);
        var soft = Math.Clamp(luma - threshold + knee, 0f, 2f * knee);
        soft = soft * soft / (4f * knee);
        var contribution = MathF.Max(soft, luma - threshold);
        return contribution / MathF.Max(luma, 1e-4f);
    }

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
