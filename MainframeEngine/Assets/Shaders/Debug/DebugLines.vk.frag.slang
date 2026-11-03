#include "frame.slang"

// 1: drawn after post-processing into a display-encoded (sRGB values in UNORM) target, a post-processed sub-viewport's
// overlay pass (ADR 0169): the authored sRGB colour is written as it is, with the alpha encoded like a colour, which
// approximates the linear-light blend of the HDR pass for translucent lines, and the depth is pulled towards the camera
// (afterPostLineDepth: the scene depth is jittered, the line is not). 0: the linear HDR scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

struct FsOut
{
    float4 color : SV_Target0;
    float depth : SV_DepthLessEqual;
};

[shader("fragment")]
FsOut main([[vk::location(0)]] float4 fragColor, float4 fragCoord : SV_Position)
{
    // Line colours are sRGB-authored with straight alpha; the HDR scene target is linear. No distance fade (unlike
    // the scene grid): a collision shape far away is still a collision shape.
    FsOut o;
    if (kDisplayOutput)
    {
        o.color = float4(fragColor.rgb, linearToSrgb(float3(saturate(fragColor.a))).x);
        o.depth = afterPostLineDepth(fragCoord.z);
    }
    else
    {
        o.color = float4(srgbToLinear(fragColor.rgb), fragColor.a);
        o.depth = fragCoord.z;
    }

    return o;
}
