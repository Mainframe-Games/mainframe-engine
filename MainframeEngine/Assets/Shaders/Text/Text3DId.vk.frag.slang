// Object-ID pass for batched Label3D glyphs (Text3DRenderer, ADR 0258; editor picking): writes the label's object id into
// the R32_UINT target. Blended glyph quads pick whole (as the mesh path's blended surfaces do); cutouts discard like
// they do in colour, so their holes stay unpickable.
#include "text3d.slang"

struct FsIn
{
    [[vk::location(1)]] float2 uv;
    [[vk::location(2)]] nointerpolation float4 color;
    [[vk::location(3)]] nointerpolation uint page;
    [[vk::location(4)]] nointerpolation uint objectId;
};

[shader("fragment")]
uint main(FsIn input) : SV_Target0
{
    if (pc.cutout != 0u && input.color.a * samplePage(input.page, input.uv).a < pc.cutoff)
        discard;
    return input.objectId;
}
