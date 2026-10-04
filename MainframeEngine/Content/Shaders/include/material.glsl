// StandardMaterial3D descriptor set 2 (MaterialGpu in C#): parameters UBO, one sampler shared by the material's
// textures (keeps the fragment stage within MoltenVK's 16 samplers next to the 15 shadow samplers), and the
// albedo / normal / emission images (1x1 fallbacks when a slot is empty).
#ifndef MF_MATERIAL_GLSL
#define MF_MATERIAL_GLSL

#include "common.glsl"

// The material set (2 in the mesh pipelines; the cutout shadow casters define MATERIAL_SET 1).
#ifndef MATERIAL_SET
#define MATERIAL_SET 2
#endif

// MaterialGpu.Params (std140, 80 bytes).
layout(set = MATERIAL_SET, binding = 0) uniform MaterialParams {
    vec4  albedo;      // linear rgb, a = alpha
    vec4  emission;    // linear rgb × energy
    vec4  uvTransform; // xy = scale, zw = offset
    vec4  params;      // x = specular, y = shininess, z = alpha cutoff, w = normal scale
    uvec4 flags;       // x = texture bits (1 albedo, 2 normal, 4 emission), y = 1 unshaded, z = 1 double-sided
} material;

layout(set = MATERIAL_SET, binding = 1) uniform sampler materialSampler;
layout(set = MATERIAL_SET, binding = 2) uniform texture2D albedoTexture;
layout(set = MATERIAL_SET, binding = 3) uniform texture2D normalTexture;
layout(set = MATERIAL_SET, binding = 4) uniform texture2D emissionTexture;

const uint kHasAlbedoTexture   = 1u;
const uint kHasNormalTexture   = 2u;
const uint kHasEmissionTexture = 4u;

// Alpha modes (AlphaMode in C#), the pipeline's specialization constant 0.
const int kAlphaOpaque = 0;
const int kAlphaCutout = 1;
const int kAlphaBlend  = 2;

vec2 materialUv(vec2 uv)
{
    return uv * material.uvTransform.xy + material.uvTransform.zw;
}

// UV derivatives are taken once, before any discard or non-uniform branch, and every lookup uses textureGrad, so
// mip selection stays defined everywhere (derivatives after a non-uniform discard are undefined).

// Albedo colour × texture (sampled as sRGB unless the texture is imported as linear data).
vec4 materialAlbedo(vec2 uv, vec2 duvdx, vec2 duvdy)
{
    vec4 albedo = material.albedo;
    if ((material.flags.x & kHasAlbedoTexture) != 0u)
        albedo *= textureGrad(sampler2D(albedoTexture, materialSampler), uv, duvdx, duvdy);
    return albedo;
}

vec3 materialEmission(vec2 uv, vec2 duvdx, vec2 duvdy)
{
    vec3 emission = material.emission.rgb;
    if ((material.flags.x & kHasEmissionTexture) != 0u)
        emission *= textureGrad(sampler2D(emissionTexture, materialSampler), uv, duvdx, duvdy).rgb;
    return emission;
}

// Perturbs N with the tangent-space normal map (OpenGL/glTF convention, +Y up) using a tangent frame solved from
// screen-space derivatives of the position and UV (no vertex tangents needed): dP/du and dP/dv from the 2x2
// inverse of the UV Jacobian, so mirrored UVs and the flipped viewport keep the right handedness.
// dp1/dp2 and duv1/duv2 are the screen-space derivatives of the world position and UV.
vec3 materialNormal(vec3 N, vec2 uv, vec3 dp1, vec3 dp2, vec2 duv1, vec2 duv2)
{
    if ((material.flags.x & kHasNormalTexture) == 0u)
        return N;

    float det = duv1.x * duv2.y - duv2.x * duv1.y;
    if (abs(det) < 1e-12)
        return N;
    float s = sign(det); // the frame is normalised below, so only the sign of 1/det matters
    vec3 dPdu = (dp1 * duv2.y - dp2 * duv1.y) * s;
    vec3 dPdv = (dp2 * duv1.x - dp1 * duv2.x) * s;
    vec3 T = dPdu - N * dot(N, dPdu);
    vec3 B = N * dot(N, dPdv) - dPdv; // +Y in the map points towards decreasing v (up in the image)
    if (dot(T, T) < 1e-20 || dot(B, B) < 1e-20)
        return N;
    T = normalize(T);
    B = normalize(B);

    vec3 n = textureGrad(sampler2D(normalTexture, materialSampler), uv, duv1, duv2).xyz * 2.0 - 1.0;
    n.xy *= material.params.w;
    return normalize(T * n.x + B * n.y + N * max(n.z, 1e-3));
}

#endif
