#version 450

layout(location = 0) in vec3 fragPos;
layout(location = 1) in vec3 fragNormal;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform LightingUBO {
    mat4 model;
    mat4 view;
    mat4 projection;
    vec3 lightPos;
    float _p0;
    vec3 viewPos;
    float _p1;
    vec3 lightColor;
    float _p2;
    vec3 objectColor;
    float _p3;
} ubo;

void main() {
    // Ambient
    float ambientStrength = 0.1;
    vec3 ambient = ambientStrength * ubo.lightColor;

    // Diffuse
    vec3 norm = normalize(fragNormal);
    vec3 lightDir = normalize(ubo.lightPos - fragPos);
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * ubo.lightColor;

    // Specular
    float specularStrength = 0.5;
    vec3 viewDir = normalize(ubo.viewPos - fragPos);
    vec3 reflectDir = reflect(-lightDir, norm);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), 32.0);
    vec3 specular = specularStrength * spec * ubo.lightColor;

    vec3 result = (ambient + diffuse + specular) * ubo.objectColor;
    outColor = vec4(result, 1.0);
}
