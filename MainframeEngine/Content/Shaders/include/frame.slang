// Per-frame shared set 0, binding 0: the camera (FrameContext / FrameData in C#, std140, 432 bytes).
// Binding 1 of the same set is the lights UBO (lights.slang, default LIGHTS_SET 0 / LIGHTS_BINDING 1).
// Matrices are the C# System.Numerics values (row-major layout): transform with mul(v, M).
#pragma once

#include "common.slang"

struct FrameData
{
    float4x4 view;
    float4x4 projection;
    float4x4 viewProjection;
    float4x4 invProjection;
    float4x4 invViewRotation; // inverse view without translation: camera-relative rays (sky)
    float4 cameraPosition;    // xyz = world position
    float4 viewport;          // width, height, 1/width, 1/height (pixels)
    float4 clip;              // near, far, time (s), exposure
    float4 wind;              // xyz = world direction the wind blows towards (normalized), w = strength (0 = calm)
    float4 windParams;        // x = frequency (Hz), y = turbulence (0..1), z = noise scale (m), w = unused
    float4 fogColor;          // rgb = linear fog light colour, a = 1 when fog is enabled
    float4 fogParams;         // x = density (1/m), y = height (m, fog base), z = height density (falloff 1/m), w = sun scatter
};

[[vk::binding(0, 0)]] ConstantBuffer<FrameData> frame;

// Linear view depth from a [0, 1] depth-buffer value (perspective projection).
float linearizeDepth(float depth)
{
    float near = frame.clip.x;
    float far  = frame.clip.y;
    return near * far / (far - depth * (far - near));
}
