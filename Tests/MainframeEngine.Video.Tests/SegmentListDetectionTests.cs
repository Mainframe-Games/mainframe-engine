using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentListDetectionTests
{
    private const double Duration = 10.0;

    private static PlannedCut Cut(double start, double end) => new(start, end, $"silence {end - start:0.00}s");

    [Fact]
    public void OnAFreshListCutsBecomeDisabledAutoSegmentsWithKeptGapsBetween()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(6.0, 7.5)]);

        Assert.Equal([
            (0.0, 2.0, true),
            (2.0, 3.0, false),
            (3.0, 6.0, true),
            (6.0, 7.5, false),
            (7.5, 10.0, true)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled)));
        Assert.All(list.Segments, s => Assert.True(s.Origin == SegmentOrigin.Auto));
        Assert.Equal("silence 1.00s", list.Segments[1].Reason);
        Assert.Null(list.Segments[0].Reason);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ReRunningReplacesPreviousAutoCuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        list.ReplaceAutoSegments([Cut(5.0, 6.0)]);

        Assert.Equal([
            (0.0, 5.0, true),
            (5.0, 6.0, false),
            (6.0, 10.0, true)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled)));
    }

    [Fact]
    public void ManualSegmentsArePreservedExactly()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);
        list.Toggle(1); // the user decides to keep the 2..3 "silence": now manual + enabled
        var manual = list.Segments[1];

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(6.0, 7.0)]);

        Assert.Contains(list.Segments, s => s.Id == manual.Id && s.Start == 2.0 && s.End == 3.0 && s.Enabled && s.Origin == SegmentOrigin.Manual);
        Assert.Contains(list.Segments, s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ANewCutOverlappingAManualSegmentIsClippedAroundIt()
    {
        var list = new SegmentList(Duration,
        [
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 5.0, enabled: true, SegmentOrigin.Manual), // the user insists on keeping this
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        ]);

        list.ReplaceAutoSegments([Cut(3.0, 6.0)]);

        Assert.Equal([
            (0.0, 3.0, true, SegmentOrigin.Auto),
            (3.0, 4.0, false, SegmentOrigin.Auto),
            (4.0, 5.0, true, SegmentOrigin.Manual),
            (5.0, 6.0, false, SegmentOrigin.Auto),
            (6.0, 10.0, true, SegmentOrigin.Auto)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ClaudeSegmentsArePreservedLikeManualOnes()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 5.0, enabled: false, SegmentOrigin.Claude, "false start"),
            Segment.Create(5.0, 10.0, enabled: true, SegmentOrigin.Auto),
        };
        var list = new SegmentList(Duration, segments);

        list.ReplaceAutoSegments([]);

        Assert.Contains(list.Segments, s => s.Id == segments[1].Id && s.Origin == SegmentOrigin.Claude);
        Assert.Equal(3, list.Segments.Count);
    }

    [Fact]
    public void FreeRegionsWithNoCutsCollapseToOneKeptSegment()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(4.0, 5.0)]);

        list.ReplaceAutoSegments([]);

        var only = Assert.Single(list.Segments);
        Assert.Equal(0.0, only.Start);
        Assert.Equal(Duration, only.End);
        Assert.True(only.Enabled);
    }

    [Fact]
    public void CutsOutsideOrTouchingEdgesAreHandled()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(0.0, 1.0), Cut(9.0, 10.0)]);

        Assert.Equal([
            (0.0, 1.0, false),
            (1.0, 9.0, true),
            (9.0, 10.0, false)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void AdjacentCutsAreMergedIntoOneDisabledSegment()
    {
        var list = new SegmentList(Duration);

        list.ReplaceAutoSegments([Cut(2.0, 3.0), Cut(3.0, 4.0)]);

        Assert.Equal(3, list.Segments.Count);
        Assert.Equal(2.0, list.Segments[1].Start);
        Assert.Equal(4.0, list.Segments[1].End);
        Assert.False(list.Segments[1].Enabled);
    }

    [Fact]
    public void ReplaceAutoSegmentsRaisesChangedOnce()
    {
        var list = new SegmentList(Duration);
        var count = 0;
        list.Changed += (_, _) => count++;

        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void SplittingThenDisablingTheTailLeavesTheHeadOpenToDetection()
    {
        // The scenario that motivated the origin rules: the user chops off the end by hand, then
        // asks for silence detection over the rest.
        var list = new SegmentList(Duration);
        list.Split(7.0);
        list.Toggle(1);

        list.ReplaceAutoSegments([Cut(3.0, 4.0)]);

        Assert.Equal([
            (0.0, 3.0, true, SegmentOrigin.Auto),
            (3.0, 4.0, false, SegmentOrigin.Auto),
            (4.0, 7.0, true, SegmentOrigin.Auto),
            (7.0, 10.0, false, SegmentOrigin.Manual)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
    }

    [Fact]
    public void ExistingAutoSegmentIdsAreNotReused()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);
        var oldIds = list.Segments.Select(s => s.Id).ToHashSet();

        list.ReplaceAutoSegments([Cut(2.0, 3.0)]);

        Assert.Empty(list.Segments.Select(s => s.Id).Intersect(oldIds));
    }
}
