// The cutout alpha test, optionally dithered for TAA (ADR 0166; FoliageMaterial3D.AlphaDither = material.pbr.w > 0).
// With TAA on (the frame is jittered) a dithered material keeps a fragment when its coverage beats a per-frame
// interleaved-gradient-noise threshold: coverage = (alpha - cutoff) / fwidth(alpha) + 0.5, the alpha edge sharpened by
// its screen-space derivative (about one pixel wide at any distance). TAA's history averages the dither into partial
// coverage, so leaf and needle edges resolve smoothly instead of crawling. Without the jitter, or without AlphaDither,
// it is the plain test (alpha >= cutoff), so other frames and materials are unchanged. Call it in uniform control flow
// (fwidth). Include frame.slang and material.slang first.
#pragma once

// Jimenez's interleaved gradient noise in [0, 1), shifted per jitter sample so the pattern changes each frame.
float interleavedGradientNoise(float2 pixel, float frameIndex)
{
    pixel += 5.588238 * frameIndex;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// A per-pixel dither threshold in [0, 1): with TAA (a jittered frame) the interleaved gradient noise of this frame, so
// TAA averages it into a fade; without it a stable 4 × 4 Bayer pattern (ADR 0172's LOD cross-fades).
float ditherThreshold(float2 pixel)
{
    float2 p = floor(pixel);
    if (frame.jitter.x != 0.0 || frame.jitter.y != 0.0)
        return interleavedGradientNoise(p, frame.temporal.z);
    uint2 q = uint2(p) & 3u;
    static const float kBayer[16] = { 0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0 };
    return (kBayer[q.y * 4u + q.x] + 0.5) / 16.0;
}

// True when the cutout keeps the fragment at `pixel` (SV_Position.xy) with `alpha` against `cutoff`.
bool cutoutKeeps(float alpha, float cutoff, float2 pixel)
{
    if (material.pbr.w > 0.0 && (frame.jitter.x != 0.0 || frame.jitter.y != 0.0))
    {
        // pbr.w scales the edge: 1 for AlphaDither, 2 × AlphaAntialiasingEdge for AlphaToCoverage (ADR 0172).
        float coverage = saturate((alpha - cutoff) / max(fwidth(alpha) * material.pbr.w, 1e-4) + 0.5);
        return coverage > interleavedGradientNoise(floor(pixel), frame.temporal.z);
    }

    return alpha >= cutoff;
}
