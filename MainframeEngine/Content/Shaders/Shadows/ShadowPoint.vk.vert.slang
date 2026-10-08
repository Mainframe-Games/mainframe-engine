struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

struct PC
{
    float4x4 model;
    float4   lightPosRange; // xyz = light world position, w = range
};

[[vk::push_constant]] ConstantBuffer<PC> pc;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outFragWorldPos;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPosition)
{
    float4 worldPos = mul(float4(inPosition, 1.0), pc.model);
    VsOut o;
    o.outFragWorldPos = worldPos.xyz;

    // System.Numerics matrices use D3D/Vulkan convention: NDC z is already [0,1].
    // The fragment depth is overridden with linear distance anyway, so clip-z only matters for clipping.
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}
