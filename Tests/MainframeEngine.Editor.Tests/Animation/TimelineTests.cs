namespace MainframeEngine.Editor.Tests;

/// <summary>G1.5: the shared timeline component's math (the song and video timelines keep their own tests).</summary>
public sealed class TimelineTests
{
    [Fact]
    public void TimeAndXRoundTripAndZoomKeepsTheMouseTime()
    {
        var view = new TimelineZoom { PixelsPerUnit = 100, Inset = 10 };
        Assert.Equal(10f, view.X(0));
        Assert.Equal(160f, view.X(1.5));
        Assert.Equal(1.5, view.TimeAt(160f), 6);
        var at = view.TimeAt(300f);
        view.ZoomAround(300f, 2);
        Assert.Equal(at, view.TimeAt(300f), 6);
        view.ScrollBy(-10000);
        Assert.Equal(0, view.Scroll);
    }

    [Fact]
    public void SnapAndRulerSteps()
    {
        Assert.Equal(0.5, TimelineSnap.Round(0.47, 0.25), 6);
        Assert.Equal(0.25, TimelineSnap.Floor(0.47, 0.25), 6);
        Assert.Equal(0.5, TimelineSnap.Ceiling(0.26, 0.25), 6);
        Assert.Equal(0.47, TimelineSnap.Round(0.47, 0), 6);
        Assert.Equal(0.5, TimelineRuler.StepFor(160).Step);  // 0.5 s = 80 px ≥ 60
        Assert.Equal(5, TimelineRuler.StepFor(20).Step);
        Assert.Equal("1.5", TimelineRuler.Label(1.5));
        Assert.Equal("45", TimelineRuler.Label(1.5, framesPerSecond: 30));
        Assert.Same(TimelineRuler.Label(2.25), TimelineRuler.Label(2.25)); // cached: drawing allocates nothing
    }
}
