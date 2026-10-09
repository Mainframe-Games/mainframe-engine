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

// True when the cutout keeps the fragment at `pixel` (SV_Position.xy) with `alpha` against `cutoff`.
bool cutoutKeeps(float alpha, float cutoff, float2 pixel)
{
    if (material.pbr.w > 0.0 && (frame.jitter.x != 0.0 || frame.jitter.y != 0.0))
    {
        float coverage = saturate((alpha - cutoff) / max(fwidth(alpha), 1e-4) + 0.5);
        return coverage > interleavedGradientNoise(floor(pixel), frame.temporal.z);
    }

    return alpha >= cutoff;
}
