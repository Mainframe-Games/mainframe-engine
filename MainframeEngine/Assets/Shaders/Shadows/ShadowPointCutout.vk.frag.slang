// Point-light shadow pass of cutout materials: the alpha test of ShadowCutout.vk.frag, then the linear distance of
// ShadowPoint.vk.frag.
#define MATERIAL_SET 1
#include "material.slang"

struct PC
{
    float4x4 model;
    float4   lightPosRange; // xyz = light world position, w = range
};

[[vk::push_constant]] ConstantBuffer<PC> pc;

[shader("fragment")]
float main([[vk::location(0)]] float3 inFragWorldPos, [[vk::location(1)]] float2 inUV) : SV_Depth
{
    float2 uv = materialUv(inUV);
    if (materialAlbedo(uv, ddx(uv), ddy(uv)).a < material.params.z)
        discard;
    return length(inFragWorldPos - pc.lightPosRange.xyz) / pc.lightPosRange.w;
}
