using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class TranscriptMarkupTests
{
    [Fact]
    public void WordsBecomeSpansWithTheirCutState()
    {
        Word[] words = [new("so", 1.0, 1.3, 0.9), new("um", 1.4, 1.8, 0.8)];
        Assert.Equal(
            "<div class=\"para\"><span id=\"w-0\" class=\"w\" data-i=\"0\">so</span> <span id=\"w-1\" class=\"w cut\" data-i=\"1\">um</span> </div>",
            TranscriptMarkup.Build(words, [false, true]));
    }

    [Fact]
    public void ALongPauseStartsANewParagraph()
    {
        Word[] words = [new("a", 0, 0.5, 1), new("b", 2.6, 3.0, 1)];
        Assert.Equal(2, TranscriptMarkup.Build(words, [false, false]).Split("<div class=\"para\">").Length - 1);
    }

    [Fact]
    public void AShortPauseStaysInTheParagraph()
    {
        Word[] words = [new("a", 0, 0.5, 1), new("b", 2.4, 3.0, 1)];
        Assert.DoesNotContain("</div><div", TranscriptMarkup.Build(words, [false, false]), StringComparison.Ordinal);
    }

    [Fact]
    public void NoWordsBuildNothing() => Assert.Equal("", TranscriptMarkup.Build([], []));

    [Fact]
    public void AMissingCutFlagIsRejected() =>
        Assert.Throws<ArgumentException>(() => TranscriptMarkup.Build([new Word("a", 0, 1, 1)], []));

    [Fact]
    public void WordTextIsEscapedInTheSpan()
    {
        var markup = TranscriptMarkup.Build([new Word("<b>&", 0, 1, 1)], [false]);
        Assert.Contains(">&lt;b&gt;&amp;</span>", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TextIsEscaped() => Assert.Equal("&lt;b&gt; &amp; &quot;", TranscriptMarkup.Escape("<b> & \""));
}
