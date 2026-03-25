#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec4 inColor;

layout(set = 0, binding = 0) uniform ViewProjection {
    mat4 view;
    mat4 projection;
} vp;

layout(push_constant) uniform PushConstants {
    mat4 model;
} pc;

layout(location = 0) out vec2 outUV;
layout(location = 1) out vec4 outColor;

void main() {
    gl_Position = vp.projection * vp.view * pc.model * vec4(inPos, 1.0);
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
    outUV = inUV;
    outColor = inColor;
}
