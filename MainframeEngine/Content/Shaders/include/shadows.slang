// Shadow sampling (ShadowSystem, or the renderer's "no shadows" fallback set). Layout of the shadow set:
//   b0 ShadowUBO (ShadowUniforms in C#, std140)
//   b1 cascade array of the primary directional light (Sampler2DArrayShadow, immutable comparison sampler)
//   b2 shadow atlas: spot lights and secondary directional lights (Sampler2DShadow, immutable comparison sampler)
//   b3 point-light cubes holding linear distance / range (SamplerCubeShadow[MAX_SHADOW_POINT], immutable comparison)
// Six samplers in all (the material adds one), well inside MoltenVK's 16 per stage.
// Define SHADOW_SET before including to override the set (default 1: the shared sets are 0 = frame, 1 = shadows).
// Needs frame.slang (the camera's view matrix selects the cascade).
#pragma once
#define MF_SHADOWS_SLANG

#include "common.slang"
#include "frame.slang"

#ifndef SHADOW_SET
#define SHADOW_SET 1
#endif

struct ShadowMap2D
{
    float4x4 viewProj; // world -> light clip space (row-major: mul(v, viewProj))
    float4 rect;       // xy = offset, zw = size in the texture's UV space (cascades: 0, 0, 1, 1)
    float4 params;     // x = texel world size (ortho) or per unit of distance (perspective), y = 1 perspective
                       // (0 ortho, -d ortho up to view depth d), z = depth bias (texels), w = normal bias (texels)
};

struct ShadowUBO
{
    ShadowMap2D cascades[MAX_SHADOW_CASCADES];
    ShadowMap2D atlasMaps[MAX_SHADOW_ATLAS_MAPS];
    float4 cascadeSplits;  // view depth where each cascade ends
    float4 cascadeEnabled; // 1 = the cascade has casters (0: lit)
    float4 csm;            // x = cascade count, y = blend band (fraction), z = shadow distance, w = 1 debug tint
    float4 filterParams;   // x = filter (0 hard, 1 3x3, 2 Poisson 16), y = radius (texels), z = 1/cascade size, w = 1/atlas size
    int4   dirCodes[(MAX_DIR_LIGHTS + 3) / 4];     // 0 none, 1 cascades, k + 2 atlas map k
    int4   spotCodes[(MAX_SPOT_LIGHTS + 3) / 4];   // 0 none, k + 1 atlas map k
    int4   pointCodes[(MAX_POINT_LIGHTS + 3) / 4]; // 0 none, c + 1 cube c
    float4 pointParams[MAX_SHADOW_POINT];          // x = texel per unit of distance (2 / size), y = depth bias, z = normal bias
};

[[vk::binding(0, SHADOW_SET)]] ConstantBuffer<ShadowUBO> shadow;

[[vk::binding(1, SHADOW_SET)]] Sampler2DArrayShadow cascadeShadowMap;
[[vk::binding(2, SHADOW_SET)]] Sampler2DShadow      atlasShadowMap;
[[vk::binding(3, SHADOW_SET)]] SamplerCubeShadow    pointShadowMaps[MAX_SHADOW_POINT];

static const int kShadowFilterHard      = 0;
static const int kShadowFilter3x3       = 1;
static const int kShadowFilterPoisson16 = 2;

// Fixed (unrotated) taps: the filtered result is a function of the world position alone, so shadows do not crawl
// when the camera moves (rotation noise is screen-anchored).
static const float2 kPoisson16[16] = {
    float2(-0.94201624, -0.39906216), float2( 0.94558609, -0.76890725), float2(-0.09418410, -0.92938870), float2( 0.34495938,  0.29387760),
    float2(-0.91588581,  0.45771432), float2(-0.81544232, -0.87912464), float2(-0.38277543,  0.27676845), float2( 0.97484398,  0.75648379),
    float2( 0.44323325, -0.97511554), float2( 0.53742981, -0.47373420), float2(-0.26496911, -0.41893023), float2( 0.79197514,  0.19090188),
    float2(-0.24188840,  0.99706507), float2(-0.81409955,  0.91437590), float2( 0.19984126,  0.78641367), float2( 0.14383161, -0.14100790) };

// 20 directions spread over the sphere (normalised in use): the point-light disc.
static const float3 kPointDisc20[20] = {
    float3( 1,  1,  1), float3( 1, -1,  1), float3(-1, -1,  1), float3(-1,  1,  1),
    float3( 1,  1, -1), float3( 1, -1, -1), float3(-1, -1, -1), float3(-1,  1, -1),
    float3( 1,  1,  0), float3( 1, -1,  0), float3(-1, -1,  0), float3(-1,  1,  0),
    float3( 1,  0,  1), float3(-1,  0,  1), float3( 1,  0, -1), float3(-1,  0, -1),
    float3( 0,  1,  1), float3( 0, -1,  1), float3( 0, -1, -1), float3( 0,  1, -1) };

