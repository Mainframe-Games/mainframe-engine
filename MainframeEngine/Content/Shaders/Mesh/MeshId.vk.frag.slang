// Object-ID pass (editor picking): writes the instance's object id (NodeId) into an R32_UINT target. Same vertex
// shader and pipeline layout as Mesh.vk.frag; cutout materials discard like they do when lit, so holes stay
// unpickable. Id 0 (the clear value) means "nothing".
#include "material.slang"

[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] nointerpolation uint inObjectId;
};

[shader("fragment")]
uint main(FsIn input) : SV_Target0
{
    float2 uv = materialUv(input.inUV);
    if (kAlphaMode == kAlphaCutout && materialAlbedo(uv, ddx(uv), ddy(uv)).a < material.params.z)
        discard;
    return input.inObjectId;
}
