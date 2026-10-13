using MainframeEngine.Video.Playback;

namespace MainframeEngine.Video.Tests;

public sealed class PlayheadInterpolatorTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void BeforeAnyReportTheEstimateIsZero()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };

        Assert.Equal(0, clock.Estimate(now: 100));
    }

    [Fact]
    public void TheEstimateAdvancesWithTheWallClockBetweenReports()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        Assert.Equal(10.25, clock.Estimate(now: 100.25), Tolerance);
    }

    [Fact]
    public void AReportBehindTheEstimateDoesNotMoveThePlayheadBackwards()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        Assert.Equal(10.4, clock.Estimate(now: 100.4), Tolerance);

        clock.Report(position: 10.3, now: 100.4);

        Assert.Equal(10.4, clock.Estimate(now: 100.4), Tolerance);
        Assert.Equal(10.6, clock.Estimate(now: 100.6), Tolerance);
    }

    [Fact]
    public void AReportAheadOfTheEstimateJumpsForwardToIt()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        clock.Report(position: 12, now: 100.1);

        Assert.Equal(12, clock.Estimate(now: 100.1), Tolerance);
    }

    [Fact]
    public void ASeekMayMoveThePlayheadBackwards()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        clock.Estimate(now: 100.5);

        clock.Seek(position: 2, now: 100.5);

        Assert.Equal(2, clock.Estimate(now: 100.5), Tolerance);
        Assert.Equal(2.2, clock.Estimate(now: 100.7), Tolerance);
    }

    [Fact]
    public void TheEstimateNeverPassesTheDuration()
    {
        var clock = new PlayheadInterpolator { Duration = 11 };
        clock.Report(position: 10.9, now: 100);

        Assert.Equal(11, clock.Estimate(now: 101));
    }

    [Fact]
    public void WhilePausedTheEstimateFreezesAndResumesFromWhereItStopped()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);

        clock.Pause(now: 100.3);
        Assert.Equal(10.3, clock.Estimate(now: 101), Tolerance);

        clock.Resume(now: 101);
        Assert.Equal(10.5, clock.Estimate(now: 101.2), Tolerance);
    }

    [Fact]
    public void AReportWhilePausedMovesTheFrozenPlayhead()
    {
        var clock = new PlayheadInterpolator { Duration = 60 };
        clock.Report(position: 10, now: 100);
        clock.Pause(now: 100);

        clock.Report(position: 10.5, now: 100.2);

        Assert.Equal(10.5, clock.Estimate(now: 105), Tolerance);
    }
}
