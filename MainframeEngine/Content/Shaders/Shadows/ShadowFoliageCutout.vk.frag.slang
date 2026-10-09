// Directional/spot shadow pass of FoliageMaterial3D (ADR 0172): the cutout test of ShadowCutout.vk.frag, then the
// material's shadow density (material.foliage3.w, FoliageMaterial3D.ShadowDensity; 1 = all) thins what it casts with a
// stable 4 × 4 Bayer pattern on the shadow map's texels, which the PCF/PCSS kernel reads as a partial shadow.
#define MATERIAL_SET 1
#include "material.slang"
#include "shadow_density.slang"

[shader("fragment")]
void main([[vk::location(0)]] float2 inUV, float4 fragCoord : SV_Position)
{
    float2 uv = materialUv(inUV);
    if (materialAlbedo(uv, ddx(uv), ddy(uv)).a < material.params.z || !shadowDensityKeeps(material.foliage3.w, fragCoord.xy))
        discard;
}
