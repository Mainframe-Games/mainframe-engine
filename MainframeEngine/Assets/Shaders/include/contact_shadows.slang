// Screen-space contact shadows of the main view (ADR 0167, 0170). The ContactShadows post effect (AfterPrepass stage)
// writes them into the screen-space occlusion image at set 0 binding 5 (include/ambient_occlusion.slang): g = the primary
// light's contact shadow (1 = lit), b = the view depth it was computed at. Without the effect, and in offscreen views, g is
// 1 (SSAO's output or the white fallback), so contactShadow() is exactly 1. shadows.slang multiplies the primary light's
// cascaded shadow by it when the shadow UBO's contact.x is set. (ADR 0167 bound them at binding 6, which the light probe
// volume took over in ADR 0170.)
#pragma once

#include "ambient_occlusion.slang"

// The contact shadow (1 = lit) at worldPos, a point of the surface being shaded. Only the surface the prepass drew at that
// pixel gets one: a transparent surface, water or anything else in front of it (or behind) sees a different view depth
// in b and stays lit.
float contactShadow(float3 worldPos)
{
    float4 clip = mul(float4(worldPos, 1.0), frame.viewProjection);
    if (!(clip.w > 0.0))
        return 1.0;
    float2 contact = ssaoTexture.SampleLevel(ndcToScreenUv(clip.xy / clip.w), 0.0).gb;
    // clip.w is the view depth (perspective); the tolerance covers the RGBA16F depth and the bilinear footprint.
    return abs(contact.y - clip.w) <= 0.02 * clip.w + 0.02 ? contact.x : 1.0;
}
