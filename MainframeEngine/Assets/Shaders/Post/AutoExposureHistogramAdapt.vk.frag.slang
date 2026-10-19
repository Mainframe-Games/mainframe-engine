// Histogram auto exposure (ADR 0177), pass 3: the 1 × 1 R32F adapted luminance, like AutoExposureAdapt but measured from
// the histogram: the mean log luminance between the low and high percentiles, clamped to [min, max], raised so the
// highlight percentile is exposed to at most the white target (highlight protection), then approached from the
// previous frame's value by `blend`. The tonemap and the glow's first level multiply by scale / this.
#include "auto_exposure.slang"

[[vk::binding(0, 0)]] Sampler2D histogram; // 64 × 1, R32F
[[vk::binding(1, 0)]] Sampler2D previous;  // 1 × 1, R32F: last frame's adapted luminance

[[vk::push_constant]] ConstantBuffer<HistogramParams> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float bins[kHistogramBins];
    for (int i = 0; i < kHistogramBins; i++)
        bins[i] = histogram.Load(int3(i, 0, 0)).r;
    float measured = histogramTargetLuminance(bins, pc);
    float last = previous.Load(int3(0, 0, 0)).r;
    float adapted = (pc.blend >= 1.0 || !(last > 0.0)) ? measured : last + (measured - last) * pc.blend;
    return float4(adapted, 0.0, 0.0, 1.0);
}
