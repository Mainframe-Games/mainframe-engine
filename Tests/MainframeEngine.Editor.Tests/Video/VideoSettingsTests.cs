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
        // The ffprobe with no ffmpeg beside it: the sibling ffmpeg, which is not there, never the ffprobe as the encoder.
        var lonely = Directory.CreateDirectory(Path.Combine(_dir, "lonely")).FullName;
        File.WriteAllBytes(Path.Combine(lonely, probe), []);
        Assert.Equal(Path.Combine(lonely, exe), VideoFfmpeg.GameArgument(Path.Combine(lonely, probe)));
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
    public void ApplyKeepsTheModeSavedWhileTheDialogWasOpen()
    {
        W.EditorSettingsDialog.Open();
        _editor.Tick(2);

        W.SetMode(EditorMode.Music); // saved while the dialog is open
        W.EditorSettingsDialog.Apply();

        Assert.Equal(EditorMode.Music, W.Settings.LastMode);
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
        var unscrolled = SectionTop();

        W.EditorSettingsDialog.Cancel();
        W.EditorSettingsDialog.Open(scrollToVideo: true);
        _editor.Tick(3);
        Assert.True(SectionIsInView(), SectionPlace());
        Assert.True(SectionTop() < unscrolled - 10, SectionPlace()); // the body scrolled to it
        var scrolled = W.EditorSettingsDialog.Document.GetElementById("dialog").Bounds;
        Assert.Equal(dialog.Height, scrolled.Height, 1); // the dialog did not grow
        Assert.Equal(dialog.Width, scrolled.Width, 1);

        // A plain Open starts at the top again, whatever the last visit scrolled to.
        W.EditorSettingsDialog.Cancel();
        W.EditorSettingsDialog.Open();
        _editor.Tick(3);
        Assert.Equal(unscrolled, SectionTop(), 1);
        Assert.Empty(_editor.RmlMessages);
    }

    private float SectionTop() => W.EditorSettingsDialog.Document.GetElementById(EditorSettingsDialog.VideoSectionId).Bounds.Y;

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

        // The labels the select shows, and choosing one reaches the working copy.
        var labels = WhisperModelInfo.All.Select(EditorSettingsDialog.ModelLabel).ToList();
        Assert.Equal(["tiny.en (78 MB)", "base.en (148 MB)", "small.en (488 MB)", "medium.en (1534 MB)"], labels);
        var options = EditorSettingsDialog.ModelOptionsRml(); // RmlUi does not let a select's options be queried back
        for (var i = 0; i < labels.Count; i++)
            Assert.Contains($"<option value=\"{WhisperModelInfo.All[i].Key}\">{labels[i]}</option>", options, StringComparison.Ordinal);
        select.SetValue("small.en");
        _editor.Tick(2);
        Assert.Equal("small.en", W.EditorSettingsDialog.Working.VideoWhisperModel);
        Assert.Empty(_editor.RmlMessages);
    }

    // ── Typing into the fields ───────────────────────────────────────────────────────────────────────────────────

    // Types into the real, focused element through RmlUi, a frame after each character, as a person would. Returns what the
    // field showed after each one.
    private List<string> Type(string id, string text)
    {
        W.EditorSettingsDialog.Open(scrollToVideo: true);
        _editor.Tick(4);
        var context = W.EditorSettingsDialog.Layer!.Context!;
        var field = W.EditorSettingsDialog.Document.GetElementById(id);
        Assert.True(field.Focus());
        context.ProcessKeyDown(UI.Rml.RmlKey.A, UI.Rml.RmlKeyModifiers.Ctrl);
        context.ProcessKeyDown(UI.Rml.RmlKey.Back, UI.Rml.RmlKeyModifiers.None);
        _editor.Tick();
        Assert.Equal("", field.Value);

        var shown = new List<string>();
        foreach (var c in text)
        {
            Assert.True(context.ProcessTextInput(c.ToString()));
            _editor.Tick();
            shown.Add(field.Value ?? "");
        }

        return shown;
    }

    private void Commit(string id)
    {
        W.EditorSettingsDialog.Document.GetElementById(id).Blur();
        _editor.Tick(2);
    }

    [Fact]
    public void ANumberFieldKeepsWhatIsTypedUntilTheFieldIsLeft()
    {
        var shown = Type("es-video-min-silence", "100");
        Assert.Equal(["1", "10", "100"], shown); // not snapped to 50, 500 or 5000 on the way
        Commit("es-video-min-silence");
        Assert.Equal(100, W.EditorSettingsDialog.Working.VideoMinSilenceMs);
        Assert.Equal("100", W.EditorSettingsDialog.Document.GetElementById("es-video-min-silence").Value);
    }

    [Fact]
    public void ANumberFieldClampsWhenCommittedAndShowsTheValueThatStuck()
    {
        Type("es-video-padding", "99999");
        Commit("es-video-padding");
        Assert.Equal(500, W.EditorSettingsDialog.Working.VideoPaddingMs);
        Assert.Equal("500", W.EditorSettingsDialog.Document.GetElementById("es-video-padding").Value);
    }

    [Fact]
    public void ANumberFieldCommitsOnEnter()
    {
        Type("es-video-min-keep", "250");
        W.EditorSettingsDialog.Layer!.Context!.ProcessKeyDown(UI.Rml.RmlKey.Return, UI.Rml.RmlKeyModifiers.None);
        _editor.Tick(2);
        Assert.Equal(250, W.EditorSettingsDialog.Working.VideoMinKeepMs);
    }

    [Fact]
    public void TheThresholdFieldCanBeTypedWithItsMinusSign()
    {
        var shown = Type("es-video-threshold", "-40");
        Assert.Equal(["-", "-4", "-40"], shown);
        Commit("es-video-threshold");
        Assert.Equal(-40.0, W.EditorSettingsDialog.Working.VideoSilenceThresholdDb);
    }

    [Fact]
    public void FillerWordsCanBeTypedWithTrailingCommasAndSpaces()
    {
        var shown = Type("es-video-fillers", "um, uh, like");
        Assert.Equal("um, uh, like", shown[^1]);
        Assert.Equal("um, ", shown[3]); // the comma and the space after it stay while typing
        Commit("es-video-fillers");
        Assert.Equal(["um", "uh", "like"], W.EditorSettingsDialog.Working.VideoFillerWords);
        Assert.Equal("um, uh, like", W.EditorSettingsDialog.Document.GetElementById("es-video-fillers").Value);
    }

    [Fact]
    public void AnFfmpegPathWithSpacesSurvivesTyping()
    {
        const string Path = "C:\\Program Files\\ffmpeg\\bin";
        var shown = Type("es-video-ffmpeg", Path);
        Assert.Equal(Path, shown[^1]);
        Assert.Equal("C:\\Program ", shown[10]); // the trailing space is not trimmed away while typing
        Commit("es-video-ffmpeg");
        Assert.Equal(Path, W.EditorSettingsDialog.Working.VideoFfmpegPath);
    }

    [Fact]
    public void ChoosingAModelChangesTheWorkingModel()
    {
        W.EditorSettingsDialog.Open();
        _editor.Tick(2);
        var select = W.EditorSettingsDialog.Document.GetElementById("es-video-model");
        select.SetValue("small.en");
        _editor.Tick(2);
        Assert.Equal("small.en", W.EditorSettingsDialog.Working.VideoWhisperModel);
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