// Receiver offset (ShadowMath.ReceiverPosition in C#): towards the light by the depth bias and along the normal by
// the normal bias × sin(angle to the light), both in texels of `texel` world units.
float3 shadowReceiver(float3 worldPos, float3 N, float3 L, float texel, float depthBias, float normalBias)
{
    float cosT = clamp(dot(N, L), 0.0, 1.0);
    float sinT = sqrt(1.0 - cosT * cosT);
    return worldPos + L * (depthBias * texel) + N * (normalBias * texel * sinT);
}

// Kernel over one cascade layer. uv in [0, 1] of the layer.
float filterCascade(float2 uv, float layer, float depth)
{
    int   mode  = int(shadow.filterParams.x);
    float texel = shadow.filterParams.z;
    if (mode == kShadowFilterHard)
        return cascadeShadowMap.SampleCmp(float3(uv, layer), depth);

    float sum = 0.0;
    if (mode == kShadowFilter3x3)
    {
        float stepSize = texel * max(shadow.filterParams.y / 1.5, 0.5);
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += cascadeShadowMap.SampleCmp(float3(uv + float2(x, y) * stepSize, layer), depth);
        return sum / 9.0;
    }

    float radius = texel * shadow.filterParams.y;
    for (int i = 0; i < 16; i++)
        sum += cascadeShadowMap.SampleCmp(float3(uv + kPoisson16[i] * radius, layer), depth);
    return sum / 16.0;
}

// Kernel over one atlas tile; taps are clamped half a texel inside the tile so they never read a neighbour.
float filterAtlas(float2 uv, float4 rect, float depth)
{
    int    mode  = int(shadow.filterParams.x);
    float  texel = shadow.filterParams.w;
    float2 lo    = rect.xy + float2(texel * 0.5);
    float2 hi    = rect.xy + rect.zw - float2(texel * 0.5);
    float2 base  = rect.xy + uv * rect.zw;
    if (mode == kShadowFilterHard)
        return atlasShadowMap.SampleCmp(clamp(base, lo, hi), depth);

    float sum = 0.0;
    if (mode == kShadowFilter3x3)
    {
        float stepSize = texel * max(shadow.filterParams.y / 1.5, 0.5);
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += atlasShadowMap.SampleCmp(clamp(base + float2(x, y) * stepSize, lo, hi), depth);
        return sum / 9.0;
    }

    float radius = texel * shadow.filterParams.y;
    for (int i = 0; i < 16; i++)
        sum += atlasShadowMap.SampleCmp(clamp(base + kPoisson16[i] * radius, lo, hi), depth);
    return sum / 16.0;
}

// Light-clip position of the biased receiver for a 2D map; xyz = (uv, depth). Returns false outside the map.
bool shadowProject(ShadowMap2D map, float3 worldPos, float3 N, float3 L, float3 lightPos, out float3 coord)
{
    float texel = map.params.x;
    if (map.params.y > 0.0) // perspective: texel size grows with the distance to the light
        texel *= max(length(worldPos - lightPos), 1e-3);
    float3 p = shadowReceiver(worldPos, N, L, texel, map.params.z, map.params.w);
    float4 ls = mul(float4(p, 1.0), map.viewProj);
    ls.xyz /= ls.w;
    coord = float3(ls.xy * 0.5 + 0.5, ls.z);
    return ls.w > 0.0 && all(coord >= float3(0.0)) && all(coord <= float3(1.0));
}

// Every dynamic index into the shadow block or a sampler array is clamped to its range. The compiler may evaluate
// both sides of `?:`, `&&` and `||` (side-effect-free operands become selects), and out-of-range indices are not
// clamped by Metal: an out-of-range vector component or array layer read faults the GPU, which drops whole tiles of
// the frame.
int clampCascade(int c) { return clamp(c, 0, MAX_SHADOW_CASCADES - 1); }

// Shadow term of cascade c, or -1 when worldPos lies outside the cascade's map.
float cascadeTerm(int c, float3 worldPos, float3 N, float3 L)
{
    c = clampCascade(c);
    if (shadow.cascadeEnabled[c] == 0.0)
        return 1.0;
    float3 coord;
    if (!shadowProject(shadow.cascades[c], worldPos, N, L, float3(0.0), coord))
        return -1.0;
    return filterCascade(coord.xy, float(c), coord.z);
}

float shadowViewDepth(float3 worldPos)
{
    return -mul(float4(worldPos, 1.0), frame.view).z;
}

// The cascade covering worldPos (count when beyond the shadow distance).
int shadowCascadeIndex(float viewDepth)
{
    int count = int(shadow.csm.x);
    for (int i = 0; i < MAX_SHADOW_CASCADES; i++)
    {
        if (i >= count || viewDepth <= shadow.cascadeSplits[i]) // i is always in range
            return i;
    }
    return MAX_SHADOW_CASCADES;
}

