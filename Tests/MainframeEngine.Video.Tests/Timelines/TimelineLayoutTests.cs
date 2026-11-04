using MainframeEngine.Video.Timelines;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class TimelineLayoutTests
{
    [Fact]
    public void FrameRatesSnapAndRecogniseNtsc()
    {
        Assert.Equal(1.0, Rate30.Snap(1.0101));
        Assert.Equal(30, Rate30.FrameAt(1.0101));
        Assert.Equal(new FrameRate(30000, 1001), FrameRate.FromFps(29.97));
        Assert.Equal(new FrameRate(60, 1), FrameRate.FromFps(59.999));
        Assert.Equal("30000/1001", new FrameRate(30000, 1001).ToFfmpeg());
    }

    [Fact]
    public void NewTimelinesHaveTwoTracksOfEachKindAndATimelineUid()
    {
        var t = Vertical();
        Assert.Equal(["V1", "V2", "A1", "A2"], t.Tracks.Select(x => x.Name));
        Assert.Matches("^vtl_[0-9a-f]{12}$", t.Uid);
        Assert.Throws<ArgumentException>(() => TimelineFactory.New(new TimelineSettings(1081, 1920, Rate30)));
    }

    [Fact]
    public void LengthIsKeptTimeOverSpeedInFrames()
    {
        var t = Append(Vertical(), 10, 20);
        var clip = On(t, V1(t), 0);
        Assert.Equal(10, TimelineLayout.Length(clip, t.Rate), 6);
        Assert.Equal(5, TimelineLayout.Length(clip with { Speed = 2 }, t.Rate), 6);
        Assert.Equal(Rate30.Snap(10 / 3.0), TimelineLayout.Length(clip with { Speed = 3 }, t.Rate), 9);
    }

    [Fact]
    public void MapsAcrossCollapsedCut()
    {
        var t = Append(Vertical(), 10, 20);
        t = Cut(t, On(t, V1(t), 0), 12, 15); // 3 s cut inside the window: 7 s left
        var clip = On(t, V1(t), 0);

        Assert.Equal(7, TimelineLayout.Length(clip, t.Rate), 6);
        Assert.Equal([new SourceRange(10, 12), new SourceRange(15, 20)], TimelineLayout.KeptRanges(clip));
        Assert.Equal(11, TimelineLayout.SourceTimeAt(clip, t.Rate, 1)!.Value, 6);
        Assert.Equal(15, TimelineLayout.SourceTimeAt(clip, t.Rate, 2)!.Value, 6); // the cut is skipped
        Assert.Equal(16, TimelineLayout.SourceTimeAt(clip, t.Rate, 3)!.Value, 6);
        Assert.Null(TimelineLayout.SourceTimeAt(clip, t.Rate, 7));
        Assert.Equal(3, TimelineLayout.TimelineTimeOf(clip, 16)!.Value, 6);
        Assert.Null(TimelineLayout.TimelineTimeOf(clip, 13)); // cut
        Assert.Null(TimelineLayout.TimelineTimeOf(clip, 25)); // outside the window
    }

    [Fact]
    public void SpeedScalesTheMapping()
    {
        var t = Append(Vertical(), 0, 10);
        var clip = On(t, V1(t), 0) with { Speed = 2 };
        Assert.Equal(6, TimelineLayout.SourceTimeAt(clip, t.Rate, 3)!.Value, 6);
        Assert.Equal(3, TimelineLayout.TimelineTimeOf(clip, 6)!.Value, 6);
    }

    [Fact]
    public void ClipAtDurationAndRange()
    {
        var t = Append(Append(Vertical(), 0, 4), 10, 12);
        Assert.Equal(6, TimelineLayout.Duration(t), 6);
        Assert.Equal(10, TimelineLayout.SourceTimeAt(TimelineLayout.ClipAt(t, V1(t), 4)!, t.Rate, 4)!.Value, 6);
        Assert.Null(TimelineLayout.ClipAt(t, V1(t), 6));
        Assert.Equal((0.0, 6.0), TimelineLayout.Range(t));
        Assert.Equal((1.0, 5.0), TimelineLayout.Range(TimelineEdits.SetInOut(t, 1, 5)));
    }
}
