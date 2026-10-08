#include "frame.slang"

struct PushConstants
{
    float4x4 model;
    float4   worldNormal; // xyz = precomputed world-space normal, w = unused
};

[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outUV;
    [[vk::location(1)]] float4 outColor;
    [[vk::location(2)]] float3 outWorldPos;
    [[vk::location(3)]] float3 outNormal;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPos, [[vk::location(1)]] float2 inUV, [[vk::location(2)]] float4 inColor)
{
    float4 worldPos = mul(float4(inPos, 1.0), pc.model);
    VsOut o;
    o.position    = mul(worldPos, frame.viewProjection);
    o.outUV       = inUV;
    o.outColor    = inColor;
    o.outWorldPos = worldPos.xyz;
    o.outNormal   = pc.worldNormal.xyz;
    return o;
}
