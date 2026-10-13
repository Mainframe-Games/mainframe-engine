using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentListFillerTests
{
    private const double Duration = 10.0;

    private static PlannedCut Filler(double start, double end, string word = "um") => new(start, end, $"filler: {word}");

    private static PlannedCut Silence(double start, double end) => new(start, end, $"silence {end - start:0.00}s");

    [Fact]
    public void SetRangeWithOriginStampsOriginAndReason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false, SegmentOrigin.Filler, "filler: um");

        Assert.Equivalent(new { Start = 2.0, End = 2.3, Enabled = false, Origin = SegmentOrigin.Filler, Reason = "filler: um" }, list.Segments[index]);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ThreeArgumentSetRangeIsStillManualWithNoReason()
    {
        var list = new SegmentList(Duration);

        var index = list.SetRange(2.0, 2.3, enabled: false);

        Assert.Equal(SegmentOrigin.Manual, list.Segments[index].Origin);
        Assert.Null(list.Segments[index].Reason);
    }

    [Fact]
    public void FillerCutsBecomeDisabledFillerSegmentsInsideKeptFootage()
    {
        var list = new SegmentList(Duration);

        var applied = list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4, "uh")]);

        Assert.Equal(2, applied);
        Assert.Equal([
            (0.0, 2.0, true, SegmentOrigin.Auto),
            (2.0, 2.3, false, SegmentOrigin.Filler),
            (2.3, 5.0, true, SegmentOrigin.Auto),
            (5.0, 5.4, false, SegmentOrigin.Filler),
            (5.4, 10.0, true, SegmentOrigin.Auto)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        Assert.Equal("filler: uh", list.Segments[3].Reason);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ReRunningReplacesEarlierFillerCuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(6.0, 6.2)]);

        Assert.Equal([
            (0.0, 6.0, true),
            (6.0, 6.2, false),
            (6.2, 10.0, true)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled)));
        Assert.DoesNotContain(list.Segments, s => s.Origin == SegmentOrigin.Filler && s.Start == 2.0);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void SilenceReDetectionPreservesFillerCuts()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);

        Assert.Contains(list.Segments, s => s.Start == 2.0 && s.End == 2.3 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        Assert.Contains(list.Segments, s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void FillerReDetectionPreservesSilenceCuts()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);

        list.ApplyFillerCuts([Filler(3.0, 3.2)]);

        Assert.Contains(list.Segments, s => s.Start == 6.0 && s.End == 7.0 && !s.Enabled && s.Origin == SegmentOrigin.Auto);
        Assert.Contains(list.Segments, s => s.Start == 3.0 && s.End == 3.2 && !s.Enabled && s.Origin == SegmentOrigin.Filler);
        Assert.DoesNotContain(list.Segments, s => s.Start == 2.0 && s.End == 2.3);
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void RemovingAFillerCutNextToASilenceCutGivesTheFootageBackToTheKeptSide()
    {
        var list = new SegmentList(Duration);
        list.ReplaceAutoSegments([Silence(6.0, 7.0)]);
        list.ApplyFillerCuts([Filler(5.8, 6.0)]);

        list.ApplyFillerCuts([]);

        Assert.Equal([
            (0.0, 6.0, true),
            (6.0, 7.0, false),
            (7.0, 10.0, true)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void ApplyFillerCutsRaisesChangedOnce()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([Filler(2.0, 2.3), Filler(5.0, 5.4)]);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ApplyFillerCutsWithNothingToDoDoesNotRaiseChanged()
    {
        var list = new SegmentList(Duration);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.ApplyFillerCuts([]);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ReRunningDoesNotSpreadManualOriginOverKeptAutoFootage()
    {
        var list = new SegmentList(Duration);
        list.ApplyFillerCuts([Filler(2.0, 2.3)]);
        list.SetRange(2.3, 2.5, enabled: true); // a manual kept sliver right after the filler cut

        list.ApplyFillerCuts([]);

        Assert.Equal([
            (0.0, 2.3, true, SegmentOrigin.Auto),
            (2.3, 2.5, true, SegmentOrigin.Manual),
            (2.5, 10.0, true, SegmentOrigin.Auto)],
            list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        PartitionAssert.Valid(list.Segments, Duration);
    }

    [Fact]
    public void PlanningAgainstTheCurrentPartitionAndApplyingTwiceKeepsTheSameCuts()
    {
        var list = new SegmentList(Duration);
        var settings = DetectionSettings.Default;
        FillerSpan[] spans = [new(2.0, 2.3, "um"), new(5.0, 5.4, "uh")];

        list.ApplyFillerCuts(FillerCutPlanner.Plan(spans, list.Segments, settings, Duration));
        var first = list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)).ToList();

        list.ApplyFillerCuts(FillerCutPlanner.Plan(spans, list.Segments, settings, Duration));

        Assert.Equal(first, list.Segments.Select(s => (s.Start, s.End, s.Enabled, s.Origin)));
        Assert.Equal(2, list.Segments.Where(s => !s.Enabled).Count());
        PartitionAssert.Valid(list.Segments, Duration);
    }
}
