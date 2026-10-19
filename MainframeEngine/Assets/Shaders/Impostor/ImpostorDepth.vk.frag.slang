// Depth prepass (ADR 0163) of ImpostorMaterial3D (ADR 0172): the blended views' alpha test (dithered as Impostor.vk.frag
// does) and the per-instance visibility bands; writes the velocity.
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "alpha_dither.slang"
#include "impostor.slang"
#include "impostor_views.slang"

[vk::constant_id(0)] const int kAlphaMode = kAlphaCutout;

struct FsIn
{
    [[vk::location(0)]] float4 inCurrent;
    [[vk::location(1)]] float4 inPrevious;
    [[vk::location(2)]] float2 inLocal0;
    [[vk::location(3)]] float2 inLocal1;
    [[vk::location(4)]] float2 inLocal2;
    [[vk::location(5)]] nointerpolation float4 inCells01;
    [[vk::location(6)]] nointerpolation float2 inCell2;
    [[vk::location(7)]] nointerpolation float3 inWeights;
    [[vk::location(8)]] nointerpolation float inDistance;
};

[shader("fragment")]
float2 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    if (kAlphaMode == kAlphaCutout)
    {
        ImpostorUvs views = impostorUvs(input.inCells01, input.inCell2, input.inLocal0, input.inLocal1, input.inLocal2, input.inWeights,
                                        fragCoord.xy);
        float3 alphaWeights;
        float4 albedo = impostorAlbedo(views, alphaWeights);
        if (!impostorKeeps(albedo.a, fragCoord.xy) ||
            !distanceFadeKeeps(material.foliage1, input.inDistance, ditherThreshold(fragCoord.xy)))
            discard;
    }

    return velocityFromClip(input.inCurrent, input.inPrevious);
}
