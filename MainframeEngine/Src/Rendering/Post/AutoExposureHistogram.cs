namespace MainframeEngine;

/// <summary>
/// The math of <see cref="AutoExposureMode.Histogram"/> (ADR 0177), the C# reference of <c>include/auto_exposure.slang</c>
/// (the GPU passes run the same steps; unit tests check this side). Bins hold the metering weight of the 64 × 64
/// log2-luminance texels that fall in them: bin <c>i</c> covers <c>[logMin + i·w, logMin + (i + 1)·w)</c> with
/// <c>w = (logMax − logMin) / </c><see cref="BinCount"/>, and values outside the range count in the end bins.
/// </summary>
public static class AutoExposureHistogram
{
    /// <summary>Bins per histogram (the GPU target is <see cref="BinCount"/> × 1).</summary>
    public const int BinCount = 64;

    /// <summary>The bin a texel of <paramref name="log2Luminance"/> counts in.</summary>
    public static int Bin(float log2Luminance, float logMin, float logMax)
    {
        var width = BinWidth(logMin, logMax);
        var bin = (int)MathF.Floor((log2Luminance - logMin) / width);
        return Math.Clamp(bin, 0, BinCount - 1);
    }

    /// <summary>The width of a bin in log2 units (at least 1/1024 of a stop over the whole range).</summary>
    public static float BinWidth(float logMin, float logMax) => MathF.Max(logMax - logMin, BinCount / 1024f) / BinCount;

    /// <summary>
    /// The weight of a texel whose centre is at <paramref name="u"/>, <paramref name="v"/> (0–1 over the image):
    /// 1 everywhere for <see cref="AutoExposureMetering.Uniform"/>; for <see cref="AutoExposureMetering.CenterWeighted"/>
    /// <c>1 + 3·e^(−4·d²)</c>, where <c>d²</c> is the squared distance from the centre over that of a corner (4 at the
    /// centre, 1.4 at the middle of an edge, 1.05 at a corner).
    /// </summary>
    public static float MeteringWeight(float u, float v, AutoExposureMetering metering)
    {
        if (metering != AutoExposureMetering.CenterWeighted)
            return 1f;
        float du = u - 0.5f, dv = v - 0.5f;
        var d2 = (du * du + dv * dv) * 2f;
        return 1f + 3f * MathF.Exp(-4f * d2);
    }

    /// <summary>
    /// The log2 luminance at <paramref name="percent"/> (0–100) of the histogram's total weight, interpolated linearly
    /// inside the bin that crosses it (so 50 is the weighted median). An empty histogram gives <paramref name="logMin"/>.
    /// </summary>
    public static float PercentileLog(ReadOnlySpan<float> bins, float percent, float logMin, float logMax)
    {
        var width = BinWidth(logMin, logMax);
        var total = Total(bins);
        if (!(total > 0f))
            return logMin;
        var target = total * Math.Clamp(percent, 0f, 100f) / 100f;
        var cumulative = 0f;
        for (var i = 0; i < bins.Length; i++)
        {
            var count = bins[i];
            if (count > 0f && cumulative + count >= target)
                return logMin + (i + Math.Clamp((target - cumulative) / count, 0f, 1f)) * width;
            cumulative += count;
        }

        return logMin + bins.Length * width;
    }

    /// <summary>
    /// The weighted mean log2 luminance of the share of the histogram between <paramref name="lowPercent"/> and
    /// <paramref name="highPercent"/> (Unreal's band average): each bin counts at its centre, with the part of its weight
    /// that lies inside the band (the band's edge bins count in part). When the band is empty (low ≥ high) it is the
    /// percentile at <paramref name="lowPercent"/>.
    /// </summary>
    public static float BandAverageLog(ReadOnlySpan<float> bins, float lowPercent, float highPercent, float logMin, float logMax)
    {
        var width = BinWidth(logMin, logMax);
        var total = Total(bins);
        if (!(total > 0f))
            return logMin;
        var low = total * Math.Clamp(lowPercent, 0f, 100f) / 100f;
        var high = total * Math.Clamp(highPercent, 0f, 100f) / 100f;
        float sum = 0f, weight = 0f, cumulative = 0f;
        for (var i = 0; i < bins.Length; i++)
        {
            var count = bins[i];
            var inside = MathF.Max(MathF.Min(cumulative + count, high) - MathF.Max(cumulative, low), 0f);
            sum += inside * (logMin + (i + 0.5f) * width);
            weight += inside;
            cumulative += count;
        }

        return weight > 0f ? sum / weight : PercentileLog(bins, lowPercent, logMin, logMax);
    }

    /// <summary>
    /// The luminance auto exposure adapts towards for a histogram (before the temporal smoothing): <c>2^</c> the band
    /// average, clamped to [<see cref="PostProcessSettings.AutoExposureMinLuminance"/>,
    /// <see cref="PostProcessSettings.AutoExposureMaxLuminance"/>]; with highlight protection at least
    /// <c>scale × exposure × 2^p / white</c>, where <c>p</c> is the highlight percentile's log luminance, so that
    /// percentile is exposed to at most the white target (still at most the max luminance).
    /// <paramref name="exposure"/> is the frame's manual exposure (the tonemap multiplies by it too).
    /// </summary>
    public static float TargetLuminance(ReadOnlySpan<float> bins, in PostProcessSettings settings, float exposure)
    {
        var (minLuminance, maxLuminance) = LuminanceRange(settings);
        var logMin = settings.AutoExposureHistogramLogMin;
        var logMax = settings.AutoExposureHistogramLogMax;
        var mean = BandAverageLog(bins, settings.AutoExposureLowPercent, settings.AutoExposureHighPercent, logMin, logMax);
        var luminance = Math.Clamp(MathF.Pow(2f, mean), minLuminance, maxLuminance);
        var limit = HighlightLimit(settings, exposure);
        if (limit > 0f)
        {
            var highlight = MathF.Pow(2f, PercentileLog(bins, settings.AutoExposureHighlightPercent, logMin, logMax));
            luminance = MathF.Min(MathF.Max(luminance, limit * highlight), maxLuminance);
        }

        return luminance;
    }

    /// <summary>The adapt pass's clamp: min at least 1e-6, max at least min.</summary>
    public static (float Min, float Max) LuminanceRange(in PostProcessSettings settings)
    {
        var min = MathF.Max(settings.AutoExposureMinLuminance, 1e-6f);
        return (min, MathF.Max(settings.AutoExposureMaxLuminance, min));
    }

    /// <summary>
    /// <c>scale × exposure / white</c>: the adapted luminance must be at least this times the highlight percentile's
    /// luminance; 0 when highlight protection is off.
    /// </summary>
    public static float HighlightLimit(in PostProcessSettings settings, float exposure) =>
        settings.AutoExposureHighlightProtection
            ? settings.AutoExposureScale * MathF.Max(exposure, 0f) / MathF.Max(settings.AutoExposureHighlightWhite, 1e-3f)
            : 0f;

    private static float Total(ReadOnlySpan<float> bins)
    {
        var total = 0f;
        foreach (var count in bins)
            total += count;
        return total;
    }
}
