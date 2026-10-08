// Copies a UI layer (premultiplied, sRGB-encoded) scaled by `factor` (the opacity filter). When compositing onto a
// swapchain view that encodes sRGB on write, `linearize` converts the colour back to linear so the stored value is
// unchanged (blending then happens in linear space; see docs/design/color-pipeline.md).
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D source;

struct PassthroughPush
{
    [[vk::offset(16)]] float factor;
    uint linearize;
};

[[vk::push_constant]] ConstantBuffer<PassthroughPush> pc;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    float4 color = source.Sample(inTexCoord) * pc.factor;
    if (pc.linearize != 0u && color.a > 0.0)
        color.rgb = srgbToLinear(color.rgb / color.a) * color.a;
    return color;
}
