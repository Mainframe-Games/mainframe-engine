using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// sRGB ↔ linear conversions (IEC 61966-2-1), matching <c>include/common.glsl</c>. The engine lights and blends in
/// linear space; colours authored by people (pickers, <see cref="System.Drawing.Color"/>, light and sky colours)
/// are sRGB and are converted once, where they enter the pipeline. See docs/design/color-pipeline.md.
/// </summary>
public static class ColorSpace
{
    /// <summary>One sRGB-encoded channel (0..1) to linear.</summary>
    public static float SrgbToLinear(float c)
    {
        if (c <= 0f) return 0f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>One linear channel to sRGB encoding (clamped at 0; values above 1 extrapolate).</summary>
    public static float LinearToSrgb(float c)
    {
        if (c <= 0f) return 0f;
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    /// <summary>An sRGB colour to linear (per channel).</summary>
    public static Vector3 SrgbToLinear(Vector3 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));

    /// <summary>An sRGB colour to linear; alpha is already linear and is kept.</summary>
    public static Vector4 SrgbToLinear(Vector4 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z), c.W);

    /// <summary>A linear colour to sRGB encoding (per channel).</summary>
    public static Vector3 LinearToSrgb(Vector3 c) => new(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z));

    /// <summary>
    /// The ACES filmic tonemap used by the renderer (Stephen Hill's fit of the RRT + sRGB ODT, applied in linear
    /// Rec.709) for one linear RGB colour; output is linear display-referred in [0, 1]. Mirrors <c>Tonemap.vk.frag</c>;
    /// exposed for tests and tools.
    /// </summary>
    public static Vector3 AcesFitted(Vector3 color)
    {
        // Input: sRGB/Rec.709 → ACES AP1 with the RRT saturation folded in (rows of Hill's ACESInputMat).
        var v = new Vector3(
            0.59719f * color.X + 0.35458f * color.Y + 0.04823f * color.Z,
            0.07600f * color.X + 0.90834f * color.Y + 0.01566f * color.Z,
            0.02840f * color.X + 0.13383f * color.Y + 0.83777f * color.Z);

        var a = v * (v + new Vector3(0.0245786f)) - new Vector3(0.000090537f);
        var b = v * (0.983729f * v + new Vector3(0.4329510f)) + new Vector3(0.238081f);
        v = a / b;

        // Output: ODT → Rec.709 (rows of ACESOutputMat).
        var o = new Vector3(
            1.60475f * v.X - 0.53108f * v.Y - 0.07367f * v.Z,
            -0.10208f * v.X + 1.10813f * v.Y - 0.00605f * v.Z,
            -0.00327f * v.X - 0.07276f * v.Y + 1.07602f * v.Z);
        return Vector3.Clamp(o, Vector3.Zero, Vector3.One);
    }
}
