using MainframeEngine.Editor.Video;
using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineViewTests
{
    private static VideoTimelineView View(double duration = 100, float width = 1000)
    {
        var view = new VideoTimelineView { Width = width };
        view.Reset(duration);
        return view;
    }

    private static SegmentList ThreeSegments() => new(10,
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ]);

    [Fact]
    public void ResetShowsTheWholeRecording()
    {
        var view = View();
        Assert.Equal(0, view.Start);
        Assert.Equal(100, view.Span);
        Assert.Equal(500, view.TimeToX(50), 6);
        Assert.Equal(50, view.XToTime(500), 6);
        Assert.Equal(0.1, view.SecondsPerPixel, 9);
    }

    [Fact]
    public void SetViewClampsTheSpanAndTheStart()
    {
        var view = View();
        view.SetView(-5, 0.01);
        Assert.Equal((0.0, 0.25), (view.Start, view.Span));
        view.SetView(99, 10);
        Assert.Equal(90, view.Start);
        view.SetView(0, 500);
        Assert.Equal(100, view.Span);
    }

    [Fact]
    public void TheMinimumSpanNeverExceedsAShortRecording()
    {
        var view = View(duration: 0.1);
        view.SetView(0, 0.01);
        Assert.Equal(0.1, view.Span, 9);
    }

    [Fact]
    public void WheelZoomKeepsTheTimeUnderTheCursor()
    {
        var view = View();
        view.ZoomAbout(500, wheelDeltaY: 1);
        Assert.Equal(80, view.Span, 6);
        Assert.Equal(10, view.Start, 6);
        Assert.Equal(50, view.XToTime(500), 6);
    }

    [Fact]
    public void PanStaysWithinTheRecording()
    {
        var view = View();
        view.SetView(20, 10);
        view.Pan(-30);
        Assert.Equal(0, view.Start);
    }

    [Fact]
    public void KeepVisibleRecentresOnlyWhenThePlayheadLeavesTheView()
    {
        var view = View();
        view.SetView(0, 10);
        Assert.False(view.KeepVisible(5));
        Assert.True(view.KeepVisible(15));
        Assert.Equal(14, view.Start, 6); // 15 − 0.1 × 10
    }

    [Fact]
    public void EveryViewChangeBumpsTheVersion()
    {
        var view = View();
        var version = view.Version;
        view.SetView(10, 10);
        Assert.True(view.Version > version);
    }

    [Fact]
    public void BoundaryHitsNeedToBeBelowTheRulerAndWithinSixDp()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10); // 100 dp per second: boundaries at x 400 and 600
        Assert.Equal(1, view.BoundaryAt(segments.Segments, 404, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 406, 50));
        Assert.Equal(2, view.BoundaryAt(segments.Segments, 597, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 400, 10));
    }

    [Fact]
    public void SegmentHitsUseTheTimeUnderThePointer()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10);
        Assert.Equal(0, view.SegmentAt(segments, 100, 50));
        Assert.Equal(1, view.SegmentAt(segments, 500, 50));
        Assert.Equal(2, view.SegmentAt(segments, 999, 50));
        Assert.Equal(-1, view.SegmentAt(segments, 500, 10));
        Assert.Equal(-1, view.SegmentAt(segments, 1001, 50));
    }

    [Theory]
    [InlineData(1000.0, 0.1, 0.02)]
    [InlineData(10.0, 10.0, 2.0)]
    [InlineData(1.0, 120.0, 30.0)]
    [InlineData(0.01, 3600.0, 900.0)]
    public void RulerLabelsAreAtLeast80DpApart(double pxPerSecond, double major, double minor)
    {
        var (ma, mi) = VideoTimelineView.RulerIntervals(pxPerSecond);
        Assert.Equal(major, ma, 9);
        Assert.Equal(minor, mi, 9);
    }
}
