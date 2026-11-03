#include "frame.slang"

// 1: drawn after post-processing into a display-encoded (sRGB values in UNORM) target, a post-processed sub-viewport's
// overlay pass (ADR 0169): the faded colour is encoded back to sRGB and the alpha raised halfway towards its sRGB
// encoding, which approximates the linear-light blend of the HDR pass (faint lines stay about as visible as before the
// effects), and the depth is pulled towards the camera (afterPostLineDepth: the scene depth is jittered, the grid is
// not). 0: the linear HDR scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

struct FsOut
{
    float4 color : SV_Target0;
    float depth : SV_DepthLessEqual;
};

[shader("fragment")]
FsOut main([[vk::location(0)]] float4 fragColor, float4 fragCoord : SV_Position)
{
    // Fade with view distance (near/far come from the camera's projection, set 0).
    // Vertex colours are sRGB-authored; the scene target is linear.
    float depth = 1.0 - linearizeDepth(fragCoord.z) / frame.clip.y;
    float3 color = srgbToLinear(fragColor.rgb) * depth;
    FsOut o;
    if (kDisplayOutput)
    {
        float alpha = saturate(fragColor.a * depth);
        o.color = float4(linearToSrgb(saturate(color)), 0.5 * (alpha + linearToSrgb(float3(alpha)).x));
        o.depth = afterPostLineDepth(fragCoord.z);
    }
    else
    {
        o.color = float4(color, fragColor.a * depth);
        o.depth = fragCoord.z;
    }

    return o;
}
