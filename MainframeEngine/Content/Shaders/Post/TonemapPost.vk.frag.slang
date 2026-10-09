// The tonemap pass with a world's PostProcessSettings (ADR 0124): light shafts added to the scene (ADR 0160), exposure,
// then Godot 4.7's glow (every blend mode but
// soft light before the curve), the tonemap curve (the engine's ACES fit, Godot 4.7's ACES, or Godot 4.4's linear,
// Reinhard, filmic and AgX: ADR 0168), soft-light glow after it, then sRGB encoding. Ported from Godot 4.7's and 4.4's
// tonemap.glsl (MIT). Tonemap.vk.frag stays the pass for the default settings and for SubViewports.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D hdrScene;
[[vk::binding(1, 0)]] Sampler2D glowLevels[7];
[[vk::binding(2, 0)]] Sampler2D adaptedLuminance; // ADR 0154: 1 × 1 R32F, AutoExposureAdapt
[[vk::binding(3, 0)]] Sampler2D lightShafts;      // ADR 0160: half resolution, bilinear; read when shafts > 0

struct Post
{
    float exposure;
    uint  encodeSrgb;      // 1: the target stores raw values (UNORM), so encode here
    uint  tonemapper;      // Tonemapper: 0 engine ACES fit, 1 Godot ACES, 2 linear, 3 Reinhard, 4 filmic, 5 AgX
    uint  glowMode;        // GlowBlendMode; only read when glowEnabled
    uint  glowEnabled;
    float glowIntensity;   // glow_intensity, or glow_mix in Mix mode
    float white;           // Godot's environment white (≥ 1 for ACES, Reinhard, filmic; 1 otherwise)
    float whiteTonemapped; // Godot ACES: the curve at 1.8·white; filmic: the filmic curve at white
    float glowWeights[7];  // normalised level weights
    uint  autoExposure;    // 1: multiply by autoExposureScale / the adapted luminance (ADR 0154)
    float autoExposureScale;
    float shafts;          // ADR 0160: intensity × fade, 0 when no shafts were drawn
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

// Godot 4.4 tonemap_reinhard: Reinhard's extended formula, c (1 + c / white²) / (1 + c).
float3 reinhard(float3 color, float white)
{
    float white2 = white * white;
    float3 white2Color = white2 * color;
    return (white2Color + color * color) / (white2Color + white2);
}

// Godot 4.4 tonemap_filmic (Hable's curve with a 2× exposure bias baked into the constants), before ÷ the curve at white.
float3 filmic(float3 color)
{
    const float A = 0.22 * 4.0, B = 0.30 * 2.0, C = 0.10, D = 0.20, E = 0.01, F = 0.30;
    return ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
}

// Godot 4.4 agx_contrast_approx: a 6th-order polynomial fit of EaryChow's AgX sigmoid, for x in [0, 1].
float3 agxContrast(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 0.021 * x + 4.0111 * x2 - 25.682 * x2 * x + 70.359 * x4 - 74.778 * x4 * x + 27.069 * x4 * x2;
}

// Linear sRGB → linear Rec.2020 combined with Blender's AgX inset (GLSL column order, transform with mul(v, M)).
static const float3x3 kAgxInset = float3x3(
    0.54490813676363087053, 0.14044005884001287035, 0.088827411851915368603,
    0.37377945959812267119, 0.75410959864013760045, 0.17887712465043811023,
    0.081384976686407536266, 0.10543358536857773485, 0.73224999956948382528);

// The inverse AgX outset combined with linear Rec.2020 → linear sRGB.
static const float3x3 kAgxOutset = float3x3(
    1.9645509602733325934, -0.29932243390911083839, -0.16436833806080403409,
    -0.85585845117807513559, 1.3264510741502356555, -0.23822464068860595117,
    -0.10886710826831608324, -0.027084020983874825605, 1.402665347143271889);

// Godot 4.4 tonemap_agx (Blender's AgX base, EaryChow's AgXBasesRGB.py): log2 between −12.47 and +4.03 EV around
// middle grey, the sigmoid, back to linear (^2.4), the outset. May return negative components (clamped before encoding).
float3 agx(float3 color)
{
    const float minEv = -12.4739311883324; // log2(2^-10 · 0.18)
    const float maxEv = 4.02606881166759;  // log2(2^6.5 · 0.18)
    color = mul(max(color, float3(2e-10)), kAgxInset);
    color = clamp(log2(color), minEv, maxEv);
    color = (color - minEv) / (maxEv - minEv);
    color = agxContrast(color);
    color = pow(max(color, float3(0.0)), float3(2.4));
    return mul(color, kAgxOutset);
}

float3 tonemap(float3 color)
{
    if (pc.tonemapper == 5u)
        return agx(color);
    color = max(color, float3(0.0));
    if (pc.tonemapper == 1u) // Godot 4.7 tonemap_aces: input × 1.8, output ÷ white_tonemapped
        return mul(rrtAndOdtFit(mul(color * 1.8, kAcesInput)), kAcesOutput) / pc.whiteTonemapped;
    if (pc.tonemapper == 2u) // linear: clipped by the encode
        return color;
    if (pc.tonemapper == 3u)
        return reinhard(color, pc.white);
    if (pc.tonemapper == 4u)
        return filmic(color) / pc.whiteTonemapped;
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
    float exposure = pc.exposure;
    if (pc.autoExposure != 0u)
        exposure *= pc.autoExposureScale / max(adaptedLuminance.Load(int3(0, 0, 0)).r, 1e-6);
    float3 hdr = hdrScene.Load(int3(int2(fragCoord.xy), 0)).rgb;
    if (pc.shafts > 0.0)
        hdr += lightShafts.SampleLevel(uv, 0.0).rgb * pc.shafts; // scene radiance, before exposure, alongside glow
    float3 color = hdr * exposure;

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
