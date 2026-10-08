#include "frame.slang"

[shader("fragment")]
float4 main([[vk::location(0)]] float4 fragColor) : SV_Target0
{
    // Line colours are sRGB-authored with straight alpha; the HDR scene target is linear. No distance fade (unlike
    // the scene grid): a collision shape far away is still a collision shape.
    return float4(srgbToLinear(fragColor.rgb), fragColor.a);
}
