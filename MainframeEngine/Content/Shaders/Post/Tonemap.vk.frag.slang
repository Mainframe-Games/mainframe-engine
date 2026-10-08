// HDR scene (linear Rec.709, RGBA16F) → display: exposure, ACES filmic tonemap (Stephen Hill's fit of the RRT +
// sRGB ODT; ADR in memory/decisions), then sRGB encoding — by the swapchain's sRGB view, or here when the
// swapchain is UNORM. Mirrored in C# by ColorSpace.AcesFitted for tests.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D hdrScene;

struct Tonemap
{
    float exposure;
    uint  encodeSrgb; // bit 0: the target stores raw values (UNORM), so encode here; bit 1: keep the scene's alpha
};

[[vk::push_constant]] ConstantBuffer<Tonemap> pc;

// sRGB/Rec.709 → ACES AP1 with the RRT's saturation folded in. Each row of three is a column of the matrix
// (row-major layout: transform with mul(v, M)).
static const float3x3 kAcesInput = float3x3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777);

// ODT → Rec.709.
static const float3x3 kAcesOutput = float3x3(
     1.60475, -0.10208, -0.00327,
    -0.53108,  1.10813, -0.07276,
    -0.07367, -0.00605,  1.07602);

float3 rrtAndOdtFit(float3 v)
{
    float3 a = v * (v + 0.0245786) - 0.000090537;
    float3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

float3 acesFitted(float3 color)
{
    color = mul(color, kAcesInput);
    color = rrtAndOdtFit(color);
    color = mul(color, kAcesOutput);
    return clamp(color, 0.0, 1.0);
}

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inNdc, float4 fragCoord : SV_Position) : SV_Target0
{
    float4 hdr = hdrScene.Load(int3(int2(fragCoord.xy), 0));
    float3 color = acesFitted(max(hdr.rgb, float3(0.0)) * pc.exposure);
    if ((pc.encodeSrgb & 1u) != 0u)
        color = linearToSrgb(color);
    // A transparent sub-viewport (SubViewport.TransparentBg) keeps what its scene wrote; every other view is opaque.
    return float4(color, (pc.encodeSrgb & 2u) != 0u ? clamp(hdr.a, 0.0, 1.0) : 1.0);
}
