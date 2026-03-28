#version 450

layout(location = 0) in vec3 inPosition;

layout(set = 0, binding = 0) uniform LightVP {
    mat4 lightViewProj;
} lvp;

layout(push_constant) uniform PC {
    mat4  model;
    vec4  lightPosRange; // xyz = light world position, w = range
} pc;

layout(location = 0) out vec3 outFragWorldPos;

void main()
{
    vec4 worldPos   = pc.model * vec4(inPosition, 1.0);
    outFragWorldPos = worldPos.xyz;

    vec4 pos = lvp.lightViewProj * worldPos;
    pos.z    = pos.z * 0.5 + pos.w * 0.5;
    gl_Position = pos;
}
