using System.Numerics;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Preview;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

[Collection(nameof(SerialEditor))]
public sealed class VideoTabTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly HeadlessEditor _editor = new();
    private readonly List<string> _dirs = [];

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose()
    {
        _editor.Dispose();
        foreach (var dir in _dirs)
            Directory.Delete(dir, recursive: true);
    }

    private string VideoProject(string name = "trim", string extension = ".mp4")
    {
        var dir = Directory.CreateTempSubdirectory("video-tab-").FullName; // recordings live outside any project
        _dirs.Add(dir);
        var video = Path.Combine(dir, name + extension);
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(dir, name + ".mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    [Fact]
    public void OpeningAProjectAddsAnActiveVideoTabAndSwitchesMode()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        Assert.Same(tab, W.Session.ActiveTab);
        Assert.Same(tab, W.Session.ActiveVideo);
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Equal("trim.mfcut", tab.Title);
    }

    [Fact]
    public void OpeningTheSameProjectTwiceActivatesTheFirstTab()
    {
        var path = VideoProject();
        var first = W.Session.OpenVideo(path, NoFfmpeg, 100);
        W.SetMode(EditorMode.Game);
        Assert.Same(first, W.Session.OpenVideo(path, NoFfmpeg, 100));
        Assert.Single(W.Session.Tabs.OfType<VideoTab>());
    }

    [Fact]
    public void ARecordingOutsideTheProjectDoesNotChangeTheProjectRoot()
    {
        var root = W.Session.ProjectRoot;
        W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        Assert.Equal(root, W.Session.ProjectRoot);
    }

    [Fact]
    public void EditsShowAStarAndSavingClearsIt()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        Assert.Equal("trim.mfcut*", tab.Title);
        W.Session.SaveVideo(tab);
        Assert.Equal("trim.mfcut", tab.Title);
    }

    [Fact]
    public void UndoAndRedoCommandsReachTheVideoTab()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Commands.Execute("edit.undo");
        Assert.Single(tab.Document.Segments.Segments);
        W.Commands.Execute("edit.redo");
        Assert.Equal(3, tab.Document.Segments.Count);
    }

    [Fact]
    public void LeavingVideoModePausesPlayback()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.TogglePlay(); // its decoder fails without ffmpeg, which only logs
        W.SetMode(EditorMode.Game);
        Assert.False(tab.Preview.IsPlaying);
    }

    [Fact]
    public void ClosingTheTabFreesItsViewports()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        W.Session.Close(tab);
        Assert.Empty(W.Session.Tabs.OfType<VideoTab>());
        Assert.True(tab.TimelineViewport.IsFreed);
        Assert.True(tab.Preview.Viewport.IsFreed);
    }

    [Fact]
    public void ClosingADirtyVideoTabAsksToSave()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Commands.CloseTab(tab);
        Assert.True(W.Message.Visible);
        Assert.Equal(["Save", "Don't Save", "Cancel"], W.Message.Current!.Buttons);
    }

    [Fact]
    public void RecentVideosAreMostRecentFirstAndCapped()
    {
        var s = new EditorSettings();
        for (var i = 0; i < 12; i++)
            EditorCommands.TouchRecent(s.RecentVideos, $"/r/{i}.mp4");
        Assert.Equal(10, s.RecentVideos.Count);
        Assert.Equal("/r/11.mp4", s.RecentVideos[0]);
        EditorCommands.TouchRecent(s.RecentVideos, "/r/5.mp4");
        Assert.Equal("/r/5.mp4", s.RecentVideos[0]);
        Assert.Equal(10, s.RecentVideos.Count);
    }

    [Theory]
    [InlineData("/opt/ff", "/env/ff", "/opt/ff")]
    [InlineData("  ", "/env/ff", "/env/ff")]
    [InlineData(null, "", null)]
    public void TheFfmpegSettingWinsOverTheEnvironment(string? setting, string? env, string? expected) =>
        Assert.Equal(expected, VideoFfmpeg.ConfiguredPath(setting, env));

    [Theory]
    [InlineData("a.mp4", FileKind.Video)]
    [InlineData("a.MOV", FileKind.Video)]
    [InlineData("a.webm", FileKind.Video)]
    [InlineData("a.mfcut", FileKind.VideoProject)]
    [InlineData("a.cutback", FileKind.VideoProject)]
    public void RecordingsAndVideoProjectsHaveTheirOwnFileKinds(string path, FileKind kind) =>
        Assert.Equal(kind, FileKinds.Of(path, isDirectory: false));

    // ── Beyond the brief: the controller's rulings and the rest of the wiring ──────────────────────────────────

    [Theory]
    [InlineData("/r/trim.mp4", "/r/trim.mfcut")]
    [InlineData("/r/trim.cutback", "/r/trim.mfcut")]
    [InlineData("/r/trim", "/r/trim.mfcut")]
    [InlineData("/r/trim.mfcut", "/r/trim.mfcut")]
    [InlineData("/r/trim.mfcut.mfcut", "/r/trim.mfcut")]
    [InlineData("/r/trim.MFCUT", "/r/trim.MFCUT")]
    public void AProjectPathAlwaysEndsInOneMfcut(string path, string expected) =>
        Assert.Equal(Path.GetFullPath(expected), VideoTab.NormalizeProjectPath(path));

    [Fact]
    public void SavingAsTheRecordingsNameWritesAProjectAndLeavesTheVideoAlone()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        var video = tab.Document.SourcePath;
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Session.SaveVideo(tab, video); // "trim.mp4": saved as trim.mfcut
        Assert.Equal([0], File.ReadAllBytes(video));
        Assert.Equal(Path.ChangeExtension(video, ".mfcut"), tab.Document.FilePath);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task SaveAsRefusesTheRecordingItself()
    {
        // A source whose own name already ends in .mfcut is the one path normalisation leaves pointing at the video.
        var dir = Path.GetDirectoryName(VideoProject())!;
        var video = Path.Combine(dir, "clip.mfcut");
        File.WriteAllBytes(video, [0]);
        var project = Path.Combine(dir, "other.mfcut");
        await ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), project, TestContext.Current.CancellationToken);
        var tab = W.Session.OpenVideo(project, NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);

        W.Commands.Execute("file.save_as");
        W.FilePicker.Model!.FileName = video;
        W.FilePicker.Accept();
        Assert.Equal("Replace file?", W.Message.Current?.Title);
        W.Message.Answer(0); // "Replace file?" — yes
        Assert.Equal("Error", W.Message.Current?.Title);
        Assert.Equal([0], File.ReadAllBytes(video));
        Assert.True(tab.IsDirty);
        Assert.Equal(project, tab.Document.FilePath);
    }

    [Fact]
    public void UndoDuringABoundaryDragRevertsExactlyThatDrag()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10); // boundaries at 10 s and 20 s
        tab.View.Width = 600; // 10 dp per second
        tab.Controller.Press(new Vector2(100, 50), EditorModifiers.None);
        tab.Controller.Drag(new Vector2(150, 50), EditorModifiers.None);
        Assert.True(tab.Document.Editor.IsDragging);
        Assert.Equal(15, tab.Document.Segments.Segments[1].Start, 9);

        W.Commands.Execute("edit.undo");
        Assert.False(tab.Document.Editor.IsDragging);
        Assert.Equal(VideoGesture.None, tab.Controller.Gesture);
        Assert.Equal(10, tab.Document.Segments.Segments[1].Start, 9);
        Assert.Equal(3, tab.Document.Segments.Count);

        W.Commands.Execute("edit.undo"); // the section before it is still there
        Assert.Single(tab.Document.Segments.Segments);
        W.Commands.Execute("edit.redo");
        W.Commands.Execute("edit.redo"); // and the drag was recorded as one step
        Assert.Equal(15, tab.Document.Segments.Segments[1].Start, 9);
    }

    [Fact]
    public void ClosingTheOnlyVideoTabStaysInVideoModeOnTheEmptyState()
    {
        W.Session.NewScene(); // two scenes
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        W.Session.Close(tab);
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Null(W.Session.ActiveTab);
        Assert.Equal(2, W.Session.Scenes.Count);
    }

    [Fact]
    public void ClosingOneOfTwoVideoTabsActivatesTheOther()
    {
        var first = W.Session.OpenVideo(VideoProject("a"), NoFfmpeg, 100);
        var second = W.Session.OpenVideo(VideoProject("b"), NoFfmpeg, 100);
        W.Session.Close(second);
        Assert.Same(first, W.Session.ActiveTab);
        Assert.Equal(EditorMode.Video, W.Mode);
    }

    [Fact]
    public void ClosingTheLastSceneDoesNotSwitchToAVideoTab()
    {
        var video = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        W.SetMode(EditorMode.Game);
        W.Session.Close(W.Session.Active!);
        Assert.Equal(EditorMode.Game, W.Mode);
        Assert.Null(W.Session.ActiveTab);
        Assert.Contains(video, W.Session.Tabs);
    }

    [Fact]
    public void DraggingWhilePlayingDoesNotRestartThePreviewUntilTheDragEnds()
    {
        var factory = new CountingFactory(320 * 180 * 4);
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, []);
        document.Editor.CreateSection(10, 20, anchor: 10);
        var preview = new VideoPreview(factory, () => null, "/r/a.mp4", 320, 180, 30, number: 1);
        using var tab = new VideoTab(document, preview, ffmpeg: null, number: 1);
        tab.View.Width = 600;
        tab.TogglePlay();
        tab.Tick();
        Assert.True(tab.Preview.IsPlaying);
        var starts = factory.Count;

        tab.Controller.Press(new Vector2(100, 50), EditorModifiers.None);
        for (var i = 1; i <= 5; i++)
        {
            tab.Controller.Drag(new Vector2(100 + i * 5, 50), EditorModifiers.None);
            tab.Tick();
        }

        Assert.Equal(starts, factory.Count);
        Assert.True(tab.Preview.IsPlaying);

        tab.Controller.Release(new Vector2(125, 50));
        tab.Tick();
        Assert.Equal(starts + 1, factory.Count); // one restart, for the finished drag
    }

    [Fact]
    public void TheWaveformJobFailingLeavesANotice()
    {
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, ["Source changed."]);
        var preview = new VideoPreview(new CountingFactory(4), () => null, "/r/a.mp4", 320, 180, 30, number: 1);
        using var tab = new VideoTab(document, preview, NoFfmpeg, number: 1); // /x/ffmpeg cannot start
        var job = tab.WaveformJob;
        Assert.NotNull(job);
        Assert.True(SpinWait.SpinUntil(() => job.IsDone, TimeSpan.FromSeconds(10)));
        tab.Tick();
        Assert.Null(tab.WaveformJob);
        Assert.Null(document.Waveform);
        Assert.StartsWith("Source changed. Waveform unavailable: ", tab.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecentVideoOpensAndAMissingOneIsForgotten()
    {
        var project = VideoProject();
        var missing = Path.Combine(Path.GetDirectoryName(project)!, "gone.mp4");
        var settings = W.Settings.Clone();
        settings.VideoFfmpegPath = FakeFfmpegFolder();
        settings.RecentVideos = [missing, project];
        W.ApplySettings(settings);

        W.Commands.Execute("video.recent:0");
        Assert.Equal("gone.mp4 was moved or deleted.", W.Message.Current?.Message);
        Assert.Equal([project], W.Settings.RecentVideos);
        W.Message.Answer(0);

        W.Commands.Execute("video.recent:0");
        Assert.Equal(project, W.Session.ActiveVideo?.FilePath);
        Assert.Equal(EditorMode.Video, W.Mode);
    }

    [Fact]
    public void OpenAnySendsRecordingsToTheVideoEditorAndRemembersThem()
    {
        var settings = W.Settings.Clone();
        settings.VideoFfmpegPath = FakeFfmpegFolder();
        W.ApplySettings(settings);
        var project = VideoProject();
        W.Commands.OpenAny(project);
        Assert.NotNull(W.Session.ActiveVideo);
        Assert.Equal(project, W.Settings.RecentVideos[0]);
    }

    [Fact]
    public void TheVideoFileMenuListsTheRecentVideos()
    {
        var settings = W.Settings.Clone();
        settings.RecentVideos = [.. Enumerable.Range(0, 7).Select(i => $"/r/{i}.mp4")];
        W.ApplySettings(settings);
        W.SetMode(EditorMode.Video);
        var recent = W.Commands.MenuItems("file").Where(i => i.Command?.StartsWith("video.recent:", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(5, recent.Count);
        Assert.Equal("0.mp4", recent[0].Label);
        Assert.All(recent, i => Assert.Equal("video", i.Icon));
        Assert.Contains(W.Commands.MenuItems("file"), i => i is { IsHeader: true, Label: "Recent Videos" });
    }

    [Fact]
    public void FileOpenInVideoModeAsksForARecording()
    {
        W.SetMode(EditorMode.Video);
        W.Commands.Execute("file.open");
        Assert.Equal(["*.mp4", "*.mov", "*.mkv", "*.webm", "*.mfcut", "*.cutback"], W.FilePicker.Model!.Filter);
        W.FilePicker.Cancel();
        W.SetMode(EditorMode.Game);
        W.Commands.Execute("file.open");
        Assert.Equal(["*.mscene", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.mfcut", "*.cutback"], W.FilePicker.Model!.Filter);
    }

    [Fact]
    public void TheVideoSettingsSurviveASaveAndAClone()
    {
        var path = Path.Combine(_editor.Directory, "editor_settings.json");
        var settings = new EditorSettings { VideoFfmpegPath = "/opt/ffmpeg", RecentVideos = ["/r/a.mfcut", "/r/b.mp4"] };
        settings.Save(path);
        var loaded = EditorSettings.Load(path);
        Assert.Equal("/opt/ffmpeg", loaded.VideoFfmpegPath);
        Assert.Equal(["/r/a.mfcut", "/r/b.mp4"], loaded.RecentVideos);
        var clone = loaded.Clone();
        clone.RecentVideos.Add("/r/c.mp4");
        Assert.Equal(2, loaded.RecentVideos.Count);
        Assert.Equal("/opt/ffmpeg", clone.VideoFfmpegPath);
    }

    [Fact]
    public void ADirtyVideoTabBadgesNothingButRetitlesTheStrip()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        var changes = 0;
        W.Session.ScenesChanged += () => changes++;
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        tab.Document.Editor.CreateSection(30, 40, anchor: 30); // still dirty: no second event
        Assert.Equal(1, changes);
        Assert.Equal("icon icon-video icon-audio", tab.IconClasses);
        Assert.Contains("unsaved changes", tab.Tooltip, StringComparison.Ordinal);
    }

    // An ffmpeg/ffprobe pair the locator accepts (it only checks that the files exist); nothing ever runs them.
    private string FakeFfmpegFolder()
    {
        var dir = Directory.CreateTempSubdirectory("video-ffmpeg-").FullName;
        _dirs.Add(dir);
        var locator = new FfmpegLocator();
        File.WriteAllBytes(Path.Combine(dir, locator.ExecutableName("ffmpeg")), []);
        File.WriteAllBytes(Path.Combine(dir, locator.ExecutableName("ffprobe")), []);
        return dir;
    }

    internal sealed class CountingFactory(int frameBytes) : IVideoDecoderFactory
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            Interlocked.Increment(ref _count);
            return new Decoder(new byte[frameBytes * 90]);
        }

        private sealed class Decoder(byte[] bytes) : IVideoDecoder
        {
            public Stream Output { get; } = new MemoryStream(bytes);
            public bool Failed => false;
            public void Dispose() { } // the reader may still be reading; a killed ffmpeg only ends the stream
        }
    }
}
