#version 450

layout(location = 0) in vec3 aPos;

layout(set = 0, binding = 0) uniform LightingUBO {
    mat4 model;
    mat4 view;
    mat4 projection;
    vec3 lightColor;
    float _pad0;
    vec3 objectColor;
    float _pad1;
    vec3 lightPos;
    float _pad2;
    float ambientStrength;
} ubo;

void main() {
    gl_Position = ubo.projection * ubo.view * ubo.model * vec4(aPos, 1.0);
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
}
