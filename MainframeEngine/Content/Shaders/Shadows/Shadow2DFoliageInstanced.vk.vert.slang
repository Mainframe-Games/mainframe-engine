// FoliageMaterial3D directional/spot shadow casters (ADR 0151): Shadow2DCutoutInstanced plus the foliage wind, so the
// shadows sway with the leaves. Custom0 comes from the second vertex stream (binding 2, location 6); the wind and time
// are push constants (the caster layouts' set 0 is the light matrix, not the frame); the material (its wind scales) is
// set 1, as for every cutout caster. Pairs with ShadowCutout.vk.frag.
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
    [[vk::location(0)]] float2 outUV;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    worldPos.xyz += windOffset(worldPos.xyz, input.inCustom0, input.inUV, pc.wind, pc.windParams, pc.time.x,
                               foliageWindStrength(), foliageBranchBend());
    VsOut o;
    o.outUV = input.inUV;
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}
