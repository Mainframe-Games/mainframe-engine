using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class SilenceDetectParserTests
{
    [Fact]
    public void ParsesStartEndPairs()
    {
        var parser = new SilenceDetectParser(durationSeconds: 12.0);

        parser.Feed("[silencedetect @ 0x600001] silence_start: 3.02");
        parser.Feed("[silencedetect @ 0x600001] silence_end: 4.98 | silence_duration: 1.96");

        Assert.Equal([new SilenceSpan(3.02, 4.98)], parser.Finish());
    }

    [Fact]
    public void IgnoresUnrelatedStderrLines()
    {
        var parser = new SilenceDetectParser(12.0);

        parser.Feed("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'sample.mp4':");
        parser.Feed("  Duration: 00:00:12.02, start: 0.000000, bitrate: 269 kb/s");
        parser.Feed("size=N/A time=00:00:12.00 bitrate=N/A speed= 1.2e+03x");

        Assert.Empty(parser.Finish());
    }

    [Fact]
    public void ATrailingSilenceWithoutAnEndIsClosedAtTheDuration()
    {
        var parser = new SilenceDetectParser(12.0);

        parser.Feed("[silencedetect @ 0x1] silence_start: 10.5");

        Assert.Equal([new SilenceSpan(10.5, 12.0)], parser.Finish());
    }

    [Fact]
    public void HandlesNegativeAndScientificNotationValues()
    {
        // ffmpeg can report a silence_start slightly before 0 for leading silence.
        var parser = new SilenceDetectParser(12.0);

        parser.Feed("[silencedetect @ 0x1] silence_start: -0.0213333");
        parser.Feed("[silencedetect @ 0x1] silence_end: 1.5 | silence_duration: 1.52133");

        Assert.Equal([new SilenceSpan(-0.0213333, 1.5)], parser.Finish());
    }

    [Fact]
    public void MultipleSpansComeBackInOrder()
    {
        var parser = new SilenceDetectParser(12.0);

        parser.Feed("[silencedetect @ 0x1] silence_start: 1");
        parser.Feed("[silencedetect @ 0x1] silence_end: 2 | silence_duration: 1");
        parser.Feed("[silencedetect @ 0x1] silence_start: 5");
        parser.Feed("[silencedetect @ 0x1] silence_end: 7 | silence_duration: 2");

        Assert.Equal([new SilenceSpan(1, 2), new SilenceSpan(5, 7)], parser.Finish());
    }

    [Fact]
    public void AnEndWithoutAStartIsIgnored()
    {
        var parser = new SilenceDetectParser(12.0);

        parser.Feed("[silencedetect @ 0x1] silence_end: 2 | silence_duration: 1");

        Assert.Empty(parser.Finish());
    }

    [Fact]
    public void ParsingIsCultureInvariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var parser = new SilenceDetectParser(12.0);

            parser.Feed("[silencedetect @ 0x1] silence_start: 3.5");
            parser.Feed("[silencedetect @ 0x1] silence_end: 4.25 | silence_duration: 0.75");

            Assert.Equal([new SilenceSpan(3.5, 4.25)], parser.Finish());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
