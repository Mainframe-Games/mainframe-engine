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

    // System.Numerics matrices use D3D/Vulkan convention: NDC z is already [0,1].
    // gl_FragDepth is overridden with linear distance anyway, so clip-z only matters for clipping.
    gl_Position = lvp.lightViewProj * worldPos;
}
