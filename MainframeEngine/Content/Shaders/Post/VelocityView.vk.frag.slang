// The velocity debug view (ADR 0163, RenderServer.DebugView = Velocity): the final image shows the prepass's motion
// vectors, red = (128 + velocity.x * scale * 255) / 255, green the same for velocity.y (screen UV, +y down), blue =
// 128/255: still is exactly 128 grey. Written as display values (the UNORM swapchain stores them as they are), so a
// capture decodes to the nearest 1/255. Without velocity this frame it passes the tonemapped image through.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D ldrInput;
[[vk::binding(1, 0)]] Sampler2D velocity;

struct VelocityView
{
    float scale;
    uint flags; // bit 0: velocity available; bit 1: the target encodes sRGB itself (decode first)
};

[[vk::push_constant]] ConstantBuffer<VelocityView> pc;

static const float kStill = 128.0 / 255.0; // zero motion, exactly representable in an 8-bit UNORM target

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int3 texel = int3(int2(fragCoord.xy), 0);
    float3 color = (pc.flags & 1u) != 0u
        ? float3(clamp(kStill + velocity.Load(texel).xy * pc.scale, 0.0, 1.0), kStill)
        : ldrInput.Load(texel).rgb;
    if ((pc.flags & 2u) != 0u)
        color = srgbToLinear(color);
    return float4(color, 1.0);
}
