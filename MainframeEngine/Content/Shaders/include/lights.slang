// Shading with the lights UBO (lights_data.slang) and shadows: Blinn-Phong (StandardMaterial3D's default, Spine) and
// PBR (ShadingMode.Pbr, ADR 0150: Cook-Torrance GGX + Lambert, ambient from the sky's image-based lighting).
// Include shadows.slang first. Define LIGHTS_SET / LIGHTS_BINDING before including to override where the UBO lives.
// Screen-space ambient occlusion (ADR 0165): the overloads taking `ssao` (ambientOcclusionAt) apply it to the ambient and
// reflected light (and, by the world's light affect, to direct light); without SSAO it is exactly 1 and they shade
// bit-exactly as the overloads without it.
#pragma once

#include "common.slang"

#ifndef MF_SHADOWS_SLANG
#error "Include shadows.slang before lights.slang"
#endif

#include "lights_data.slang"
#include "environment.slang"
#include "ambient_occlusion.slang"

float attenuate(float dist, float range)
{
    float x = clamp(1.0 - dist / range, 0.0, 1.0);
    return x * x;
}

// Blinn-Phong: (diffuse + highlight of strength `specular`, exponent `shininess`) × base colour.
float3 calcDir(DirLight l, float3 N, float3 V, float3 base, float shadow, float specular, float shininess)
{
    float3 L    = normalize(-l.directionIntensity.xyz);
    float3 H    = normalize(L + V);
    float  diff = max(dot(N, L), 0.0);
    float  spec = pow(max(dot(N, H), 0.0), shininess);
    return l.color.xyz * l.directionIntensity.w * (diff + spec * specular) * base * shadow;
}

float3 calcPoint(PointLight l, float3 N, float3 V, float3 pos, float3 base, float shadow, float specular, float shininess)
{
    float3 toLight = l.positionRange.xyz - pos;
    float  dist    = length(toLight);
    float3 L       = normalize(toLight);
    float3 H       = normalize(L + V);
    float  diff    = max(dot(N, L), 0.0);
    float  spec    = pow(max(dot(N, H), 0.0), shininess);
    float  atten   = attenuate(dist, l.positionRange.w);
    return l.colorIntensity.xyz * l.colorIntensity.w * (diff + spec * specular) * atten * base * shadow;
}

// Cone factor of a spot light for the direction L towards it (1 inside the inner cone, 0 outside the outer one).
float spotCone(SpotLight l, float3 L)
{
    float cosTheta = dot(-L, normalize(l.directionIntensity.xyz));
    float inner    = l.colorInner.w;
    float outer    = l.outerPad.x;
    return clamp((cosTheta - outer) / max(inner - outer, 0.0001), 0.0, 1.0);
}

float3 calcSpot(SpotLight l, float3 N, float3 V, float3 pos, float3 base, float shadow, float specular, float shininess)
{
    float3 toLight  = l.positionRange.xyz - pos;
    float  dist     = length(toLight);
    float3 L        = normalize(toLight);
    float3 H        = normalize(L + V);
    float  spot     = spotCone(l, L);
    float  diff     = max(dot(N, L), 0.0);
    float  spec     = pow(max(dot(N, H), 0.0), shininess);
    float  atten    = attenuate(dist, l.positionRange.w);
    return l.colorInner.xyz * l.directionIntensity.w * (diff + spec * specular) * spot * atten * base * shadow;
}

