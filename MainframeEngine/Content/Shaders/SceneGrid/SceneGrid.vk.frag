#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"

layout(location = 0) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

void main() {
    // Fade with view distance (near/far come from the camera's projection, set 0).
    float depth = 1.0 - linearizeDepth(gl_FragCoord.z) / frame.clip.y;
    outColor = vec4(vec3(depth), depth) * fragColor;
}
