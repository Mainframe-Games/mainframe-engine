#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"

layout(location = 0) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

void main() {
    // Line colours are sRGB-authored with straight alpha; the HDR scene target is linear. No distance fade (unlike
    // the scene grid): a collision shape far away is still a collision shape.
    outColor = vec4(srgbToLinear(fragColor.rgb), fragColor.a);
}
