namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// ADR 0177: histogram auto exposure's math (<see cref="AutoExposureHistogram"/>, the C# reference that
/// <c>include/auto_exposure.slang</c> mirrors): bins, metering, percentiles, the band average and highlight protection.
/// </summary>
public sealed class AutoExposureHistogramTests
{
    private const float LogMin = -10f, LogMax = 6f; // 0.25 stops per bin

    [Fact]
    public void DefaultsAreAverageModeWithUnrealsHistogramSettings()
    {
        var s = PostProcessSettings.Default;
        Assert.Equal(AutoExposureMode.Average, s.AutoExposureMode); // ADR 0154's behaviour stays the default
        Assert.Equal(10f, s.AutoExposureLowPercent);
        Assert.Equal(90f, s.AutoExposureHighPercent);
        Assert.Equal(-10f, s.AutoExposureHistogramLogMin);
        Assert.Equal(6f, s.AutoExposureHistogramLogMax);
        Assert.Equal(AutoExposureMetering.CenterWeighted, s.AutoExposureMetering);
        Assert.False(s.AutoExposureHighlightProtection);
        Assert.Equal(98f, s.AutoExposureHighlightPercent);
        Assert.Equal(2f, s.AutoExposureHighlightWhite);
        Assert.Equal(0f, AutoExposureHistogram.HighlightLimit(s, 1f));

        var profile = new PostProcessProfile();
        Assert.Equal(PostProcessSettings.Default, profile.Settings);
        profile.AutoExposureMode = AutoExposureMode.Histogram;
        profile.AutoExposureHighlightProtection = true;
        Assert.Equal(AutoExposureMode.Histogram, profile.Settings.AutoExposureMode);
        Assert.True(profile.Settings.AutoExposureHighlightProtection);
    }

    [Fact]
    public void LogLuminanceMapsToBinsAndClampsAtTheEnds()
    {
        Assert.Equal(0.25f, AutoExposureHistogram.BinWidth(LogMin, LogMax));
        Assert.Equal(0, AutoExposureHistogram.Bin(-10f, LogMin, LogMax));
        Assert.Equal(0, AutoExposureHistogram.Bin(-9.8f, LogMin, LogMax));
        Assert.Equal(1, AutoExposureHistogram.Bin(-9.75f, LogMin, LogMax));
        Assert.Equal(40, AutoExposureHistogram.Bin(0f, LogMin, LogMax));
        Assert.Equal(63, AutoExposureHistogram.Bin(5.9f, LogMin, LogMax));
        Assert.Equal(63, AutoExposureHistogram.Bin(100f, LogMin, LogMax));
        Assert.Equal(0, AutoExposureHistogram.Bin(-100f, LogMin, LogMax));

        // A degenerate range still has a positive width.
        Assert.True(AutoExposureHistogram.BinWidth(2f, 2f) > 0f);
        Assert.Equal(0, AutoExposureHistogram.Bin(2f, 2f, 2f));
    }

    [Fact]
    public void CenterWeightedMeteringFavoursTheCentre()
    {
        Assert.Equal(1f, AutoExposureHistogram.MeteringWeight(0.5f, 0.5f, AutoExposureMetering.Uniform));
        Assert.Equal(1f, AutoExposureHistogram.MeteringWeight(0f, 1f, AutoExposureMetering.Uniform));

        var centre = AutoExposureHistogram.MeteringWeight(0.5f, 0.5f, AutoExposureMetering.CenterWeighted);
        var edge = AutoExposureHistogram.MeteringWeight(0.5f, 0f, AutoExposureMetering.CenterWeighted);
        var corner = AutoExposureHistogram.MeteringWeight(0f, 0f, AutoExposureMetering.CenterWeighted);
        Assert.Equal(4f, centre, 1e-6f);
        Assert.Equal(1f + 3f * MathF.Exp(-2f), edge, 1e-6f);
        Assert.Equal(1f + 3f * MathF.Exp(-4f), corner, 1e-6f);
        Assert.Equal(corner, AutoExposureHistogram.MeteringWeight(1f, 1f, AutoExposureMetering.CenterWeighted), 1e-6f);
    }

    [Fact]
    public void PercentilesInterpolateInsideTheCrossingBin()
    {
        Span<float> bins = stackalloc float[AutoExposureHistogram.BinCount];
        Assert.Equal(LogMin, AutoExposureHistogram.PercentileLog(bins, 50f, LogMin, LogMax)); // empty

        bins[40] = 8f; // everything in [0, 0.25)
        Assert.Equal(0f, AutoExposureHistogram.PercentileLog(bins, 0f, LogMin, LogMax), 1e-6f);
        Assert.Equal(0.125f, AutoExposureHistogram.PercentileLog(bins, 50f, LogMin, LogMax), 1e-6f);
        Assert.Equal(0.25f, AutoExposureHistogram.PercentileLog(bins, 100f, LogMin, LogMax), 1e-6f);

        // Uniform over every bin: the percentile is linear in the range.
        bins.Fill(1f);
        Assert.Equal(LogMin + 0.25f * 16f, AutoExposureHistogram.PercentileLog(bins, 25f, LogMin, LogMax), 1e-4f);
        Assert.Equal(-2f, AutoExposureHistogram.PercentileLog(bins, 50f, LogMin, LogMax), 1e-4f);
        Assert.Equal(LogMax, AutoExposureHistogram.PercentileLog(bins, 100f, LogMin, LogMax), 1e-4f);
        Assert.Equal(LogMax, AutoExposureHistogram.PercentileLog(bins, 400f, LogMin, LogMax), 1e-4f); // clamped
    }

