using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

/// <summary>Auto-captions: kept words only, at most N a caption, breaking at pauses and sentence ends, in timeline time.</summary>
public sealed class CaptionPlannerTests
{
    // Eight words 0.3 s apart from 1 s, a 0.8 s pause after word 6, then "again." and "Ready".
    private static Word[] Words() =>
    [
        new("one", 1.0, 1.2, 1), new("two", 1.3, 1.5, 1), new("three", 1.6, 1.8, 1), new("four", 1.9, 2.1, 1),
        new("five", 2.2, 2.4, 1), new("six", 2.5, 2.7, 1), new("again.", 3.5, 3.8, 1), new("Ready", 3.9, 4.2, 1),
    ];

    [Fact]
    public void BreaksAtPausesSkipsCuts()
    {
        var t = Append(Vertical(), 0, 10);
        t = Cut(t, On(t, V1(t), 0), 1.55, 1.85); // "three" is cut
        var clip = On(t, V1(t), 0);

        var captions = CaptionPlanner.Plan(clip, Words(), t.Rate, maxWords: 7);

        Assert.Equal(["one two four five six", "again.", "Ready"], captions.Select(c => c.Text));
        Assert.Equal(1.0, captions[0].Start, 6);
        Assert.True(captions[0].End <= captions[1].Start); // never overlapping
        Assert.Equal(TimelineLayout.TimelineTimeOf(clip, 3.5)!.Value, captions[1].Start, 1); // after the cut, earlier on the timeline
    }

    [Fact]
    public void AtMostNWordsACaption()
    {
        var t = Append(Vertical(), 0, 10);
        Assert.Equal(["one two", "three four", "five six", "again.", "Ready"],
            CaptionPlanner.Plan(On(t, V1(t), 0), Words(), t.Rate, maxWords: 2).Select(c => c.Text));
    }

    [Fact]
    public void CaptionsBecomeTitlesOnANewTopTrack()
    {
        var t = Append(Vertical(), 0, 10);
        var captions = CaptionPlanner.Plan(On(t, V1(t), 0), Words(), t.Rate);

        var captioned = TimelineEdits.AddCaptions(t, captions);

        var track = captioned.TracksOf(TrackKind.Video)[^1];
        Assert.Equal("Captions", track.Name);
        var clips = captioned.ClipsOn(track.Id);
        Assert.Equal(captions.Count, clips.Count);
        Assert.All(clips, c => Assert.Equal(ClipKind.Title, c.Kind));
        Assert.Equal("one two three four five six", clips[0].Title!.Text);
        Assert.True(clips[0].Title!.Y > 0); // low in the frame
    }
}
