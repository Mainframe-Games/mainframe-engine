using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;
using VideoCapture = MainframeEngine.Video.Capture;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoDocumentTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly string _dir = Directory.CreateTempSubdirectory("video-doc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static VideoProject Project(string sourcePath, double duration = 60) =>
        VideoProject.CreateNew(new SourceInfo(sourcePath, new string('0', 64), duration, 1920, 1080, 60));

    internal const string Sidecar = """
        { "format": "mainframe-capture", "version": 1, "video": "a.mp4", "engineVersion": "0.42.0", "game": "Demo",
          "scene": "res://Scenes/Main.mscene", "mode": "live", "frameRate": 60, "width": 1920, "height": 1080,
          "durationSeconds": 60.0, "droppedFrames": 0,
          "input": [ { "start": 0.0, "end": 14.0 }, { "start": 20.0, "end": 60.0 } ],
          "loading": [ { "start": 14.5, "end": 19.0 } ], "paused": [], "keep": [], "skip": [],
          "markers": [ { "time": 30.0, "label": "boss" } ] }
        """;

    // so · um · we · go — "um" is a filler; words carry DTW anchors
    internal static Word[] Words() =>
    [
        new("so", 1.0, 1.3, 0.9, 1.1), new("um", 1.4, 1.8, 0.8, 1.5), new("we", 2.0, 2.2, 0.9, 2.05), new("go", 2.3, 2.6, 0.9, 2.4),
    ];

    private VideoDocument Doc(VideoCapture.CaptureSidecar? capture = null) =>
        VideoDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, capture, []);

    [Fact]
    public void ANewDocumentIsCleanAndUnnamed()
    {
        var doc = Doc();
        Assert.False(doc.IsDirty);
        Assert.Null(doc.FilePath);
        Assert.Equal(Path.Combine(_dir, "a.mfcut"), doc.DefaultProjectPath);
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void SaveWritesTheProjectFormatAndAddsTheExtension()
    {
        var doc = Doc();
        doc.Editor.CreateSection(10, 20, anchor: 10);
        doc.Save(Path.Combine(_dir, "trim"));
        var path = Path.Combine(_dir, "trim.mfcut");
        Assert.Equal(path, doc.FilePath);
        Assert.False(doc.IsDirty);
        Assert.Equal(ProjectSerializer.Serialize(doc.Project), File.ReadAllText(path));
        Assert.Equal(3, doc.Project.Segments.Count);
    }

    [Fact]
    public async Task ASavedProjectReopensIdentically()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.Editor.CreateSection(5, 7, anchor: 5);
        doc.Save(Path.Combine(_dir, "a.mfcut"));
        var loaded = await ProjectSerializer.LoadAsync(Path.Combine(_dir, "a.mfcut"), TestContext.Current.CancellationToken);
        Assert.Equal(doc.Segments.Segments, loaded.Segments);
        Assert.Equal(Words(), loaded.Transcript);
    }

    [Fact]
    public void SavingAnUnnamedDocumentWithoutAPathThrows() => Assert.Throws<InvalidOperationException>(() => Doc().Save());

    [Fact]
    public void DetectIdleGameplayCutsTheLoadingScreenAsOneUndoStep()
    {
        var doc = Doc(VideoCapture.CaptureSidecarSerializer.Deserialize(Sidecar));
        Assert.StartsWith("Cut ", doc.DetectIdleGameplay(GameplaySettings.Default), StringComparison.Ordinal);
        Assert.Contains(doc.Segments.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Gameplay && s.Start <= 14.5 && s.End >= 19.0);
        Assert.True(doc.Editor.Undo());
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void NoSidecarMeansNoGameplayDetection() =>
        Assert.Throws<InvalidOperationException>(() => Doc().DetectIdleGameplay(GameplaySettings.Default));

    [Fact]
    public void ApplySilenceCutsPaddedSpansAndReportsThem()
    {
        var doc = Doc();
        Assert.Equal("Detected 2 silences, 0:02.8 removed.",
            doc.ApplySilence([new SilenceSpan(10, 12), new SilenceSpan(30, 31)], DetectionSettings.Default));
        Assert.Equal(5, doc.Segments.Count);
    }

    [Fact]
    public void SilenceInsideAnExistingCutReportsThatNothingChanged()
    {
        var doc = Doc();
        Assert.True(doc.Editor.CreateSection(5, 20, anchor: 5)); // cut by hand
        Assert.Equal("Detected 1 silence; all were already cut.",
            doc.ApplySilence([new SilenceSpan(10, 12)], DetectionSettings.Default));
    }

    [Fact]
    public void SilenceOverlappingAnExistingCutReportsOnlyWhatItRemoved()
    {
        var doc = Doc();
        Assert.True(doc.Editor.CreateSection(5, 11, anchor: 5)); // 10–11 s of the silence is cut already
        // Padded 10.06–11.94: only 11–11.94 s is new.
        Assert.Equal("Detected 1 silence, 0:00.9 removed.", doc.ApplySilence([new SilenceSpan(10, 12)], DetectionSettings.Default));
    }

    [Fact]
    public void IdleGameplayAlreadyCutReportsThatNothingChanged()
    {
        var doc = Doc(VideoCapture.CaptureSidecarSerializer.Deserialize(Sidecar));
        var first = doc.DetectIdleGameplay(GameplaySettings.Default);
        Assert.StartsWith("Cut 1 stretch of idle gameplay, ", first, StringComparison.Ordinal);
        Assert.Equal("Found 1 stretch of idle gameplay; all were already cut.", doc.DetectIdleGameplay(GameplaySettings.Default));
    }

    [Fact]
    public void IdleGameplayReportsTheKeptTimeItRemoved()
    {
        var doc = Doc(VideoCapture.CaptureSidecarSerializer.Deserialize(Sidecar));
        doc.ApplySilence([new SilenceSpan(15, 17)], DetectionSettings.Default); // 15.06–16.94 s of the loading screen, cut already
        var cuts = MainframeEngine.Video.Detection.GameplayCutPlanner.Plan(doc.Capture!, doc.Segments.Segments, GameplaySettings.Default, doc.Duration);
        var planned = cuts.Sum(c => c.Duration); // the planner lays its cut over auto cuts
        Assert.Equal($"Cut 1 stretch of idle gameplay, {VideoFormat.Clock(planned - 1.88)} removed.", doc.DetectIdleGameplay(GameplaySettings.Default));
    }

    [Fact]
    public void SettingATranscriptMakesTheDocumentDirtyUntilSaved()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        Assert.True(doc.IsDirty);
        Assert.True(doc.TranscriptHasAnchors);
        doc.Save(Path.Combine(_dir, "a.mfcut"));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void CuttingWordsIsOneManualUndoStep()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        Assert.True(doc.CutWords(1, 1, anchor: 1)); // "um"
        var cut = doc.Segments.Segments[doc.Segments.IndexAt(1.6)];
        Assert.False(cut.Enabled);
        Assert.Equal(SegmentOrigin.Manual, cut.Origin);
        Assert.Equal(1.4, cut.Start, 6); // no waveform: the raw word edges
        Assert.Equal(1.8, cut.End, 6);
        Assert.True(doc.Editor.Undo());
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void ClickingACutWordRestoresIt()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.CutWords(1, 1, 1);
        doc.CutWords(1, 1, 1);
        Assert.True(doc.Segments.Segments[doc.Segments.IndexAt(1.6)].Enabled);
    }

    [Fact]
    public void FillerRemovalCutsTheFillerAsOneStep()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        var status = doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.StartsWith("Removed 1 filler word", status, StringComparison.Ordinal);
        Assert.Contains(doc.Segments.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Filler);
        Assert.True(doc.Editor.Undo());
        Assert.DoesNotContain(doc.Segments.Segments, s => s.Origin == SegmentOrigin.Filler);
    }

    [Fact]
    public void FillerRemovalBeforeTheWaveformLoadsSaysSoInsteadOfAskingToTranscribeAgain()
    {
        var doc = Doc(); // anchored words, no waveform yet
        doc.SetTranscript(Words());
        var status = doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.Equal("Removed 1 filler word, 0:00.4 cut. The waveform is still loading; filler cuts use Whisper's rough timing.", status);
    }

    [Fact]
    public void FillerRemovalWithoutAnchorsAsksToTranscribeAgain()
    {
        var doc = Doc();
        doc.Waveform = Loud();
        doc.SetTranscript([.. Words().Select(w => w with { Anchor = null })]);
        var status = doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.EndsWith(" Transcribe again for cuts aligned to the audio.", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FillerRemovalLeavesManualEditsAlone()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.Editor.CreateSection(1.3, 1.9, anchor: 1.3); // the user cut around "um" by hand…
        doc.Editor.CreateSection(1.3, 1.9, anchor: 1.5); // …then restored it
        doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.True(doc.Segments.Segments[doc.Segments.IndexAt(1.6)].Enabled); // manual beats filler
    }

    [Fact]
    public void TheExportedTranscriptOmitsCutWords()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.CutWords(1, 1, 1);
        Assert.DoesNotContain("um", doc.TranscriptText(srt: false), StringComparison.Ordinal);
        Assert.Contains("-->", doc.TranscriptText(srt: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectWhoseSourceIsMissingIsRefused()
    {
        var path = Path.Combine(_dir, "gone.mfcut");
        await ProjectSerializer.SaveAsync(Project(Path.Combine(_dir, "missing.mp4")), path, TestContext.Current.CancellationToken);
        var e = await Assert.ThrowsAsync<FileNotFoundException>(() => VideoDocument.OpenAsync(path, NoFfmpeg, 100, TestContext.Current.CancellationToken));
        Assert.Contains("missing.mp4", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedSourceOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3], TestContext.Current.CancellationToken);
        var path = Path.Combine(_dir, "a.mfcut");
        await ProjectSerializer.SaveAsync(Project(video), path, TestContext.Current.CancellationToken); // the zero hash never matches
        var doc = await VideoDocument.OpenAsync(path, NoFfmpeg, 100, TestContext.Current.CancellationToken);
        Assert.Contains(doc.Warnings, w => w.Contains("changed", StringComparison.Ordinal));
        Assert.Equal(path, doc.FilePath);
    }

    [Fact]
    public async Task ABadSidecarOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(VideoCapture.CaptureSidecar.PathFor(video), "{ not json", TestContext.Current.CancellationToken);
        var path = Path.Combine(_dir, "a.mfcut");
        await ProjectSerializer.SaveAsync(Project(video), path, TestContext.Current.CancellationToken);
        var doc = await VideoDocument.OpenAsync(path, NoFfmpeg, 100, TestContext.Current.CancellationToken);
        Assert.Null(doc.Capture);
        Assert.Contains(doc.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ALegacyProjectSavesAsMfcutNextToIt()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3], TestContext.Current.CancellationToken);
        var legacy = Path.Combine(_dir, "old.cutback");
        await ProjectSerializer.SaveAsync(Project(video), legacy, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken);

        var doc = await VideoDocument.OpenAsync(legacy, NoFfmpeg, 100, TestContext.Current.CancellationToken);
        Assert.Null(doc.FilePath);
        Assert.True(doc.IsDirty);
        Assert.Equal(Path.Combine(_dir, "old.mfcut"), doc.DefaultProjectPath);
        doc.Save(doc.DefaultProjectPath);
        Assert.True(File.Exists(Path.Combine(_dir, "old.mfcut")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken)); // never overwritten
    }

    // 10 ms buckets, loud everywhere except at 1.425 s and 1.785 s: the quietest buckets beside the word "um" (1.4 to 1.8).
    private static Waveform Loud()
    {
        var peaks = Enumerable.Repeat(new Peak(-1000, 1000), 6000).ToArray();
        peaks[142] = peaks[178] = new Peak(-5, 5);
        return Waveform.Build(peaks, finestSamplesPerBucket: 1, sampleRate: 100, levelFactor: 2, levelCount: 1);
    }

    [Fact]
    public void WordEdgesSnapToTheQuietestBucketAndBoundariesUseANarrowerWindow()
    {
        var doc = Doc();
        doc.Waveform = Loud();
        var (start, end) = doc.SnapWordSpan(1.4, 1.8);
        Assert.Equal(1.425, start, 6);
        Assert.Equal(1.785, end, 6);
        Assert.Equal(1.425, doc.SnapTime(1.4), 6);
        Assert.Equal(1.505, doc.SnapTime(1.5), 6); // no quiet bucket within 20 ms: the bucket centre
    }

    [Fact]
    public void ACollapsingSnapKeepsTheRawWordEdges()
    {
        var doc = Doc();
        doc.Waveform = Loud();
        Assert.Equal((1.42, 1.43), doc.SnapWordSpan(1.42, 1.43)); // both edges snap to 1.425
    }

    [Fact]
    public void DirtyChangedFiresOnlyWhenTheFlagFlips()
    {
        var doc = Doc();
        var flips = 0;
        doc.DirtyChanged += () => flips++;
        doc.Editor.CreateSection(5, 6, anchor: 5);
        doc.Editor.CreateSection(8, 9, anchor: 8);
        Assert.Equal(1, flips);
        doc.Editor.Undo();
        doc.Editor.Undo();
        Assert.Equal(2, flips);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void TheProjectReflectsLiveEditsAndVersionCountsChanges()
    {
        var doc = Doc();
        var version = doc.Version;
        var changed = 0;
        doc.Changed += () => changed++;
        doc.Editor.CreateSection(5, 6, anchor: 5);
        doc.SetTranscript(Words());
        Assert.Equal(version + 2, doc.Version);
        Assert.Equal(2, changed);
        Assert.Equal(3, doc.Project.Segments.Count);
        Assert.Equal(4, doc.Project.Transcript.Count);
    }

    [Fact]
    public void ATranscriptWithoutAnchorsIsReportedSo()
    {
        var doc = Doc();
        doc.SetTranscript([new Word("um", 1.4, 1.8, 0.8)]);
        Assert.False(doc.TranscriptHasAnchors);
    }

    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(2.84, "0:02.8")]
    [InlineData(65.31, "1:05.3")]
    [InlineData(3725.0, "1:02:05.0")]
    public void ClockFormatsMinutesSecondsAndTenths(double seconds, string expected) => Assert.Equal(expected, VideoFormat.Clock(seconds));

    [Theory]
    [InlineData(0.0, "0:00")]
    [InlineData(75.0, "1:15")]
    [InlineData(3725.0, "1:02:05")]
    public void RulerLabelsAreWholeSeconds(double seconds, string expected) => Assert.Equal(expected, VideoFormat.Ruler(seconds));
}
