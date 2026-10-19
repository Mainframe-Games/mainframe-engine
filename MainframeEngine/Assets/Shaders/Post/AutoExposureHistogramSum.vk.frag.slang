// Histogram auto exposure (ADR 0177), pass 2: the per-row histograms (64 × 64) → the histogram (64 × 1 R32F), each bin
// the sum of its column.
[[vk::binding(0, 0)]] Sampler2D rows; // 64 bins × 64 rows, R32F

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    rows.GetDimensions(0u, w, h, levels);
    int bin = int(fragCoord.x);
    float sum = 0.0;
    for (int y = 0; y < int(h); y++)
        sum += rows.Load(int3(bin, y, 0)).r;
    return float4(sum, 0.0, 0.0, 1.0);
}
