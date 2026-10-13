using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Recording;

/// <summary>What counts as player input, and the spans the sidecar records.</summary>
public sealed class CaptureTimelineTests
{
    [Fact]
    public void KeyDownCountsAndHoldsUntilKeyUp()
    {
        var a = new InputActivity();
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = true }, 0));
        Assert.True(a.IsHeld);
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = false }, 0));
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void SmallMouseMotionAndTinyStickDriftDoNotCount()
    {
        var a = new InputActivity();
        Assert.False(a.Observe(new InputEventMouseMotion { Relative = new Vector2(1f, 1f) }, 0.0));
        Assert.True(a.Observe(new InputEventMouseMotion { Relative = new Vector2(3f, 0f) }, 1.0));
        Assert.False(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.1f, 0.1f) }, 0));
        Assert.True(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.5f, 0f) }, 0));
        Assert.True(a.IsHeld); // a deflected stick is held until it returns
        a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = Vector2.Zero }, 0);
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void SlowMouseLookCountsOnceItsMotionAddsUpToMoreThanTwoPixels()
    {
        // A slow camera pan sends many 1 px motions; each alone is below the threshold, together they are play.
        var a = new InputActivity();
        var step = new InputEventMouseMotion { Relative = new Vector2(1f, 0f) };
        Assert.False(a.Observe(step, 0.000));
        Assert.False(a.Observe(step, 0.008));
        Assert.True(a.Observe(step, 0.016));  // 3 px since the last counted motion
        Assert.False(a.Observe(step, 0.024)); // counting starts again
        Assert.False(a.Observe(step, 0.032));
        Assert.True(a.Observe(step, 0.040));
    }

    [Fact]
    public void ASinglePixelTwitchAfterAPauseDoesNotCount()
    {
        var a = new InputActivity();
        var twitch = new InputEventMouseMotion { Relative = new Vector2(0f, 1.5f) };
        Assert.False(a.Observe(twitch, 0.0));
        Assert.False(a.Observe(twitch, 0.5)); // 100 ms without motion forgot the first one: 1.5 px, not 3
        Assert.False(a.Observe(twitch, 0.61));
    }

    [Fact]
    public void LosingFocusForgetsAccumulatedMotion()
    {
        var a = new InputActivity();
        var step = new InputEventMouseMotion { Relative = new Vector2(1.5f, 0f) };
        Assert.False(a.Observe(step, 0.0));
        a.Reset();
        Assert.False(a.Observe(step, 0.01));
    }

    [Fact]
    public void AnEventHoldsTheInputSpanOpenFor250Ms()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        t.Update(1.2, held: false, loading: false, paused: false);
        t.Update(1.3, held: false, loading: false, paused: false);
        var spans = t.Finish(5.0);
        Assert.Equal([new CaptureSpan(1.0, 1.25)], spans.Input);
    }

    [Fact]
    public void HeldKeyKeepsSpanOpenWithoutEvents()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        for (var s = 1.0; s <= 9.0; s += 0.5)
            t.Update(s, held: true, loading: false, paused: false);
        t.Update(9.5, held: false, loading: false, paused: false);
        t.Update(9.8, held: false, loading: false, paused: false);
        Assert.Equal([new CaptureSpan(1.0, 9.25)], t.Finish(20).Input); // tail runs 250 ms from the last held update (9.0)
    }

    [Fact]
    public void GapsUnder100MsMerge()
    {
        var t = new CaptureTimeline();
        t.Input(1.0);
        t.Update(1.3, held: false, loading: false, paused: false); // closes at 1.25
        t.Input(1.33);                                             // 80 ms later: same span
        t.Update(2.0, held: false, loading: false, paused: false);
        Assert.Equal([new CaptureSpan(1.0, 1.58)], t.Finish(3).Input);
    }

    [Fact]
    public void LoadingAndPausedFollowTheirFlags()
    {
        var t = new CaptureTimeline();
        t.Update(0.0, held: false, loading: true, paused: false);
        t.Update(2.0, held: false, loading: false, paused: false);
        t.Update(5.0, held: false, loading: false, paused: true);
        var spans = t.Finish(6.0);
        Assert.Equal([new CaptureSpan(0.0, 2.0)], spans.Loading);
        Assert.Equal([new CaptureSpan(5.0, 6.0)], spans.Paused);
    }

    [Fact]
    public void KeepSkipAndMarkersCarryLabelsAndOpenScopesCloseAtFinish()
    {
        var t = new CaptureTimeline();
        var keep = t.OpenKeep(1.0, "cutscene");
        t.Close(keep, 3.0);
        t.OpenSkip(4.0, "menu");
        t.Mark(2.0, "boss intro");
        var spans = t.Finish(6.0);
        Assert.Equal([new CaptureSpan(1.0, 3.0, "cutscene")], spans.Keep);
        Assert.Equal([new CaptureSpan(4.0, 6.0, "menu")], spans.Skip);
        Assert.Equal([new CaptureMarker(2.0, "boss intro")], spans.Markers);
    }

    [Fact]
    public void ResetForgetsEveryHeldControl()
    {
        var a = new InputActivity();
        a.Observe(new InputEventKey { Key = Key.W, Pressed = true }, 0);
        Assert.True(a.IsHeld);
        a.Reset();
        Assert.False(a.IsHeld);

        a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.8f, 0f) }, 0);
        a.Observe(new InputEventAction { Action = "jump", Pressed = true }, 0);
        Assert.True(a.IsHeld);
        a.Reset();
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void ActionsCountAndHoldUntilReleased()
    {
        var a = new InputActivity();
        Assert.True(a.Observe(new InputEventAction { Action = "jump", Pressed = true }, 0));
        Assert.True(a.IsHeld);
        Assert.True(a.Observe(new InputEventAction { Action = "jump", Pressed = false }, 0));
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void TextAndUnknownEventsCountButHoldNothing()
    {
        var a = new InputActivity();
        Assert.True(a.Observe(new InputEventText { Character = 'x' }, 0));
        Assert.True(a.Observe(new UnknownEvent(), 0));
        Assert.False(a.IsHeld);
    }

    private sealed class UnknownEvent : InputEvent;
}
