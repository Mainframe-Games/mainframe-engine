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
    vec4 pos = lvp.lightViewProj * pc.model * vec4(inPosition, 1.0);
    // Vulkan depth [0,1]: adjust z from GLM/C# convention [-w, w]
    pos.z = pos.z * 0.5 + pos.w * 0.5;
    gl_Position = pos;
}
