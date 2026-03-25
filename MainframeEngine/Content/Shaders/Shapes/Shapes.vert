#version 330 core

layout(location = 0) in vec3 vPos;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 fWorldPos;
out vec3 fNormal;

void main()
{
    vec4 worldPos = uModel * vec4(vPos, 1.0);
    fWorldPos = worldPos.xyz;
    fNormal   = normalize(mat3(transpose(inverse(uModel))) * vNormal);
    gl_Position = uProjection * uView * worldPos;
}
