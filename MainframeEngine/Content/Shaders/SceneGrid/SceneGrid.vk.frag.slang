#include "frame.slang"

[shader("fragment")]
float4 main([[vk::location(0)]] float4 fragColor, float4 fragCoord : SV_Position) : SV_Target0
{
    // Fade with view distance (near/far come from the camera's projection, set 0).
    // Vertex colours are sRGB-authored; the scene target is linear.
    float depth = 1.0 - linearizeDepth(fragCoord.z) / frame.clip.y;
    return float4(srgbToLinear(fragColor.rgb) * depth, fragColor.a * depth);
}
