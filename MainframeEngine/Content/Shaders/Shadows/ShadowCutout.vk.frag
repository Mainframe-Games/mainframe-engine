#version 450
#extension GL_GOOGLE_include_directive : require

// Directional/spot shadow pass of cutout materials: discards where the material's alpha is below its cutoff, so
// leaves and fences cast their holes. The material set is set 1 of the cutout caster layout.
#define MATERIAL_SET 1
#include "material.glsl"

layout(location = 0) in vec2 inUV;

void main()
{
    vec2 uv = materialUv(inUV);
    if (materialAlbedo(uv, dFdx(uv), dFdy(uv)).a < material.params.z)
        discard;
}
