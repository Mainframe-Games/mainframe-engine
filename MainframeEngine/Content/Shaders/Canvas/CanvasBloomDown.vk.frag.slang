// 2D canvas glow (ADR 0185), down the chain: Jimenez's 13-tap downsample (SIGGRAPH 2014) of the level above. The first
// level reads the canvas layer (premultiplied, gamma space) and keeps only what is above the threshold: a soft-knee
// bright-pass on each tap's luma (CanvasPostMath.BrightPass), so a lone bright pixel spreads no more than its weight.
[[vk::binding(0, 0)]] Sampler2D source; // the level above (or the canvas layer), bilinear, clamp to edge

struct Down
{
    float2 dstSize;   // destination size in pixels
    uint   flags;     // 1: first level (bright-pass)
    float  threshold;
    float  knee;      // threshold × soft knee
};

[[vk::push_constant]] ConstantBuffer<Down> pc;

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

float3 tap(float2 uv)
{
    float3 c = source.SampleLevel(uv, 0.0).rgb;
    if ((pc.flags & 1u) == 0u)
        return c;
    float luma = dot(c, kLuma);
    float knee = max(pc.knee, 1e-4);
    float soft = clamp(luma - pc.threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contribution = max(soft, luma - pc.threshold);
    return c * (contribution / max(luma, 1e-4));
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy / pc.dstSize;
    uint w, h, levels;
    source.GetDimensions(0u, w, h, levels);
    float2 t = 1.0 / float2(float(w), float(h));

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

    float3 color = (j + k + l + m) * 0.125;
    color += (a + b + d + e) * 0.03125;
    color += (b + c + e + f) * 0.03125;
    color += (d + e + g + hh) * 0.03125;
    color += (e + f + hh + i) * 0.03125;
    return float4(color, 1.0);
}
