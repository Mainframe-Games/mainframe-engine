// Debug lines (physics collision shapes, gizmos): world-space line list drawn with the shared set 0 camera.
#include "frame.slang"

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 fragColor;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPosition, [[vk::location(1)]] float4 inColor)
{
    VsOut o;
    o.position = mul(float4(inPosition, 1.0), frame.viewProjection);
    o.fragColor = inColor;
    return o;
}
