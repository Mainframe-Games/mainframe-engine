#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(location = 0) out vec4 fragColor;

void main() {
    gl_Position = frame.viewProjection * vec4(inPosition, 1.0);
    fragColor = inColor;
}
