// Colour-matrix filters (brightness, contrast, invert, grayscale, sepia, hue-rotate, saturate), from RmlUi's GL3
// backend: an RGB transform applied directly in premultiplied space (the constant column scales with alpha).
[[vk::binding(0, 0)]] Sampler2D source;

struct ColorMatrixPush
{
    [[vk::offset(16)]] float4x4 colorMatrix; // row-major: mul(v, colorMatrix)
};

[[vk::push_constant]] ConstantBuffer<ColorMatrixPush> pc;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    float4 texel = source.Sample(inTexCoord);
    return float4(mul(texel, pc.colorMatrix).xyz, texel.a);
}
