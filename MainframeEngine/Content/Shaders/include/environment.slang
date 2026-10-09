// Image-based lighting from the world's sky (SkyRadiance in C#, ADR 0150). The maps live in the per-frame set 0 next to
// the camera and lights, written per frame slot and view for the view's world (FrameContext):
//   b2 radiance cube: the sky prefiltered with GGX; mip m holds perceptual roughness m / kRadianceMaxMip
//   b3 irradiance cube: the sky convolved with a cosine lobe and divided by π (an ambient colour per normal)
//   b4 BRDF LUT: the split-sum scale (r) and bias (g) of F0; u = N·V, v = perceptual roughness (BrdfLut in C#)
// Worlds without a baked sky bind a 1×1 black cube; the flags then make the helpers fall back to lights.ambientColor.
// Three combined samplers: the fragment stage stays within MoltenVK's 16 (shadows 6, material 1 sampler + 4 images).
#pragma once

#include "lights_data.slang"

[[vk::binding(2, 0)]] SamplerCube radianceMap;
[[vk::binding(3, 0)]] SamplerCube irradianceMap;
[[vk::binding(4, 0)]] Sampler2D   brdfLut;

// SkyRadiance.RadianceMips - 1 in C#.
static const float kRadianceMaxMip = 5.0;

// Diffuse ambient light arriving around N, in ambient-colour units (a white Lambert surface shows it as is).
float3 iblDiffuse(float3 N)
{
    if ((environmentFlags() & kEnvSkyDiffuse) != 0u)
        return irradianceMap.SampleLevel(N, 0.0).rgb * lights.ambientColor.w;
    return lights.ambientColor.rgb;
}

// Sky radiance around the reflection vector R, blurred for a perceptual roughness (0 = mirror, 1 = fully rough).
float3 iblSpecular(float3 R, float roughness)
{
    uint flags = environmentFlags();
    if ((flags & kEnvNoSpecular) != 0u)
        return float3(0.0);
    if ((flags & kEnvSkySpecular) != 0u)
        return radianceMap.SampleLevel(R, saturate(roughness) * kRadianceMaxMip).rgb;
    return lights.ambientColor.rgb; // no sky: a uniform environment of the ambient colour
}

// Split-sum environment BRDF: reflected light = iblSpecular(R, r) × (F0 × x + y).
float2 iblBrdf(float NdotV, float roughness)
{
    return brdfLut.SampleLevel(float2(saturate(NdotV), saturate(roughness)), 0.0).rg;
}
