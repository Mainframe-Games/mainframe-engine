#include "common.slang"

// Screen gizmos are drawn after tonemapping, in the overlay pass, with sRGB-authored colours: normally the target
// stores them as-is (a UNORM view of the swapchain). Only when the swapchain can only be written through an encoding
// sRGB view does the renderer set kLinearizeColors.
[vk::constant_id(0)] const bool kLinearizeColors = false;

[shader("fragment")]
float4 main([[vk::location(0)]] float4 fragColor) : SV_Target0
{
    float4 color = fragColor;
    if (kLinearizeColors)
        color.rgb = srgbToLinear(color.rgb);
    return color;
}
