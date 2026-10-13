using MainframeEngine.Video.Models;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FilterGraphBuilderTests
{
    private static Segment Kept(double start, double end) => Segment.Create(start, end, enabled: true, SegmentOrigin.Auto);

    private static Segment Cut(double start, double end) => Segment.Create(start, end, enabled: false, SegmentOrigin.Auto);

    [Fact]
    public void SingleSegmentTrimsFadesAndConcatsOnce()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 3.21)]);

        Assert.Equal(
            "[0:v]trim=start=0:end=3.21,setpts=PTS-STARTPTS[v0];\n" +
            "[0:a]atrim=start=0:end=3.21,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=3.202:d=0.008[a0];\n" +
            "[v0][a0]concat=n=1:v=1:a=1[outv][outa]",
            graph);
    }

    [Fact]
    public void DisabledSegmentsAreSkippedAndIndicesStayDense()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 3.21), Cut(3.21, 4.86), Kept(4.86, 12.04)]);

        Assert.Equal(
            "[0:v]trim=start=0:end=3.21,setpts=PTS-STARTPTS[v0];\n" +
            "[0:a]atrim=start=0:end=3.21,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=3.202:d=0.008[a0];\n" +
            "[0:v]trim=start=4.86:end=12.04,setpts=PTS-STARTPTS[v1];\n" +
            "[0:a]atrim=start=4.86:end=12.04,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=7.172:d=0.008[a1];\n" +
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]",
            graph);
    }

    [Fact]
    public void EveryKeptSegmentGetsAn8msFadeInAndOut()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 1.0), Cut(1.0, 2.0), Kept(2.0, 3.0), Cut(3.0, 4.0), Kept(4.0, 5.0)]);

        var audioLines = graph.Split('\n').Where(l => l.StartsWith("[0:a]", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, audioLines.Count);
        Assert.All(audioLines, l =>
        {
            Assert.Contains("afade=t=in:st=0:d=0.008", l, StringComparison.Ordinal);
            Assert.Contains("afade=t=out:st=0.992:d=0.008", l, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ASegmentShorterThanTwoFadesUsesHalfItsLengthPerFade()
    {
        var graph = FilterGraphBuilder.Build([Kept(0.0, 0.01)]);

        Assert.Contains("afade=t=in:st=0:d=0.005,afade=t=out:st=0.005:d=0.005", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersAreInvariantCultureWithNoScientificNotation()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var graph = FilterGraphBuilder.Build([Kept(0.0000001, 1234567.5)]);

            Assert.Contains("trim=start=0:end=1234567.5,", graph, StringComparison.Ordinal);
            Assert.DoesNotContain(",5", graph, StringComparison.Ordinal);
            Assert.DoesNotContain("E-", graph, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TimesKeepMicrosecondPrecision()
    {
        var graph = FilterGraphBuilder.Build([Kept(1.123456789, 2.5)]);

        Assert.Contains("trim=start=1.123457:end=2.5,", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void NoKeptSegmentsIsAnError()
    {
        var act = () => FilterGraphBuilder.Build([Cut(0.0, 1.0)]);

        var e = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("nothing", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeptDurationSumsEnabledSegments()
    {
        Assert.Equal(2.5, FilterGraphBuilder.KeptDuration([Kept(0.0, 1.0), Cut(1.0, 2.0), Kept(2.0, 3.5)]), 1e-9);
    }
}
