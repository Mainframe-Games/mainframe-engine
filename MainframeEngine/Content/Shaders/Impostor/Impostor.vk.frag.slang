// ImpostorMaterial3D (ADR 0172): blends the three baked views (albedo + coverage; object normal + depth; thickness, AO,
// leaf mask), alpha-tests (dithered as alpha to coverage with TAA, and the per-instance visibility bands), moves the
// fragment to the baked surface along the view (for its lighting, shadows and fog; the depth stays the quad's) and lights
// it with the foliage model: PBR (leaf or bark roughness), the baked AO on the ambient term, SSAO, translucency on leaves.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "foliage.slang"
#include "fog.slang"
#include "alpha_dither.slang"
#include "foliage_lighting.slang"
#include "impostor.slang"
#include "impostor_views.slang"

// The pipeline's alpha mode: a prepassed impostor runs as opaque (the prepass's EQUAL depth test keeps what it kept).
[vk::constant_id(0)] const int kAlphaMode = kAlphaCutout;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float2 inLocal0;
    [[vk::location(2)]] float2 inLocal1;
    [[vk::location(3)]] float2 inLocal2;
    [[vk::location(4)]] nointerpolation float4 inCells01;
    [[vk::location(5)]] nointerpolation float2 inCell2;
    [[vk::location(6)]] nointerpolation float3 inWeights;
    [[vk::location(7)]] nointerpolation float3 inRow0;
    [[vk::location(8)]] nointerpolation float3 inRow1;
    [[vk::location(9)]] nointerpolation float3 inRow2;
    [[vk::location(10)]] nointerpolation float4 inView;
    [[vk::location(11)]] nointerpolation float inDistance;
};

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    ImpostorUvs views = impostorUvs(input.inCells01, input.inCell2, input.inLocal0, input.inLocal1, input.inLocal2, input.inWeights,
                                    fragCoord.xy);
    float3 alphaWeights;
    float4 albedo = impostorAlbedo(views, alphaWeights);
    if (kAlphaMode == kAlphaCutout)
    {
        if (!impostorKeeps(albedo.a, fragCoord.xy) ||
            !distanceFadeKeeps(material.foliage1, input.inDistance, ditherThreshold(fragCoord.xy)))
            discard;
    }

    // ADR 0174: the normal and detail maps at the output resolution's mip under upscaling (the albedo and its alpha test
    // stay unbiased, exactly as ImpostorDepth's prepass test).
    views.gx = materialGrad(views.gx);
    views.gy = materialGrad(views.gy);
    float covered = max(alphaWeights.x + alphaWeights.y + alphaWeights.z, 1e-5);
    float4 nd = impostorSample(normalTexture, views, alphaWeights) / covered;
    float3 detail = impostorSample(emissionTexture, views, alphaWeights).rgb / covered;

    float3 nObject = nd.rgb * 2.0 - 1.0;
    float3 N = mul(nObject, float3x3(input.inRow0, input.inRow1, input.inRow2));
    N = dot(N, N) > 1e-8 ? normalize(N) : input.inView.xyz;
    // The baked surface: the quad's point moved along the view by the baked depth (towards the viewer is positive).
    float3 surface = input.inWorldPos + input.inView.xyz * ((nd.a * 2.0 - 1.0) * input.inView.w);

    float leaf = saturate(detail.b);
    PbrSurface s;
    s.albedo = albedo.rgb;
    s.metallic = 0.0;
    s.roughness = lerp(impostorBarkRoughness(), material.pbr.y, leaf);
    s.ao = saturate(detail.g);
    s.N = N;
    // The mesh's leaves shadow each other; the quad's own caster cannot shadow the quad, so the baked occlusion (deep
    // inside the canopy) also dims the direct light on leaves.
    float ssao = ambientOcclusionAt(fragCoord);
    float3 V = normalize(lights.cameraPosition.xyz - surface);
    float alpha = clamp(s.roughness, 0.045, 1.0);
    float selfShadow = lerp(1.0, s.ao * s.ao, leaf);
    float directAo = ssaoDirect(ssao) * selfShadow;
    float3 color = shadeLightsPbr(s, N, surface, ssaoCombine(s.ao, ssao), ssaoSpecularRatio(max(dot(N, V), 1e-4), ssao, alpha * alpha), directAo) +
                   leaf * foliageTranslucency(albedo.rgb, N, N, surface, directAo, saturate(detail.r));
    return float4(applyFog(color, surface), 1.0);
}
