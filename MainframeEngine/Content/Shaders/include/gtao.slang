// Ground-truth ambient occlusion (ADR 0165; Jimenez et al. 2016, "Practical Real-Time Strategies for Accurate Indirect
// Occlusion"): the push block and helpers shared by the SSAO effect's three passes (SsaoEffect in C#):
//   Post/Gtao.vk.frag        half resolution: AO from the prepass depth → RG16F (r raw visibility, g linear depth)
//   Post/GtaoDenoise.vk.frag half resolution, twice: 4 × 4 depth-aware bilateral blurs (remove the rotation pattern)
//   Post/GtaoUpsample.vk.frag full resolution: joint bilateral upsample against the full depth, intensity and power →
//                             RGBA8 (r AO; g, b, a reserved: contact shadow, bent normal), set 0 binding 5
// Half-resolution texel h stands for full-resolution texel 2h (its depth, its view position).
#pragma once

#include "common.slang"

// SsaoEffect.GtaoPush (std430, 96 bytes). The projection is the one the prepass rasterised with (jittered with TAA):
// clip = view · M (row vectors), so x' = x·M11 + z·M31 + M41, y' = y·M22 + z·M32 + M42, z' = z·M33 + M43, w = z·M34 + M44.
struct GtaoParams
{
    float4 proj;    // M11, M22, M31, M32
    float4 proj2;   // M41, M42, M33, M43
    float4 proj3;   // M34, M44, full width, full height (pixels)
    float4 shape;   // radius (m), max radius (full pixels), horizon bias (rad), detail
    float4 filter;  // intensity, power, denoise depth tolerance, upsample depth tolerance (relative)
    float4 noise;   // x = temporal index (0 without TAA), y = slices, z = steps per side, w = denoise window start (−1, −2)
};

[[vk::push_constant]] ConstantBuffer<GtaoParams> gtao;

// View-space depth (negative in front of the camera) of a [0, 1] depth-buffer value.
float gtaoViewZ(float depth)
{
    return (gtao.proj2.w - depth * gtao.proj3.y) / (depth * gtao.proj3.x - gtao.proj2.z);
}

// The view-space position of full-resolution texel `p` (integer coordinates) at a [0, 1] depth: its centre unprojected.
float3 gtaoViewPosition(int2 p, float depth)
{
    float z = gtaoViewZ(depth);
    float w = z * gtao.proj3.x + gtao.proj3.y;
    float2 uv = (float2(p) + 0.5) / gtao.proj3.zw;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0); // the main pass flips the viewport: NDC +Y is row 0
    float x = (ndc.x * w - z * gtao.proj.z - gtao.proj2.x) / gtao.proj.x;
    float y = (ndc.y * w - z * gtao.proj.w - gtao.proj2.y) / gtao.proj.y;
    return float3(x, y, z);
}

// Linear distance in front of the camera (metres) of a [0, 1] depth.
float gtaoLinearDepth(float depth)
{
    return -gtaoViewZ(depth);
}

// Depth values at or beyond this are the sky (the cleared far plane).
static const float kGtaoSkyDepth = 1.0;

// The linear depth written for sky texels: far beyond any radius, so no occluder and no blur weight comes from the sky.
static const float kGtaoSkyDistance = 65504.0;
