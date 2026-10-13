using MainframeEngine.Video.Capture;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class GameplayCutPlannerTests
{
    private static CaptureSidecar Fixture()
        => CaptureSidecarSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mfcap.json")));

    private static CaptureSidecar Capture(
        double duration,
        CaptureSpan[]? input = null,
        CaptureSpan[]? loading = null,
        CaptureSpan[]? paused = null,
        CaptureSpan[]? keep = null,
        CaptureSpan[]? skip = null,
        CaptureMarker[]? markers = null,
        CaptureMode mode = CaptureMode.Live)
        => new(mode, duration, 60, 1920, 1080, "Game", "Scene",
            input ?? [], loading ?? [], paused ?? [], keep ?? [], skip ?? [], markers ?? []);

    private static IReadOnlyList<Segment> Untouched(double duration) => new SegmentList(duration).Segments;

    private static (double, double, string)[] Shape(IReadOnlyList<PlannedCut> cuts)
        => cuts.Select(c => (Math.Round(c.Start, 6), Math.Round(c.End, 6), c.Reason)).ToArray();

    [Fact]
    public void TheSpecDiagramMinutePlansTheThreeDrawnCuts()
    {
        var cuts = GameplayCutPlanner.Plan(Fixture(), Untouched(60), GameplaySettings.Default, 60);

        Assert.Equal([
            (14.5, 21.5, "loading + idle 7.0s"),
            (35.5, 39.0, "idle 3.5s"),
            (56.0, 60.0, "paused 4.0s")],
            Shape(cuts));
    }

    [Fact]
    public void IdleShorterThanMinIdleIsLeftAlone()
    {
        var capture = Capture(30, input: [new(0, 10), new(12.9, 30)]); // 2.9 s gap

        Assert.Empty(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30));
    }

    [Fact]
    public void IdleOfExactlyMinIdleIsCutAfterPadding()
    {
        var capture = Capture(30, input: [new(0, 10), new(13, 30)]); // 3.0 s gap

        Assert.Equal([(10.5, 12.5, "idle 2.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30)));
    }

    [Fact]
    public void KeepBeatsSkip()
    {
        var capture = Capture(30, input: [new(0, 30)], skip: [new(10, 20, "menu")], keep: [new(12, 14, "cutscene")]);

        Assert.Equal([
            (10.0, 12.0, "skip: menu 2.0s"),
            (14.0, 20.0, "skip: menu 6.0s")],
            Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30)));
    }

    [Fact]
    public void KeepAtTheStartIsNotAbsorbedByTheMinKeepSliverRule()
    {
        // The 0.8 s kept logo is shorter than MinKeep, so merging alone would extend the cut back to 0
        // (and the 0.5 s tail out to the end of the file). Keep is subtracted again afterwards.
        var capture = Capture(10, mode: CaptureMode.Offline, loading: [new(0.8, 9.5)], keep: [new(0, 0.8, "logo")]);

        Assert.Equal([(0.8, 10.0, "loading 9.2s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(10), GameplaySettings.Default, 10)));
    }

    [Fact]
    public void AShortKeepBetweenTwoCutsIsNotBridged()
    {
        var capture = Capture(20, mode: CaptureMode.Offline,
            loading: [new(2, 5), new(5.5, 9)], keep: [new(5, 5.5, "beat")]);

        Assert.Equal([
            (2.0, 5.0, "loading 3.0s"),
            (5.5, 9.0, "loading 3.5s")],
            Shape(GameplayCutPlanner.Plan(capture, Untouched(20), GameplaySettings.Default, 20)));
    }

    [Fact]
    public void MarkersDoNotOverrideSkip()
    {
        var capture = Capture(30, input: [new(0, 30)], loading: [new(10, 20)], markers: [new(15, "x")]);

        Assert.Equal([(10.0, 20.0, "loading 10.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30)));
    }

    [Fact]
    public void SkipWithoutALabelReadsSkip()
    {
        var capture = Capture(30, input: [new(0, 30)], skip: [new(10, 12)]);

        Assert.Equal("skip 2.0s", GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30).Single().Reason);
    }

    [Fact]
    public void OfflineRecordingsAreNeverCutForIdleness()
    {
        var capture = Capture(60, mode: CaptureMode.Offline, loading: [new(0, 2)]);

        Assert.Equal([(0.0, 2.0, "loading 2.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(60), GameplaySettings.Default, 60)));
    }

    [Fact]
    public void LiveRecordingWithNoInputIsCutEntirely()
    {
        // Idle [0,20] pads to [0.5,19.5]; the 0.5 s kept slivers at either end are under MinKeep,
        // so the shared end-of-file rule absorbs them, exactly as silence detection does.
        var capture = Capture(20);

        Assert.Equal([(0.0, 20.0, "idle 20.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(20), GameplaySettings.Default, 20)));
    }

    [Fact]
    public void ManualSegmentsAreClippedOutOfCuts()
    {
        var list = new SegmentList(40);
        list.SetRange(15, 18, enabled: true); // the user restored part of an earlier idle cut
        var capture = Capture(40, input: [new(0, 10), new(30, 40)]);

        Assert.Equal([
            (10.5, 15.0, "idle 4.5s"),
            (18.0, 29.5, "idle 11.5s")],
            Shape(GameplayCutPlanner.Plan(capture, list.Segments, GameplaySettings.Default, 40)));
    }

    [Fact]
    public void SpansPastTheEndAreClamped()
    {
        var capture = Capture(10, input: [new(0, 4)], paused: [new(8, 12)], markers: [new(11, "late")]);

        var cuts = GameplayCutPlanner.Plan(capture, Untouched(10), GameplaySettings.Default, 10);

        Assert.All(cuts, c => Assert.True(c.Start >= 0 && c.End <= 10));
        Assert.Equal([(4.5, 10.0, "idle + paused 5.5s")], Shape(cuts));
    }

    [Fact]
    public void UnsortedOverlappingInputSpansAreNormalised()
    {
        var capture = Capture(30, input: [new(20, 30), new(0, 6), new(4, 10)]);

        Assert.Equal([(10.5, 19.5, "idle 9.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(30), GameplaySettings.Default, 30)));
    }

    [Fact]
    public void KeptGapsShorterThanMinKeepAreBridged()
    {
        var capture = Capture(30, input: [new(0, 5), new(10, 10.2), new(15, 30)]);
        var settings = GameplaySettings.Default with { PaddingMs = 0 };

        Assert.Equal([(5.0, 15.0, "idle 10.0s")], Shape(GameplayCutPlanner.Plan(capture, Untouched(30), settings, 30)));
    }
}
