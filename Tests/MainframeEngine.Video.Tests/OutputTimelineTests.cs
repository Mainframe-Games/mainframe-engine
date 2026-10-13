using MainframeEngine.Video.Models;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Video.Tests;

public sealed class OutputTimelineTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 6.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(6.0, 10.0, enabled: false, SegmentOrigin.Auto),
    ];

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]   // start of the cut maps to where the next kept region begins
    [InlineData(2.7, 2.0)]   // inside the cut: same
    [InlineData(3.0, 2.0)]
    [InlineData(4.5, 3.5)]
    [InlineData(6.0, 5.0)]   // trailing cut maps to the output end
    [InlineData(9.9, 5.0)]
    public void SourceTimesMapToOutputTimes(double source, double expected)
    {
        Assert.Equal(expected, new OutputTimeline(Segments).ToOutput(source), 1e-9);
    }

    [Fact]
    public void OutputDurationIsTheSumOfKeptSegments()
    {
        Assert.Equal(5.0, new OutputTimeline(Segments).OutputDuration, 1e-9);
    }

    [Fact]
    public void WithNothingKeptEverythingMapsToZero()
    {
        var timeline = new OutputTimeline([Segment.Create(0.0, 5.0, enabled: false, SegmentOrigin.Auto)]);

        Assert.Equal(0.0, timeline.OutputDuration);
        Assert.Equal(0.0, timeline.ToOutput(3.0));
    }
}
