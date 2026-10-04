// Shadow sampling (ShadowSystem, or the renderer's "no shadows" fallback set). Layout of the shadow set:
//   b0 ShadowUBO (ShadowUniforms in C#, std140)
//   b1 cascade array of the primary directional light (sampler2DArrayShadow, immutable comparison sampler)
//   b2 shadow atlas: spot lights and secondary directional lights (sampler2DShadow, immutable comparison sampler)
//   b3 point-light cubes holding linear distance / range (samplerCubeShadow[MAX_SHADOW_POINT], immutable comparison)
// Six samplers in all (the material adds one), well inside MoltenVK's 16 per stage.
// Define SHADOW_SET before including to override the set (default 1: the shared sets are 0 = frame, 1 = shadows).
// Needs frame.glsl (the camera's view matrix selects the cascade).
#ifndef MF_SHADOWS_GLSL
#define MF_SHADOWS_GLSL

#include "common.glsl"
#include "frame.glsl"

#ifndef SHADOW_SET
#define SHADOW_SET 1
#endif

struct ShadowMap2D {
    mat4 viewProj; // world -> light clip space
    vec4 rect;     // xy = offset, zw = size in the texture's UV space (cascades: 0, 0, 1, 1)
    vec4 params;   // x = texel world size (ortho) or per unit of distance (perspective), y = 1 perspective,
                   // z = depth bias (texels), w = normal bias (texels)
};

layout(set = SHADOW_SET, binding = 0) uniform ShadowUBO {
    ShadowMap2D cascades[MAX_SHADOW_CASCADES];
    ShadowMap2D atlasMaps[MAX_SHADOW_ATLAS_MAPS];
    vec4  cascadeSplits;  // view depth where each cascade ends
    vec4  cascadeEnabled; // 1 = the cascade has casters (0: lit)
    vec4  csm;            // x = cascade count, y = blend band (fraction), z = shadow distance, w = 1 debug tint
    vec4  filterParams;   // x = filter (0 hard, 1 3x3, 2 Poisson 16), y = radius (texels), z = 1/cascade size, w = 1/atlas size
    ivec4 dirCodes[(MAX_DIR_LIGHTS + 3) / 4];     // 0 none, 1 cascades, k + 2 atlas map k
    ivec4 spotCodes[(MAX_SPOT_LIGHTS + 3) / 4];   // 0 none, k + 1 atlas map k
    ivec4 pointCodes[(MAX_POINT_LIGHTS + 3) / 4]; // 0 none, c + 1 cube c
    vec4  pointParams[MAX_SHADOW_POINT];          // x = texel per unit of distance (2 / size), y = depth bias, z = normal bias
} shadow;

layout(set = SHADOW_SET, binding = 1) uniform sampler2DArrayShadow cascadeShadowMap;
layout(set = SHADOW_SET, binding = 2) uniform sampler2DShadow      atlasShadowMap;
layout(set = SHADOW_SET, binding = 3) uniform samplerCubeShadow    pointShadowMaps[MAX_SHADOW_POINT];

const int kShadowFilterHard      = 0;
const int kShadowFilter3x3       = 1;
const int kShadowFilterPoisson16 = 2;

// Fixed (unrotated) taps: the filtered result is a function of the world position alone, so shadows do not crawl
// when the camera moves (rotation noise is screen-anchored).
const vec2 kPoisson16[16] = vec2[](
    vec2(-0.94201624, -0.39906216), vec2( 0.94558609, -0.76890725), vec2(-0.09418410, -0.92938870), vec2( 0.34495938,  0.29387760),
    vec2(-0.91588581,  0.45771432), vec2(-0.81544232, -0.87912464), vec2(-0.38277543,  0.27676845), vec2( 0.97484398,  0.75648379),
    vec2( 0.44323325, -0.97511554), vec2( 0.53742981, -0.47373420), vec2(-0.26496911, -0.41893023), vec2( 0.79197514,  0.19090188),
    vec2(-0.24188840,  0.99706507), vec2(-0.81409955,  0.91437590), vec2( 0.19984126,  0.78641367), vec2( 0.14383161, -0.14100790));

