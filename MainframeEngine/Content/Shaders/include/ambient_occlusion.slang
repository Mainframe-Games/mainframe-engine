// Screen-space ambient occlusion of the main view (ADR 0163, ADR 0165): set 0, binding 5 (FrameContext.AmbientOcclusionBinding).
// The SSAO post effect (AfterPrepass stage, GTAO) binds its full-resolution output here before the main pass; otherwise,
// and in offscreen views, it is a white 1×1 image. Sampled with the frame set's linear clamp sampler at pixel centres.
//   r  ambient occlusion (1 = unoccluded), intensity and power applied
//   g  reserved: contact shadow (G8e.2), 1 now
//   ba reserved: view-space bent normal, octahedral (G8e.1), 1 now
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
