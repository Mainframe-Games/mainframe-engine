// ImpostorMaterial3D directional shadow casters (ADR 0172): each instance's impostor quad faces the baked view nearest the
// light (pc.pass.yzw, include/foliage_caster.slang), textured with that one view, so the cutout fragment shader
// (ShadowCutout.vk.frag: the material is set 1) casts the tree's silhouette as the sun sees it. Two triangles per tree
// in the far cascades instead of a level of thousands of leaf cards. Honours the per-instance visibility range outside
// coarse passes.
#define MATERIAL_SET 1
#include "material.slang"
#include "foliage.slang"
#include "foliage_caster.slang"
#include "impostor.slang"

struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

struct VsIn
{
    [[vk::location(1)]] float4 inModel0;
    [[vk::location(2)]] float4 inModel1;
    [[vk::location(3)]] float4 inModel2;
    [[vk::location(4)]] float4 inModel3;
    [[vk::location(5)]] float2 inUV; // the quad corner (0..1, v down)
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
    float3x3 m = (float3x3)model;
    float frames = impostorFrames();
    bool hemi = impostorHemi();
    float radius = impostorRadius();

    // The baked view nearest the light; the quad faces that view exactly, so its image maps onto the quad.
    float3 light = normalize(mul(m, pc.pass.yzw));
    float2 cell = clamp(round(impostorEncode(light, hemi) * (frames - 1.0)), 0.0, frames - 1.0);
    float3 direction = impostorDecode(cell / (frames - 1.0), hemi);
    float3 right, up;
    impostorBasis(direction, right, up);
    float2 c = float2(input.inUV.x * 2.0 - 1.0, 1.0 - input.inUV.y * 2.0);
    float3 offset = (right * c.x + up * c.y) * radius;
    float4 worldPos = mul(float4(impostorCenter() + offset, 1.0), model);

    VsOut o;
    o.outUV = (cell + clamp(input.inUV, 0.002, 0.998)) / frames;
    o.position = casterInstanceVisible(input.inModel3.xyz) ? mul(worldPos, lvp.lightViewProj) : kCollapsedPosition;
    return o;
}
