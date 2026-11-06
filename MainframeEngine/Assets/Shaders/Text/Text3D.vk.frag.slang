// Batched Label3D glyphs (Text3DRenderer, ADR 0258): the unshaded mesh path's colour (the label colour × the atlas
// texel), fogged like other geometry. Blended glyphs output their coverage as alpha; cutouts discard below the cutoff and
// write opaque.
#include "text3d.slang"
#include "fog.slang"

struct FsIn
{
    [[vk::location(0)]] float3 worldPos;
    [[vk::location(1)]] float2 uv;
    [[vk::location(2)]] nointerpolation float4 color;
    [[vk::location(3)]] nointerpolation uint page;
};

[shader("fragment")]
float4 main(FsIn input) : SV_Target0
{
    float4 albedo = input.color * samplePage(input.page, input.uv);
    if (pc.cutout != 0u && albedo.a < pc.cutoff)
        discard;
    float3 color = applyFog(albedo.rgb, input.worldPos);
    return float4(color, pc.cutout != 0u ? 1.0 : albedo.a);
}
