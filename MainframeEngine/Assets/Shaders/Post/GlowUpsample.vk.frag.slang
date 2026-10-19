// High-quality glow (ADR 0168, GlowQuality.High), up the chain: level k's result is the 3 × 3 tent-filtered result of
// level k + 1 (half its size, taps `radius` of its texels apart; Jimenez 2014) plus level k's downsample × its glow weight. The top level is its weighted
// downsample alone. The tent is normalised, so the chain conserves energy: level 0 ends up holding Σ weight_k × level k,
// spread smoothly, which the post tonemap composites as one image (weight 1).
[[vk::binding(0, 0)]] Sampler2D lower;   // level k + 1's result (bilinear, clamp to edge); unread at the top level
[[vk::binding(0, 1)]] Sampler2D current; // level k's downsample

struct Up
{
    float2 dstSize; // destination size in pixels
    uint   flags;   // 1: the top level (no lower level)
    float  weight;  // level k's glow weight
    float  radius;  // the tent's tap spacing in texels of the lower level (GlowEffect.UpsampleRadius)
};

[[vk::push_constant]] ConstantBuffer<Up> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy / pc.dstSize;
    float3 color = current.SampleLevel(uv, 0.0).rgb * pc.weight;
    if ((pc.flags & 1u) == 0u)
    {
        uint w, h, levels;
        lower.GetDimensions(0u, w, h, levels);
        float2 t = pc.radius / float2(float(w), float(h));
        float3 s = lower.SampleLevel(uv, 0.0).rgb * 4.0;
        s += (lower.SampleLevel(uv + float2(-t.x, 0.0), 0.0).rgb + lower.SampleLevel(uv + float2(t.x, 0.0), 0.0).rgb +
              lower.SampleLevel(uv + float2(0.0, -t.y), 0.0).rgb + lower.SampleLevel(uv + float2(0.0, t.y), 0.0).rgb) * 2.0;
        s += lower.SampleLevel(uv - t, 0.0).rgb + lower.SampleLevel(uv + t, 0.0).rgb +
             lower.SampleLevel(uv + float2(t.x, -t.y), 0.0).rgb + lower.SampleLevel(uv + float2(-t.x, t.y), 0.0).rgb;
        color += s / 16.0;
    }

    return float4(color, 1.0);
}
