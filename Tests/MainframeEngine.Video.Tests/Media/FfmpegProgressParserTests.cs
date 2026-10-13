using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void ReportsFractionFromOutTimeUs()
    {
        var parser = new FfmpegProgressParser(totalSeconds: 10.0);

        Assert.Null(parser.Feed("frame=120"));
        Assert.Equal(0.25, Assert.NotNull(parser.Feed("out_time_us=2500000")), 1e-9);
    }

    [Fact]
    public void FallsBackToOutTimeMsWhichIsActuallyMicroseconds()
    {
        // ffmpeg's out_time_ms key has always carried microseconds.
        var parser = new FfmpegProgressParser(10.0);

        Assert.Equal(0.5, Assert.NotNull(parser.Feed("out_time_ms=5000000")), 1e-9);
    }

    [Fact]
    public void ParsesTheHmsOutTimeForm()
    {
        var parser = new FfmpegProgressParser(120.0);

        Assert.Equal(0.5, Assert.NotNull(parser.Feed("out_time=00:01:00.000000")), 1e-9);
    }

    [Fact]
    public void ClampsToOne()
    {
        var parser = new FfmpegProgressParser(10.0);

        Assert.Equal(1.0, parser.Feed("out_time_us=99000000"));
    }

    [Fact]
    public void ProgressEndReportsCompletion()
    {
        var parser = new FfmpegProgressParser(10.0);

        Assert.Equal(1.0, parser.Feed("progress=end"));
        Assert.True(parser.IsComplete);
    }

    [Fact]
    public void IgnoresGarbageValues()
    {
        var parser = new FfmpegProgressParser(10.0);

        Assert.Null(parser.Feed("out_time_us=N/A"));
        Assert.Null(parser.Feed("out_time_us="));
    }

    [Fact]
    public void UnknownTotalYieldsNoFraction()
    {
        var parser = new FfmpegProgressParser(0);

        Assert.Null(parser.Feed("out_time_us=2500000"));
    }
}
