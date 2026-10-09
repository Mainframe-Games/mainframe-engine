using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Sub-pixel projection jitter for temporal anti-aliasing (ADR 0163): the Halton (2, 3) sequence, one sample per frame,
/// as an offset in NDC added to the projection (<see cref="Apply"/>). Only the matrices the main view rasterises with are
/// jittered (<see cref="FrameContext.ProjectionJitter"/>); the camera's own matrices — culling, shadows, light shafts, UI —
/// never are. The index comes from the frame number, so <c>--fixed-fps</c> runs are deterministic.
/// </summary>
public static class TemporalJitter
{
    /// <summary>Samples in the cycle (Unreal and Godot use 8 for TAA).</summary>
    public const int DefaultSampleCount = 8;

    /// <summary>The radical inverse of <paramref name="index"/> in <paramref name="radix"/> (Halton), in [0, 1).</summary>
    public static float Halton(int index, int radix)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(radix, 2);
        var result = 0f;
        var fraction = 1f / radix;
        for (var i = index; i > 0; i /= radix)
        {
            result += i % radix * fraction;
            fraction /= radix;
        }

        return result;
    }

    /// <summary>The sample index of frame <paramref name="frame"/> in a cycle of <paramref name="sampleCount"/> samples.</summary>
    public static int SampleIndex(ulong frame, int sampleCount = DefaultSampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        return (int)(frame % (ulong)sampleCount);
    }

    /// <summary>
    /// Sample <paramref name="sampleIndex"/>'s offset in pixels, in [-0.5, 0.5)² (Halton (2, 3) from index 1, so the first
    /// sample is not the pixel corner): x right, y down (image rows).
    /// </summary>
    public static Vector2 PixelOffset(int sampleIndex) =>
        new(Halton(sampleIndex + 1, 2) - 0.5f, Halton(sampleIndex + 1, 3) - 0.5f);

    /// <summary>
    /// Sample <paramref name="sampleIndex"/>'s offset in NDC (+Y up) for a <paramref name="width"/> × <paramref name="height"/>
    /// pixel view: a pixel is 2 / size in NDC, and +Y in NDC is up the image (the main pass flips the viewport), so the
    /// image-space y offset changes sign.
    /// </summary>
    public static Vector2 NdcOffset(int sampleIndex, uint width, uint height)
    {
        var pixels = PixelOffset(sampleIndex);
        return new Vector2(2f * pixels.X / Math.Max(1u, width), -2f * pixels.Y / Math.Max(1u, height));
    }

    /// <summary>
    /// <paramref name="projection"/> (a System.Numerics row-vector projection) with its NDC output shifted by
    /// <paramref name="jitter"/>: clip.xy += jitter · clip.w, so every projected point moves by exactly the jitter after
    /// the divide, perspective or orthographic. Depth and w are unchanged.
    /// </summary>
    public static Matrix4x4 Apply(in Matrix4x4 projection, Vector2 jitter)
    {
        if (jitter == Vector2.Zero)
            return projection;
        var p = projection;
        p.M11 += jitter.X * p.M14;
        p.M21 += jitter.X * p.M24;
        p.M31 += jitter.X * p.M34;
        p.M41 += jitter.X * p.M44;
        p.M12 += jitter.Y * p.M14;
        p.M22 += jitter.Y * p.M24;
        p.M32 += jitter.Y * p.M34;
        p.M42 += jitter.Y * p.M44;
        return p;
    }
}