    [Fact]
    public void TheBandAverageLeavesOutTheDarkestAndBrightestPixels()
    {
        Span<float> bins = stackalloc float[AutoExposureHistogram.BinCount];
        float Centre(int bin) => LogMin + (bin + 0.5f) * 0.25f;

        // 5 % black, 90 % mid grey, 5 % sun: the 10–90 band is all mid grey.
        bins[0] = 5f;
        bins[30] = 90f;
        bins[63] = 5f;
        Assert.Equal(Centre(30), AutoExposureHistogram.BandAverageLog(bins, 10f, 90f, LogMin, LogMax), 1e-5f);

        // The whole range (0–100) is the plain weighted mean of the bin centres.
        var mean = (5f * Centre(0) + 90f * Centre(30) + 5f * Centre(63)) / 100f;
        Assert.Equal(mean, AutoExposureHistogram.BandAverageLog(bins, 0f, 100f, LogMin, LogMax), 1e-4f);

        // Edge bins count in part: 70 % shade, 30 % sun with a 10–90 band → (60 shade + 20 sun) / 80.
        bins.Clear();
        bins[20] = 70f;
        bins[44] = 30f;
        var band = (60f * Centre(20) + 20f * Centre(44)) / 80f;
        Assert.Equal(band, AutoExposureHistogram.BandAverageLog(bins, 10f, 90f, LogMin, LogMax), 1e-4f);

        // An empty band is the low percentile; an empty histogram the range's start.
        Assert.Equal(AutoExposureHistogram.PercentileLog(bins, 50f, LogMin, LogMax), AutoExposureHistogram.BandAverageLog(bins, 50f, 50f, LogMin, LogMax), 1e-6f);
        bins.Clear();
        Assert.Equal(LogMin, AutoExposureHistogram.BandAverageLog(bins, 10f, 90f, LogMin, LogMax));
    }

    [Fact]
    public void HighlightProtectionKeepsASunlitPatchBelowTheWhiteTarget()
    {
        // Brogan's start view in miniature: 94 % shade at 2^-6, 6 % sunlit slope at 2^-1.
        Span<float> bins = stackalloc float[AutoExposureHistogram.BinCount];
        var shade = AutoExposureHistogram.Bin(-6f, LogMin, LogMax);
        var sun = AutoExposureHistogram.Bin(-1f, LogMin, LogMax);
        bins[shade] = 94f;
        bins[sun] = 6f;
        const float exposure = 1.3f;
        var s = PostProcessSettings.Default with
        {
            AutoExposureEnabled = true,
            AutoExposureMode = AutoExposureMode.Histogram,
            AutoExposureScale = 0.16f,
            AutoExposureMinLuminance = 0.01f,
        };

        // Without protection the shade sets the exposure: the sun patch is exposed far past the shoulder.
        var plain = AutoExposureHistogram.TargetLuminance(bins, s, exposure);
        Assert.Equal(MathF.Pow(2f, LogMin + (shade + 0.5f) * 0.25f), plain, 1e-4f);
        var sunLuminance = MathF.Pow(2f, AutoExposureHistogram.PercentileLog(bins, 98f, LogMin, LogMax));
        Assert.True(s.AutoExposureFor(plain) * exposure * sunLuminance > 4f);

        // With it, the 98th percentile is exposed to exactly the white target.
        var protectedSettings = s with { AutoExposureHighlightProtection = true, AutoExposureHighlightWhite = 0.8f };
        var limited = AutoExposureHistogram.TargetLuminance(bins, protectedSettings, exposure);
        Assert.True(limited > plain);
        Assert.Equal(0.8f, protectedSettings.AutoExposureFor(limited) * exposure * sunLuminance, 1e-4f);

        // Still never darker than the max luminance allows, and no effect when the highlights are already in range.
        var capped = protectedSettings with { AutoExposureMaxLuminance = 0.02f };
        Assert.Equal(0.02f, AutoExposureHistogram.TargetLuminance(bins, capped, exposure), 1e-6f);
        var dim = protectedSettings with { AutoExposureHighlightWhite = 16f };
        Assert.Equal(plain, AutoExposureHistogram.TargetLuminance(bins, dim, exposure), 1e-6f);
    }

    [Fact]
    public void TheTargetIsClampedLikeAverageMode()
    {
        Span<float> bins = stackalloc float[AutoExposureHistogram.BinCount];
        bins[0] = 1f; // pitch black
        var s = PostProcessSettings.Default with { AutoExposureMode = AutoExposureMode.Histogram };
        Assert.Equal(s.AutoExposureMinLuminance, AutoExposureHistogram.TargetLuminance(bins, s, 1f), 1e-6f);
        bins[0] = 0f;
        bins[63] = 1f; // brighter than the range
        Assert.Equal(s.AutoExposureMaxLuminance, AutoExposureHistogram.TargetLuminance(bins, s, 1f), 1e-6f);
        Assert.Equal((s.AutoExposureMinLuminance, s.AutoExposureMaxLuminance), AutoExposureHistogram.LuminanceRange(s));
        Assert.Equal((1e-6f, 1e-6f), AutoExposureHistogram.LuminanceRange(s with { AutoExposureMinLuminance = 0f, AutoExposureMaxLuminance = -1f }));
    }
}
