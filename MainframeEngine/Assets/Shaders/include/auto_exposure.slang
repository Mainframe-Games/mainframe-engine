// Histogram auto exposure (ADR 0177): the math of MainframeEngine.AutoExposureHistogram (C#, unit-tested), step for step.
// Bin i of 64 covers [logMin + i·w, logMin + (i + 1)·w), w = (logMax − logMin) / 64; values outside count in the end bins.

static const int kHistogramBins = 64;

// The push block of every histogram pass (AutoExposure.HistogramPush).
struct HistogramParams
{
    float logMin;          // AutoExposureHistogramLogMin
    float logMax;          // AutoExposureHistogramLogMax
    uint  metering;        // AutoExposureMetering: 0 uniform, 1 centre-weighted
    float lowPercent;      // AutoExposureLowPercent
    float highPercent;     // AutoExposureHighPercent
    float highlightPercent;
    float highlightLimit;  // scale × exposure / white; 0: no highlight protection
    float minLuminance;
    float maxLuminance;
    float blend;           // 1 − e^(−dt·speed); 1 snaps
};

float histogramBinWidth(float logMin, float logMax)
{
    return max(logMax - logMin, float(kHistogramBins) / 1024.0) / float(kHistogramBins);
}

int histogramBin(float logLuminance, float logMin, float logMax)
{
    int bin = int(floor((logLuminance - logMin) / histogramBinWidth(logMin, logMax)));
    return clamp(bin, 0, kHistogramBins - 1);
}

// 1 (uniform), or 1 + 3·e^(−4·d²) with d² the squared distance from the centre over a corner's (centre-weighted).
float meteringWeight(float2 uv, uint metering)
{
    if (metering != 1u)
        return 1.0;
    float2 d = uv - 0.5;
    float d2 = dot(d, d) * 2.0;
    return 1.0 + 3.0 * exp(-4.0 * d2);
}

// The log2 luminance at `percent` of the total weight, interpolated inside the crossing bin.
float histogramPercentile(float bins[kHistogramBins], float total, float percent, float logMin, float logMax)
{
    float width = histogramBinWidth(logMin, logMax);
    if (!(total > 0.0))
        return logMin;
    float target = total * clamp(percent, 0.0, 100.0) / 100.0;
    float cumulative = 0.0;
    for (int i = 0; i < kHistogramBins; i++)
    {
        float count = bins[i];
        if (count > 0.0 && cumulative + count >= target)
            return logMin + (float(i) + clamp((target - cumulative) / count, 0.0, 1.0)) * width;
        cumulative += count;
    }
    return logMin + float(kHistogramBins) * width;
}

// The weighted mean log2 luminance between the low and high percentiles (bin centres, partial edge bins).
float histogramBandAverage(float bins[kHistogramBins], float total, float lowPercent, float highPercent, float logMin, float logMax)
{
    float width = histogramBinWidth(logMin, logMax);
    if (!(total > 0.0))
        return logMin;
    float low = total * clamp(lowPercent, 0.0, 100.0) / 100.0;
    float high = total * clamp(highPercent, 0.0, 100.0) / 100.0;
    float sum = 0.0, weight = 0.0, cumulative = 0.0;
    for (int i = 0; i < kHistogramBins; i++)
    {
        float count = bins[i];
        float inside = max(min(cumulative + count, high) - max(cumulative, low), 0.0);
        sum += inside * (logMin + (float(i) + 0.5) * width);
        weight += inside;
        cumulative += count;
    }
    return weight > 0.0 ? sum / weight : histogramPercentile(bins, total, lowPercent, logMin, logMax);
}

// The luminance to adapt towards (AutoExposureHistogram.TargetLuminance).
float histogramTargetLuminance(float bins[kHistogramBins], HistogramParams p)
{
    float total = 0.0;
    for (int i = 0; i < kHistogramBins; i++)
        total += bins[i];
    float mean = histogramBandAverage(bins, total, p.lowPercent, p.highPercent, p.logMin, p.logMax);
    float luminance = clamp(exp2(mean), p.minLuminance, p.maxLuminance);
    if (p.highlightLimit > 0.0)
    {
        float highlight = exp2(histogramPercentile(bins, total, p.highlightPercent, p.logMin, p.logMax));
        luminance = min(max(luminance, p.highlightLimit * highlight), p.maxLuminance);
    }
    return luminance;
}
