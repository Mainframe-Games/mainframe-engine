// FoliageMaterial3D point-light shadow casters (ADR 0151): ShadowPointCutoutInstanced plus the foliage wind (see
// Shadow2DFoliageInstanced.vk.vert). The wind push constants sit at offset 0, below ShadowPointCutout.vk.frag's light
// position and range (offset 64). Pairs with ShadowPointCutout.vk.frag.
#define MATERIAL_SET 1
#include "material.slang"
#include "foliage.slang"
#include "wind.slang"

struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

// MeshRenderer's FoliageCasterWind (offset 0, 48 bytes).
struct CasterWind
{
    float4 wind;       // FrameData.wind
    float4 windParams; // FrameData.windParams
    float4 time;       // x = seconds
};

[[vk::push_constant]] ConstantBuffer<CasterWind> pc;

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float4 inModel0;
    [[vk::location(2)]] float4 inModel1;
    [[vk::location(3)]] float4 inModel2;
    [[vk::location(4)]] float4 inModel3;
    [[vk::location(5)]] float2 inUV;
    [[vk::location(6)]] float4 inCustom0;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outFragWorldPos;
    [[vk::location(1)]] float2 outUV;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    worldPos.xyz += windOffset(worldPos.xyz, input.inCustom0, input.inUV, pc.wind, pc.windParams, pc.time.x,
                               foliageWindStrength(), foliageBranchBend());
    VsOut o;
    o.outFragWorldPos = worldPos.xyz;
    o.outUV = input.inUV;
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}
