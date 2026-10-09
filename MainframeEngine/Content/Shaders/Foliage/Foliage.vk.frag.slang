// FoliageMaterial3D (ADR 0151): set 0 frame + lights, set 1 shadows, set 2 material (the StandardMaterial3D layout,
// foliage parameters per include/foliage.slang). Cutout, back faces lit with a flipped (or kept) normal, translucency
// from the first directional light, AO from Custom0.w on the ambient term (at kCanopyAoWithProbes of its strength under
// light probes, ADR 0170), combined with the screen-space AO (ADR 0165).
// Linear HDR output.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "foliage.slang"
#include "fog.slang"
#include "alpha_dither.slang"

// AlphaMode of the pipeline: opaque pipelines never discard (early depth test stays on). Foliage never blends.
[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor;
    [[vk::location(4)]] float inAo;
};

// The lit colour of a foliage surface: the one place the shading model is chosen. PBR: the material's roughness and
// metallic (0) × its ORM map (`orm` = materialOrm: AO, roughness, metallic); Blinn-Phong: the roughness-derived
// highlight. `ao` (the vertex AO, × the map's in PBR) darkens only the ambient term; `ssao` is the screen-space AO.
float3 foliageLight(float3 albedo, float3 N, float3 Ngeo, float3 worldPos, float ao, float3 orm, float ssao)
{
    if (material.flags.y == kShadingPbr)
    {
        PbrSurface s;
        s.albedo = albedo;
        s.metallic = orm.z;
        s.roughness = orm.y;
        s.ao = ao * orm.x;
        s.N = N;
        return shadeLightsPbr(s, Ngeo, worldPos, ssao);
    }
    float3 color = shadeLightsBlinnPhong(albedo, N, Ngeo, worldPos, material.params.x, material.params.y, ssao);
    return color - lights.ambientColor.xyz * albedo * (1.0 - ao) * ssao;
}

// Light from the first directional light shining through the leaf: back-lighting where the light hits the other side,
// brighter when looking towards the light, shadowed like the light itself (and by the SSAO's light affect). With light
// probes (ADR 0170) the leaf also passes the ambient light reaching its back side (the probes' sky and bounce around −N,
// from the sample foliageLight just took), so a canopy out of the sun still glows green from below.
float3 foliageTranslucency(float3 albedo, float3 N, float3 Ngeo, float3 worldPos, float directAo)
{
    float amount = foliageTranslucencyAmount();
    if (amount <= 0.0)
        return float3(0.0);
    float3 ambient = probesActive() && material.flags.y != kShadingUnshaded ? albedo * amount * probeAmbient(gProbeSample, -N) : float3(0.0);
    if (lights.counts.x == 0)
        return ambient;

    DirLight l = lights.dir[0];
    float3 L = normalize(-l.directionIntensity.xyz);
    float back = saturate(dot(-N, L));
    if (back <= 0.0)
        return ambient;
    float3 V = normalize(lights.cameraPosition.xyz - worldPos);
    float view = pow(saturate(dot(V, -L)), 4.0);
    float3 Nl = dot(Ngeo, L) < 0.0 ? -Ngeo : Ngeo; // the side facing the light, for the receiver offset
    float shadow = lights.counts.w == 0 ? dirShadow(0, worldPos, Nl, L) : 1.0;
    shadow = lerp(1.0, shadow, l.color.w) * directAo;
    return albedo * amount * l.color.xyz * l.directionIntensity.w * back * (0.4 + 0.6 * view) * shadow + ambient;
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
    if (kAlphaMode == kAlphaCutout && !cutoutKeeps(albedo.a, material.params.z, fragCoord.xy))
        discard; // dithered with TAA when the material asks (AlphaDither), as the prepass does

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
        float3 Nmap = materialNormal(N, uv, dpdx, dpdy, duvdx, duvdy);
        float3 orm = materialOrm(uv, duvdx, duvdy);
        float ssao = ambientOcclusionAt(fragCoord);
        float vertexAo = probesActive() ? lerp(1.0, input.inAo, kCanopyAoWithProbes) : input.inAo;
        color = foliageLight(albedo.rgb, Nmap, N, input.inWorldPos, vertexAo, orm, ssao); // first: takes the probe sample
        color += foliageTranslucency(albedo.rgb, Nmap, N, input.inWorldPos, ssaoDirect(ssao));
    }

    return float4(applyFog(color, input.inWorldPos), 1.0);
}
