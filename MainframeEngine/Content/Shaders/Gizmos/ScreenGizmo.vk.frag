#version 450
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

// Screen gizmos are drawn after tonemapping, in the overlay pass, with sRGB-authored colours: normally the target
// stores them as-is (a UNORM view of the swapchain). Only when the swapchain can only be written through an encoding
// sRGB view does the renderer set kLinearizeColors.
layout(constant_id = 0) const bool kLinearizeColors = false;

layout(location = 0) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

void main() {
    vec4 color = fragColor;
    if (kLinearizeColors)
        color.rgb = srgbToLinear(color.rgb);
    outColor = color;
}
