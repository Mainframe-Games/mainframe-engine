// Alpha-tested Spine shadow casters for directional (cascade) and spot (atlas tile) lights (ADR 0204): the Spine vertex
// (SpineRenderer.Vertex, stride 40) with its UV and, at offset 32, the tint alpha and the node's ShadowAlphaThreshold
// for SpineShadow.vk.frag. Set 0 = light view-projection (dynamic-offset ring), set 1 = the atlas page; the push
// constant is the caster layout's 64-byte model matrix.
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

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outUV;
    [[vk::location(1)]] float2 outAlphaTest; // x = tint alpha, y = threshold
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPosition, [[vk::location(1)]] float2 inUV, [[vk::location(2)]] float2 inAlphaTest)
{
    VsOut o;
    o.outUV = inUV;
    o.outAlphaTest = inAlphaTest;
    o.position = mul(float4(inPosition, 1.0), mul(pc.model, lvp.lightViewProj));
    return o;
}
