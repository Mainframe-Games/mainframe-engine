// Depth prepass (ADR 0163): writes the velocity (R16G16_SFLOAT, screen-UV motion: previousUv = uv - velocity) of
// opaque and cutout surfaces; the depth is the pass's. Cutout materials alpha-test exactly as their colour shaders do
// (Mesh.vk.frag, Foliage.vk.frag: the albedo × the vertex colour's alpha against the cutoff), so the scene pass can
// skip its own discard and keep only what the prepass kept (an EQUAL depth test). Set 2 is the material.
#include "frame.slang"
#include "material.slang"

[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

// The vertex colour's alpha multiplies the albedo's (MeshDepthExt.vk.vert, FoliageDepth.vk.vert).
[vk::constant_id(1)] const bool kVertexColor = false;

struct FsIn
{
    [[vk::location(0)]] float2 inUV;
    [[vk::location(1)]] float4 inCurrent;
    [[vk::location(2)]] float4 inPrevious;
    [[vk::location(3)]] float inAlpha;
};

[shader("fragment")]
float2 main(FsIn input) : SV_Target0
{
    if (kAlphaMode == kAlphaCutout)
    {
        float2 uv = materialUv(input.inUV);
        float alpha = materialAlbedo(uv, ddx(uv), ddy(uv)).a;
        if (kVertexColor)
            alpha *= input.inAlpha;
        if (alpha < material.params.z)
            discard;
    }

    return velocityFromClip(input.inCurrent, input.inPrevious);
}
