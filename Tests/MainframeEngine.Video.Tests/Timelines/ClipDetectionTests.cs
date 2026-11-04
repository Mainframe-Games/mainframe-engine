using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Transcript;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

/// <summary>
/// Cutback's detectors on a timeline clip (G20d): planned over the clip's window only, merged with the same origin's cuts
/// outside it, applied through <see cref="TimelineEdits.SetClipCuts"/> (the link group, with ripple).
/// </summary>
public sealed class ClipDetectionTests
{
    private static readonly DetectionSettings Settings = DetectionSettings.Default with { PaddingMs = 0, MinKeepMs = 0, MinSilenceMs = 400 };

    // Detect Silence on a clip, as the editor does it.
    private static Timeline Silence(Timeline t, Clip clip, IEnumerable<SilenceSpan> spans)
    {
        var inside = ClipDetection.SpansInWindow(spans, clip.SourceIn, clip.SourceOut);
        var planned = SilenceCutPlanner.Plan(inside, Settings, clip.Media.Duration);
        return TimelineEdits.SetClipCuts(t, clip.Id, list =>
            list.ReplaceAutoSegments(ClipDetection.InWindow(planned, list.Segments, SegmentOrigin.Auto, clip.SourceIn, clip.SourceOut)));
    }

    [Fact]
    public void SilenceOnlyInsideWindow()
    {
        // 10-40 s of a 60 s recording, then a second clip after it; silences at 5 s (outside) and 20 s (inside).
        var t = Append(Append(Vertical(), 10, 40), 45, 50);
        var clip = On(t, V1(t), 0);
        var next = On(t, V1(t), 1);
        Assert.Equal(30, next.Start, 6);

        t = Silence(t, clip, [new SilenceSpan(5, 7), new SilenceSpan(20, 22)]);

        var cut = On(t, V1(t), 0);
        Assert.DoesNotContain(cut.Segments, s => !s.Enabled && s.Start < 10); // the 5 s silence is outside the window
        Assert.Contains(cut.Segments, s => !s.Enabled && s.Start == 20 && s.End == 22);
        Assert.Equal(28, End(t, cut), 6);
        Assert.Equal(28, On(t, V1(t), 1).Start, 6); // the next clip rippled
        Assert.Equal(cut.Segments, On(t, A1(t), 0).Segments); // the linked audio cut too
        Partitioned(t);
    }

    [Fact]
    public void ManualCutsSurvive()
    {
        var t = Append(Vertical(), 10, 40);
        var clip = On(t, V1(t), 0);
        t = Cut(t, clip, 30, 31); // by hand
        t = Silence(t, On(t, V1(t), 0), [new SilenceSpan(20, 22)]);
        t = Silence(t, On(t, V1(t), 0), [new SilenceSpan(25, 26)]); // a re-run replaces the auto cuts only

        var segments = On(t, V1(t), 0).Segments;
        Assert.Contains(segments, s => !s.Enabled && s.Origin == SegmentOrigin.Manual && s.Start == 30 && s.End == 31);
        Assert.DoesNotContain(segments, s => !s.Enabled && s.Start == 20);
        Assert.Contains(segments, s => !s.Enabled && s.Origin == SegmentOrigin.Auto && s.Start == 25);
    }

    [Fact]
    public void OtherWindowsKeepTheirCuts()
    {
        // One partition, two windows (the razor copies it): detection in the second window keeps the first's cuts.
        var t = Append(Vertical(), 0, 40);
        t = Silence(t, On(t, V1(t), 0), [new SilenceSpan(5, 6)]);
        var clip = On(t, V1(t), 0);
        var segments = ClipDetection.InWindow([new PlannedCut(30, 31, "silence")], clip.Segments, SegmentOrigin.Auto, 20, 40);

        Assert.Equal([(5.0, 6.0), (30.0, 31.0)], segments.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void KeptTimeAndStatusCountTheWindowOnly()
    {
        var t = Append(Vertical(), 10, 40);
        t = Silence(t, On(t, V1(t), 0), [new SilenceSpan(5, 7), new SilenceSpan(20, 22)]);
        var clip = On(t, V1(t), 0);

        Assert.Equal(28, ClipDetection.KeptInWindow(clip.Segments, 10, 40), 6);
        Assert.Equal("Detected 1 silence, 0:02.0 removed.", ClipDetection.Status(1, "silence", "silences", 30, 28, "Detected", "none"));
        Assert.Equal("Detected 2 silences; all were already cut.", ClipDetection.Status(2, "silence", "silences", 28, 28, "Detected", "none"));
        Assert.Equal("No silence.", ClipDetection.Status(0, "silence", "silences", 28, 28, "Detected", "No silence."));
    }

    [Fact]
    public void WordsInWindowGoByTheirMiddle()
    {
        Word[] words = [new("a", 9.5, 9.9, 1), new("b", 9.8, 10.4, 1), new("c", 20, 21, 1), new("d", 39.9, 40.5, 1)];
        Assert.Equal(["b", "c"], ClipDetection.WordsInWindow(words, 10, 40).Select(w => w.Text));
    }

    [Fact]
    public void TheDetectorReadsOnlyTheWindow()
    {
        var args = SilenceDetector.Arguments("/m/rec.mp4", Settings, 10, 40, 60);
        Assert.Equal(["-ss", "10", "-t", "30", "-i", "/m/rec.mp4"], args.Take(6));
        Assert.Equal("-i", SilenceDetector.Arguments("/m/rec.mp4", Settings, 0, null, 60)[0]); // the whole file
    }
}

/// <summary>The <c>.mftranscript.json</c> sidecar: round-trips, and is ignored for another file or a newer format.</summary>
public sealed class TranscriptSerializerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-transcript").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task RoundTripsAndChecksTheHash()
    {
        var media = Path.Combine(_dir, "rec.mp4");
        var file = new TranscriptFile(1, "hash-a", "base.en", [new Word("so", 1.0, 1.2, 0.9, 1.05), new Word("um", 2, 2.3, 0.5)]);
        await TranscriptSerializer.SaveAsync(media, file, TestContext.Current.CancellationToken);

        Assert.Equal(media + ".mftranscript.json", TranscriptSerializer.PathFor(media));
        var loaded = await TranscriptSerializer.LoadAsync(media, "hash-a", TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(file.Words, loaded.Words);
        Assert.Equal("base.en", loaded.Model);
        Assert.Null(await TranscriptSerializer.LoadAsync(media, "hash-b", TestContext.Current.CancellationToken)); // the file changed
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ANewerOrBrokenFileIsIgnored()
    {
        Assert.Null(TranscriptSerializer.Deserialize("""{ "version": 2, "sourceHash": "h", "model": "x", "words": [] }"""));
        Assert.Null(TranscriptSerializer.Deserialize("not json"));
        Assert.NotNull(TranscriptSerializer.Deserialize("""{ "version": 1, "sourceHash": "h", "model": "x", "words": [] }"""));
    }
}
