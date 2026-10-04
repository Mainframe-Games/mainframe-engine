#version 450

// Directional/spot shadow casters drawn in instanced batches (MeshRenderer): positions at binding 0 (stride 32,
// MeshVertex), model matrix per instance at binding 1 (MeshInstanceData). Same layout as Shadow2D.vk.vert (the
// light view-projection comes from the dynamic-offset ring at set 0; the push range is unused).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inModel0;
layout(location = 2) in vec4 inModel1;
layout(location = 3) in vec4 inModel2;
layout(location = 4) in vec4 inModel3;

layout(set = 0, binding = 0) uniform LightVP {
    mat4 lightViewProj;
} lvp;

void main()
{
    mat4 model = mat4(inModel0, inModel1, inModel2, inModel3);
    gl_Position = lvp.lightViewProj * model * vec4(inPosition, 1.0);
}
