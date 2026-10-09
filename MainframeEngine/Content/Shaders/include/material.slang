// StandardMaterial3D descriptor set 2 (MaterialGpu in C#): parameters UBO, one sampler shared by the material's
// textures, and the albedo / normal / emission / ORM images (1x1 fallbacks when a slot is empty). Fragment-stage budget
// (MoltenVK and the Android baseline: 16 samplers and sampled images per stage, rendering-features.md): shadows 6
// combined samplers (set 1), the material 1 sampler + 4 images, the sky's image-based lighting 3 combined samplers
// (set 0, environment.slang) = 13 images, 10 samplers.
#pragma once

#include "common.slang"

// The material set (2 in the mesh pipelines; the cutout shadow casters define MATERIAL_SET 1).
#ifndef MATERIAL_SET
#define MATERIAL_SET 2
#endif

// MaterialGpu.Params (std140, 96 bytes).
struct MaterialParams
{
    float4 albedo;      // linear rgb, a = alpha
    float4 emission;    // linear rgb × energy
    float4 uvTransform; // xy = scale, zw = offset
    float4 params;      // x = specular, y = shininess, z = alpha cutoff, w = normal scale
    uint4  flags;       // x = texture bits (1 albedo, 2 normal, 4 emission, 8 ORM), y = shading (kShading*),
                        // z = 1 double-sided, w = outline width in pixels (float bits; OutlineMaterial3D)
    float4 pbr;         // x = metallic, y = roughness, z = ambient occlusion (ShadingMode.Pbr; × the ORM texture),
                        // w = 1 for a dithered cutout (FoliageMaterial3D.AlphaDither, alpha_dither.slang)
};

[[vk::binding(0, MATERIAL_SET)]] ConstantBuffer<MaterialParams> material;

[[vk::binding(1, MATERIAL_SET)]] SamplerState materialSampler;
[[vk::binding(2, MATERIAL_SET)]] Texture2D albedoTexture;
[[vk::binding(3, MATERIAL_SET)]] Texture2D normalTexture;
[[vk::binding(4, MATERIAL_SET)]] Texture2D emissionTexture;
[[vk::binding(5, MATERIAL_SET)]] Texture2D ormTexture; // linear: R = ambient occlusion, G = roughness, B = metallic

static const uint kHasAlbedoTexture   = 1u;
static const uint kHasNormalTexture   = 2u;
static const uint kHasEmissionTexture = 4u;
static const uint kHasOrmTexture      = 8u;

// Shading modes (flags.y; ShadingMode in C#).
static const uint kShadingBlinnPhong = 0u;
static const uint kShadingUnshaded   = 1u;
static const uint kShadingPbr        = 2u;

// Alpha modes (AlphaMode in C#), the pipeline's specialization constant 0.
static const int kAlphaOpaque = 0;
static const int kAlphaCutout = 1;
static const int kAlphaBlend  = 2;

float2 materialUv(float2 uv)
{
    return uv * material.uvTransform.xy + material.uvTransform.zw;
}

// UV derivatives are taken once, before any discard or non-uniform branch, and every lookup uses SampleGrad, so
// mip selection stays defined everywhere (derivatives after a non-uniform discard are undefined).

// Albedo colour × texture (sampled as sRGB unless the texture is imported as linear data).
float4 materialAlbedo(float2 uv, float2 duvdx, float2 duvdy)
{
    float4 albedo = material.albedo;
    if ((material.flags.x & kHasAlbedoTexture) != 0u)
        albedo *= albedoTexture.SampleGrad(materialSampler, uv, duvdx, duvdy);
    return albedo;
}

float3 materialEmission(float2 uv, float2 duvdx, float2 duvdy)
{
    float3 emission = material.emission.rgb;
    if ((material.flags.x & kHasEmissionTexture) != 0u)
        emission *= emissionTexture.SampleGrad(materialSampler, uv, duvdx, duvdy).rgb;
    return emission;
}

// Ambient occlusion, roughness and metallic (x, y, z): the material's values × the ORM texture (Godot's ORM convention).
float3 materialOrm(float2 uv, float2 duvdx, float2 duvdy)
{
    float3 orm = float3(material.pbr.z, material.pbr.y, material.pbr.x);
    if ((material.flags.x & kHasOrmTexture) != 0u)
        orm *= ormTexture.SampleGrad(materialSampler, uv, duvdx, duvdy).rgb;
    return orm;
}

// Perturbs N with the tangent-space normal map (OpenGL/glTF convention, +Y up) using a tangent frame solved from
// screen-space derivatives of the position and UV (no vertex tangents needed): dP/du and dP/dv from the 2x2
// inverse of the UV Jacobian, so mirrored UVs and the flipped viewport keep the right handedness.
// dp1/dp2 and duv1/duv2 are the screen-space derivatives of the world position and UV.
float3 materialNormal(float3 N, float2 uv, float3 dp1, float3 dp2, float2 duv1, float2 duv2)
{
    if ((material.flags.x & kHasNormalTexture) == 0u)
        return N;

    float det = duv1.x * duv2.y - duv2.x * duv1.y;
    if (abs(det) < 1e-12)
        return N;
    float s = sign(det); // the frame is normalised below, so only the sign of 1/det matters
    float3 dPdu = (dp1 * duv2.y - dp2 * duv1.y) * s;
    float3 dPdv = (dp2 * duv1.x - dp1 * duv2.x) * s;
    float3 T = dPdu - N * dot(N, dPdu);
    float3 B = N * dot(N, dPdv) - dPdv; // +Y in the map points towards decreasing v (up in the image)
    if (dot(T, T) < 1e-20 || dot(B, B) < 1e-20)
        return N;
    T = normalize(T);
    B = normalize(B);

    float3 n = normalTexture.SampleGrad(materialSampler, uv, duv1, duv2).xyz * 2.0 - 1.0;
    n.xy *= material.params.w;
    return normalize(T * n.x + B * n.y + N * max(n.z, 1e-3));
}
