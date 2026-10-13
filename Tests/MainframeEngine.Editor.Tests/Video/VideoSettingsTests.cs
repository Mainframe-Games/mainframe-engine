using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Editor Settings › Video as data: defaults are Cutback's, every number clamps, the file round-trips and an old or
/// hand-edited one cannot leave a range. Nothing here needs the editor.
/// </summary>
public sealed class VideoSettingsDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("video-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "editor_settings.json");

    [Fact]
    public void TheDefaultsAreCutbacks()
    {
        var s = new EditorSettings();
        Assert.Null(s.VideoFfmpegPath);
        Assert.Equal("base.en", s.VideoWhisperModel);
        Assert.Equal(FillerDetector.DefaultWords, s.VideoFillerWords);
        Assert.Equal(new DetectionSettings(PaddingMs: 60, MinSilenceMs: 400, SilenceThresholdDb: -34.0, MinKeepMs: 120), s.VideoDetection);
        Assert.Equal(new GameplaySettings(MinIdleMs: 3000, PaddingMs: 500, MinKeepMs: 1000, MarkerWindowMs: 1500), s.VideoGameplay);
        Assert.Equal(100, s.VideoUndoHistoryLimit);
    }

    [Fact]
    public void VideoSettingsRoundTripAndClamp()
    {
        var s = new EditorSettings
        {
            VideoFfmpegPath = "  /opt/ff  ",
            VideoWhisperModel = "small.en",
            VideoFillerWords = [" Um ", "uh,", "UM", "", "  "],
            VideoSilenceThresholdDb = -40.5,
            VideoMinSilenceMs = 250,
            VideoPaddingMs = 90,
            VideoMinKeepMs = 200,
            VideoGameplayMinIdleMs = 5000,
            VideoGameplayPaddingMs = 250,
            VideoGameplayMinKeepMs = 1500,
            VideoGameplayMarkerWindowMs = 2500,
            VideoUndoHistoryLimit = 300,
        };
        Assert.Equal("/opt/ff", s.VideoFfmpegPath); // trimmed
        Assert.Equal(["um", "uh"], s.VideoFillerWords); // trimmed, normalised, de-duplicated, empty entries dropped

        s.Save(FilePath);
        var loaded = EditorSettings.Load(FilePath);
        Assert.Equal("/opt/ff", loaded.VideoFfmpegPath);
        Assert.Equal("small.en", loaded.VideoWhisperModel);
        Assert.Equal(["um", "uh"], loaded.VideoFillerWords);
        Assert.Equal(new DetectionSettings(PaddingMs: 90, MinSilenceMs: 250, SilenceThresholdDb: -40.5, MinKeepMs: 200), loaded.VideoDetection);
        Assert.Equal(new GameplaySettings(MinIdleMs: 5000, PaddingMs: 250, MinKeepMs: 1500, MarkerWindowMs: 2500), loaded.VideoGameplay);
        Assert.Equal(300, loaded.VideoUndoHistoryLimit);

        var clone = loaded.Clone();
        clone.VideoFillerWords.Add("hm");
        Assert.Equal(["um", "uh"], loaded.VideoFillerWords); // a clone shares nothing
        Assert.Equal("small.en", clone.VideoWhisperModel);
        Assert.Equal(loaded.VideoDetection, clone.VideoDetection);
        Assert.Equal(loaded.VideoGameplay, clone.VideoGameplay);
        Assert.Equal(300, clone.VideoUndoHistoryLimit);
    }

    [Fact]
    public void EveryNumberClampsToItsRange()
    {
        var low = new EditorSettings
        {
            VideoSilenceThresholdDb = -500,
            VideoMinSilenceMs = -1,
            VideoPaddingMs = -1,
            VideoMinKeepMs = -1,
            VideoGameplayMinIdleMs = -1,
            VideoGameplayPaddingMs = -1,
            VideoGameplayMinKeepMs = -1,
            VideoGameplayMarkerWindowMs = -1,
            VideoUndoHistoryLimit = -1,
        };
        Assert.Equal(-80.0, low.VideoSilenceThresholdDb);
        Assert.Equal([50, 0, 0, 500, 0, 0, 0, 10], new[]
        {
            low.VideoMinSilenceMs, low.VideoPaddingMs, low.VideoMinKeepMs, low.VideoGameplayMinIdleMs, low.VideoGameplayPaddingMs,
            low.VideoGameplayMinKeepMs, low.VideoGameplayMarkerWindowMs, low.VideoUndoHistoryLimit,
        });

        var high = new EditorSettings
        {
            VideoSilenceThresholdDb = 12,
            VideoMinSilenceMs = int.MaxValue,
            VideoPaddingMs = int.MaxValue,
            VideoMinKeepMs = int.MaxValue,
            VideoGameplayMinIdleMs = int.MaxValue,
            VideoGameplayPaddingMs = int.MaxValue,
            VideoGameplayMinKeepMs = int.MaxValue,
            VideoGameplayMarkerWindowMs = int.MaxValue,
            VideoUndoHistoryLimit = int.MaxValue,
        };
        Assert.Equal(0.0, high.VideoSilenceThresholdDb);
        Assert.Equal([5000, 500, 2000, 60000, 2000, 10000, 10000, 1000], new[]
        {
            high.VideoMinSilenceMs, high.VideoPaddingMs, high.VideoMinKeepMs, high.VideoGameplayMinIdleMs, high.VideoGameplayPaddingMs,
            high.VideoGameplayMinKeepMs, high.VideoGameplayMarkerWindowMs, high.VideoUndoHistoryLimit,
        });

        // A NaN would go straight into an ffmpeg filter string.
        Assert.Equal(-34.0, new EditorSettings { VideoSilenceThresholdDb = double.NaN }.VideoSilenceThresholdDb);
    }

    [Fact]
    public void AHandEditedFileCannotLeaveTheRanges()
    {
        File.WriteAllText(FilePath, """
            { "format": 1, "videoFfmpegPath": "   ", "videoWhisperModel": "gigantic.en",
              "videoFillerWords": ["", "  "], "videoSilenceThresholdDb": -900, "videoMinSilenceMs": 1,
              "videoPaddingMs": 99999, "videoMinKeepMs": -5, "videoGameplayMinIdleMs": 3,
              "videoGameplayPaddingMs": 99999, "videoGameplayMinKeepMs": 99999, "videoGameplayMarkerWindowMs": -2,
              "videoUndoHistoryLimit": 5000000 }
            """);
        var s = EditorSettings.Load(FilePath);
        Assert.Null(s.VideoFfmpegPath); // blank: search
        Assert.Equal("base.en", s.VideoWhisperModel); // an unknown model key
        Assert.Equal(FillerDetector.DefaultWords, s.VideoFillerWords); // nothing usable left: the defaults
        Assert.Equal(new DetectionSettings(PaddingMs: 500, MinSilenceMs: 50, SilenceThresholdDb: -80.0, MinKeepMs: 0), s.VideoDetection);
        Assert.Equal(new GameplaySettings(MinIdleMs: 500, PaddingMs: 2000, MinKeepMs: 10000, MarkerWindowMs: 0), s.VideoGameplay);
        Assert.Equal(1000, s.VideoUndoHistoryLimit);
    }

    [Fact]
    public void AnOldFileWithoutVideoKeysGetsTheDefaults()
    {
        File.WriteAllText(FilePath, """{ "format": 1, "accent": "#3b82f6", "videoFfmpegPath": "/opt/ff" }""");
        var s = EditorSettings.Load(FilePath);
        Assert.Equal("/opt/ff", s.VideoFfmpegPath);
        Assert.Equal(new EditorSettings().VideoDetection, s.VideoDetection);
        Assert.Equal(new EditorSettings().VideoGameplay, s.VideoGameplay);
        Assert.Equal(FillerDetector.DefaultWords, s.VideoFillerWords);
        Assert.Equal(100, s.VideoUndoHistoryLimit);
    }

    [Fact]
    public void DefaultsAreNotWrittenSoALaterDefaultReachesEveryone()
    {
        new EditorSettings().Save(FilePath);
        var json = File.ReadAllText(FilePath);
        foreach (var key in (string[])["videoWhisperModel", "videoFillerWords", "videoSilenceThresholdDb", "videoMinSilenceMs", "videoPaddingMs",
                     "videoMinKeepMs", "videoGameplayMinIdleMs", "videoGameplayPaddingMs", "videoGameplayMinKeepMs",
                     "videoGameplayMarkerWindowMs", "videoUndoHistoryLimit"])
            Assert.Contains($"\"{key}\": null", json, StringComparison.Ordinal);

        new EditorSettings { VideoPaddingMs = 80 }.Save(FilePath);
        Assert.Contains("\"videoPaddingMs\": 80", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tiny.en", "tiny.en")]
    [InlineData("MEDIUM.EN", "medium.en")]
    [InlineData("base", "base.en")]
    [InlineData(null, "base.en")]
    public void TheModelIsAKnownKeyOrBaseEn(string? key, string expected) =>
        Assert.Equal(expected, new EditorSettings { VideoWhisperModel = key! }.VideoWhisperModel);

    [Fact]
    public void AGameIsHandedTheEncoderWhateverTheSettingNames()
    {
        var exe = new FfmpegLocator().ExecutableName("ffmpeg");
        var probe = new FfmpegLocator().ExecutableName("ffprobe");
        File.WriteAllBytes(Path.Combine(_dir, exe), []);
        File.WriteAllBytes(Path.Combine(_dir, probe), []);

        Assert.Null(VideoFfmpeg.GameArgument(null));
        Assert.Null(VideoFfmpeg.GameArgument("  "));
        Assert.Equal(Path.Combine(_dir, exe), VideoFfmpeg.GameArgument(_dir)); // a folder: the ffmpeg in it
        Assert.Equal(Path.Combine(_dir, exe), VideoFfmpeg.GameArgument(Path.Combine(_dir, probe))); // the ffprobe: its neighbour
        Assert.Equal(Path.Combine(_dir, exe), VideoFfmpeg.GameArgument(Path.Combine(_dir, exe)));
        Assert.Equal("/opt/ff", VideoFfmpeg.GameArgument(" /opt/ff ")); // missing: as typed, the game says it was not found
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        Assert.Equal(empty, VideoFfmpeg.GameArgument(empty));
    }
}

/// <summary>
/// The settings wired into the video editor: Detect Silence and Remove Filler Words read them when they start, jobs keep
/// what they started with, Apply changes the undo limit of tabs already open, and the dialog and the notice link reach
/// the Video section.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class VideoSettingsWiringTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly string _dir = Directory.CreateTempSubdirectory("video-settings-ws-").FullName;
    private readonly string _models;
    private readonly HeadlessEditor _editor;
    private int _tabs;

    public VideoSettingsWiringTests()
    {
        _models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(_models);
        _editor = new HeadlessEditor(configure: o => o with { VideoModelsDirectory = _models });
    }

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose()
    {
        _editor.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Apply(Action<EditorSettings> change)
    {
        var settings = W.Settings.Clone();
        change(settings);
        W.ApplySettings(settings);
    }

    // A one-byte "recording" and a .mfcut beside it (and its Mainframe Engine sidecar); returns the project's path.
    private string SaveProject(string name, bool sidecar = false)
    {
        var video = Path.Combine(_dir, name + ".mp4");
        File.WriteAllBytes(video, [0]);
        if (sidecar)
            File.WriteAllText(video + MainframeEngine.Video.Capture.CaptureSidecar.FileSuffix, VideoDocumentTests.Sidecar);
        var path = Path.Combine(_dir, name + ".mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    // A tab on a real one-byte recording with an ffmpeg location that cannot run (no job here ever reaches ffmpeg).
    private VideoTab OpenTab(bool sidecar = false)
    {
        var path = SaveProject($"talk{++_tabs}", sidecar);
        var tab = W.Session.OpenVideo(path, NoFfmpeg, W.Settings.VideoUndoHistoryLimit);
        // The waveform job fails on the missing ffmpeg and adds a notice; let it finish so it cannot land after ours.
        Assert.True(SpinWait.SpinUntil(() => { _editor.Tick(); return tab.WaveformJob is null; }, TimeSpan.FromSeconds(10)));
        return tab;
    }

    private static void Replace(VideoTab tab, IReadOnlyList<SilenceSpan> spans)
    {
        tab.SilenceJob!.Dispose(); // the real job (it cannot start /x/ffmpeg) makes way for one that finishes with these spans
        tab.SilenceJob = VideoJob.Start("silence", (_, _) => Task.FromResult(spans));
        Assert.True(SpinWait.SpinUntil(() => tab.SilenceJob.IsDone, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void DetectSilenceUsesTheSettings()
    {
        var tab = OpenTab();
        Apply(s =>
        {
            s.VideoPaddingMs = 0;
            s.VideoMinKeepMs = 0;
        });

        W.Commands.Execute("video.detect_silence");
        Assert.NotNull(tab.SilenceJob);
        Assert.Equal(W.Settings.VideoDetection, tab.SilenceSettings);

        // The settings change while the job runs: it still lays its cuts out with the ones it started with.
        Apply(s => s.VideoPaddingMs = 500);
        Replace(tab, [new SilenceSpan(10, 20), new SilenceSpan(30, 31)]);
        _editor.Tick();

        Assert.Equal(0, tab.Document.Project.Settings.PaddingMs);
        var cuts = tab.Document.Segments.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End)).ToList();
        Assert.Equal([(10.0, 20.0), (30.0, 31.0)], cuts); // padding 0: the cuts are exactly the spans
        Assert.Null(tab.SilenceSettings);
    }

    [Fact]
    public void TheNextSilenceRunSeesTheChangedSettings()
    {
        var tab = OpenTab();
        Apply(s => s.VideoPaddingMs = 0);
        W.Commands.Execute("video.detect_silence");
        Assert.Equal(0, tab.SilenceSettings!.PaddingMs);
        tab.SilenceJob!.Dispose();
        tab.SilenceJob = null;

        Apply(s =>
        {
            s.VideoPaddingMs = 200;
            s.VideoSilenceThresholdDb = -50;
        });
        W.Commands.Execute("video.detect_silence");
        Assert.Equal(200, tab.SilenceSettings!.PaddingMs);
        Assert.Equal(-50, tab.SilenceSettings.SilenceThresholdDb);
    }

    [Fact]
    public void DetectIdleUsesTheGameplaySettings()
    {
        // The fixture: input until 14 s and from 20 s, loading from 14.5 s to 19 s.
        var tab = OpenTab(sidecar: true);
        List<(double Start, double End)> Cuts() => [.. tab.Document.Segments.Segments.Where(s => !s.Enabled).Select(s => (s.Start, s.End))];

        Apply(s => s.VideoGameplayMinIdleMs = 60000); // longer than the 6 s without input: only the loading is cut
        W.Commands.Execute("video.detect_idle");
        Assert.Equal([(14.5, 19.0)], Cuts());

        Apply(s =>
        {
            s.VideoGameplayMinIdleMs = 3000;
            s.VideoGameplayPaddingMs = 0;
        });
        W.Commands.Execute("video.detect_idle"); // a re-run withdraws its own cuts first
        Assert.Equal([(14.0, 20.0)], Cuts()); // all six seconds without input, no lead-in or lead-out kept
    }

    [Fact]
    public void RemoveFillersUsesTheFillerWordsAndTheDetectionSettings()
    {
        var tab = OpenTab();
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        Apply(s => s.VideoFillerWords = ["nothing-matches-this"]);
        W.Commands.Execute("video.remove_fillers");
        Assert.Single(tab.Document.Segments.Segments);

        Apply(s => s.VideoFillerWords = [.. FillerDetector.DefaultWords]);
        W.Commands.Execute("video.remove_fillers");
        Assert.True(tab.Document.Segments.Count > 1);
    }

    [Fact]
    public void TranscribeUsesTheModelFromTheSettingsAndKeepsItWhileItRuns()
    {
        File.WriteAllBytes(Path.Combine(_models, WhisperModelInfo.For(WhisperModel.TinyEn).FileName), [1, 2, 3]);
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        WhisperModel? usedModel = null;
        string? usedPath = null;
        W.VideoView.TranscriberFactory = (_, path, model) =>
        {
            usedModel = model;
            usedPath = path;
            return new SlowTranscriber(started, release);
        };
        var tab = OpenTab();
        Apply(s => s.VideoWhisperModel = "tiny.en");

        W.Commands.Execute("video.transcribe");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Apply(s => s.VideoWhisperModel = "medium.en"); // too late for this run
        Assert.Equal(WhisperModel.TinyEn, tab.TranscribeRun!.Model);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => tab.TranscribeJob is { IsDone: true }, TimeSpan.FromSeconds(10)));
        _editor.Tick();

        Assert.Equal(WhisperModel.TinyEn, usedModel);
        Assert.Equal(Path.Combine(_models, "ggml-tiny.en.bin"), usedPath);
        Assert.Null(tab.TranscribeJob);
    }

    private sealed class SlowTranscriber(ManualResetEventSlim started, ManualResetEventSlim release) : ITranscriber
    {
        public Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.Run<IReadOnlyList<Word>>(() =>
            {
                started.Set();
                release.Wait(cancellationToken);
                return [];
            }, cancellationToken);
    }

    [Fact]
    public void TheUndoLimitAppliesToOpenTabs()
    {
        var first = OpenTab();
        var second = OpenTab();
        Assert.Equal(100, first.Document.Editor.History.Limit);

        for (var i = 0; i < 30; i++)
            first.Document.Editor.CreateSection(i * 2, i * 2 + 1, anchor: i * 2);
        Apply(s => s.VideoUndoHistoryLimit = 12);
        Assert.Equal(12, first.Document.Editor.History.Limit);
        Assert.Equal(12, second.Document.Editor.History.Limit);
        var undone = 0;
        while (first.Document.Editor.History.CanUndo)
        {
            first.Document.Editor.Undo();
            undone++;
        }

        Assert.Equal(12, undone); // lowering the limit dropped the oldest steps at once

        Apply(s => s.VideoUndoHistoryLimit = 1000);
        Assert.Equal(1000, second.Document.Editor.History.Limit);
        Assert.False(second.IsDirty); // a limit is not an edit
    }

    [Fact]
    public void ANewTabStartsWithTheConfiguredUndoLimit()
    {
        Apply(s => s.VideoUndoHistoryLimit = 25);
        var path = SaveProject("limit");
        W.Commands.LocateFfmpeg = _ => NoFfmpeg;

        W.Commands.OpenVideo(path);
        _editor.Tick(3);

        Assert.Equal(25, W.Session.ActiveVideo!.Document.Editor.History.Limit);
    }

    // ── ffmpeg not found ─────────────────────────────────────────────────────────────────────────────────────────

    private VideoTab TabWithoutFfmpeg()
    {
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, []);
        var preview = new VideoPreview(new VideoTabTests.CountingFactory(320 * 180 * 4), () => null,
            "/r/a.mp4", 320, 180, 30, 850 + ++_tabs);
        var tab = new VideoTab(document, preview, null, 850 + _tabs);
        W.ScenesHost.AddChild(tab.TimelineViewport);
        W.ScenesHost.AddChild(preview.Viewport);
        W.Session.Add(tab);
        _editor.Tick(2);
        return tab;
    }

    [Theory]
    [InlineData("video.detect_silence", "Detect Silence")]
    [InlineData("video.transcribe", "Transcribe")]
    [InlineData("video.export", "Export")]
    public void WithoutFfmpegAnOperationSaysSoInTheNoticeLineNotInAModal(string command, string what)
    {
        var tab = TabWithoutFfmpeg();
        W.Commands.Execute(command);
        _editor.Tick();

        Assert.False(W.Message.Visible);
        Assert.Equal($"ffmpeg not found: {what} needs it.", tab.Notice);
        Assert.True(tab.NoticeNeedsFfmpegSetting);
        Assert.Equal(tab.Notice, W.VideoPanel.NoticeText);
        Assert.True(W.VideoPanel.NoticeHasSettingsLink);
        Assert.Null(tab.SilenceJob);
        Assert.Null(tab.TranscribeJob);
        Assert.Null(tab.ExportJob);
        Assert.False(W.ExportVideo.Visible);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TheWaveformWithoutFfmpegIsTheSameNotice()
    {
        var tab = TabWithoutFfmpeg();
        Assert.Null(tab.WaveformJob);
        Assert.Equal("ffmpeg not found: the waveform needs it.", tab.Notice);
        Assert.True(tab.NoticeNeedsFfmpegSetting);
    }

    [Fact]
    public void TheNoticeLinkOpensTheDialogOnTheVideoSection()
    {
        var tab = TabWithoutFfmpeg();
        W.Commands.Execute("video.detect_silence");
        _editor.Tick(2);
        var link = W.VideoPanel.Document.GetElementById("video-notice-link");
        Assert.Equal("Editor Settings", link.InnerRml);
        Assert.False(link.Bounds.Width <= 0);
        Assert.False(W.EditorSettingsDialog.Visible);

        link.Click();
        _editor.Tick(3);

        Assert.True(W.EditorSettingsDialog.Visible);
        Assert.True(SectionIsInView(), SectionPlace());
        Assert.Empty(_editor.RmlMessages);

        // The link goes with the message: any other notice drops it.
        W.EditorSettingsDialog.Cancel();
        tab.Notice = "Detected 2 silences.";
        _editor.Tick(2);
        Assert.False(W.VideoPanel.NoticeHasSettingsLink);
    }

    [Fact]
    public void OpeningWithoutFfmpegOffersTheSettingsOnTheMessage()
    {
        W.Commands.LocateFfmpeg = _ => throw new FfmpegMissingException("ffmpeg is not installed.");
        W.Commands.OpenVideo(Path.Combine(_dir, "none.mp4"));

        var request = W.Message.Current;
        Assert.NotNull(request);
        Assert.Contains("The video editor needs ffmpeg", request.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg is not installed.", request.Message, StringComparison.Ordinal);
        Assert.Equal(["Video Settings…", "OK"], request.Buttons);
        Assert.Null(W.Session.ActiveVideo);

        W.Message.Answer(0);
        _editor.Tick(3);
        Assert.True(W.EditorSettingsDialog.Visible);
        Assert.True(SectionIsInView(), SectionPlace());
    }

    [Fact]
    public void TheEditMenuOpensTheVideoSettings()
    {
        W.SetMode(EditorMode.Video);
        var item = Assert.Single(W.Commands.MenuItems("edit"), i => i.Command == "video.settings");
        Assert.True(item.Enabled);

        Assert.True(W.Commands.Execute("video.settings"));
        _editor.Tick(3);
        Assert.True(W.EditorSettingsDialog.Visible);
        Assert.True(SectionIsInView(), SectionPlace());
    }

    // ── The dialog ───────────────────────────────────────────────────────────────────────────────────────────────

    private (float Top, float Bottom) BodyRange()
    {
        var bounds = W.EditorSettingsDialog.Document.GetElementById("es-body").Bounds;
        return (bounds.Y, bounds.Y + bounds.Height);
    }

    private string SectionPlace()
    {
        var section = W.EditorSettingsDialog.Document.GetElementById(EditorSettingsDialog.VideoSectionId).Bounds;
        var (top, bottom) = BodyRange();
        return $"the Video section is at {section.Y} ({section.Height} high), the dialog body spans {top} to {bottom}";
    }

    private bool SectionIsInView()
    {
        var section = W.EditorSettingsDialog.Document.GetElementById(EditorSettingsDialog.VideoSectionId).Bounds;
        var (top, bottom) = BodyRange();
        return section.Height > 0 && section.Y >= top - 1 && section.Y < bottom;
    }

    [Fact]
    public void TheDialogKeepsItsSizeAndTheBodyScrollsToTheVideoSection()
    {
        W.EditorSettingsDialog.Open();
        _editor.Tick(3);
        var dialog = W.EditorSettingsDialog.Document.GetElementById("dialog").Bounds;
        var scale = W.Host.PixelScale;
        Assert.InRange(dialog.Height, 540 * scale, 544 * scale); // its size plus the border
        Assert.InRange(dialog.Width, 560 * scale, 564 * scale);
        Assert.False(SectionIsInView(), SectionPlace()); // below the fold until asked for

        W.EditorSettingsDialog.Cancel();
        W.EditorSettingsDialog.Open(scrollToVideo: true);
        _editor.Tick(3);
        Assert.True(SectionIsInView(), SectionPlace());
        var scrolled = W.EditorSettingsDialog.Document.GetElementById("dialog").Bounds;
        Assert.Equal(dialog.Height, scrolled.Height, 1); // the dialog did not grow
        Assert.Equal(dialog.Width, scrolled.Width, 1);

        // A plain Open starts at the top again, whatever the last visit scrolled to.
        W.EditorSettingsDialog.Cancel();
        W.EditorSettingsDialog.Open();
        _editor.Tick(3);
        Assert.False(SectionIsInView(), SectionPlace());
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TheVideoSectionOffersEveryModelWithItsSize()
    {
        W.EditorSettingsDialog.Open();
        _editor.Tick(2);
        var select = W.EditorSettingsDialog.Document.GetElementById("es-video-model");
        Assert.False(select.IsNull);
        Assert.Equal("base.en", select.Value); // the working copy's model is the one selected

        // Every model is an option the select accepts (it keeps the old value for one it does not have).
        foreach (var model in WhisperModelInfo.All)
        {
            select.SetValue(model.Key);
            Assert.Equal(model.Key, select.Value);
        }

        Assert.Equal("base.en (148 MB)", EditorSettingsDialog.ModelLabel(WhisperModelInfo.Default));
        Assert.Equal("tiny.en (78 MB)", EditorSettingsDialog.ModelLabel(WhisperModelInfo.For(WhisperModel.TinyEn)));
        Assert.Equal("medium.en (1534 MB)", EditorSettingsDialog.ModelLabel(WhisperModelInfo.For(WhisperModel.MediumEn)));
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void ApplyingTheDialogTakesTheVideoSettings()
    {
        W.EditorSettingsDialog.Open(scrollToVideo: true);
        _editor.Tick(2);
        var working = W.EditorSettingsDialog.Working;
        working.VideoFfmpegPath = "/opt/ff";
        working.VideoWhisperModel = "small.en";
        working.VideoFillerWords = ["um", "uh", "er"];
        working.VideoPaddingMs = 90;
        working.VideoGameplayMarkerWindowMs = 2000;
        working.VideoUndoHistoryLimit = 50;
        W.EditorSettingsDialog.Apply();

        Assert.Equal("/opt/ff", W.Settings.VideoFfmpegPath);
        Assert.Equal("small.en", W.Settings.VideoWhisperModel);
        Assert.Equal(["um", "uh", "er"], W.Settings.VideoFillerWords);
        Assert.Equal(90, W.Settings.VideoDetection.PaddingMs);
        Assert.Equal(2000, W.Settings.VideoGameplay.MarkerWindowMs);
        Assert.Equal(50, W.Settings.VideoUndoHistoryLimit);
        Assert.False(W.EditorSettingsDialog.Visible);
        Assert.Empty(_editor.RmlMessages);
    }
}
