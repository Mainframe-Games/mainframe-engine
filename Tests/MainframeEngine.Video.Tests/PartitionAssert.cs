using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

internal static class PartitionAssert
{
    /// <summary>
    /// Asserts the partition invariant directly, independent of SegmentList's own checks:
    /// sorted, no gaps, no overlaps, first starts at 0, last ends at duration, every segment has
    /// positive length, ids are unique.
    /// </summary>
    public static void Valid(IReadOnlyList<Segment> segments, double duration)
    {
        Assert.NotEmpty(segments);
        Assert.Equal(0.0, segments[0].Start);
        Assert.Equal(duration, segments[^1].End);

        for (var i = 0; i < segments.Count; i++)
        {
            Assert.True(segments[i].End > segments[i].Start, $"segment {i} must have positive length");
            if (i > 0)
                Assert.True(segments[i].Start == segments[i - 1].End, $"segments {i - 1} and {i} must share a boundary exactly");
        }

        var ids = segments.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
