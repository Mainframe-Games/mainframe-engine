using MainframeEngine.Video.Models;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Video.Tests;

public sealed class TranscriptExporterTests
{
    // "um" (1.0..1.2) is cut; a 3.3 s pause precedes "Bye".
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 1.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(1.0, 1.2, enabled: false, SegmentOrigin.Filler, "filler: um"),
        Segment.Create(1.2, 6.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("Hello", 0.0, 0.4, 0.9),
        new("there", 0.5, 0.9, 0.9),
        new("um", 1.0, 1.2, 0.9),
        new("friend", 1.3, 1.7, 0.9),
        new("Bye", 5.0, 5.3, 0.9),
    ];

    [Fact]
    public void PlainTextOmitsCutWordsAndBreaksParagraphsOnLongPauses()
    {
        Assert.Equal("Hello there friend\n\nBye\n", TranscriptExporter.ToPlainText(Words, Segments));
    }

    [Fact]
    public void PlainTextOfAnEmptyTranscriptIsEmpty()
    {
        Assert.Empty(TranscriptExporter.ToPlainText([], Segments));
    }

    [Fact]
    public void SrtRemapsTimesToTheOutputAndSplitsCuesOnPauses()
    {
        var expected =
            "1\n00:00:00,000 --> 00:00:01,500\nHello there friend\n\n" +
            "2\n00:00:04,800 --> 00:00:05,100\nBye\n";

        Assert.Equal(expected, TranscriptExporter.ToSrt(Words, Segments));
    }

    [Fact]
    public void SrtWrapsLongCuesIntoTwoLinesOfAtMost42Characters()
    {
        var words = Enumerable.Range(1, 12).Select(i => new Word($"word{i}", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 10.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        var lines = srt.Split('\n');
        Assert.Equal("1", lines[0]);
        Assert.True(lines[2].Length <= 42, $"{lines[2].Length} <= 42");
        Assert.True(lines[3].Length <= 42, $"{lines[3].Length} <= 42");
        Assert.Equal(string.Join(' ', words.Select(w => w.Text)), lines[2] + " " + lines[3]); ;
        Assert.Empty(lines[4]);
    }

    [Fact]
    public void SrtStartsANewCueWhenTheTextWouldExceed84Characters()
    {
        var words = Enumerable.Range(1, 30).Select(i => new Word("abcde", i * 0.3, i * 0.3 + 0.2, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        Assert.Contains("\n\n2\n", srt, StringComparison.Ordinal);
        foreach (var cueText in srt.Split("\n\n").Select(block => string.Join(' ', block.Split('\n').Skip(2))))
        {
            Assert.True(cueText.Length <= 84, $"{cueText.Length} <= 84");
        }
    }

    [Fact]
    public void SrtStartsANewCueWhenACueWouldExceedFiveSeconds()
    {
        var words = Enumerable.Range(0, 25).Select(i => new Word("go", i * 0.5, i * 0.5 + 0.3, 0.9)).ToList();
        var segments = new[] { Segment.Create(0.0, 20.0, enabled: true, SegmentOrigin.Auto) };

        var srt = TranscriptExporter.ToSrt(words, segments);

        Assert.Contains("\n\n2\n", srt, StringComparison.Ordinal);
        Assert.Contains("\n\n3\n", srt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(1.5, "00:00:01,500")]
    [InlineData(61.0, "00:01:01,000")]
    [InlineData(3661.25, "01:01:01,250")]
    [InlineData(1.1000000000000001, "00:00:01,100")]
    public void SrtTimeFormat(double seconds, string expected)
    {
        Assert.Equal(expected, TranscriptExporter.FormatSrtTime(seconds));
    }
}
