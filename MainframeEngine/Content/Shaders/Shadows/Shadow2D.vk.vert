#version 450

layout(location = 0) in vec3 inPosition;

layout(set = 0, binding = 0) uniform LightVP {
    mat4 lightViewProj;
} lvp;

layout(push_constant) uniform PC {
    mat4 model;
} pc;

void main()
{
    // System.Numerics matrices use D3D/Vulkan convention: NDC z is already [0,1].
    gl_Position = lvp.lightViewProj * pc.model * vec4(inPosition, 1.0);
}
