// Batched Label3D text (Text3DRenderer, ADR 0258): set 0 frame + lights (fog), set 1 the page group (one sampler and up
// to eight atlas page images, white where a slot is empty), set 2 the label records of this frame slot (std430). One
// instance per glyph quad (binding 0, instance rate): its rectangle in label space (world units, +Y up), its atlas UVs,
// its label record and page. Six vertices per instance (SV_VulkanVertexID), no vertex buffer.
#pragma once

#include "frame.slang"

static const uint kTextPages = 8;

// Text3DLabelRecord in C# (112 bytes).
struct TextLabelRecord
{
    float4x4 model;       // the label's model matrix for this view (billboard and FixedSize applied on the CPU)
    float4 color;         // linear rgb (× 1 / exposure with IgnoreExposure), a = alpha
    float4 outlineColor;  // the same for the outline
    uint4 info;           // x = object id, y = flags (unused), zw = 0
};

[[vk::binding(0, 1)]] SamplerState pageSampler;
[[vk::binding(1, 1)]] Texture2D pages[kTextPages];
[[vk::binding(0, 2)]] StructuredBuffer<TextLabelRecord> labels;

// Text3DPush in C#: per draw.
struct TextPush
{
    uint cutout;   // 1: alpha-tested (discard below cutoff, opaque output), 0: blended
    float cutoff;  // the alpha cutoff of cutouts (0.5, StandardMaterial3D's default)
    uint pad0;
    uint pad1;
};

[[vk::push_constant]] ConstantBuffer<TextPush> pc;

static const uint kTextOutlineBit = 0x80000000u;

// The colour pass reads the world position (fog), the object-ID pass the object id (Text3DId.vk.vert defines
// TEXT3D_OBJECT_ID): each vertex shader writes only what its fragment shader reads.
struct TextVsOut
{
    float4 position : SV_Position;
#ifndef TEXT3D_OBJECT_ID
    [[vk::location(0)]] float3 worldPos;
#endif
    [[vk::location(1)]] float2 uv;
    [[vk::location(2)]] nointerpolation float4 color;
    [[vk::location(3)]] nointerpolation uint page;
#ifdef TEXT3D_OBJECT_ID
    [[vk::location(4)]] nointerpolation uint objectId;
#endif
};

// The page's coverage at uv, with gradients taken in uniform control flow (the page differs between glyphs of a draw, so
// each slot is sampled under its own branch with a constant index).
float4 samplePage(uint page, float2 uv)
{
    float2 dx = materialGrad(ddx(uv));
    float2 dy = materialGrad(ddy(uv));
    float4 texel = float4(1.0, 1.0, 1.0, 0.0);
    [ForceUnroll]
    for (uint i = 0; i < kTextPages; i++)
    {
        if (page == i)
            texel = pages[i].SampleGrad(pageSampler, uv, dx, dy);
    }

    return texel;
}
