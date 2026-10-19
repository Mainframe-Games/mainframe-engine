struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

struct PC
{
    float4x4 model;
};

[[vk::push_constant]] ConstantBuffer<PC> pc;

[shader("vertex")]
float4 main([[vk::location(0)]] float3 inPosition) : SV_Position
{
    // System.Numerics matrices use D3D/Vulkan convention: NDC z is already [0,1].
    return mul(float4(inPosition, 1.0), mul(pc.model, lvp.lightViewProj)); // (lightViewProj · model) · v, as before

}
