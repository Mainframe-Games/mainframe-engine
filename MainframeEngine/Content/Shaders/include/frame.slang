// Per-frame shared set 0, binding 0: the camera (FrameContext / FrameData in C#, std140, 560 bytes).
// Binding 1 of the same set is the lights UBO (lights.slang, default LIGHTS_SET 0 / LIGHTS_BINDING 1), bindings 2-4 the
// sky lighting (environment.slang), binding 5 the screen-space ambient occlusion (ambient_occlusion.slang).
// With TAA (ADR 0163) projection, viewProjection and invProjection are jittered; prevViewProjection is last frame's
// unjittered matrix and jitter holds both frames' offsets (unjitterClip, velocityFromClip below).
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
    float4x4 prevViewProjection; // last frame's unjittered view-projection (this frame's when temporal.y = 0)
    float4 jitter;            // xy = this frame's projection jitter, zw = last frame's (NDC, +Y up; 0 without TAA)
    float4 temporal;          // x = last frame's time (s), y = 1 when the prev* fields are last frame's, z = jitter sample index
    float4 prevWind;          // last frame's wind (foliage motion vectors)
    float4 prevWindParams;    // last frame's windParams
};

[[vk::binding(0, 0)]] ConstantBuffer<FrameData> frame;

// Linear view depth from a [0, 1] depth-buffer value (perspective projection).
float linearizeDepth(float depth)
{
    float near = frame.clip.x;
    float far  = frame.clip.y;
    return near * far / (far - depth * (far - near));
}

// A clip position of this frame's (jittered) viewProjection without the jitter: what the unjittered projection gives.
float4 unjitterClip(float4 clip)
{
    return float4(clip.xy - frame.jitter.xy * clip.w, clip.zw);
}

// Screen UV (0,0 top-left, 1,1 bottom-right; the image's rows) of an NDC position: the main pass flips the viewport, so
// NDC +Y is up and lands on row 0.
float2 ndcToScreenUv(float2 ndc)
{
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
}

// The motion vector of a point (ADR 0163): its screen UV this frame minus last frame, both unjittered, so
// previousUv = uv - velocity. `current` is the unjittered clip position (unjitterClip), `previous` the one through
// prevViewProjection.
float2 velocityFromClip(float4 current, float4 previous)
{
    float2 a = current.xy / current.w;
    float2 b = previous.xy / previous.w;
    return (a - b) * float2(0.5, -0.5);
}
