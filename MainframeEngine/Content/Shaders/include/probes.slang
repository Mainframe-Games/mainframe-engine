// The light probe volume (ADR 0170, LightProbeVolume / LightProbeData in C#): set 0, binding 6 (FrameContext.ProbeVolumeBinding).
// One RGBA16F 3D texture of countX × (5 · layers) × countZ texels, five slabs stacked along Y, each an SH L1 set
// (c0, c1.x, c1.y, c1.z; SphericalHarmonics in C#, band 1 as a vector in x, y, z order) per probe:
//   slab 0 sky visibility (0..1 per direction), slabs 1-3 bounce radiance red, green, blue (ambient units), slab 4 the
//   ground height under each column in r (terrain-following layouts).
// FrameData.probe* describe the grid (frame.slang). Without a volume probeOrigin.w is 0, probesActive() is false and the
// lit shaders take their old paths (bit-exact frames); the binding then holds a 1×1×1 zero volume that is never read.
#pragma once

#include "frame.slang"

[[vk::binding(6, 0)]] Sampler3D probeVolume;

// SH constants: Y0, Y1, and the cosine-lobe convolution of band 1 over π (2/3 · Y1).
static const float kProbeY0 = 0.282094792;
static const float kProbeY1 = 0.488602512;
static const float kProbeA1Y1 = 0.325735008;

// One trilinear lookup of the probes: sky visibility and the bounce's three channels.
struct ProbeSample
{
    float4 sky;
    float4 red;
    float4 green;
    float4 blue;
};

bool probesActive()
{
    return frame.probeOrigin.w > 0.5;
}

// How much of the baked sky occlusion applies (LightProbeVolume.SkyOcclusion, 0..1): probeOrigin.w = 1 + strength.
float probeOcclusionStrength()
{
    return saturate(frame.probeOrigin.w - 1.0);
}

// A baked visibility with the volume's occlusion strength applied (reflections).
float probeApplyStrength(float visibility)
{
    return 1.0 - probeOcclusionStrength() * (1.0 - visibility);
}

// The sky light that reaches a point whose sky visibility is `visibility`, as a factor on the sky's irradiance: the visible
// part, plus the occluded part the volume's strength leaves (1 - SkyOcclusion), tinted by LightProbeVolume.OcclusionTint
// (frame.probeTint; Unreal's sky light occlusion tint): under a canopy, light the leaves scatter instead of the blocked sky.
// A white tint gives probeApplyStrength in every channel.
float3 probeSkyLight(float visibility)
{
    return visibility + (1.0 - visibility) * (1.0 - probeOcclusionStrength()) * frame.probeTint.rgb;
}

// Metres between probes along the ground (the smaller of x and z).
float probeSpacing()
{
    return 1.0 / max(frame.probeSpacing.x, frame.probeSpacing.z);
}

// Terrain-following: the continuous layer coordinate of a point `height` metres above the ground (piecewise linear between
// the layer heights, clamped). ProbeGrid.LayerCoordinate in C#.
// Branchless: the sum of how far the height is through each segment (h[i], h[i + 1]) that exists (i + 1 < layers); the
// heights increase, so the segments below count 1, the one it is in its fraction, the ones above 0.
float probeLayerCoordinate(float height)
{
    float layers = frame.probeCounts.y;
    float4 lo0 = frame.probeLayers0;
    float4 hi0 = float4(frame.probeLayers0.yzw, frame.probeLayers1.x);
    float4 lo1 = frame.probeLayers1;
    float4 hi1 = float4(frame.probeLayers1.yzw, frame.probeLayers1.w);
    float4 f0 = saturate((height - lo0) / max(hi0 - lo0, 1e-4));
    float4 f1 = saturate((height - lo1) / max(hi1 - lo1, 1e-4));
    float4 m0 = step(float4(1.0, 2.0, 3.0, 4.0), layers - 0.5); // segment i exists: i + 1 < layers
    float4 m1 = step(float4(5.0, 6.0, 7.0, 8.0), layers - 0.5);
    return dot(f0, m0) + dot(f1, m1);
}

