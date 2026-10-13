using MainframeEngine.Editor.Video;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineCanvasTests
{
    [Fact]
    public void AColumnCoversTheBucketsUnderItsPixel()
    {
        // 0.01 s per pixel and 0.002 s per bucket: five buckets per column
        Assert.Equal((0, 4), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 0, bucketCount: 1000));
        Assert.Equal((50, 54), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 10, bucketCount: 1000));
    }

    [Fact]
    public void ColumnsPastTheEndAreEmptyAndAColumnSpansAtLeastOneBucket()
    {
        Assert.Equal((-1, -1), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 500, bucketCount: 1000));
        Assert.Equal((5, 5), VideoTimelineCanvas.BucketsForColumn(0, 0.001, 0.002, column: 10, bucketCount: 1000));
    }

    [Theory]
    [InlineData(0.0, 0.5, "0:00.0")]
    [InlineData(0.5, 0.5, "0:00.5")]
    [InlineData(1.0, 0.5, "0:01.0")]
    [InlineData(61.5, 0.5, "1:01.5")]
    [InlineData(0.30000000000000004, 0.1, "0:00.3")] // a tick index times 0.1
    [InlineData(1.0, 1.0, "0:01")]
    [InlineData(74.9999, 5.0, "1:15")]
    [InlineData(3725.0, 60.0, "1:02:05")]
    public void RulerLabelsShowTenthsOnlyBelowASecond(double time, double major, string expected) =>
        Assert.Equal(expected, VideoTimelineCanvas.RulerLabel(time, major));

    // Every major interval the ruler can use, from the densest zoom to the widest.
    public static TheoryData<double> MajorIntervals()
    {
        var data = new TheoryData<double>();
        foreach (var interval in VideoTimelineView.NiceIntervals)
            data.Add(interval);
        return data;
    }

    [Theory]
    [MemberData(nameof(MajorIntervals))]
    public void EveryMajorTickIsLabelledWithItsTrueTimeAndNoneRepeats(double major)
    {
        // As DrawRuler does: integer tick indices times the minor interval, labelled every perMajor ticks.
        var (_, minor) = VideoTimelineView.RulerIntervals(VideoTimelineView.LabelSpacing / major);
        var perMajor = (long)Math.Round(major / minor);
        var labels = new List<string>();
        for (var k = 0L; k <= 60 * perMajor; k += perMajor)
        {
            var label = VideoTimelineCanvas.RulerLabel(k * minor, major);
            Assert.Equal(TrueTime(k / perMajor, major), label);
            labels.Add(label);
        }

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    // The n-th major tick's time written out from exact integers: m:ss.t below a second, else m:ss or h:mm:ss.
    private static string TrueTime(long n, double major)
    {
        if (major < 1)
        {
            var tenths = n * (long)Math.Round(major * 10);
            return $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}";
        }

        var seconds = n * (long)Math.Round(major);
        return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    }

    [Fact]
    public void SubSecondRulerIntervalsAreWholeTenths()
    {
        for (var pxPerSecond = 0.001; pxPerSecond < 1e6; pxPerSecond *= 1.07)
        {
            var (major, _) = VideoTimelineView.RulerIntervals(pxPerSecond);
            if (major < 1)
                Assert.True(Math.Abs(major * 10 - Math.Round(major * 10)) < 1e-9, $"{major} s at {pxPerSecond} px/s");
        }
    }

    [Fact]
    public void ALabelIsCachedPerFormattedUnit()
    {
        Assert.Same(VideoTimelineCanvas.RulerLabel(2.5, 0.5), VideoTimelineCanvas.RulerLabel(2.5000001, 0.1));
        Assert.Same(VideoTimelineCanvas.RulerLabel(2, 1), VideoTimelineCanvas.RulerLabel(2.2, 2));
    }
}
