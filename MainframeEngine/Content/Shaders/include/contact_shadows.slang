// Screen-space contact shadows of the main view (ADR 0167): set 0, binding 6 (FrameContext.ContactShadowBinding). The
// ContactShadows post effect (AfterPrepass stage) writes, per pixel of the prepass, r = the primary light's contact
// shadow (1 = lit) and g = the view depth it was computed at; it is bound here before the main pass. Otherwise, and in
// offscreen views, it is a white 1×1 image, so contactShadow() is exactly 1. Sampled with the frame set's linear clamp
// sampler. shadows.slang multiplies the primary light's cascaded shadow by it when the shadow UBO's contact.x is set.
// Needs frame.slang.
#pragma once

[[vk::binding(6, 0)]] Sampler2D contactShadowTexture;

// The contact shadow (1 = lit) at worldPos, a point of the surface being shaded. Only the surface the prepass drew at that
// pixel gets one: a transparent surface, water or anything else in front of it (or behind) sees a different view depth
// in g and stays lit.
float contactShadow(float3 worldPos)
{
    float4 clip = mul(float4(worldPos, 1.0), frame.viewProjection);
    if (!(clip.w > 0.0))
        return 1.0;
    float2 contact = contactShadowTexture.SampleLevel(ndcToScreenUv(clip.xy / clip.w), 0.0).rg;
    // clip.w is the view depth (perspective); the tolerance covers the RG16F depth and the bilinear footprint.
    return abs(contact.g - clip.w) <= 0.02 * clip.w + 0.02 ? contact.r : 1.0;
}
