#version 450

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aTexCoord;

layout(location = 0) out vec2 fragTexCoord;

layout(set = 0, binding = 0) uniform UBO {
    mat4 model;
    mat4 view;
    mat4 projection;
} ubo;

void main() {
    gl_Position = ubo.projection * ubo.view * ubo.model * vec4(aPos, 1.0);
    // Vulkan NDC Z remap
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
    fragTexCoord = aTexCoord;
}
