// Alpha-tested Spine shadow casters for point lights (ADR 0204): SpineShadow2D.vk.vert with the world position for the
// linear distance of SpineShadowPoint.vk.frag. Push constant: the point caster layout's model + light position/range.
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
    [[vk::location(1)]] float2 outUV;
    [[vk::location(2)]] float2 outAlphaTest; // x = tint alpha, y = threshold
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPosition, [[vk::location(1)]] float2 inUV, [[vk::location(2)]] float2 inAlphaTest)
{
    float4 worldPos = mul(float4(inPosition, 1.0), pc.model);
    VsOut o;
    o.outFragWorldPos = worldPos.xyz;
    o.outUV = inUV;
    o.outAlphaTest = inAlphaTest;
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}
