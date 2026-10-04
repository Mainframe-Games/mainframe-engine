#version 450

// Point-light shadow casters drawn in instanced batches (MeshRenderer). Pairs with ShadowPoint.vk.frag, which reads
// the light position and range from the push constants (offset 64; the model matrix slot is unused here).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inModel0;
layout(location = 2) in vec4 inModel1;
layout(location = 3) in vec4 inModel2;
layout(location = 4) in vec4 inModel3;

layout(set = 0, binding = 0) uniform LightVP {
    mat4 lightViewProj;
} lvp;

layout(location = 0) out vec3 outFragWorldPos;

void main()
{
    mat4 model = mat4(inModel0, inModel1, inModel2, inModel3);
    vec4 worldPos = model * vec4(inPosition, 1.0);
    outFragWorldPos = worldPos.xyz;
    gl_Position = lvp.lightViewProj * worldPos;
}
