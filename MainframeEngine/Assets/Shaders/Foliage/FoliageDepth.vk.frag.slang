// Depth prepass (ADR 0163) of FoliageMaterial3D: Mesh/MeshDepth.vk.frag plus the per-instance visibility range's dithered
// bands (ADR 0172), so the scene pass's EQUAL test keeps exactly what this pass kept. Writes the velocity.
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "alpha_dither.slang"

[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

// The vertex colour's alpha multiplies the albedo's (always: foliage reads the second stream).
[vk::constant_id(1)] const bool kVertexColor = false;

struct FsIn
{
    [[vk::location(0)]] float2 inUV;
    [[vk::location(1)]] float4 inCurrent;
    [[vk::location(2)]] float4 inPrevious;
    [[vk::location(3)]] float inAlpha;
    [[vk::location(4)]] float inDistance; // the instance's distance to the camera
};

[shader("fragment")]
float2 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    if (kAlphaMode == kAlphaCutout)
    {
        float2 uv = materialUv(input.inUV);
        float alpha = materialAlbedo(uv, ddx(uv), ddy(uv)).a;
        if (kVertexColor)
            alpha *= input.inAlpha;
        if (!cutoutKeeps(alpha, material.params.z, fragCoord.xy) ||
            !distanceFadeKeeps(material.foliage1, input.inDistance, ditherThreshold(fragCoord.xy)))
            discard;
    }

    return velocityFromClip(input.inCurrent, input.inPrevious);
}
