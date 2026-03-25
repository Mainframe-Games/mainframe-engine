#version 450

layout(location = 0) in vec3 fragPos;
layout(location = 1) in vec3 fragNormal;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform MaterialUBO {
    mat4 model;
    mat4 view;
    mat4 projection;
    vec3 matAmbient;   float _mp0;
    vec3 matDiffuse;   float _mp1;
    vec3 matSpecular;  float _mp2;
    float matShininess;
    float _pad3; float _pad4; float _pad5;
    vec3 lightPos;     float _lp0;
    vec3 viewPos;      float _lp1;
    vec3 lightAmbient; float _lp2;
    vec3 lightDiffuse; float _lp3;
    vec3 lightSpecular;float _lp4;
} ubo;

void main() {
    // Ambient
    vec3 ambient = ubo.lightAmbient * ubo.matAmbient;

    // Diffuse
    vec3 norm = normalize(fragNormal);
    vec3 lightDir = normalize(ubo.lightPos - fragPos);
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = ubo.lightDiffuse * (diff * ubo.matDiffuse);

    // Specular
    vec3 viewDir = normalize(ubo.viewPos - fragPos);
    vec3 reflectDir = reflect(-lightDir, norm);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), ubo.matShininess);
    vec3 specular = ubo.lightSpecular * (spec * ubo.matSpecular);

    outColor = vec4(ambient + diffuse + specular, 1.0);
}
