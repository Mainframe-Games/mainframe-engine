using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class FillerCutPlannerTests
{
    private const double Duration = 10.0;
    private static readonly DetectionSettings Settings = DetectionSettings.Default with { MinKeepMs = 120 };

    private static FillerSpan Span(double start, double end, string word = "um") => new(start, end, word);

    private static List<Segment> Partition(params (double Start, double End, bool Enabled, SegmentOrigin Origin)[] parts)
        => parts.Select(p => Segment.Create(p.Start, p.End, p.Enabled, p.Origin)).ToList();

    private static List<Segment> AllKept() => Partition((0.0, Duration, true, SegmentOrigin.Auto));

    [Fact]
    public void ASpanInKeptFootageBecomesACutWithAFillerReason()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3)], AllKept(), Settings, Duration);

        Assert.Equal(new PlannedCut(2.0, 2.3, "filler: um"), Assert.Single(cuts));
    }

    [Fact]
    public void SpansAreClampedToTheTimelineAndEmptyOnesDropped()
    {
        var cuts = FillerCutPlanner.Plan([Span(-0.5, 0.2), Span(9.9, 10.5), Span(4.0, 4.0)], AllKept(), Settings, Duration);

        Assert.Equal([(0.0, 0.2), (9.9, 10.0)], cuts.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void ASpanTouchingAManualSegmentIsDropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, true, SegmentOrigin.Manual), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.9, 2.1), Span(5.0, 5.2)], current, Settings, Duration);

        Assert.Equal([5.0], cuts.Select(c => c.Start));
    }

    [Fact]
    public void ASpanAlreadyInsideACutIsDropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.2, 2.5)], current, Settings, Duration);

        Assert.Empty(cuts);
    }

    [Fact]
    public void ASpanStraddlingACutEdgeIsKept()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.9, 3.2)], current, Settings, Duration);

        Assert.Equal(new PlannedCut(2.9, 3.2, "filler: um"), Assert.Single(cuts));
    }

    [Fact]
    public void OverlappingAndTouchingSpansMergeAndJoinTheirWords()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3, "um"), Span(2.3, 2.6, "uh"), Span(2.5, 2.8, "er")], AllKept(), Settings, Duration);

        Assert.Equal(new PlannedCut(2.0, 2.8, "filler: um uh er"), Assert.Single(cuts));
    }

    [Fact]
    public void AKeptSliverShorterThanMinKeepBetweenTwoSpansIsBridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3), Span(2.35, 2.6, "uh")], AllKept(), Settings, Duration);

        Assert.Equal(new PlannedCut(2.0, 2.6, "filler: um uh"), Assert.Single(cuts));
    }

    [Fact]
    public void AKeptSliverBetweenASpanAndAnExistingCutIsBridgedOnBothSides()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(1.7, 1.95), Span(3.05, 3.3, "uh")], current, Settings, Duration);

        Assert.Equal([(1.7, 2.0), (3.0, 3.3)], cuts.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void ASliverIsNotBridgedOverAManualSegment()
    {
        var current = Partition(
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 3.0, false, SegmentOrigin.Auto),
            (3.0, 3.05, true, SegmentOrigin.Manual),
            (3.05, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(3.05, 3.3)], current, Settings, Duration);

        Assert.Equal(3.05, Assert.Single(cuts).Start);
    }

    [Fact]
    public void SliversAtTheEndsOfTheTimelineAreBridged()
    {
        var cuts = FillerCutPlanner.Plan([Span(0.05, 0.3), Span(9.7, 9.95, "uh")], AllKept(), Settings, Duration);

        Assert.Equal([(0.0, 0.3), (9.7, 10.0)], cuts.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void OutputIsSortedByStart()
    {
        var cuts = FillerCutPlanner.Plan([Span(6.0, 6.2), Span(2.0, 2.2)], AllKept(), Settings, Duration);

        Assert.Equal(cuts.Select(c => c.Start).Order(), cuts.Select(c => c.Start));
    }

    [Fact]
    public void ASpanInsideAPreviousFillerCutIsPlannedAgain()
    {
        // Second run: the old filler cut is about to be dissolved by ApplyFillerCuts, so it must not count as "already cut".
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 2.3, false, SegmentOrigin.Filler), (2.3, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.0, 2.3)], current, Settings, Duration);

        Assert.Equal(new PlannedCut(2.0, 2.3, "filler: um"), Assert.Single(cuts));
    }

    [Fact]
    public void ASpanInsideAnAutoSilenceCutIsStillDropped()
    {
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 3.0, false, SegmentOrigin.Auto), (3.0, Duration, true, SegmentOrigin.Auto));

        Assert.Empty(FillerCutPlanner.Plan([Span(2.2, 2.5)], current, Settings, Duration));
    }

    [Fact]
    public void APreviousFillerCutIsNotABridgeTarget()
    {
        // Old filler cut at 2.0..2.3 will vanish; a new span 50 ms after it must not be stretched back to 2.3.
        var current = Partition((0.0, 2.0, true, SegmentOrigin.Auto), (2.0, 2.3, false, SegmentOrigin.Filler), (2.3, Duration, true, SegmentOrigin.Auto));

        var cuts = FillerCutPlanner.Plan([Span(2.35, 2.6, "uh")], current, Settings, Duration);

        Assert.Equal(new PlannedCut(2.35, 2.6, "filler: uh"), Assert.Single(cuts));
    }
}
