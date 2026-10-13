using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class FillerDetectorTests
{
    private static Word W(string text, double start) => new(text, start, start + 0.2, 0.9);

    [Theory]
    [InlineData("Um,", "um")]
    [InlineData("  UH...", "uh")]
    [InlineData("hmm", "hmm")]
    [InlineData("\"er\"", "er")]
    [InlineData("uh-huh", "uh-huh")]
    public void NormalizeLowerCasesAndStripsSurroundingPunctuation(string input, string expected)
    {
        Assert.Equal(expected, FillerDetector.Normalize(input));
    }

    [Fact]
    public void DefaultWordsAreTheDocumentedList()
    {
        Assert.Equal(["um", "umm", "uh", "uhh", "er", "erm", "ah", "hmm", "hm", "mm"], FillerDetector.DefaultWords);
    }

    [Fact]
    public void FindReturnsMatchingWordsInTimelineOrder()
    {
        var transcript = new[] { W("So", 0.0), W("um,", 0.5), W("this", 1.0), W("Uh", 1.5), W("works", 2.0) };

        var found = FillerDetector.Find(transcript, FillerDetector.DefaultWords);

        Assert.Equal([0.5, 1.5], found.Select(w => w.Start));
    }

    [Fact]
    public void FindMatchesTheSuppliedListNotTheDefault()
    {
        var transcript = new[] { W("um", 0.0), W("like", 0.5) };

        var found = FillerDetector.Find(transcript, ["Like"]);

        Assert.Equal("like", Assert.Single(found).Text);
    }

    [Fact]
    public void HyphenatedFormsDoNotMatchTheirPrefix()
    {
        var transcript = new[] { W("uh-huh", 0.0) };

        Assert.Empty(FillerDetector.Find(transcript, ["uh"]));
    }

    [Fact]
    public void BlankEntriesInTheListAreIgnored()
    {
        var transcript = new[] { W("", 0.0), W("um", 0.5) };

        var found = FillerDetector.Find(transcript, ["", "  ", "um"]);

        Assert.Equal(0.5, Assert.Single(found).Start);
    }
}
