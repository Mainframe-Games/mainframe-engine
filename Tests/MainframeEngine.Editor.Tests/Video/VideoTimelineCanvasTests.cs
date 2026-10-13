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
}
