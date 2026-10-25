// Point-light shadow pass of a SpineNode (ADR 0204): the alpha test of SpineShadow.vk.frag, then the linear distance of
// Shadows/ShadowPoint.vk.frag.
[[vk::binding(0, 1)]] Sampler2D uTexture;

struct PC
{
    float4x4 model;
    float4   lightPosRange; // xyz = light world position, w = range
};

[[vk::push_constant]] ConstantBuffer<PC> pc;

[shader("fragment")]
float main([[vk::location(0)]] float3 inFragWorldPos, [[vk::location(1)]] float2 inUV, [[vk::location(2)]] float2 inAlphaTest) : SV_Depth
{
    if (uTexture.Sample(inUV).a * inAlphaTest.x < inAlphaTest.y)
        discard;
    return length(inFragWorldPos - pc.lightPosRange.xyz) / pc.lightPosRange.w;
}
