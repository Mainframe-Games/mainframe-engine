namespace MainframeEngine.Tests.Core;

public class FPSCounterTests
{
    [Fact]
    public void TotalFrameCountCountsEveryUpdate()
    {
        var counter = new FPSCounter();

        for (var i = 0; i < 10; i++)
            counter.Update();

        Assert.Equal(10u, counter.TotalFrameCount);
    }

    [Fact]
    public void FpsStaysZeroUntilTheSamplingWindowElapses()
    {
        var counter = new FPSCounter();

        counter.Update();

        Assert.Equal(0u, counter.Fps);
        Assert.Equal(0u, counter.Ms);
    }

    [Fact]
    public void FpsIsComputedAfterTheSamplingWindow()
    {
        var counter = new FPSCounter();
        for (var i = 0; i < 9; i++)
            counter.Update();

        Thread.Sleep(550); // > 500 ms window
        counter.Update(); // 10th frame closes the window

        // 10 frames over ≥ 0.55 s: at most ~18 fps, and certainly not zero.
        Assert.InRange(counter.Fps, 1u, 19u);
        Assert.Equal((uint)(1000f / counter.Fps), counter.Ms);
        Assert.Equal(10u, counter.TotalFrameCount);
    }
}
