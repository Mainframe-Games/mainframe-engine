#version 450

// Alpha-tested directional/spot shadow casters (cutout materials) in instanced batches: Shadow2DInstanced plus the UV
// (binding 0, offset 24 of MeshVertex) for ShadowCutout.vk.frag. Set 0 = light view-projection (dynamic-offset ring).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inModel0;
layout(location = 2) in vec4 inModel1;
layout(location = 3) in vec4 inModel2;
layout(location = 4) in vec4 inModel3;
layout(location = 5) in vec2 inUV;

layout(set = 0, binding = 0) uniform LightVP {
    mat4 lightViewProj;
} lvp;

layout(location = 0) out vec2 outUV;

void main()
{
    mat4 model = mat4(inModel0, inModel1, inModel2, inModel3);
    outUV = inUV;
    gl_Position = lvp.lightViewProj * model * vec4(inPosition, 1.0);
}