// Primary directional light: pick the cascade by view depth, blend into the next one over the last `csm.y` of the
// cascade, and fade out over the last band before the shadow distance.
float sampleCascades(float3 worldPos, float3 N, float3 L)
{
    int   count = int(shadow.csm.x);
    float depth = shadowViewDepth(worldPos);
    int   c     = shadowCascadeIndex(depth);
    if (c >= count)
        return 1.0;

    float s     = cascadeTerm(c, worldPos, N, L);
    if (s < 0.0)
        s = 1.0; // outside its own map (cannot happen with the sphere fit): lit
    float start = c > 0 ? shadow.cascadeSplits[clampCascade(c - 1)] : 0.0;
    float end   = shadow.cascadeSplits[clampCascade(c)];
    float band  = (end - start) * shadow.csm.y;
    if (band > 0.0 && depth > end - band)
    {
        float t = smoothstep(end - band, end, depth);
        // Past the last cascade: fade to lit. The blend band lies before the next cascade's slice, so its sphere may
        // not reach the frustum's edge there: keep this cascade's term when the point is outside the next map.
        float next = 1.0;
        if (c + 1 < count)
            next = cascadeTerm(c + 1, worldPos, N, L);
        if (next >= 0.0)
            s = lerp(s, next, t);
    }
    return s;
}

float sampleAtlasMap(int k, float3 worldPos, float3 N, float3 L, float3 lightPos)
{
    ShadowMap2D map = shadow.atlasMaps[clamp(k, 0, MAX_SHADOW_ATLAS_MAPS - 1)];
    float fade = 0.0;
    if (map.params.y < 0.0) // secondary directional light: shadowed up to its distance, faded over the last tenth
    {
        float distance = -map.params.y;
        fade = smoothstep(distance * 0.9, distance, shadowViewDepth(worldPos));
        if (fade >= 1.0)
            return 1.0;
    }

    float3 coord;
    if (!shadowProject(map, worldPos, N, L, lightPos, coord))
        return 1.0;
    return lerp(filterAtlas(coord.xy, map.rect, coord.z), 1.0, fade);
}

// Shadow term of directional light i (L = direction towards the light).
float dirShadow(int i, float3 worldPos, float3 N, float3 L)
{
    int code = shadow.dirCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    if (code == 1)
        return sampleCascades(worldPos, N, L);
    return sampleAtlasMap(code - 2, worldPos, N, L, float3(0.0));
}

// Shadow term of spot light i.
float spotShadow(int i, float3 worldPos, float3 N, float3 L, float3 lightPos)
{
    int code = shadow.spotCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    return sampleAtlasMap(code - 1, worldPos, N, L, lightPos);
}

// Shadow term of point light i: compares linear distance / range (written by ShadowPoint.vk.frag) with a hardware
// comparison per tap; a 20-tap disc unless the filter is hard.
float pointShadow(int i, float3 worldPos, float3 N, float3 lightPos, float range)
{
    int code = shadow.pointCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    int    c = clamp(code - 1, 0, MAX_SHADOW_POINT - 1);
    float4 params = shadow.pointParams[c];
    float3 toLight = lightPos - worldPos;
    float  dist = length(toLight);
    float3 L = toLight / max(dist, 1e-4);
    float  texel = params.x * dist;
    float3 p = shadowReceiver(worldPos, N, L, texel, params.y, params.z);
    float3 dir = p - lightPos;
    float  ref = length(dir) / range;
    if (ref >= 1.0)
        return 1.0;

    if (int(shadow.filterParams.x) == kShadowFilterHard)
        return pointShadowMaps[c].SampleCmp(dir, ref);

    // Disc radius: the filter radius in texels at this distance (texel world size = 2·dist / size).
    float radius = texel * shadow.filterParams.y;
    float sum = 0.0;
    for (int t = 0; t < 20; t++)
        sum += pointShadowMaps[c].SampleCmp(dir + normalize(kPointDisc20[t]) * radius, ref);
    return sum / 20.0;
}

// Debug tint of the cascade covering worldPos (white beyond the cascades or when the debug view is off).
float3 shadowCascadeTint(float3 worldPos)
{
    if (shadow.csm.w == 0.0 || shadow.csm.x == 0.0)
        return float3(1.0);
    int c = shadowCascadeIndex(shadowViewDepth(worldPos));
    if (c == 0) return float3(1.0, 0.25, 0.25);
    if (c == 1) return float3(0.25, 1.0, 0.25);
    if (c == 2) return float3(0.2, 0.3, 1.0);
    if (c == 3) return float3(1.0, 0.9, 0.15);
    return float3(1.0);
}
