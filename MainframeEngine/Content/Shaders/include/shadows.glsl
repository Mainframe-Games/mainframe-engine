// Shadow-map descriptor set and sampling (ShadowSystem, or the renderer's "no shadows" fallback).
// Layout: b0 light-space matrices UBO, b1 directional maps, b2 spot maps (immutable comparison samplers),
// b3 point cube maps (linear distance / range). Define SHADOW_SET before including to override the set
// (default 1: the per-frame shared sets are 0 = frame, 1 = shadows).
#ifndef MF_SHADOWS_GLSL
#define MF_SHADOWS_GLSL

#include "common.glsl"

#ifndef SHADOW_SET
#define SHADOW_SET 1
#endif

layout(set = SHADOW_SET, binding = 0) uniform ShadowMatricesUBO {
    mat4 dirLightSpace[MAX_SHADOW_DIR];
    mat4 spotLightSpace[MAX_SHADOW_SPOT];
} shadowMat;

layout(set = SHADOW_SET, binding = 1) uniform sampler2DShadow dirShadowMaps[MAX_SHADOW_DIR];
layout(set = SHADOW_SET, binding = 2) uniform sampler2DShadow spotShadowMaps[MAX_SHADOW_SPOT];
layout(set = SHADOW_SET, binding = 3) uniform samplerCube     pointShadowMaps[MAX_SHADOW_POINT];

const float kBias2D    = 0.001;
const float kBiasPoint = 0.015;

// System.Numerics matrices use the D3D/Vulkan convention: x/y in [-1, 1], z already in [0, 1].
// Outside the light's frustum the fragment is lit (1.0).
float sampleShadow2D(mat4 lightSpace, sampler2DShadow map, vec3 worldPos)
{
    vec4 lsPos = lightSpace * vec4(worldPos, 1.0);
    lsPos /= lsPos.w;
    vec2  uv    = lsPos.xy * 0.5 + 0.5;
    float depth = lsPos.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth < 0.0 || depth > 1.0)
        return 1.0;
    return texture(map, vec3(uv, depth - kBias2D));
}

float sampleDirShadow(int i, vec3 worldPos)
{
    return sampleShadow2D(shadowMat.dirLightSpace[i], dirShadowMaps[i], worldPos);
}

float sampleSpotShadow(int i, vec3 worldPos)
{
    return sampleShadow2D(shadowMat.spotLightSpace[i], spotShadowMaps[i], worldPos);
}

float samplePointShadow(int i, vec3 worldPos, vec3 lightPos, float lightRange)
{
    vec3  fragToLight  = worldPos - lightPos;
    float currentDepth = length(fragToLight) / lightRange;
    float closestDepth = texture(pointShadowMaps[i], fragToLight).r;
    return currentDepth - kBiasPoint > closestDepth ? 0.0 : 1.0;
}

#endif
