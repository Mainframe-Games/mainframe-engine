#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec4 inColor;

layout(push_constant) uniform PushConstants {
    mat4 model;
    vec4 worldNormal; // xyz = precomputed world-space normal, w = unused
} pc;

layout(location = 0) out vec2 outUV;
layout(location = 1) out vec4 outColor;
layout(location = 2) out vec3 outWorldPos;
layout(location = 3) out vec3 outNormal;

void main() {
    vec4 worldPos   = pc.model * vec4(inPos, 1.0);
    gl_Position     = frame.viewProjection * worldPos;
    outUV           = inUV;
    outColor        = inColor;
    outWorldPos     = worldPos.xyz;
    outNormal       = pc.worldNormal.xyz;
}
