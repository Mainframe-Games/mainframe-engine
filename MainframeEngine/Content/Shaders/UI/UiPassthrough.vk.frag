#version 450
#extension GL_GOOGLE_include_directive : require

// Copies a UI layer (premultiplied, sRGB-encoded) scaled by `factor` (the opacity filter). When compositing onto a
// swapchain view that encodes sRGB on write, `linearize` converts the colour back to linear so the stored value is
// unchanged (blending then happens in linear space; see docs/design/color-pipeline.md).
#include "common.glsl"

layout(location = 0) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant) uniform PassthroughPush {
    layout(offset = 16) float factor;
    uint linearize;
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = texture(source, inTexCoord) * pc.factor;
    if (pc.linearize != 0u && color.a > 0.0)
        color.rgb = srgbToLinear(color.rgb / color.a) * color.a;
    outColor = color;
}
