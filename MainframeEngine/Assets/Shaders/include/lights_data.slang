// The lights UBO (LightEnvironment.WriteUbo, std140, 1200 bytes with the default limits) without any shading: what
// lights.slang shades with, and what fog.slang reads for the sun scatter (the sky shaders include it without the
// shadow set). Define LIGHTS_SET / LIGHTS_BINDING before including to override where the UBO lives (default: the
// per-frame set 0, binding 1, next to the camera data). Colours in the UBO are linear (LightEnvironment converts the
// sRGB-authored values when packing).
#pragma once

#include "common.slang"

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
    float4     ambientColor;     // xyz = ambient colour × ambient energy, w = ambient energy (scales the sky's, ADR 0150)
    float4     cameraPosition;   // xyz = camera position, w = environment flags (kEnv* below)
    int4       counts;           // x = numDir, y = numPoint, z = numSpot, w = 1: no shadow maps for this view
    DirLight   dir[MAX_DIR_LIGHTS];
    PointLight point[MAX_POINT_LIGHTS];
    SpotLight  spot[MAX_SPOT_LIGHTS];
};

[[vk::binding(LIGHTS_BINDING, LIGHTS_SET)]] ConstantBuffer<LightsUBO> lights;

// Environment flags (lights.cameraPosition.w; LightEnvironment.EnvironmentFlags in C#).
static const uint kEnvSkyDiffuse  = 1u; // ambient light from the sky's irradiance (else ambientColor)
static const uint kEnvSkySpecular = 2u; // reflections from the sky's prefiltered radiance (else a uniform ambientColor sky)
static const uint kEnvNoSpecular  = 4u; // reflected light disabled

uint environmentFlags()
{
    return uint(lights.cameraPosition.w);
}
