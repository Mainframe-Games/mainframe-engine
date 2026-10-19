// Directional/spot shadow casters drawn in instanced batches (MeshRenderer): positions at binding 0 (stride 32,
// MeshVertex), model matrix per instance at binding 1 (MeshInstanceData). Same layout as Shadow2D.vk.vert (the
// light view-projection comes from the dynamic-offset ring at set 0; the push range is unused).
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

[shader("vertex")]
float4 main(VsIn input) : SV_Position
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    return mul(float4(input.inPosition, 1.0), mul(model, lvp.lightViewProj)); // (lightViewProj · model) · v, as before
}
