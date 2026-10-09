namespace Forest.Tests;

/// <summary>The frame-rate readout: its averaging window and when <see cref="ForestDev"/> shows it.</summary>
public sealed class FpsHudTests
{
    [Fact]
    public void PublishesTheAverageAndTheSlowestFrameEveryHalfSecond()
    {
        var hud = new FpsHud();
        var published = false;
        for (var i = 0; i < 29; i++)
            published |= hud.Sample(1f / 60f);
        Assert.False(published);

        // 29 frames at 60 fps + one 40 ms frame: the window closes past 0.5 s.
        Assert.True(hud.Sample(0.04f));
        Assert.Equal(30f / (29f / 60f + 0.04f), hud.Fps, 3);
        Assert.Equal(1000f * (29f / 60f + 0.04f) / 30f, hud.FrameMs, 3);
        Assert.Equal(40f, hud.MaxFrameMs, 3);

        // The next window starts empty.
        Assert.False(hud.Sample(1f / 60f));
    }

    [Fact]
    public void IgnoresEmptyFrames() => Assert.False(new FpsHud().Sample(0f));

    // The UI sees input before the game: a full-window HUD that took clicks would stop a click from recapturing the mouse.
    [Fact]
    public void TheReadoutLetsClicksThroughToTheGame()
    {
        var rml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "UI", "fps.rml"));
        var body = rml[rml.IndexOf("body {", StringComparison.Ordinal)..];
        Assert.Contains("pointer-events: none", body[..body.IndexOf('}')], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--no-fps" }, false)]
    [InlineData(new[] { "--shot", "1" }, false)]
    [InlineData(new[] { "--benchmark" }, false)]
    [InlineData(new[] { "--autowalk" }, false)]
    [InlineData(new[] { "--no-audio" }, true)]
    public void ShowsInPlayButNotInCapturesOrMeasurements(string[] args, bool shown) =>
        Assert.Equal(shown, ForestDev.ShowsFps(args));
}
