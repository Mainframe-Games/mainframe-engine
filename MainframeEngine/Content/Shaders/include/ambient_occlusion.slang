// Screen-space ambient occlusion of the main view (ADR 0163): set 0, binding 5 (FrameContext.AmbientOcclusionBinding).
// The SSAO post effect (AfterPrepass stage) binds its output here before the main pass; otherwise, and in offscreen views,
// it is a white 1×1 image, so ambientOcclusion() is exactly 1. Sampled with the frame set's linear clamp sampler.
#pragma once

[[vk::binding(5, 0)]] Sampler2D ssaoTexture;

// The ambient occlusion (1 = unoccluded) at a screen UV (0,0 top-left): SV_Position.xy * frame.viewport.zw.
float ambientOcclusion(float2 screenUv)
{
    return ssaoTexture.SampleLevel(screenUv, 0.0).r;
}
