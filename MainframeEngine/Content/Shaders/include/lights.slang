// Lights UBO (LightEnvironment.WriteUbo, std140, 1200 bytes with the default limits) and Blinn-Phong shading
// with shadows. Include shadows.slang first. Define LIGHTS_SET / LIGHTS_BINDING before including to override
// where the UBO lives (default: the per-frame set 0, binding 1, next to the camera data).
// Colours in the UBO are linear (LightEnvironment converts the sRGB-authored values when packing).
#pragma once

#include "common.slang"

#ifndef MF_SHADOWS_SLANG
#error "Include shadows.slang before lights.slang"
#endif

#ifndef LIGHTS_SET
#define LIGHTS_SET 0
#endif
#ifndef LIGHTS_BINDING
#define LIGHTS_BINDING 1
#endif

struct DirLight
{
    float4 directionIntensity; // xyz = direction, w = intensity
    float4 color;              // xyz = color, w = shadow opacity (Godot's shadow_opacity, ADR 0123)
};

struct PointLight
{
    float4 positionRange;  // xyz = position, w = range
    float4 colorIntensity; // xyz = color,    w = intensity
};

struct SpotLight
{
    float4 positionRange;      // xyz = position,  w = range
    float4 directionIntensity; // xyz = direction, w = intensity
    float4 colorInner;         // xyz = color,     w = cos(innerAngle)
    float4 outerPad;           // x   = cos(outerAngle), y = shadow opacity (ADR 0123)
};

struct LightsUBO
{
    float4     ambientColor;
    float4     cameraPosition;
    int4       counts;           // x = numDir, y = numPoint, z = numSpot, w = 1: no shadow maps for this view
    DirLight   dir[MAX_DIR_LIGHTS];
    PointLight point[MAX_POINT_LIGHTS];
    SpotLight  spot[MAX_SPOT_LIGHTS];
};

[[vk::binding(LIGHTS_BINDING, LIGHTS_SET)]] ConstantBuffer<LightsUBO> lights;

float attenuate(float dist, float range)
{
    float x = clamp(1.0 - dist / range, 0.0, 1.0);
    return x * x;
}

// Blinn-Phong: (diffuse + highlight of strength `specular`, exponent `shininess`) × base colour.
float3 calcDir(DirLight l, float3 N, float3 V, float3 base, float shadow, float specular, float shininess)
{
    float3 L    = normalize(-l.directionIntensity.xyz);
    float3 H    = normalize(L + V);
    float  diff = max(dot(N, L), 0.0);
    float  spec = pow(max(dot(N, H), 0.0), shininess);
    return l.color.xyz * l.directionIntensity.w * (diff + spec * specular) * base * shadow;
}

float3 calcPoint(PointLight l, float3 N, float3 V, float3 pos, float3 base, float shadow, float specular, float shininess)
{
    float3 toLight = l.positionRange.xyz - pos;
    float  dist    = length(toLight);
    float3 L       = normalize(toLight);
    float3 H       = normalize(L + V);
    float  diff    = max(dot(N, L), 0.0);
    float  spec    = pow(max(dot(N, H), 0.0), shininess);
    float  atten   = attenuate(dist, l.positionRange.w);
    return l.colorIntensity.xyz * l.colorIntensity.w * (diff + spec * specular) * atten * base * shadow;
}

float3 calcSpot(SpotLight l, float3 N, float3 V, float3 pos, float3 base, float shadow, float specular, float shininess)
{
    float3 toLight  = l.positionRange.xyz - pos;
    float  dist     = length(toLight);
    float3 L        = normalize(toLight);
    float3 H        = normalize(L + V);
    float  cosTheta = dot(-L, normalize(l.directionIntensity.xyz));
    float  inner    = l.colorInner.w;
    float  outer    = l.outerPad.x;
    float  spot     = clamp((cosTheta - outer) / max(inner - outer, 0.0001), 0.0, 1.0);
    float  diff     = max(dot(N, L), 0.0);
    float  spec     = pow(max(dot(N, H), 0.0), shininess);
    float  atten    = attenuate(dist, l.positionRange.w);
    return l.colorInner.xyz * l.directionIntensity.w * (diff + spec * specular) * spot * atten * base * shadow;
}

// Ambient + every light, each shadowed by its map when it has one (ShadowSystem decides which lights cast).
// `specular` scales the Blinn-Phong highlight of exponent `shininess` (materials: StandardMaterial3D). `Ngeo` is the
// geometric normal, used for the shadow receivers' normal offset (a normal-mapped N would make it noisy).
float3 shadeLightsBlinnPhong(float3 base, float3 N, float3 Ngeo, float3 worldPos, float specular, float shininess)
{
    float3 V = normalize(lights.cameraPosition.xyz - worldPos);
    float3 result = lights.ambientColor.xyz * base;
    bool shadowsOn = lights.counts.w == 0;

    for (int i = 0; i < lights.counts.x; i++)
    {
        float3 L      = normalize(-lights.dir[i].directionIntensity.xyz);
        float  shadow = shadowsOn ? dirShadow(i, worldPos, Ngeo, L) : 1.0;
        shadow = lerp(1.0, shadow, lights.dir[i].color.w); // Godot: a full shadow keeps 1 - opacity of the light
        result += calcDir(lights.dir[i], N, V, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.y; i++)
    {
        float4 pr     = lights.point[i].positionRange;
        float  shadow = shadowsOn ? pointShadow(i, worldPos, Ngeo, pr.xyz, pr.w) : 1.0;
        result += calcPoint(lights.point[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.z; i++)
    {
        float3 lightPos = lights.spot[i].positionRange.xyz;
        float3 L        = normalize(lightPos - worldPos);
        float  shadow   = shadowsOn ? spotShadow(i, worldPos, Ngeo, L, lightPos) : 1.0;
        shadow = lerp(1.0, shadow, lights.spot[i].outerPad.y);
        result += calcSpot(lights.spot[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    if (shadowsOn)
        result *= shadowCascadeTint(worldPos);
    return result;
}

// The default highlight (strength 0.3, exponent 32): Spine and anything without a material.
float3 shadeLights(float3 base, float3 N, float3 worldPos)
{
    return shadeLightsBlinnPhong(base, N, N, worldPos, 0.3, 32.0);
}
