using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentListGameplayTests
{
    private const double Duration = 60.0;

    private static PlannedCut Idle(double start, double end) => new(start, end, $"idle {end - start:0.0}s");

    [Fact]
    public void GameplayCutsBecomeDisabledGameplaySegments()
    {
        var list = new SegmentList(Duration);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20), Idle(30, 35)]);

        Assert.Equal(2, applied);
        Assert.Equal([
            (0.0, 10.0, true, SegmentOrigin.Auto),
            (10.0, 20.0, false, SegmentOrigin.Gameplay),
            (20.0, 30.0, true, SegmentOrigin.Auto),
            (30.0, 35.0, false, SegmentOrigin.Gameplay),
            (35.0, 60.0, true, SegmentOrigin.Auto)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        Assert.Equal("idle 10.0s", list.Segments[1].Reason);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void RerunReplacesOnlyPreviousGameplayCuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(25, 28)]);

        Assert.Equal([
            (25.0, 28.0, SegmentOrigin.Gameplay),
            (40.0, 40.4, SegmentOrigin.Filler)],
            list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SilenceRedetectionKeepsGameplayCuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceAutoSegments([new PlannedCut(5.0, 6.0, "silence 1.00s"), new PlannedCut(15.0, 16.0, "silence 1.00s")]);

        Assert.Equal([
            (5.0, 6.0, SegmentOrigin.Auto),
            (10.0, 20.0, SegmentOrigin.Gameplay)],
            list.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End, s.Origin)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void FillerRerunKeepsGameplayCuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ApplyFillerCuts([new PlannedCut(40.0, 40.4, "filler: um")]);

        Assert.Contains(list.Segments, s => s.Origin == SegmentOrigin.Gameplay && s.Start == 10.0 && s.End == 20.0 && !s.Enabled);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Theory]
    [InlineData(SegmentOrigin.Auto)]
    [InlineData(SegmentOrigin.Manual)]
    public void AutoAndManualCannotBeReplacedInBulk(SegmentOrigin origin)
    {
        var list = new SegmentList(Duration);

        Action act = () => list.ReplaceOriginCuts(origin, []);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void AnEmptyRerunRemovesEarlierGameplayCutsAndReportsZero()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        var applied = list.ReplaceOriginCuts(SegmentOrigin.Gameplay, []);

        Assert.Equal(0, applied);
        Assert.All(list.Segments, s => Assert.True(s.Enabled));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void DissolvingBetweenAGameplayCutAndASilenceCutKeepsTheGameplayOrigin()
    {
        var list = new SegmentList(Duration);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);
        list.ReplaceAutoSegments([new PlannedCut(20.5, 25.0, "silence 4.50s")]);

        // Segments: [0,10] [10,20] gameplay cut, [20,20.5] kept, [20.5,25] auto cut, [25,60].
        var keptBetween = list.Segments.ToList().FindIndex(s => s.Start == 20.0 && s.End == 20.5);
        Assert.True(keptBetween > 0, $"{keptBetween} > 0");
        Assert.True(list.Dissolve(keptBetween));

        var merged = list.Segments.Single(s => s.Start == 10.0 && s.End == 25.0);
        Assert.False(merged.Enabled);
        Assert.Equal(SegmentOrigin.Gameplay, merged.Origin);

        // A later Detect silence discards Auto segments only, so the merged cut must survive it.
        list.ReplaceAutoSegments([]);

        Assert.Contains(list.Segments, s => s.Origin == SegmentOrigin.Gameplay && !s.Enabled && s.Start == 10.0 && s.End == 25.0);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void RerunWithdrawsGameplayCutsNextToASilenceCutWithoutTouchingIt()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([new PlannedCut(8, 12, "silence 4.00s")]);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(12, 29.5)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, []);

        Assert.Equal([
            (0.0, 8.0, true, SegmentOrigin.Auto, (string?)null),
            (8.0, 12.0, false, SegmentOrigin.Auto, "silence 4.00s"),
            (12.0, 60.0, true, SegmentOrigin.Auto, null)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin, s.Reason)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void RerunDoesNotSpreadAManualCutOverTheOldGameplayFootage()
    {
        var list = new SegmentList(Duration);
        list.SetRange(5, 10, enabled: false);
        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(10, 20)]);

        list.ReplaceOriginCuts(SegmentOrigin.Gameplay, [Idle(12, 18)]);

        Assert.Equal([
            (0.0, 5.0, true, SegmentOrigin.Auto),
            (5.0, 10.0, false, SegmentOrigin.Manual),
            (10.0, 12.0, true, SegmentOrigin.Auto),
            (12.0, 18.0, false, SegmentOrigin.Gameplay),
            (18.0, 60.0, true, SegmentOrigin.Auto)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        PartitionAssert.Valid(list.Segments, Duration);
    }
}
