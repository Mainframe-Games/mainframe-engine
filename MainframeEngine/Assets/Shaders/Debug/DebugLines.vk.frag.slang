#include "frame.slang"

// 1: drawn after post-processing into a display-encoded (sRGB values in UNORM) target, a post-processed sub-viewport's
// overlay pass (ADR 0169): the authored sRGB colour is written as it is, with the alpha encoded like a colour, which
// approximates the linear-light blend of the HDR pass for translucent lines, and the depth is pulled towards the camera
// (pulledDepth). 0: the linear HDR scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

// Lines lie on the surfaces they outline (a collision box on its mesh, a selection box): depth-tested as they are, they
// z-fight with them, and after post a TAA-jittered scene depth makes it worse. Both paths pull the line towards the camera
// by kPullPixels pixels' footprint (about 0.9 % of the view depth at 1080p): enough at grazing angles down to ~4 degrees,
// little enough that a line well behind a surface stays hidden.
static const float kPullPixels = 8.0;

float pulledDepth(float depth)
{
    float near = frame.clip.x;
    float far  = frame.clip.y;
    float pixel = 2.0 / (frame.projection[1][1] * frame.viewport.y); // one pixel's footprint per unit of view depth
    if (frame.projection[2][3] == 0.0)
        return saturate(depth - kPullPixels * pixel / (far - near)); // orthographic: depth is linear in view depth
    float z = max(linearizeDepth(depth) * (1.0 - kPullPixels * pixel), near);
    return saturate(far * (z - near) / (z * (far - near)));
}

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
        o.depth = pulledDepth(fragCoord.z);
    }
    else
    {
        o.color = float4(srgbToLinear(fragColor.rgb), fragColor.a);
        o.depth = pulledDepth(fragCoord.z);
    }

    return o;
}
