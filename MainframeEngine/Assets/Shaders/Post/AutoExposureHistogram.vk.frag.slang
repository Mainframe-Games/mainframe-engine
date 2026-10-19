// Histogram auto exposure (ADR 0177), pass 1: the 64 × 64 log2 luminance (AutoExposureLuminance) → a 64 × 64 R32F image
// of per-row histograms. Texel (bin, row) is the metering weight of that row's texels that fall in the bin;
// AutoExposureHistogramSum adds the rows up. A gather, so nothing blends.
#include "auto_exposure.slang"

[[vk::binding(0, 0)]] Sampler2D logLuminance; // 64 × 64, R16F

[[vk::push_constant]] ConstantBuffer<HistogramParams> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    logLuminance.GetDimensions(0u, w, h, levels);
    int bin = int(fragCoord.x);
    int row = int(fragCoord.y);
    float v = (float(row) + 0.5) / float(h);
    float sum = 0.0;
    for (int x = 0; x < int(w); x++)
    {
        float l = logLuminance.Load(int3(x, row, 0)).r;
        if (histogramBin(l, pc.logMin, pc.logMax) == bin)
            sum += meteringWeight(float2((float(x) + 0.5) / float(w), v), pc.metering);
    }
    return float4(sum, 0.0, 0.0, 1.0);
}
