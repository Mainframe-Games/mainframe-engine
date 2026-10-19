// Point-light shadow casters drawn in instanced batches (MeshRenderer). Pairs with ShadowPoint.vk.frag, which reads
// the light position and range from the push constants (offset 64; the model matrix slot is unused here).
struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float4 inModel0;
    [[vk::location(2)]] float4 inModel1;
    [[vk::location(3)]] float4 inModel2;
    [[vk::location(4)]] float4 inModel3;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outFragWorldPos;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    VsOut o;
    o.outFragWorldPos = worldPos.xyz;
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}
