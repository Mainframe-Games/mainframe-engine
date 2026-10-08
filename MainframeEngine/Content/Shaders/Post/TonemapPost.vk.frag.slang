// The tonemap pass with a world's PostProcessSettings (ADR 0124): exposure, then Godot 4.7's glow (every blend mode but
// soft light before the curve), the tonemap curve (the engine's ACES fit, or Godot's ACES), soft-light glow after it,
// then sRGB encoding. Ported from Godot 4.7's tonemap.glsl (MIT). Tonemap.vk.frag stays the pass for the default
// settings and for SubViewports.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D hdrScene;
[[vk::binding(1, 0)]] Sampler2D glowLevels[7];

struct Post
{
    float exposure;
    uint  encodeSrgb;      // 1: the target stores raw values (UNORM), so encode here
    uint  tonemapper;      // 0: engine ACES fit, 1: Godot ACES
    uint  glowMode;        // GlowBlendMode; only read when glowEnabled
    uint  glowEnabled;
    float glowIntensity;   // glow_intensity, or glow_mix in Mix mode
    float white;           // Godot's environment white (≥ 1 for ACES)
    float whiteTonemapped; // Godot ACES: the curve at 1.8·white
    float glowWeights[7];  // normalised level weights
};

[[vk::push_constant]] ConstantBuffer<Post> pc;

static const uint kAdd = 0u, kScreen = 1u, kSoftLight = 2u, kReplace = 3u, kMix = 4u;

// ── Tonemap curves ─────────────────────────────────────────────────────────────

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

float3 tonemap(float3 color)
{
    color = max(color, float3(0.0));
    if (pc.tonemapper == 1u) // Godot 4.7 tonemap_aces: input × 1.8, output ÷ white_tonemapped
        return mul(rrtAndOdtFit(mul(color * 1.8, kAcesInput)), kAcesOutput) / pc.whiteTonemapped;
    return clamp(mul(rrtAndOdtFit(mul(color, kAcesInput)), kAcesOutput), 0.0, 1.0); // Tonemap.vk.frag's acesFitted
}

// ── Glow gather: Godot's bicubic B-spline sampling of each level ─────────────────────────

float w0(float a) { return (1.0 / 6.0) * (a * (a * (-a + 3.0) - 3.0) + 1.0); }
float w1(float a) { return (1.0 / 6.0) * (a * a * (3.0 * a - 6.0) + 4.0); }
float w2(float a) { return (1.0 / 6.0) * (a * (a * (-3.0 * a + 3.0) + 3.0) + 1.0); }
float w3(float a) { return (1.0 / 6.0) * (a * a * a); }
float g0(float a) { return w0(a) + w1(a); }
float g1(float a) { return w2(a) + w3(a); }
float h0(float a) { return -1.0 + w1(a) / (w0(a) + w1(a)); }
float h1(float a) { return 1.0 + w3(a) / (w2(a) + w3(a)); }

float3 bicubic(Sampler2D tex, float2 uv)
{
    float2 texSize = float2(textureSize2D(tex, 0));
    float2 pixelSize = 1.0 / texSize;
    uv = uv * texSize + 0.5;
    float2 iuv = floor(uv);
    float2 fuv = frac(uv);
    float g0x = g0(fuv.x), g1x = g1(fuv.x), h0x = h0(fuv.x), h1x = h1(fuv.x);
    float h0y = h0(fuv.y), h1y = h1(fuv.y);
    float2 p0 = (float2(iuv.x + h0x, iuv.y + h0y) - 0.5) * pixelSize;
    float2 p1 = (float2(iuv.x + h1x, iuv.y + h0y) - 0.5) * pixelSize;
    float2 p2 = (float2(iuv.x + h0x, iuv.y + h1y) - 0.5) * pixelSize;
    float2 p3 = (float2(iuv.x + h1x, iuv.y + h1y) - 0.5) * pixelSize;
    return (g0(fuv.y) * (g0x * tex.SampleLevel(p0, 0.0).rgb + g1x * tex.SampleLevel(p1, 0.0).rgb)) +
           (g1(fuv.y) * (g0x * tex.SampleLevel(p2, 0.0).rgb + g1x * tex.SampleLevel(p3, 0.0).rgb));
}

float3 gatherGlow(float2 uv)
{
    float3 glow = float3(0.0);
    if (pc.glowWeights[0] > 0.0001) glow += bicubic(glowLevels[0], uv) * pc.glowWeights[0];
    if (pc.glowWeights[1] > 0.0001) glow += bicubic(glowLevels[1], uv) * pc.glowWeights[1];
    if (pc.glowWeights[2] > 0.0001) glow += bicubic(glowLevels[2], uv) * pc.glowWeights[2];
    if (pc.glowWeights[3] > 0.0001) glow += bicubic(glowLevels[3], uv) * pc.glowWeights[3];
    if (pc.glowWeights[4] > 0.0001) glow += bicubic(glowLevels[4], uv) * pc.glowWeights[4];
    if (pc.glowWeights[5] > 0.0001) glow += bicubic(glowLevels[5], uv) * pc.glowWeights[5];
    if (pc.glowWeights[6] > 0.0001) glow += bicubic(glowLevels[6], uv) * pc.glowWeights[6];
    return glow;
}

float3 applyGlow(float3 color, float3 glow, float white)
{
    if (pc.glowMode == kAdd)
        return color + glow;
    if (pc.glowMode == kScreen)
    {
        glow = clamp(glow, 0.0, white);
        return color + glow - color * glow / white;
    }
    if (pc.glowMode == kSoftLight)
    {
        glow = clamp(glow, 0.0, 1.0);
        float3 lo = ((16.0 * color - 12.0) * color + 4.0) * color;
        float3 curve = select(color <= float3(0.25), lo, sqrt(max(color, float3(0.0))));
        float3 soft = color + glow * (curve - color);
        return select(color > float3(1.0), color, soft);
    }
    return glow; // replace
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 size = float2(textureSize2D(hdrScene, 0));
    float2 uv = fragCoord.xy / size;
    float3 color = hdrScene.Load(int3(int2(fragCoord.xy), 0)).rgb * pc.exposure;

    bool glow = pc.glowEnabled != 0u;
    if (glow && pc.glowMode != kSoftLight)
    {
        float3 g = gatherGlow(uv) * pc.glowIntensity;
        color = pc.glowMode == kMix ? color * (1.0 - pc.glowIntensity) + g : applyGlow(color, g, pc.white);
    }

    color = tonemap(color);

    if (glow && pc.glowMode == kSoftLight)
        color = applyGlow(color, tonemap(gatherGlow(uv) * pc.glowIntensity), pc.white);

    color = clamp(color, 0.0, 1.0);
    if (pc.encodeSrgb != 0u)
        color = linearToSrgb(color);
    return float4(color, 1.0);
}
