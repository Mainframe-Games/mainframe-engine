using MainframeEngine.Video.Models;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Video.Tests;

public sealed class TranscriptViewTests
{
    private static readonly IReadOnlyList<Segment> Segments =
    [
        Segment.Create(0.0, 2.0, enabled: true, SegmentOrigin.Auto),
        Segment.Create(2.0, 3.0, enabled: false, SegmentOrigin.Auto),
        Segment.Create(3.0, 10.0, enabled: true, SegmentOrigin.Auto),
    ];

    private static readonly IReadOnlyList<Word> Words =
    [
        new("hello", 0.5, 0.9, 0.9),
        new("um", 1.9, 2.3, 0.9),      // midpoint 2.1: cut
        new("uh", 2.8, 3.1, 0.9),      // midpoint 2.95: cut
        new("world", 2.9, 3.5, 0.9),   // midpoint 3.2: kept
    ];

    [Fact]
    public void AWordIsCutWhenItsMidpointLiesInADisabledSegment()
    {
        Assert.False(TranscriptView.IsCut(Words[0], Segments));
        Assert.True(TranscriptView.IsCut(Words[1], Segments));
        Assert.True(TranscriptView.IsCut(Words[2], Segments));
        Assert.False(TranscriptView.IsCut(Words[3], Segments));
    }

    [Fact]
    public void CutStatesMatchesIsCutForEveryWord()
    {
        Assert.Equal([false, true, true, false], TranscriptView.CutStates(Words, Segments));
    }

    [Fact]
    public void AWordWithAnAnchorIsJudgedByTheAnchorNotTheMidpoint()
    {
        // Whisper put this "uh" on the pause after it (midpoint 3.5, kept) but its anchor is in the cut.
        var anchored = new Word("uh", 3.2, 3.8, 0.9, Anchor: 2.5);
        // And this neighbour's heuristic span swallowed the cut (midpoint 2.4) though it was spoken before it.
        var neighbour = new Word("like", 1.8, 3.0, 0.9, Anchor: 1.9);

        Assert.True(TranscriptView.IsCut(anchored, Segments));
        Assert.False(TranscriptView.IsCut(neighbour, Segments));
    }

    [Fact]
    public void AWordPastTheEndOfTheTimelineTakesTheLastSegment()
    {
        var late = new Word("late", 9.9, 10.3, 0.9);

        Assert.False(TranscriptView.IsCut(late, Segments));
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(0.89, 0)]
    [InlineData(0.9, -1)]
    [InlineData(1.0, -1)]
    [InlineData(2.0, 1)]
    [InlineData(3.4, 3)]
    [InlineData(50.0, -1)]
    public void IndexAtTimeFindsTheWordContainingTheTime(double seconds, int expected)
    {
        Assert.Equal(expected, TranscriptView.IndexAtTime(Words, seconds));
    }

    [Fact]
    public void IndexAtTimeOnAnEmptyTranscriptIsMinusOne()
    {
        Assert.Equal(-1, TranscriptView.IndexAtTime([], 1.0));
    }
}
