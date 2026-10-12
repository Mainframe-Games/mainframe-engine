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
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = true }));
        Assert.True(a.IsHeld);
        Assert.True(a.Observe(new InputEventKey { Key = Key.W, Pressed = false }));
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void SmallMouseMotionAndTinyStickDriftDoNotCount()
    {
        var a = new InputActivity();
        Assert.False(a.Observe(new InputEventMouseMotion { Relative = new Vector2(1f, 1f) }));
        Assert.True(a.Observe(new InputEventMouseMotion { Relative = new Vector2(3f, 0f) }));
        Assert.False(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.1f, 0.1f) }));
        Assert.True(a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0.5f, 0f) }));
        Assert.True(a.IsHeld); // a deflected stick is held until it returns
        a.Observe(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = Vector2.Zero });
        Assert.False(a.IsHeld);
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
}
