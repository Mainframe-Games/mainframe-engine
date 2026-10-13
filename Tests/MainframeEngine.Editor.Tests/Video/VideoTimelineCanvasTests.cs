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

    [Fact]
    public void SubSecondTicksNeverRepeatALabel()
    {
        var labels = Enumerable.Range(0, 40).Select(k => VideoTimelineCanvas.RulerLabel(k * 0.5, 0.5)).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void ALabelIsCachedPerFormattedUnit()
    {
        Assert.Same(VideoTimelineCanvas.RulerLabel(2.5, 0.5), VideoTimelineCanvas.RulerLabel(2.5000001, 0.25));
        Assert.Same(VideoTimelineCanvas.RulerLabel(2, 1), VideoTimelineCanvas.RulerLabel(2.2, 2));
    }
}