// The probes at world position p (already offset off the surface by the caller): five fetches for a terrain-following
// volume (the ground height, then the four slabs), four for a box. Trilinear in x and z (and between layers), clamped to
// the grid; Y is clamped to each slab's texel centres, so the hardware filter never blends two slabs.
ProbeSample sampleProbes(float3 p)
{
    float3 counts = frame.probeCounts.xyz;
    float layers = counts.y;
    float rows = 5.0 * layers;
    float2 g = clamp((p.xz - frame.probeOrigin.xz) * frame.probeSpacing.xz, float2(0.0), counts.xz - 1.0);
    float2 uv = (g + 0.5) / counts.xz;

    float layer;
    if (frame.probeSpacing.w > 0.5)
    {
        float ground = probeVolume.SampleLevel(float3(uv.x, (4.0 * layers + 0.5) / rows, uv.y), 0.0).r;
        layer = probeLayerCoordinate(p.y - ground);
    }
    else
    {
        layer = clamp((p.y - frame.probeOrigin.y) * frame.probeSpacing.y, 0.0, layers - 1.0);
    }

    float v = layer + 0.5;
    ProbeSample s;
    s.sky = probeVolume.SampleLevel(float3(uv.x, v / rows, uv.y), 0.0);
    s.red = probeVolume.SampleLevel(float3(uv.x, (layers + v) / rows, uv.y), 0.0);
    s.green = probeVolume.SampleLevel(float3(uv.x, (2.0 * layers + v) / rows, uv.y), 0.0);
    s.blue = probeVolume.SampleLevel(float3(uv.x, (3.0 * layers + v) / rows, uv.y), 0.0);
    return s;
}

// The sky light at p relative to open ground (probeSkyLight of the visibility's mean over the sphere × 2: an open probe
// over flat ground sees half the sphere), from two fetches (the ground height and the sky slab): media such as the
// volumetric fog, which have no normal. 1 without a volume.
float3 probeOpenSky(float3 p)
{
    if (!probesActive())
        return float3(1.0);
    float3 counts = frame.probeCounts.xyz;
    float layers = counts.y;
    float rows = 5.0 * layers;
    float2 g = clamp((p.xz - frame.probeOrigin.xz) * frame.probeSpacing.xz, float2(0.0), counts.xz - 1.0);
    float2 uv = (g + 0.5) / counts.xz;
    float layer;
    if (frame.probeSpacing.w > 0.5)
        layer = probeLayerCoordinate(p.y - probeVolume.SampleLevel(float3(uv.x, (4.0 * layers + 0.5) / rows, uv.y), 0.0).r);
    else
        layer = clamp((p.y - frame.probeOrigin.y) * frame.probeSpacing.y, 0.0, layers - 1.0);
    float sky = probeVolume.SampleLevel(float3(uv.x, (layer + 0.5) / rows, uv.y), 0.0).r;
    return probeSkyLight(saturate(2.0 * kProbeY0 * sky));
}

// The fraction of the sky visible around normal n (cosine-weighted), 0..1, as baked (probeSkyLight and
// probeApplyStrength apply the volume's strength and tint).
float probeSkyVisibility(ProbeSample s, float3 n)
{
    return saturate(kProbeY0 * s.sky.x + kProbeA1Y1 * dot(s.sky.yzw, n));
}

// The sky visibility in direction d itself (reflections), 0..1, as baked.
float probeSkyVisibilityAlong(ProbeSample s, float3 d)
{
    return saturate(kProbeY0 * s.sky.x + kProbeY1 * dot(s.sky.yzw, d));
}

// The direction the sky is most visible from (the visibility's band 1), or 0 where it is even.
float3 probeBentDirection(ProbeSample s)
{
    float l = length(s.sky.yzw);
    return l > 1e-3 ? s.sky.yzw / l : float3(0.0);
}

// The bounce light around normal n as an ambient colour (cosine-weighted, over π), × the volume's energy.
float3 probeBounce(ProbeSample s, float3 n)
{
    float3 c0 = float3(s.red.x, s.green.x, s.blue.x);
    float3 c1 = float3(dot(s.red.yzw, n), dot(s.green.yzw, n), dot(s.blue.yzw, n));
    return max(kProbeY0 * c0 + kProbeA1Y1 * c1, float3(0.0)) * frame.probeCounts.w;
}

// Where a surface samples the probes: pushed off it along the geometric normal (0.3 probe spacings) and towards the eye
// (10 cm), so a probe behind a wall or under the ground is never the nearest (ADR 0170's leak prevention, with the bake's
// fix-up of probes inside geometry).
float3 probeSamplePosition(float3 worldPos, float3 Ngeo, float3 V)
{
    return worldPos + Ngeo * (0.3 * probeSpacing()) + V * 0.1;
}
