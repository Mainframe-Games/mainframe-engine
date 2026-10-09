using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// How the main view's 3D image is brought from its render resolution up to the window (<c>rendering.scaling3DMode</c>,
/// Godot's <c>scaling_3d_mode</c>; ADR 0174). Only matters below a <c>rendering.scaling3DScale</c> of 1.
/// </summary>
public enum Scaling3DMode
{
    /// <summary>A bilinear upscale of the HDR image (default; Godot's default).</summary>
    Bilinear,

    /// <summary>
    /// AMD FidelityFX FSR 1: EASU (edge-adaptive spatial upsampling) on the HDR image, then RCAS on the tonemapped one
    /// (<c>rendering.fsrSharpness</c>). The spatial mode for TAA off; with TAA on the resolve upscales instead.
    /// </summary>
    Fsr,

    /// <summary>
    /// Temporal upscaling in the TAA resolve (TAAU): each output pixel is reconstructed from the jittered render-resolution
    /// samples around it and an output-resolution history. Turns TAA on while the scale is below 1.
    /// </summary>
    Taau,
}

/// <summary>
/// The arithmetic of render-resolution scaling (ADR 0174): the render size for a scale, the Halton cycle length that
/// covers every output pixel, and the texture mip bias that keeps output-resolution sharpness.
/// </summary>
public static class RenderScaling
{
    /// <summary>The smallest <c>rendering.scaling3DScale</c> (Godot's).</summary>
    public const float MinScale = 0.25f;

    /// <summary>The largest <c>rendering.scaling3DScale</c>: native resolution (supersampling is not supported).</summary>
    public const float MaxScale = 1f;

    /// <summary>Godot's default <c>rendering.fsrSharpness</c>: RCAS stops of attenuation (0 = sharpest).</summary>
    public const float DefaultFsrSharpness = 0.2f;

    /// <summary>The largest <c>rendering.fsrSharpness</c> (Godot's).</summary>
    public const float MaxFsrSharpness = 2f;

    /// <summary><paramref name="scale"/> clamped to [<see cref="MinScale"/>, <see cref="MaxScale"/>] (NaN: 1).</summary>
    public static float ClampScale(float scale) => float.IsNaN(scale) ? MaxScale : Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>
    /// The render size for an output of <paramref name="output"/> pixels at <paramref name="scale"/>: each side rounded
    /// to the nearest pixel, at least one (2560 × 1440 at 0.75 is 1920 × 1080). A scale of 1 returns the output.
    /// </summary>
    public static Extent2D RenderExtent(Extent2D output, float scale)
    {
        scale = ClampScale(scale);
        if (scale >= MaxScale)
            return output;
        return new Extent2D(Scaled(output.Width, scale), Scaled(output.Height, scale));

        static uint Scaled(uint size, float scale) => Math.Clamp((uint)MathF.Round(size * scale, MidpointRounding.AwayFromZero), 1u, Math.Max(1u, size));
    }

    /// <summary>
    /// The projection jitter's cycle at a render scale: <see cref="TemporalJitter.DefaultSampleCount"/> / scale², rounded
    /// up to a power of two (8 at native, 16 at 0.75, 32 at 0.5), so every output pixel sees about eight samples per cycle.
    /// </summary>
    public static int JitterSampleCount(float scale)
    {
        scale = ClampScale(scale);
        if (scale >= MaxScale)
            return TemporalJitter.DefaultSampleCount;
        var wanted = (int)MathF.Ceiling(TemporalJitter.DefaultSampleCount / (scale * scale) - 1e-4f);
        var count = TemporalJitter.DefaultSampleCount;
        while (count < wanted)
            count <<= 1;
        return count;
    }

    /// <summary>
    /// The actual render scale between <paramref name="render"/> and <paramref name="output"/> (their width ratio; 1 when
    /// they match or the output is empty).
    /// </summary>
    public static float ScaleOf(Extent2D render, Extent2D output) =>
        output.Width == 0 || render.Width >= output.Width ? 1f : (float)render.Width / output.Width;

    /// <summary>
    /// The texture LOD bias at a render scale, log2(scale) (−0.42 at 0.75; 0 at native): textures sample the mip the
    /// output resolution would, so they keep their sharpness after the upscale.
    /// </summary>
    public static float MipBias(float scale)
    {
        scale = ClampScale(scale);
        return scale >= MaxScale ? 0f : MathF.Log2(scale);
    }
}
