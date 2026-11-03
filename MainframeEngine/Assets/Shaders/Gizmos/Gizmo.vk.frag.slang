#include "frame.slang"

// 1: drawn after post-processing into a display-encoded (sRGB values in UNORM) target, a post-processed sub-viewport's
// overlay pass (ADR 0169): sRGB colour and coverage blended as they are, like the UI, and the depth pulled towards the
// camera (afterPostLineDepth). 0: the linear HDR scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

[[vk::binding(0, 1)]] Sampler2D uAtlas; // GizmoIconAtlas (sRGB): icons, and a white block the lines and solids sample

struct FsOut
{
    float4 color : SV_Target0;
    float depth : SV_DepthLessEqual;
};

[shader("fragment")]
FsOut main([[vk::location(0)]] float4 color,
           [[vk::location(1)]] float2 uv,
           [[vk::location(2)]] noperspective float4 segment,
           float4 fragCoord : SV_Position)
{
    // Distance from the segment (round caps), in pixels; coverage falls off over the last pixel.
    float beyond = max(max(-segment.x, segment.x - segment.w), 0.0);
    float distance = length(float2(beyond, segment.y));
    float coverage = saturate(segment.z + 0.5 - distance);
    float4 tex = uAtlas.Sample(uv); // linear (sRGB view)
    float3 rgb = srgbToLinear(color.rgb) * tex.rgb;
    float alpha = saturate(color.a * tex.a * coverage);
    FsOut o;
    if (kDisplayOutput)
    {
        o.color = float4(linearToSrgb(saturate(rgb)), alpha);
        o.depth = afterPostLineDepth(fragCoord.z);
    }
    else
    {
        o.color = float4(rgb, alpha);
        o.depth = fragCoord.z;
    }

    return o;
}
