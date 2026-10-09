// SSAO passes 2 and 3 (ADR 0165): a 4 × 4 depth-aware bilateral blur of the half-resolution GTAO, which holds each of
// the Bayer pattern's 16 rotations once, so the pattern averages out; run twice, with offsets −1..2 and then −2..1
// (gtao.noise.w), so the combined 7 × 7 kernel is centred and the window edges leave no blocks. Each tap's weight falls
// with its distance from the centre's depth plane (the depth extrapolated along the local gradient, taken on the side
// that continues the surface), relative to the centre's depth: grazing planes keep blurring, silhouettes stop it.
// RG16F in and out.
#include "gtao.slang"

[[vk::binding(0, 0)]] Sampler2D aoDepth;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    aoDepth.GetDimensions(0u, w, h, levels);
    int2 size = int2(int(w), int(h));
    int2 hp = int2(fragCoord.xy);
    float2 centre = aoDepth.Load(int3(hp, 0)).rg;
    float z = centre.y;
    if (z >= kGtaoSkyDistance * 0.5)
        return float4(1.0, z, 0.0, 0.0);

    float zl = aoDepth.Load(int3(max(hp - int2(1, 0), int2(0)), 0)).g;
    float zr = aoDepth.Load(int3(min(hp + int2(1, 0), size - 1), 0)).g;
    float zu = aoDepth.Load(int3(max(hp - int2(0, 1), int2(0)), 0)).g;
    float zd = aoDepth.Load(int3(min(hp + int2(0, 1), size - 1), 0)).g;
    bool useLeft = hp.x + 1 >= size.x || (hp.x > 0 && abs(z - zl) < abs(zr - z));
    bool useUp = hp.y + 1 >= size.y || (hp.y > 0 && abs(z - zu) < abs(zd - z));
    float2 gradient = float2(useLeft ? z - zl : zr - z, useUp ? z - zu : zd - z);
    float invTolerance = 1.0 / (gtao.filter.z * z);

    float sum = 0.0, weights = 0.0;
    int first = int(gtao.noise.w);
    for (int j = first; j < first + 4; j++)
    {
        for (int i = first; i < first + 4; i++)
        {
            int2 tap = clamp(hp + int2(i, j), int2(0), size - 1);
            float2 s = aoDepth.Load(int3(tap, 0)).rg;
            float r = (s.y - (z + dot(gradient, float2(tap - hp)))) * invTolerance;
            float weight = exp2(-r * r);
            sum += s.x * weight;
            weights += weight;
        }
    }

    return float4(sum / weights, z, 0.0, 0.0);
}