// 20 directions spread over the sphere (normalised in use): the point-light disc.
const vec3 kPointDisc20[20] = vec3[](
    vec3( 1,  1,  1), vec3( 1, -1,  1), vec3(-1, -1,  1), vec3(-1,  1,  1),
    vec3( 1,  1, -1), vec3( 1, -1, -1), vec3(-1, -1, -1), vec3(-1,  1, -1),
    vec3( 1,  1,  0), vec3( 1, -1,  0), vec3(-1, -1,  0), vec3(-1,  1,  0),
    vec3( 1,  0,  1), vec3(-1,  0,  1), vec3( 1,  0, -1), vec3(-1,  0, -1),
    vec3( 0,  1,  1), vec3( 0, -1,  1), vec3( 0, -1, -1), vec3( 0,  1, -1));

// Receiver offset (ShadowMath.ReceiverPosition in C#): towards the light by the depth bias and along the normal by
// the normal bias × sin(angle to the light), both in texels of `texel` world units.
vec3 shadowReceiver(vec3 worldPos, vec3 N, vec3 L, float texel, float depthBias, float normalBias)
{
    float cosT = clamp(dot(N, L), 0.0, 1.0);
    float sinT = sqrt(1.0 - cosT * cosT);
    return worldPos + L * (depthBias * texel) + N * (normalBias * texel * sinT);
}

// Kernel over one cascade layer. uv in [0, 1] of the layer.
float filterCascade(vec2 uv, float layer, float depth)
{
    int   mode  = int(shadow.filterParams.x);
    float texel = shadow.filterParams.z;
    if (mode == kShadowFilterHard)
        return texture(cascadeShadowMap, vec4(uv, layer, depth));

    float sum = 0.0;
    if (mode == kShadowFilter3x3)
    {
        float step = texel * max(shadow.filterParams.y / 1.5, 0.5);
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(cascadeShadowMap, vec4(uv + vec2(x, y) * step, layer, depth));
        return sum / 9.0;
    }

    float radius = texel * shadow.filterParams.y;
    for (int i = 0; i < 16; i++)
        sum += texture(cascadeShadowMap, vec4(uv + kPoisson16[i] * radius, layer, depth));
    return sum / 16.0;
}

// Kernel over one atlas tile; taps are clamped half a texel inside the tile so they never read a neighbour.
float filterAtlas(vec2 uv, vec4 rect, float depth)
{
    int   mode  = int(shadow.filterParams.x);
    float texel = shadow.filterParams.w;
    vec2  lo    = rect.xy + vec2(texel * 0.5);
    vec2  hi    = rect.xy + rect.zw - vec2(texel * 0.5);
    vec2  base  = rect.xy + uv * rect.zw;
    if (mode == kShadowFilterHard)
        return texture(atlasShadowMap, vec3(clamp(base, lo, hi), depth));

    float sum = 0.0;
    if (mode == kShadowFilter3x3)
    {
        float step = texel * max(shadow.filterParams.y / 1.5, 0.5);
        for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                sum += texture(atlasShadowMap, vec3(clamp(base + vec2(x, y) * step, lo, hi), depth));
        return sum / 9.0;
    }

    float radius = texel * shadow.filterParams.y;
    for (int i = 0; i < 16; i++)
        sum += texture(atlasShadowMap, vec3(clamp(base + kPoisson16[i] * radius, lo, hi), depth));
    return sum / 16.0;
}

// Light-clip position of the biased receiver for a 2D map; xyz = (uv, depth). Returns false outside the map.
bool shadowProject(ShadowMap2D map, vec3 worldPos, vec3 N, vec3 L, vec3 lightPos, out vec3 coord)
{
    float texel = map.params.x;
    if (map.params.y != 0.0) // perspective: texel size grows with the distance to the light
        texel *= max(length(worldPos - lightPos), 1e-3);
    vec3 p = shadowReceiver(worldPos, N, L, texel, map.params.z, map.params.w);
    vec4 ls = map.viewProj * vec4(p, 1.0);
    ls.xyz /= ls.w;
    coord = vec3(ls.xy * 0.5 + 0.5, ls.z);
    return ls.w > 0.0 && all(greaterThanEqual(coord, vec3(0.0))) && all(lessThanEqual(coord, vec3(1.0)));
}

float cascadeTerm(int c, vec3 worldPos, vec3 N, vec3 L)
{
    if (shadow.cascadeEnabled[c] == 0.0)
        return 1.0;
    vec3 coord;
    if (!shadowProject(shadow.cascades[c], worldPos, N, L, vec3(0.0), coord))
        return 1.0;
    return filterCascade(coord.xy, float(c), coord.z);
}

