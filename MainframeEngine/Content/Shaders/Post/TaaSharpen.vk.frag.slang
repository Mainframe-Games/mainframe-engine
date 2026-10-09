// The sharpen after TAA (ADR 0166, TaaSharpenEffect): robust contrast-adaptive sharpening, the algorithm of AMD FidelityFX
// FSR 1's RCAS (MIT), on the tonemapped LDR image. A 5-tap cross; the negative lobe is the largest that keeps every
// channel of the result inside the cross's min/max (no ringing, no clipping), capped at RCAS's limit and scaled by the
// sharpness. Paired with Fullscreen.vk.vert.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D ldrInput;

struct Sharpen
{
    float sharpness; // 0..1 (RCAS's exp2(-stops))
    uint decodeSrgb; // 1: the target encodes sRGB itself, so decode the display values first
};

[[vk::push_constant]] ConstantBuffer<Sharpen> pc;

static const float kLimit = 0.25 - 1.0 / 16.0; // FSR_RCAS_LIMIT

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 last = textureSize2D(ldrInput, 0) - 1;
    int2 p = int2(fragCoord.xy);
    float3 b = ldrInput.Load(int3(clamp(p + int2(0, -1), int2(0), last), 0)).rgb;
    float3 d = ldrInput.Load(int3(clamp(p + int2(-1, 0), int2(0), last), 0)).rgb;
    float3 e = ldrInput.Load(int3(p, 0)).rgb;
    float3 f = ldrInput.Load(int3(clamp(p + int2(1, 0), int2(0), last), 0)).rgb;
    float3 h = ldrInput.Load(int3(clamp(p + int2(0, 1), int2(0), last), 0)).rgb;

    float3 mn4 = min(min(b, d), min(f, h));
    float3 mx4 = max(max(b, d), max(f, h));
    float3 hitMin = min(mn4, e) / max(4.0 * mx4, float3(1e-5));
    float3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, float3(-1e-5));
    float3 lobeRgb = max(-hitMin, hitMax);
    float lobe = max(-kLimit, min(max(lobeRgb.r, max(lobeRgb.g, lobeRgb.b)), 0.0)) * pc.sharpness;
    float3 color = saturate((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0));
    if (pc.decodeSrgb != 0u)
        color = srgbToLinear(color);
    return float4(color, 1.0);
}
