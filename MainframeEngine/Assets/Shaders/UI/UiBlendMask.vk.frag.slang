// mask-image: the layer multiplied by the mask layer's alpha.
[[vk::binding(0, 0)]] Sampler2D source;
[[vk::binding(0, 1)]] Sampler2D mask;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    return source.Sample(inTexCoord) * mask.Sample(inTexCoord).a;
}
