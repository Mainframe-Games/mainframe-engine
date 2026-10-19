// The velocity debug view (ADR 0163, RenderServer.DebugView = Velocity): the final image shows the prepass's motion
// vectors, red = (128 + velocity.x * scale * 255) / 255, green the same for velocity.y (screen UV, +y down), blue =
// 128/255: still is exactly 128 grey. Written as display values (the UNORM swapchain stores them as they are), so a
// capture decodes to the nearest 1/255. Without velocity this frame it passes the tonemapped image through. With flag 4
// (RenderDebugView.AmbientOcclusion, ADR 0165) binding 1 is the SSAO image and its red channel is shown as grey.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D ldrInput;
[[vk::binding(1, 0)]] Sampler2D velocity;

struct VelocityView
{
    float scale;
    uint flags; // bit 0: velocity (or AO) available; bit 1: the target encodes sRGB itself (decode first); bit 2: AO view
};

[[vk::push_constant]] ConstantBuffer<VelocityView> pc;

static const float kStill = 128.0 / 255.0; // zero motion, exactly representable in an 8-bit UNORM target

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int3 texel = int3(int2(fragCoord.xy), 0);
    // ADR 0174: the velocity (or AO) image is at render resolution, the output may be larger: the texel under the centre.
    uint lw, lh, vw, vh, levels;
    ldrInput.GetDimensions(0u, lw, lh, levels);
    velocity.GetDimensions(0u, vw, vh, levels);
    int2 source = min(int2((floor(fragCoord.xy) + 0.5) * float2(float(vw), float(vh)) / float2(float(lw), float(lh))),
                      int2(int(vw) - 1, int(vh) - 1));
    int3 velocityTexel = int3(source, 0);
    float3 color = (pc.flags & 1u) == 0u ? ldrInput.Load(texel).rgb
                 : (pc.flags & 4u) != 0u ? float3(velocity.Load(velocityTexel).r)
                 : float3(clamp(kStill + velocity.Load(velocityTexel).xy * pc.scale, 0.0, 1.0), kStill);
    if ((pc.flags & 2u) != 0u)
        color = srgbToLinear(color);
    return float4(color, 1.0);
}
