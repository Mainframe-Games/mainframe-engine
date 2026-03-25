#version 450

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

layout(location = 0) out vec3 fragPos;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec2 fragTexCoord;

layout(set = 0, binding = 0) uniform LightingUBO {
    mat4 model;
    mat4 view;
    mat4 projection;
    vec3 lightPos;     float _p0;
    vec3 viewPos;      float _p1;
    vec3 lightAmbient; float _p2;
    vec3 lightDiffuse; float _p3;
    vec3 lightSpecular;float _p4;
    float shininess;
} ubo;

void main() {
    vec4 worldPos = ubo.model * vec4(aPos, 1.0);
    fragPos = worldPos.xyz;
    fragNormal = mat3(transpose(inverse(ubo.model))) * aNormal;
    fragTexCoord = aTexCoord;
    gl_Position = ubo.projection * ubo.view * worldPos;
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
}
