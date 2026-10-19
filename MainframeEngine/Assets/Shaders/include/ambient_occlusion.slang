// Screen-space ambient occlusion of the main view (ADR 0163, ADR 0165): set 0, binding 5 (FrameContext.AmbientOcclusionBinding).
// The SSAO post effect (AfterPrepass stage, GTAO) binds its full-resolution output here before the main pass; otherwise,
// and in offscreen views, it is a white 1×1 image. Sampled with the frame set's linear clamp sampler at pixel centres.
//   r  ambient occlusion (1 = unoccluded), intensity and power applied (ADR 0165)
//   g  the primary light's contact shadow (ADR 0167, 0170: contact_shadows.slang), 1 without
//   b  the view depth of the pixel's prepassed surface; a  GTAO's packed view-space bent normal (screenBentNormal, ADR 0170)
// How a lit shader applies it (frame.ambientOcclusion, FrameData.AmbientOcclusion in C#: x light affect, y AO channel
// affect) is below. Without SSAO the image is white and the parameters 0, so every term here is exactly 1 (or the
// material's own AO) and lit shaders shade bit-exactly as without SSAO.
#pragma once

#include "frame.slang"

[[vk::binding(5, 0)]] Sampler2D ssaoTexture;

// The ambient occlusion (1 = unoccluded) at a screen UV (0,0 top-left): SV_Position.xy * frame.viewport.zw.
float ambientOcclusion(float2 screenUv)
{
    return ssaoTexture.SampleLevel(screenUv, 0.0).r;
}

// The ambient occlusion at a fragment of the main pass (its SV_Position).
float ambientOcclusionAt(float4 fragCoord)
{
    return ambientOcclusion(fragCoord.xy * frame.viewport.zw);
}

// The occlusion of ambient light for a surface whose material has its own AO (Godot's ssao_ao_channel_affect): the
// darker of the two by default (0), their product at 1. Exactly materialAo when ssao is 1.
float ssaoCombine(float materialAo, float ssao)
{
    float darker = min(materialAo, ssao);
    return darker + (materialAo * ssao - darker) * frame.ambientOcclusion.y;
}

// The factor on direct light (Godot's ssao_light_affect): 1 by default; exactly 1 when ssao is 1.
float ssaoDirect(float ssao)
{
    return 1.0 + (ssao - 1.0) * frame.ambientOcclusion.x;
}

// Specular occlusion relative to the diffuse occlusion: Lagarde and de Rousiers 2014 ("Moving Frostbite to PBR") from
// the AO, N·V and α (roughness²), divided by the AO and capped at 1, so reflections are occluded at least as much as
// ambient light, more on smooth surfaces seen face-on. Exactly 1 when ssao is 1.
float ssaoSpecularRatio(float NdotV, float ssao, float alpha)
{
    if (ssao >= 1.0)
        return 1.0;
    float occlusion = saturate(pow(NdotV + ssao, exp2(-16.0 * alpha - 1.0)) - 1.0 + ssao);
    return saturate(occlusion / max(ssao, 1e-3));
}

// GTAO's bent normal (ADR 0170) at a point of the surface being shaded, in world space; float3(0) where there is none: SSAO
// off (the white image's a = 1), the sky, or a surface other than the one the prepass drew at that pixel (b holds the view
// depth it belongs to). The view-space x, y are packed in a (2 + 32·i + j, gtaoPackBentNormal), so it is read unfiltered.
float3 screenBentNormal(float3 worldPos)
{
    float4 clip = mul(float4(worldPos, 1.0), frame.viewProjection);
    if (!(clip.w > 0.0))
        return float3(0.0);
    uint w, h, levels;
    ssaoTexture.GetDimensions(0u, w, h, levels);
    float2 uv = ndcToScreenUv(clip.xy / clip.w);
    int2 pixel = clamp(int2(uv * float2(float(w), float(h))), int2(0), int2(int(w) - 1, int(h) - 1));
    float4 s = ssaoTexture.Load(int3(pixel, 0));
    if (s.a < 2.0 || abs(s.b - clip.w) > 0.02 * clip.w + 0.02)
        return float3(0.0);
    float packed = s.a - 2.0;
    float i = floor(packed / 32.0);
    float2 xy = float2(i, packed - i * 32.0) / 31.0 * 2.0 - 1.0;
    float3 view = float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
    return normalize(mul(float4(view, 0.0), frame.invViewRotation).xyz);
}
