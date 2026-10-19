// FoliageMaterial3D (ADR 0151, 0172): set 0 frame + lights, set 1 shadows, set 2 material (the StandardMaterial3D layout,
// foliage parameters per include/foliage.slang). Cutout, back faces lit with a flipped (or kept) normal, translucency
// from the first directional light (thickness-driven wrap transmission with a thickness map), AO from Custom0.w (and the
// thickness map's G) on the ambient term (at kCanopyAoWithProbes of its strength under light probes, ADR 0170), combined
// with the screen-space AO (ADR 0165), moss on top and detail normals (bark), and the per-instance visibility range's
// dithered bands. Linear HDR output.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "foliage.slang"
#include "fog.slang"
#include "alpha_dither.slang"
#include "wind.slang"
#include "foliage_lighting.slang"

// AlphaMode of the pipeline: opaque pipelines never discard (early depth test stays on). Foliage never blends.
// Materials with a per-instance visibility range draw as cutouts (their bands discard).
[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor;
    [[vk::location(4)]] float inAo;
    [[vk::location(5)]] float2 inInstance; // x = instance distance, y = height above the instance origin (m)
};

// The normal map with detail normals (bark, ADR 0172): the same map again at foliage2.y × the tiling, blended with UDN.
float3 foliageNormal(float3 N, float2 uv, float3 dp1, float3 dp2, float2 duv1, float2 duv2)
{
    if ((material.flags.x & kHasNormalTexture) == 0u || material.foliage2.y <= 0.0)
        return materialNormal(N, uv, dp1, dp2, duv1, duv2);

    float det = duv1.x * duv2.y - duv2.x * duv1.y;
    if (abs(det) < 1e-12)
        return N;
    float s = sign(det);
    float3 dPdu = (dp1 * duv2.y - dp2 * duv1.y) * s;
    float3 dPdv = (dp2 * duv1.x - dp1 * duv2.x) * s;
    float3 T = dPdu - N * dot(N, dPdu);
    float3 B = N * dot(N, dPdv) - dPdv;
    if (dot(T, T) < 1e-20 || dot(B, B) < 1e-20)
        return N;
    T = normalize(T);
    B = normalize(B);

    float scale = material.foliage2.y;
    float3 n = normalTexture.SampleGrad(materialSampler, uv, duv1, duv2).xyz * 2.0 - 1.0;
    float3 d = normalTexture.SampleGrad(materialSampler, uv * scale, duv1 * scale, duv2 * scale).xyz * 2.0 - 1.0;
    n.xy = n.xy * material.params.w + d.xy * material.foliage2.z; // UDN
    return normalize(T * n.x + B * n.y + N * max(n.z, 1e-3));
}

// Moss on bark (ADR 0172): the share of the surface covered, from the world normal (up-facing bark first), a world-space
// noise (patches) and the height above the tree's base (moss climbs the trunk from the roots, on every side).
float mossMask(float3 N, float3 worldPos, float height)
{
    float coverage = material.foliage2.x;
    if (coverage <= 0.0)
        return 0.0;
    float3 p = worldPos * material.foliage2.w;
    float noise = 0.65 * simplex3(p) + 0.35 * simplex3(p * 2.7 + 17.0);
    float ground = 0.8 * saturate(1.0 - height / 2.2);
    float x = N.y + 0.7 * noise + ground * ground * (3.0 - 2.0 * ground);
    float threshold = 1.0 - coverage;
    return smoothstep(threshold - 0.2, threshold + 0.2, x);
}

[shader("fragment")]
float4 main(FsIn input, bool frontFacing : SV_IsFrontFace, float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = materialUv(input.inUV);
    float2 duvdx = materialGrad(ddx(uv)); // ADR 0174: the output resolution's mip under upscaling
    float2 duvdy = materialGrad(ddy(uv));
    float3 dpdx = ddx(input.inWorldPos);
    float3 dpdy = ddy(input.inWorldPos);
    float4 albedo = materialAlbedo(uv, duvdx, duvdy) * input.inColor;
    if (kAlphaMode == kAlphaCutout)
    {
        // Dithered with TAA when the material asks (AlphaDither), as the prepass does; then the instance's LOD band.
        if (!cutoutKeeps(albedo.a, material.params.z, fragCoord.xy) ||
            !distanceFadeKeeps(material.foliage1, input.inInstance.x, ditherThreshold(fragCoord.xy)))
            discard;
    }

    float3 N = normalize(input.inNormal);
    if (material.flags.z == kFoliageBackFlip && !frontFacing)
        N = -N;

    float3 color;
    if (material.flags.y == kShadingUnshaded)
    {
        color = albedo.rgb; // unshaded
    }
    else
    {
        float3 Nmap = foliageNormal(N, uv, dpdx, dpdy, duvdx, duvdy);
        float3 orm = materialOrm(uv, duvdx, duvdy);
        float ao = probesActive() ? lerp(1.0, input.inAo, kCanopyAoWithProbes) : input.inAo;
        float thickness = -1.0;
        if (foliageHasDetailTexture())
        {
            float2 detail = emissionTexture.SampleGrad(materialSampler, uv, duvdx, duvdy).rg; // thickness, AO
            thickness = detail.r;
            ao *= detail.g;
        }

        float moss = mossMask(N, input.inWorldPos, input.inInstance.y);
        if (moss > 0.0)
        {
            // The bark's relief shows through: the moss is darker in its cracks.
            float tone = 0.8 + 0.4 * (0.5 + 0.5 * simplex3(input.inWorldPos * material.foliage2.w * 4.7));
            float relief = 0.55 + 0.9 * saturate(dot(albedo.rgb, float3(0.2126, 0.7152, 0.0722)) * 3.0);
            albedo.rgb = lerp(albedo.rgb, material.foliage3.rgb * tone * relief, moss);
            orm.y = lerp(orm.y, 1.0, moss);
            Nmap = normalize(lerp(Nmap, N, moss * 0.4));
        }

        float ssao = ambientOcclusionAt(fragCoord);
        color = foliageLight(albedo.rgb, Nmap, N, input.inWorldPos, ao, orm, ssao); // first: takes the probe sample
        color += foliageTranslucency(albedo.rgb, Nmap, N, input.inWorldPos, ssaoDirect(ssao), thickness);
    }

    return float4(applyFog(color, input.inWorldPos), 1.0);
}
