// Shared by the sky fragment shaders: SkyEnvironment's push constants (96 bytes) and the view ray of a
// fullscreen-triangle fragment, reconstructed from the per-frame camera (set 0). Sky shaders end with
// applySkyFog(color, ray) (fog.slang: height fog towards the horizon). They also render the sky into the image-based
// lighting cube (SkyRadiance, ADR 0150) through the same camera data, one 90° view per face; sun.z = 1 marks that
// capture, where the sun disk is left out (the directional light already lights with it).
#pragma once

#include "frame.slang"
#include "fog.slang"

struct SkyParams
{
    float4 skyColor;          // rgb = zenith colour (sRGB-authored)
    float4 horizonColor;      // rgb = horizon colour
    float4 groundColor;       // rgb = ground / nadir colour
    float4 sunDirection;      // xyz = world-space direction toward the sun (normalized)
    float4 sunColorIntensity; // rgb = sun colour, a = intensity multiplier
    float4 sun;               // x = cos(sun angular radius), y = horizon sharpness, z = 1 radiance capture (no sun disk)
};

[[vk::push_constant]] ConstantBuffer<SkyParams> sky;

// World-space view direction through NDC position `ndc` (x, y in [-1, 1]; +y up with the flipped viewport).
float3 skyRay(float2 ndc)
{
    float4 viewPos = mul(float4(ndc, 1.0, 1.0), frame.invProjection);
    viewPos /= viewPos.w;
    return normalize(mul(viewPos.xyz, (float3x3)frame.invViewRotation));
}
