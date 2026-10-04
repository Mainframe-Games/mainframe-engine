#version 450
#extension GL_GOOGLE_include_directive : require

// Object-ID pass (editor picking): writes the instance's object id (NodeId) into an R32_UINT target. Same vertex
// shader and pipeline layout as Mesh.vk.frag; cutout materials discard like they do when lit, so holes stay
// unpickable. Id 0 (the clear value) means "nothing".
#include "material.glsl"

layout(constant_id = 0) const int kAlphaMode = kAlphaOpaque;

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;
layout(location = 3) flat in uint inObjectId;

layout(location = 0) out uint outObjectId;

void main()
{
    if (kAlphaMode == kAlphaCutout && materialAlbedo(materialUv(inUV)).a < material.params.z)
        discard;
    outObjectId = inObjectId;
}