float shadowViewDepth(vec3 worldPos)
{
    return -(frame.view * vec4(worldPos, 1.0)).z;
}

// The cascade covering worldPos (count when beyond the shadow distance).
int shadowCascadeIndex(float viewDepth)
{
    int count = int(shadow.csm.x);
    int c = 0;
    while (c < count && viewDepth > shadow.cascadeSplits[c])
        c++;
    return c;
}

// Primary directional light: pick the cascade by view depth, blend into the next one over the last `csm.y` of the
// cascade, and fade out over the last band before the shadow distance.
float sampleCascades(vec3 worldPos, vec3 N, vec3 L)
{
    int   count = int(shadow.csm.x);
    float depth = shadowViewDepth(worldPos);
    int   c     = shadowCascadeIndex(depth);
    if (c >= count)
        return 1.0;

    float s     = cascadeTerm(c, worldPos, N, L);
    float start = c == 0 ? 0.0 : shadow.cascadeSplits[c - 1];
    float end   = shadow.cascadeSplits[c];
    float band  = (end - start) * shadow.csm.y;
    if (band > 0.0 && depth > end - band)
    {
        float t = smoothstep(end - band, end, depth);
        float next = c + 1 < count ? cascadeTerm(c + 1, worldPos, N, L) : 1.0; // past the last cascade: fade to lit
        s = mix(s, next, t);
    }
    return s;
}

float sampleAtlasMap(int k, vec3 worldPos, vec3 N, vec3 L, vec3 lightPos)
{
    ShadowMap2D map = shadow.atlasMaps[k];
    vec3 coord;
    if (!shadowProject(map, worldPos, N, L, lightPos, coord))
        return 1.0;
    return filterAtlas(coord.xy, map.rect, coord.z);
}

// Shadow term of directional light i (L = direction towards the light).
float dirShadow(int i, vec3 worldPos, vec3 N, vec3 L)
{
    int code = shadow.dirCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    if (code == 1)
        return sampleCascades(worldPos, N, L);
    return sampleAtlasMap(code - 2, worldPos, N, L, vec3(0.0));
}

// Shadow term of spot light i.
float spotShadow(int i, vec3 worldPos, vec3 N, vec3 L, vec3 lightPos)
{
    int code = shadow.spotCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    return sampleAtlasMap(code - 1, worldPos, N, L, lightPos);
}

// Shadow term of point light i: compares linear distance / range (written by ShadowPoint.vk.frag) with a hardware
// comparison per tap; a 20-tap disc unless the filter is hard.
float pointShadow(int i, vec3 worldPos, vec3 N, vec3 lightPos, float range)
{
    int code = shadow.pointCodes[i >> 2][i & 3];
    if (code == 0)
        return 1.0;
    int  c = code - 1;
    vec4 params = shadow.pointParams[c];
    vec3 toLight = lightPos - worldPos;
    float dist = length(toLight);
    vec3 L = toLight / max(dist, 1e-4);
    float texel = params.x * dist;
    vec3 p = shadowReceiver(worldPos, N, L, texel, params.y, params.z);
    vec3 dir = p - lightPos;
    float ref = length(dir) / range;
    if (ref >= 1.0)
        return 1.0;

    if (int(shadow.filterParams.x) == kShadowFilterHard)
        return texture(pointShadowMaps[c], vec4(dir, ref));

    // Disc radius: the filter radius in texels at this distance (texel world size = 2·dist / size).
    float radius = texel * shadow.filterParams.y;
    float sum = 0.0;
    for (int t = 0; t < 20; t++)
        sum += texture(pointShadowMaps[c], vec4(dir + normalize(kPointDisc20[t]) * radius, ref));
    return sum / 20.0;
}

// Debug tint of the cascade covering worldPos (white beyond the cascades or when the debug view is off).
vec3 shadowCascadeTint(vec3 worldPos)
{
    if (shadow.csm.w == 0.0 || shadow.csm.x == 0.0)
        return vec3(1.0);
    int c = shadowCascadeIndex(shadowViewDepth(worldPos));
    if (c == 0) return vec3(1.0, 0.25, 0.25);
    if (c == 1) return vec3(0.25, 1.0, 0.25);
    if (c == 2) return vec3(0.2, 0.3, 1.0);
    if (c == 3) return vec3(1.0, 0.9, 0.15);
    return vec3(1.0);
}

#endif
