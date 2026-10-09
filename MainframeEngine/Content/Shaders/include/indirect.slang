// Indirect light with the light probes (ADR 0170): how lights.slang combines the sky's image-based lighting with the
// probe volume (probes.slang) and GTAO (ambient_occlusion.slang) when a volume is bound. Without one the lit shaders keep
// their old ambient terms (probesActive() is false), so these functions never run.
//   diffuse   = (sky irradiance around the bent normal × the sky light around N (probeSkyLight: visibility, strength,
//               occlusion tint) + the probes' bounce around N) × the multi-bounce AO
//   specular  = sky reflection × specular occlusion + the bounce seen along R × (1 − specular occlusion)
// The bent normal leans the shading normal towards where the sky is open: the probe's (large scale) and GTAO's (under
// 2 m). Specular occlusion is the sky visibility along the reflection, widened towards the diffuse one with roughness.
#pragma once

#include "probes.slang"
#include "environment.slang"
#include "ambient_occlusion.slang"

// What the probes give one shading point.
struct ProbeLighting
{
    float3 skyDiffuse;        // the sky's ambient light around the bent normal × the sky light (multiply by albedo)
    float3 bounceDiffuse;     // the bounce light around N (multiply by albedo)
    float  specularOcclusion; // how much of the sky reflection survives (0..1)
    float3 occludedSpecular;  // what the reflection shows where the sky is blocked: the bounce along R
};

// The last probe sample a lit shader took (a leaf's translucency reuses it for its back side; ADR 0170). Valid after
// probeLighting ran in this invocation.
static ProbeSample gProbeSample;

// v / |v|, or 0 for a zero vector (normalize would give NaN, which TAA and auto exposure spread over the frame).
float3 probeSafeNormalize(float3 v)
{
    return v * rsqrt(max(dot(v, v), 1e-12));
}

// Jimenez et al. 2016's multi-bounce fit: bright surfaces lose less to occlusion than one bounce of AO says.
float3 multiBounceAo(float ao, float3 albedo)
{
    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(float3(ao), ((ao * a + b) * ao + c) * ao);
}

ProbeLighting probeLighting(float3 worldPos, float3 N, float3 Ngeo, float3 V, float3 R, float roughness)
{
    ProbeSample p = sampleProbes(probeSamplePosition(worldPos, Ngeo, V));
    gProbeSample = p;
    float visibility = probeSkyVisibility(p, N);

    // The bent normal: GTAO's where it has one (small scale), else N, leaned towards the probe's open sky.
    float3 screenBent = screenBentNormal(worldPos);
    float3 base = dot(screenBent, screenBent) > 0.5 ? probeSafeNormalize(N + screenBent) : N;
    float3 bent = probeSafeNormalize(base + probeBentDirection(p) * 0.5);

    ProbeLighting l;
    l.skyDiffuse = iblDiffuse(bent) * probeSkyLight(visibility);
    l.bounceDiffuse = probeBounce(p, N);
    float along = probeApplyStrength(probeSkyVisibilityAlong(p, R));
    l.specularOcclusion = saturate(lerp(along, probeApplyStrength(visibility), saturate(roughness)));
    l.occludedSpecular = probeBounce(p, R);
    return l;
}

// A surface's ambient light around normal n from an existing sample (a leaf's back side): sky × sky light + bounce.
float3 probeAmbient(ProbeSample p, float3 n)
{
    return iblDiffuse(n) * probeSkyLight(probeSkyVisibility(p, n)) + probeBounce(p, n);
}
