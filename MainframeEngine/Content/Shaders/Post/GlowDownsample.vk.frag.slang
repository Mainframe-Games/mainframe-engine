// High-quality glow (ADR 0168, GlowQuality.High), down the chain: Jimenez's 13-tap downsample ("Next Generation Post
// Processing in Call of Duty: Advanced Warfare", SIGGRAPH 2014). Each destination texel reads 13 bilinear taps of the
// level above (twice its size): a centre box and four overlapping corner boxes. On the first level (from the HDR scene)
// the five boxes are each weighted by 1 / (1 + luma) (Karis average), so a single sun glint cannot flare a whole block,
// and exposure (× auto exposure's), the HDR threshold and the luminance cap are applied as Godot's first level does.
// Every level is multiplied by the glow strength (level k carries strength^(k+1), as in Godot).
[[vk::binding(0, 0)]] Sampler2D source;           // the level above (or the scene), bilinear, clamp to edge
[[vk::binding(1, 0)]] Sampler2D adaptedLuminance; // ADR 0154: 1 × 1 R32F, read when flags & 4 (auto exposure)

struct Glow
{
    float2 dstSize;   // destination size in pixels
    uint   flags;     // 2: first level, 4: auto exposure
    float  strength;
    float  exposure;
    float  threshold;
    float  scale;
    float  bloom;
    float  luminanceCap;
    float  autoExposureScale;
};

[[vk::push_constant]] ConstantBuffer<Glow> pc;

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

float3 tap(float2 uv) { return source.SampleLevel(uv, 0.0).rgb; }

// A box's Karis weight: 1 / (1 + luma), luma of the box's mean.
float karis(float3 c) { return 1.0 / (1.0 + dot(c, kLuma)); }

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy / pc.dstSize;
    uint w, h, levels;
    source.GetDimensions(0u, w, h, levels);
    float2 t = 1.0 / float2(float(w), float(h)); // one source texel

    float3 a = tap(uv + t * float2(-2.0, -2.0));
    float3 b = tap(uv + t * float2(0.0, -2.0));
    float3 c = tap(uv + t * float2(2.0, -2.0));
    float3 d = tap(uv + t * float2(-2.0, 0.0));
    float3 e = tap(uv);
    float3 f = tap(uv + t * float2(2.0, 0.0));
    float3 g = tap(uv + t * float2(-2.0, 2.0));
    float3 hh = tap(uv + t * float2(0.0, 2.0));
    float3 i = tap(uv + t * float2(2.0, 2.0));
    float3 j = tap(uv + t * float2(-1.0, -1.0));
    float3 k = tap(uv + t * float2(1.0, -1.0));
    float3 l = tap(uv + t * float2(-1.0, 1.0));
    float3 m = tap(uv + t * float2(1.0, 1.0));

    bool first = (pc.flags & 2u) != 0u;
    float3 color;
    if (first)
    {
        // Five boxes: the centre (weight 0.5) and four corners (0.125 each), each Karis-weighted, renormalised.
        float3 box0 = (j + k + l + m) * 0.25;
        float3 box1 = (a + b + d + e) * 0.25;
        float3 box2 = (b + c + e + f) * 0.25;
        float3 box3 = (d + e + g + hh) * 0.25;
        float3 box4 = (e + f + hh + i) * 0.25;
        float w0 = 0.5 * karis(box0);
        float w1 = 0.125 * karis(box1);
        float w2 = 0.125 * karis(box2);
        float w3 = 0.125 * karis(box3);
        float w4 = 0.125 * karis(box4);
        color = (box0 * w0 + box1 * w1 + box2 * w2 + box3 * w3 + box4 * w4) / (w0 + w1 + w2 + w3 + w4);
    }
    else
    {
        color = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + hh) * 0.0625 + (j + k + l + m) * 0.125;
    }

    color *= pc.strength;
    if (first)
    {
        float exposure = pc.exposure;
        if ((pc.flags & 4u) != 0u)
            exposure *= pc.autoExposureScale / max(adaptedLuminance.Load(int3(0, 0, 0)).r, 1e-6);
        color *= exposure;
        float luminance = max(color.r, max(color.g, color.b));
        float feedback = max(smoothstep(pc.threshold, pc.threshold + pc.scale, luminance), pc.bloom);
        color = min(color * feedback, float3(pc.luminanceCap));
    }

    return float4(color, 1.0);
}
