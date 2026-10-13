using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SilenceCutPlannerTests
{
    private const double Duration = 20.0;

    private static DetectionSettings Settings(int paddingMs = 60, int minKeepMs = 120)
        => DetectionSettings.Default with { PaddingMs = paddingMs, MinKeepMs = minKeepMs };

    [Fact]
    public void ASilenceBecomesACutShrunkByThePaddingOnEachSide()
    {
        var spans = new[] { new SilenceSpan(3.00, 5.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        Assert.Single(cuts);
        Assert.Equal(3.06, cuts[0].Start, 1e-9);
        Assert.Equal(4.94, cuts[0].End, 1e-9);
    }

    [Fact]
    public void TheReasonStatesTheRemovedLength()
    {
        var spans = new[] { new SilenceSpan(3.00, 5.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        Assert.Equal("silence 1.88s", cuts[0].Reason);
    }

    [Fact]
    public void ASilenceShorterThanTwiceThePaddingIsDropped()
    {
        var spans = new[] { new SilenceSpan(3.00, 3.10) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60), Duration);

        Assert.Empty(cuts);
    }

    [Fact]
    public void AKeptFragmentShorterThanMinKeepBetweenTwoCutsIsAbsorbed()
    {
        // cuts after padding: [1.06, 1.94] and [2.00, 2.94] -> kept gap 0.06 s < 0.12 s
        var spans = new[] { new SilenceSpan(1.00, 2.00), new SilenceSpan(1.94, 3.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        Assert.Single(cuts);
        Assert.Equal(1.06, cuts[0].Start, 1e-9);
        Assert.Equal(2.94, cuts[0].End, 1e-9);
    }

    [Fact]
    public void AKeptFragmentLongerThanMinKeepSurvives()
    {
        var spans = new[] { new SilenceSpan(1.00, 2.00), new SilenceSpan(2.30, 3.00) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        Assert.Equal(2, cuts.Count);
    }

    [Fact]
    public void AShortKeptFragmentAtTheStartOfTheFileIsAbsorbed()
    {
        var spans = new[] { new SilenceSpan(0.02, 2.00) }; // cut starts at 0.08

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        Assert.Single(cuts);
        Assert.Equal(0.0, cuts[0].Start);
    }

    [Fact]
    public void AShortKeptFragmentAtTheEndOfTheFileIsAbsorbed()
    {
        var spans = new[] { new SilenceSpan(18.00, 19.95) }; // cut ends at 19.89, 0.11 s before the end

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 60, minKeepMs: 120), Duration);

        Assert.Single(cuts);
        Assert.Equal(Duration, cuts[0].End);
    }

    [Fact]
    public void SpansAreClampedToTheDurationAndSorted()
    {
        var spans = new[] { new SilenceSpan(15.0, 25.0), new SilenceSpan(-1.0, 2.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0), Duration);

        Assert.Equal([(0.0, 2.0), (15.0, Duration)], cuts.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void OverlappingSpansAreMerged()
    {
        var spans = new[] { new SilenceSpan(1.0, 3.0), new SilenceSpan(2.0, 4.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0), Duration);

        Assert.Single(cuts);
        Assert.Equal(1.0, cuts[0].Start);
        Assert.Equal(4.0, cuts[0].End);
    }

    [Fact]
    public void NoSpansMeansNoCuts()
    {
        Assert.Empty(SilenceCutPlanner.Plan([], Settings(), Duration));
    }

    [Fact]
    public void ZeroPaddingAndZeroMinKeepAreAllowed()
    {
        var spans = new[] { new SilenceSpan(1.0, 2.0) };

        var cuts = SilenceCutPlanner.Plan(spans, Settings(paddingMs: 0, minKeepMs: 0), Duration);

        var only = Assert.Single(cuts);
        Assert.Equal(1.0, only.Start);
        Assert.Equal(2.0, only.End);
    }
}
