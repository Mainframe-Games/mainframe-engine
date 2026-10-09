// Auto exposure (ADR 0154): one level of the reduction, the mean of a 4 × 4 block of the source (64 → 16 → 4 → 1).
// The last copy pass reuses it with a 1 × 1 source (every tap clamps to that texel).
[[vk::binding(0, 0)]] Sampler2D source;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    source.GetDimensions(0u, w, h, levels);
    int2 origin = int2(floor(fragCoord.xy)) * 4;
    float sum = 0.0;
    for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
            sum += source.Load(int3(clamp(origin + int2(x, y), int2(0, 0), int2(int(w) - 1, int(h) - 1)), 0)).r;
    return float4(sum / 16.0, 0.0, 0.0, 1.0);
}
