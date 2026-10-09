#include "frame.slang"

// 1: drawn after post-processing into a display-encoded (sRGB values in UNORM) target, a post-processed sub-viewport's
// overlay pass (ADR 0169): the authored sRGB colour is written as it is, with the alpha encoded like a colour, which
// approximates the linear-light blend of the HDR pass for translucent lines. 0: the linear HDR scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

[shader("fragment")]
float4 main([[vk::location(0)]] float4 fragColor) : SV_Target0
{
    // Line colours are sRGB-authored with straight alpha; the HDR scene target is linear. No distance fade (unlike
    // the scene grid): a collision shape far away is still a collision shape.
    if (kDisplayOutput)
        return float4(fragColor.rgb, linearToSrgb(float3(saturate(fragColor.a))).x);
    return float4(srgbToLinear(fragColor.rgb), fragColor.a);
}
