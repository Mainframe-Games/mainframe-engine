#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec3 inNormal;

layout(set = 0, binding = 0) uniform ViewProjection {
    mat4 view;
    mat4 projection;
} vp;

layout(push_constant) uniform PushConstants {
    mat4 model;
    vec4 color;
} pc;

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;

void main()
{
    vec4 worldPos = pc.model * vec4(inPosition, 1.0);
    outWorldPos   = worldPos.xyz;
    outNormal     = normalize(mat3(transpose(inverse(pc.model))) * inNormal);
    gl_Position   = vp.projection * vp.view * worldPos;
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
}