// Ambient + every light, each shadowed by its map when it has one (ShadowSystem decides which lights cast).
// `specular` scales the Blinn-Phong highlight of exponent `shininess` (materials: StandardMaterial3D). `Ngeo` is the
// geometric normal, used for the shadow receivers' normal offset (a normal-mapped N would make it noisy). The ambient
// term is the sky's irradiance when the world's AmbientSource is Sky, else the ambient colour. `ambientAo` scales the
// ambient term, `directAo` every light (both 1 in the overload without them).
float3 shadeLightsBlinnPhong(float3 base, float3 N, float3 Ngeo, float3 worldPos, float specular, float shininess,
                             float ambientAo, float directAo)
{
    float3 V = normalize(lights.cameraPosition.xyz - worldPos);
    float3 result = iblDiffuse(N) * base * ambientAo;
    bool shadowsOn = lights.counts.w == 0;

    for (int i = 0; i < lights.counts.x; i++)
    {
        float3 L      = normalize(-lights.dir[i].directionIntensity.xyz);
        float  shadow = shadowsOn ? dirShadow(i, worldPos, Ngeo, L) : 1.0;
        shadow = lerp(1.0, shadow, lights.dir[i].color.w) * directAo; // Godot: a full shadow keeps 1 - opacity of the light
        result += calcDir(lights.dir[i], N, V, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.y; i++)
    {
        float4 pr     = lights.point[i].positionRange;
        float  shadow = (shadowsOn ? pointShadow(i, worldPos, Ngeo, pr.xyz, pr.w) : 1.0) * directAo;
        result += calcPoint(lights.point[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.z; i++)
    {
        float3 lightPos = lights.spot[i].positionRange.xyz;
        float3 L        = normalize(lightPos - worldPos);
        float  shadow   = shadowsOn ? spotShadow(i, worldPos, Ngeo, L, lightPos) : 1.0;
        shadow = lerp(1.0, shadow, lights.spot[i].outerPad.y) * directAo;
        result += calcSpot(lights.spot[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    if (shadowsOn)
        result *= shadowCascadeTint(worldPos);
    return result;
}

float3 shadeLightsBlinnPhong(float3 base, float3 N, float3 Ngeo, float3 worldPos, float specular, float shininess)
{
    return shadeLightsBlinnPhong(base, N, Ngeo, worldPos, specular, shininess, 1.0, 1.0);
}

// With the screen-space AO at this fragment (ambientOcclusionAt): the ambient term × ssao, the lights × ssaoDirect.
float3 shadeLightsBlinnPhong(float3 base, float3 N, float3 Ngeo, float3 worldPos, float specular, float shininess, float ssao)
{
    return shadeLightsBlinnPhong(base, N, Ngeo, worldPos, specular, shininess, ssao, ssaoDirect(ssao));
}

// The default highlight (strength 0.3, exponent 32): Spine and anything without a material.
float3 shadeLights(float3 base, float3 N, float3 worldPos)
{
    return shadeLightsBlinnPhong(base, N, N, worldPos, 0.3, 32.0);
}

// ── PBR (ADR 0150) ────────────────────────────────────────────────────────────────────────────────────────────────

// A lit surface for shadeLightsPbr. albedo is linear; metallic, roughness (perceptual) and ao are 0..1; N is the shading
// normal (normal-mapped, facing the viewer for double-sided back faces).
struct PbrSurface
{
    float3 albedo;
    float  metallic;
    float  roughness;
    float  ao;
    float3 N;
};

// GGX / Trowbridge-Reitz normal distribution (a2 = α², α = roughness²).
float pbrDistribution(float NdotH, float a2)
{
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

// Smith height-correlated visibility: G / (4 N·L N·V).
float pbrVisibility(float NdotV, float NdotL, float a2)
{
    float v = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float l = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    return 0.5 / max(v + l, 1e-5);
}

float3 pbrFresnel(float3 F0, float VdotH)
{
    float f = pow(1.0 - VdotH, 5.0);
    return F0 + (1.0 - F0) * f;
}

// Light leaving the surface towards V from one light arriving along L with `radiance` (colour × intensity × attenuation
// × shadow). Light units match Blinn-Phong: Lambert's 1/π is folded into the light, so the specular lobe carries a π.
float3 pbrLight(PbrSurface s, float3 F0, float a2, float3 V, float NdotV, float3 L, float3 radiance)
{
    float NdotL = dot(s.N, L);
    if (NdotL <= 0.0)
        return float3(0.0);
    float3 H     = normalize(L + V);
    float  NdotH = saturate(dot(s.N, H));
    float  VdotH = saturate(dot(V, H));
    float3 F     = pbrFresnel(F0, VdotH);
    float3 diffuse  = s.albedo * (1.0 - s.metallic) * (1.0 - F);
    float3 specular = PI * pbrDistribution(NdotH, a2) * pbrVisibility(NdotV, NdotL, a2) * F;
    return (diffuse + specular) * radiance * NdotL;
}

// Cook-Torrance (GGX, Smith height-correlated, Schlick Fresnel with F0 = lerp(0.04, albedo, metallic)) + Lambert ×
// (1 − F)(1 − metallic) for every light, with the Blinn-Phong path's shadows, shadow opacity and cascade tint; ambient
// is the sky's image-based lighting (iblDiffuse, iblSpecular × the split-sum BRDF) × ao, or the ambient colour.
// `diffuseAo` replaces s.ao on the ambient term, `specularRatio` scales the reflections further and `directAo` every
// light (the overloads below fill them in).
float3 shadeLightsPbr(PbrSurface s, float3 Ngeo, float3 worldPos, float diffuseAo, float specularRatio, float directAo)
{
    float3 V         = normalize(lights.cameraPosition.xyz - worldPos);
    float  roughness = clamp(s.roughness, 0.045, 1.0); // α ≥ 0.002: no sub-pixel highlights (specular aliasing)
    float  a         = roughness * roughness;
    float  a2        = a * a;
    float3 F0        = lerp(float3(0.04), s.albedo, s.metallic);
    float  NdotV     = max(dot(s.N, V), 1e-4);
    bool   shadowsOn = lights.counts.w == 0;

    float2 envBrdf = iblBrdf(NdotV, roughness);
    float3 R = reflect(-V, s.N);
    float3 result = (iblDiffuse(s.N) * s.albedo * (1.0 - s.metallic) +
                     iblSpecular(R, roughness) * (F0 * envBrdf.x + envBrdf.y) * specularRatio) * diffuseAo;

    for (int i = 0; i < lights.counts.x; i++)
    {
        DirLight l    = lights.dir[i];
        float3   L    = normalize(-l.directionIntensity.xyz);
        float  shadow = shadowsOn ? dirShadow(i, worldPos, Ngeo, L) : 1.0;
        shadow = lerp(1.0, shadow, l.color.w) * directAo;
        result += pbrLight(s, F0, a2, V, NdotV, L, l.color.xyz * l.directionIntensity.w * shadow);
    }

    for (int i = 0; i < lights.counts.y; i++)
    {
        PointLight l   = lights.point[i];
        float3 toLight = l.positionRange.xyz - worldPos;
        float  dist    = length(toLight);
        float  shadow  = (shadowsOn ? pointShadow(i, worldPos, Ngeo, l.positionRange.xyz, l.positionRange.w) : 1.0) * directAo;
        float  atten   = attenuate(dist, l.positionRange.w);
        result += pbrLight(s, F0, a2, V, NdotV, toLight / max(dist, 1e-4), l.colorIntensity.xyz * l.colorIntensity.w * atten * shadow);
    }

    for (int i = 0; i < lights.counts.z; i++)
    {
        SpotLight l     = lights.spot[i];
        float3 lightPos = l.positionRange.xyz;
        float3 toLight  = lightPos - worldPos;
        float  dist     = length(toLight);
        float3 L        = toLight / max(dist, 1e-4);
        float  shadow   = shadowsOn ? spotShadow(i, worldPos, Ngeo, L, lightPos) : 1.0;
        shadow = lerp(1.0, shadow, l.outerPad.y) * directAo;
        float  atten    = attenuate(dist, l.positionRange.w) * spotCone(l, L);
        result += pbrLight(s, F0, a2, V, NdotV, L, l.colorInner.xyz * l.directionIntensity.w * atten * shadow);
    }

    if (shadowsOn)
        result *= shadowCascadeTint(worldPos);
    return result;
}

float3 shadeLightsPbr(PbrSurface s, float3 Ngeo, float3 worldPos)
{
    return shadeLightsPbr(s, Ngeo, worldPos, s.ao, 1.0, 1.0);
}

// With the screen-space AO at this fragment (ambientOcclusionAt, ADR 0165): the ambient term × ssaoCombine(s.ao, ssao),
// the reflections also × the specular-occlusion ratio, the lights × ssaoDirect.
float3 shadeLightsPbr(PbrSurface s, float3 Ngeo, float3 worldPos, float ssao)
{
    float3 V         = normalize(lights.cameraPosition.xyz - worldPos);
    float  roughness = clamp(s.roughness, 0.045, 1.0);
    float  NdotV     = max(dot(s.N, V), 1e-4);
    return shadeLightsPbr(s, Ngeo, worldPos, ssaoCombine(s.ao, ssao), ssaoSpecularRatio(NdotV, ssao, roughness * roughness),
                          ssaoDirect(ssao));
}
