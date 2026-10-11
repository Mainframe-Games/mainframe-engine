// 2D canvas glow (ADR 0185), up the chain: level k's result is level k's downsample plus the 3 × 3 tent-filtered result
// of level k + 1 (half its size) × the spread. Level 0 ends up holding every level, wider ones weighted by spread^k.
[[vk::binding(0, 0)]] Sampler2D lower;   // level k + 1's result (bilinear, clamp to edge)
[[vk::binding(0, 1)]] Sampler2D current; // level k's downsample

struct Up
{
    float2 dstSize; // destination size in pixels
    float  spread;
};

[[vk::push_constant]] ConstantBuffer<Up> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy / pc.dstSize;
    uint w, h, levels;
    lower.GetDimensions(0u, w, h, levels);
    float2 t = 1.0 / float2(float(w), float(h));
    float3 s = lower.SampleLevel(uv, 0.0).rgb * 4.0;
    s += (lower.SampleLevel(uv + float2(-t.x, 0.0), 0.0).rgb + lower.SampleLevel(uv + float2(t.x, 0.0), 0.0).rgb +
          lower.SampleLevel(uv + float2(0.0, -t.y), 0.0).rgb + lower.SampleLevel(uv + float2(0.0, t.y), 0.0).rgb) * 2.0;
    s += lower.SampleLevel(uv - t, 0.0).rgb + lower.SampleLevel(uv + t, 0.0).rgb +
         lower.SampleLevel(uv + float2(t.x, -t.y), 0.0).rgb + lower.SampleLevel(uv + float2(-t.x, t.y), 0.0).rgb;
    float3 color = current.SampleLevel(uv, 0.0).rgb + s / 16.0 * pc.spread;
    return float4(color, 1.0);
}
