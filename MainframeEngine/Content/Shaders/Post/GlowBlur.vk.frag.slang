// One pass of Godot 4.7's glow blur (servers/rendering/renderer_rd/shaders/effects/copy.glsl, MODE_GAUSSIAN_BLUR +
// MODE_GLOW, MIT; ADR 0124), split into two raster passes per level:
//  horizontal: sample the source at each destination pixel centre (a 2×2 box on a source twice the size, clamped to
//              the edge), weight fireflies down on the first level, blur along x with Godot's 9-tap kernel;
//  vertical:   blur the horizontal result along y, undo the firefly weighting on the first level, multiply by the
//              glow strength (every level), and on the first level apply exposure, the HDR threshold feedback and the
//              luminance cap.
[[vk::binding(0, 0)]] Sampler2D source;
[[vk::binding(1, 0)]] Sampler2D adaptedLuminance; // ADR 0154: 1 × 1 R32F, read when flags & 4 (auto exposure)

struct Glow
{
    float2 dstSize;   // destination size in pixels
    uint   flags;     // 1: horizontal pass, 2: first level, 4: auto exposure
    float  strength;
    float  exposure;
    float  threshold;
    float  scale;
    float  bloom;
    float  luminanceCap;
    float  autoExposureScale;
};

[[vk::push_constant]] ConstantBuffer<Glow> pc;

static const float kKernel[5] = { 0.2024, 0.1790, 0.1240, 0.0672, 0.0285 };
static const float3 kLuma = float3(0.299, 0.587, 0.114);

bool horizontal() { return (pc.flags & 1u) != 0u; }
bool first() { return (pc.flags & 2u) != 0u; }

// The source at destination pixel `px`'s centre, clamped to the edge in destination pixels.
float4 tap(float2 px)
{
    float2 uv = clamp((px + 0.5) / pc.dstSize, 0.5 / pc.dstSize, 1.0 - 0.5 / pc.dstSize);
    float4 c = source.SampleLevel(uv, 0.0);
    if (horizontal() && first())
        c /= 1.0 + dot(c.rgb, kLuma / max(pc.luminanceCap, 6.0)); // tonemap the samples to tame fireflies
    return c;
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 px = floor(fragCoord.xy);
    float2 axis = horizontal() ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float4 color = tap(px) * kKernel[0];
    for (int i = 1; i < 5; i++)
        color += (tap(px + axis * float(i)) + tap(px - axis * float(i))) * kKernel[i];

    if (!horizontal())
    {
        if (first())
            color /= 1.0 - dot(color.rgb, kLuma / max(pc.luminanceCap, 6.0)); // undo the firefly weighting
        color *= pc.strength;
        if (first())
        {
            float exposure = pc.exposure;
            if ((pc.flags & 4u) != 0u)
                exposure *= pc.autoExposureScale / max(adaptedLuminance.Load(int3(0, 0, 0)).r, 1e-6);
            color *= exposure;
            float luminance = max(color.r, max(color.g, color.b));
            float feedback = max(smoothstep(pc.threshold, pc.threshold + pc.scale, luminance), pc.bloom);
            color = min(color * feedback, float4(pc.luminanceCap));
        }
    }

    return color;
}
