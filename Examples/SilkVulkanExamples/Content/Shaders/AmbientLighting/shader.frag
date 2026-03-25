#version 450

layout(location = 0) out vec4 outColor;

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
    vec3 ambient = ubo.ambientStrength * ubo.lightColor;
    vec3 result = ambient * ubo.objectColor;
    outColor = vec4(result, 1.0);
}
