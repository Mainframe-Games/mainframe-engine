#version 450
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

// ImGui is drawn after tonemapping, in the overlay pass. Its colours are sRGB-authored: normally the target stores
// them as-is (a UNORM view of the swapchain), exactly like before the colour pipeline. Only when the swapchain can
// only be written through an encoding sRGB view does the renderer set kLinearizeColors.
layout(constant_id = 0) const bool kLinearizeColors = false;

layout(location = 0) in vec2 inUV;
layout(location = 1) in vec4 inColor;

layout(set = 0, binding = 0) uniform sampler2D fontSampler;

layout(location = 0) out vec4 outColor;

void main() {
    vec4 color = inColor;
    if (kLinearizeColors)
        color.rgb = srgbToLinear(color.rgb);
    outColor = color * texture(fontSampler, inUV);
}
