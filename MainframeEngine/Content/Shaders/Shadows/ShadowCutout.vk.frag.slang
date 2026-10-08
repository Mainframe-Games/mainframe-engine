// Directional/spot shadow pass of cutout materials: discards where the material's alpha is below its cutoff, so
// leaves and fences cast their holes. The material set is set 1 of the cutout caster layout.
#define MATERIAL_SET 1
#include "material.slang"

[shader("fragment")]
void main([[vk::location(0)]] float2 inUV)
{
    float2 uv = materialUv(inUV);
    if (materialAlbedo(uv, ddx(uv), ddy(uv)).a < material.params.z)
        discard;
}
