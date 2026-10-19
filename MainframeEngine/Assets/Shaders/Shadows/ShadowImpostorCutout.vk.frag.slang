// Directional/spot shadow pass of ImpostorMaterial3D (ADR 0172): the cutout test of ShadowCutout.vk.frag, then the
// material's shadow density (material.emission.y, ImpostorMaterial3D.ShadowDensity) thins the silhouette
// (include/shadow_density.slang): a whole tree baked into one view is far denser than the sun's view through a real
// canopy, which lets light through between its clusters.
#define MATERIAL_SET 1
#include "material.slang"
#include "shadow_density.slang"

[shader("fragment")]
void main([[vk::location(0)]] float2 inUV, float4 fragCoord : SV_Position)
{
    float2 uv = materialUv(inUV);
    if (materialAlbedo(uv, ddx(uv), ddy(uv)).a < material.params.z || !shadowDensityKeeps(material.emission.y, fragCoord.xy))
        discard;
}
