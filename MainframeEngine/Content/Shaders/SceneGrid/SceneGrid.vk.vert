#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(set = 0, binding = 0) uniform ViewProjection {
    mat4 view;
    mat4 projection;
} vp;

layout(location = 0) out vec4 fragColor;

void main() {
    gl_Position = vp.projection * vp.view * vec4(inPosition, 1.0);
    fragColor = inColor;
}
