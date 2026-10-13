using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoPlaybackPlanTests
{
    // kept [0,4) · cut [4,6) · kept [6,10)
    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    [Fact]
    public void FromTheStartEveryKeptSegmentPlaysInOrder()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.Equal([new PlayRange(0, 4), new PlayRange(6, 10)], plan.Ranges);
        Assert.Equal(8, plan.OutputDuration, 9);
        Assert.Equal(4, plan.OutputStartOf(1), 9);
    }

    [Fact]
    public void FromInsideAKeptSegmentTheFirstRangeStartsAtThePlayhead() =>
        Assert.Equal([new PlayRange(2, 4), new PlayRange(6, 10)], VideoPlaybackPlan.Build(Segments, 2).Ranges);

    [Fact]
    public void FromInsideACutPlaybackStartsAtTheNextKeptSegment() =>
        Assert.Equal([new PlayRange(6, 10)], VideoPlaybackPlan.Build(Segments, 5).Ranges);

    [Fact]
    public void FromTheEndThereIsNothingToPlay()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 10);
        Assert.Empty(plan.Ranges);
        Assert.True(plan.IsFinished(0));
        Assert.Equal(-1, plan.RangeAt(0));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(4.0, 6.0)]
    [InlineData(5.0, 7.0)]
    [InlineData(8.0, 10.0)]
    [InlineData(9.0, 10.0)]
    public void OutputTimeMapsOverTheCut(double output, double source) =>
        Assert.Equal(source, VideoPlaybackPlan.Build(Segments, 0).SourceTimeAt(output), 9);

    [Fact]
    public void TheBoundaryBelongsToTheNextRange()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.Equal(0, plan.RangeAt(3.999));
        Assert.Equal(1, plan.RangeAt(4));
        Assert.True(plan.IsFinished(8));
    }

    [Fact]
    public void TheNextDecoderStartsOneSecondEarly()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.False(plan.ShouldPrefetch(0, 2.9));
        Assert.True(plan.ShouldPrefetch(0, 3.0));
        Assert.False(plan.ShouldPrefetch(1, 7.5)); // the last range has nothing after it
    }

    [Fact]
    public void SliversShorterThanAMillisecondAreSkipped()
    {
        Segment[] segments = [Segment.Create(0, 4, true, SegmentOrigin.Auto), Segment.Create(4, 10, false, SegmentOrigin.Auto)];
        Assert.Empty(VideoPlaybackPlan.Build(segments, 3.9995).Ranges);
    }
}
