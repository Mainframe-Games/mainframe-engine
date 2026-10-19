// drop-shadow: the layer's alpha tinted with the (premultiplied) shadow colour, sampled at an offset and limited to
// the filtered region. Blurred afterwards when the shadow has a radius.
[[vk::binding(0, 0)]] Sampler2D source;

struct DropShadowPush
{
    [[vk::offset(16)]] float4 color;
    float2 texCoordMin;
    float2 texCoordMax;
};

[[vk::push_constant]] ConstantBuffer<DropShadowPush> pc;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    float2 inRegion = step(pc.texCoordMin, inTexCoord) * step(inTexCoord, pc.texCoordMax);
    return source.Sample(inTexCoord).a * inRegion.x * inRegion.y * pc.color;
}
