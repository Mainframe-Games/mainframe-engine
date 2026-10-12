namespace MainframeEngine.Tests.Recording;

/// <summary>--record, --record-scale and --ffmpeg parsing and how they shape the engine options.</summary>
public sealed class RecordingOptionsTests
{
    private static EngineOptions Defaults() => new ProjectSettings { Name = "T" }.ToEngineOptions();

    [Fact]
    public void RecordTakesAPathAndEnablesFrameCapture()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4"]);
        Assert.Equal("out.mp4", options.RecordPath);
        Assert.False(options.RecordOffline);
        Assert.True(options.Apply(Defaults()).EnableFrameCapture);
    }

    [Fact]
    public void RecordWithFixedFpsIsOfflineAndUsesTheManualNullDevice()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4", "--fixed-fps", "60"]);
        Assert.True(options.RecordOffline);
        Assert.Equal(AudioDeviceMode.NullManual, options.Apply(Defaults()).Audio.Device);
    }

    [Fact]
    public void LiveRecordingKeepsTheRealAudioDevice()
    {
        var options = GameHostOptions.Parse(["--record", "out.mp4"]);
        Assert.Equal(AudioDeviceMode.Auto, options.Apply(Defaults()).Audio.Device);
    }

    [Fact]
    public void RecordScaleSetsTheContentScale()
    {
        var options = GameHostOptions.Parse(["--record", "o.mp4", "--record-scale", "2"]);
        Assert.Equal(2f, options.Apply(Defaults()).ContentScale);
    }

    [Fact]
    public void FfmpegPathIsKept()
    {
        Assert.Equal("/opt/ff", GameHostOptions.Parse(["--ffmpeg", "/opt/ff"]).FfmpegPath);
    }

    [Theory]
    [InlineData("--record")]
    [InlineData("--record-scale")]
    [InlineData("--ffmpeg")]
    public void FlagsWithoutAValueThrow(string flag)
    {
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse([flag]));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void RecordScaleOutOfRangeOrNotANumberThrows(string value)
    {
        var e = Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--record-scale", value]));
        Assert.Contains("--record-scale", e.Message);
    }

    [Theory]
    [InlineData("--headless", "--record", "o.mp4")]
    [InlineData("--record", "o.mp4", "--headless")]
    [InlineData("--record", "o.mp4", "--headless", "++", "--game-flag")]
    public void RecordingHeadlessIsRejected(params string[] args)
    {
        // HeadlessHost has no renderer and no recorder: --record would silently record nothing.
        var e = Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(args));
        Assert.Contains("--headless", e.Message);
    }
}
