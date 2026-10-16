namespace MainframeEngine.Tests.Project;

/// <summary>The splash clock (BRUTAL B1) on a fake 60 fps clock: fades, holds, the 1.5 s skip floor, waiting on the load.</summary>
public sealed class SplashTimelineTests
{
    private const float Frame = 1f / 60f;

    private static void Run(SplashTimeline timeline, float seconds, bool loaded = true)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Frame)
            timeline.Advance(Frame, loaded);
    }

    [Fact]
    public void EachCardFadesInHoldsAndFadesOutThenTheSequenceEnds()
    {
        var t = new SplashTimeline(2, 2f, skippable: true);
        Assert.Equal((0, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Run(t, 0.15f);
        Assert.Equal(0.5f, t.Opacity, 0.05f);
        Run(t, 0.25f); // 0.4 s
        Assert.Equal((0, SplashPhase.Hold, 1f), (t.CardIndex, t.Phase, t.Opacity));
        Run(t, 2.05f); // 2.45 s: 0.15 s into card 0's fade out
        Assert.Equal((0, SplashPhase.FadeOut), (t.CardIndex, t.Phase));
        Assert.Equal(0.5f, t.Opacity, 0.05f);
        Run(t, 0.3f); // 2.75 s
        Assert.Equal((1, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Run(t, 2.4f); // 5.15 s
        Assert.False(t.IsDone);
        Run(t, 0.1f); // 5.25 s: past 2 × (0.3 + 2 + 0.3)
        Assert.True(t.IsDone);
        Assert.Equal(0f, t.Opacity);
    }

    [Theory]
    [InlineData(0.2f, 1.5f)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(4f, 4f)]
    [InlineData(60f, 10f)]
    public void TheHoldIsClampedToOneAndAHalfToTenSeconds(float hold, float expected) =>
        Assert.Equal(expected, new SplashTimeline(1, hold, skippable: true).HoldSeconds);

    [Fact]
    public void NoCardCanBeSkippedBeforeOneAndAHalfSecondsOfItsHold()
    {
        var t = new SplashTimeline(2, 5f, skippable: true);
        Assert.False(t.TrySkip()); // fading in
        Run(t, 0.3f + 1.4f);
        Assert.Equal(SplashPhase.Hold, t.Phase);
        Assert.False(t.CanSkip);
        Assert.False(t.TrySkip());
        Run(t, 0.12f); // 1.52 s into the hold
        Assert.True(t.CanSkip);
        Assert.True(t.TrySkip());
        Assert.Equal((0, SplashPhase.FadeOut, 0f), (t.CardIndex, t.Phase, t.PhaseTime));
        Assert.False(t.TrySkip()); // fading out
    }

    [Fact]
    public void AnUnskippableSequenceIgnoresPresses()
    {
        var t = new SplashTimeline(1, 4f, skippable: false);
        Run(t, 2.5f);
        Assert.False(t.TrySkip());
        Assert.Equal(SplashPhase.Hold, t.Phase);
    }

    [Fact]
    public void TheLastCardWaitsAtFullOpacityForASlowLoadAndCannotBeSkippedThere()
    {
        var t = new SplashTimeline(1, 1.5f, skippable: true);
        Run(t, 10f, loaded: false);
        Assert.Equal((SplashPhase.WaitingForLoad, 1f), (t.Phase, t.Opacity));
        Assert.False(t.TrySkip());
        t.Advance(Frame, loadCompleted: true);
        Assert.Equal(SplashPhase.FadeOut, t.Phase);
        Run(t, 0.31f);
        Assert.True(t.IsDone);
    }

    [Fact]
    public void ALoadThatFinishedBeforeTheFirstCardShortensNothing()
    {
        var t = new SplashTimeline(2, 2f, skippable: true);
        Run(t, 5.1f, loaded: true);
        Assert.Equal((1, SplashPhase.FadeOut), (t.CardIndex, t.Phase));
        Run(t, 0.15f);
        Assert.True(t.IsDone);
    }

    [Fact]
    public void AFrameHitchCannotJumpOverACard()
    {
        var t = new SplashTimeline(2, 1.5f, skippable: true);
        t.Advance(30f, loadCompleted: true); // a 30 s stall counts as one 0.1 s step
        Assert.Equal((0, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Assert.Equal(SplashTimeline.MaxStepSeconds, t.PhaseTime, 1e-5f);
        for (var i = 0; i < 16; i++)
            t.Advance(5f, loadCompleted: true); // 1.7 s in all
        Assert.Equal((0, SplashPhase.Hold), (t.CardIndex, t.Phase));
        t.Advance(float.NaN, loadCompleted: true);
        t.Advance(-1f, loadCompleted: true);
        Assert.Equal((0, SplashPhase.Hold), (t.CardIndex, t.Phase));
    }

    [Fact]
    public void NoCardsIsDoneAtOnceAndAbortEndsImmediately()
    {
        Assert.True(new SplashTimeline(0, 2f, skippable: true).IsDone);
        var t = new SplashTimeline(3, 2f, skippable: true);
        Run(t, 1f);
        t.Abort(); // a failed load
        Assert.True(t.IsDone);
        Assert.Equal(0f, t.Opacity);
        t.Advance(Frame, loadCompleted: true);
        Assert.True(t.IsDone);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplashTimeline(-1, 2f, skippable: true));
    }
}
