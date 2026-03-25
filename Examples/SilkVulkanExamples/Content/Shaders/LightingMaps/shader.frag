#version 450

layout(location = 0) in vec3 fragPos;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec2 fragTexCoord;
layout(location = 0) out vec4 outColor;

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

layout(set = 0, binding = 1) uniform sampler2D diffuseMap;
layout(set = 0, binding = 2) uniform sampler2D specularMap;

void main() {
    vec3 diffuseSample  = texture(diffuseMap,  fragTexCoord).rgb;
    vec3 specularSample = texture(specularMap, fragTexCoord).rgb;

    // Ambient
    vec3 ambient = ubo.lightAmbient * diffuseSample;

    // Diffuse
    vec3 norm = normalize(fragNormal);
    vec3 lightDir = normalize(ubo.lightPos - fragPos);
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = ubo.lightDiffuse * diff * diffuseSample;

    // Specular
    vec3 viewDir = normalize(ubo.viewPos - fragPos);
    vec3 reflectDir = reflect(-lightDir, norm);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), ubo.shininess);
    vec3 specular = ubo.lightSpecular * spec * specularSample;

    outColor = vec4(ambient + diffuse + specular, 1.0);
}
