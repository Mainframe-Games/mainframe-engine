using System.Numerics;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Preview;
using MainframeEngine.Video.Projects;
using Silk.NET.Input;
using VideoCapture = MainframeEngine.Video.Capture;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// The video-mode screen on the headless editor: the panel's layout and notices, the toolbar's labels and disabled
/// states, the keys and pointer routed by <see cref="VideoView"/>, and Detect Idle Gameplay and Detect Silence.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class VideoWorkspaceTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly HeadlessEditor _editor = new();
    private readonly List<string> _dirs = [];
    private int _fakeTabs;

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose()
    {
        _editor.Dispose();
        foreach (var dir in _dirs)
            Directory.Delete(dir, recursive: true);
    }

    // A .mfcut beside a one-byte "recording", opened through the session as File › Open would (its decoders never run).
    private VideoTab OpenProject(bool sidecar = false)
    {
        var dir = Directory.CreateTempSubdirectory("video-ws-").FullName;
        _dirs.Add(dir);
        var video = Path.Combine(dir, "trim.mp4");
        File.WriteAllBytes(video, [0]);
        if (sidecar)
            File.WriteAllText(video + VideoCapture.CaptureSidecar.FileSuffix, VideoDocumentTests.Sidecar);
        var path = Path.Combine(dir, "trim.mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        var tab = W.Session.OpenVideo(path, NoFfmpeg, 100);
        _editor.Tick(2);
        return tab;
    }

    // A tab whose preview decodes from memory (no ffmpeg), so playback really starts and keeps going.
    private VideoTab FakeTab(FfmpegLocation? ffmpeg = null)
    {
        var number = 900 + ++_fakeTabs;
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, []);
        var preview = new VideoPreview(new VideoTabTests.CountingFactory(320 * 180 * 4), () => null, "/r/a.mp4", 320, 180, 30, number);
        var tab = new VideoTab(document, preview, ffmpeg, number);
        W.ScenesHost.AddChild(tab.TimelineViewport);
        W.ScenesHost.AddChild(preview.Viewport);
        W.Session.Add(tab);
        _editor.Tick(2);
        return tab;
    }

    private RmlElementProbe Toolbar(string id) => new(W.Toolbar.Document.GetElementById(id));

    private readonly record struct RmlElementProbe(UI.Rml.RmlElement Element)
    {
        public string Text => Element.InnerRml;
        public bool Disabled => Element.IsClassSet("disabled");
        public string? Tooltip => Element.GetAttribute("data-tooltip");
    }

    private void Push(InputEvent e) => _editor.Tree.PushInput(e);

    private Vector2 InTimeline(float x, float y) => new(W.VideoPanel.TimelineRect.X + x, W.VideoPanel.TimelineRect.Y + y);

    [Fact]
    public void OpeningAVideoShowsTheVideoScreen()
    {
        var tab = OpenProject();
        Assert.True(W.VideoPanel.Visible);
        Assert.False(W.Toolbar.Document.GetElementById("video-tools").IsClassSet("hidden"));
        Assert.Same(tab, W.VideoView.Tab);
        Assert.True(W.VideoPanel.PreviewRect.Width > 1 && W.VideoPanel.PreviewRect.Height > 1);
        Assert.Equal(180, W.VideoPanel.TimelineRect.Height, 0.5);
        Assert.Equal(320, W.VideoPanel.TranscriptRect.Width, 0.5);
        Assert.Equal(W.VideoPanel.TimelineRect.Width, tab.View.Width, 0.5);
        Assert.False(Toolbar("video-play").Disabled);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void WithoutATabTheVideoToolsAreDisabledAndIgnored()
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        foreach (var id in (string[])["video-play", "video-stop", "video-transcribe", "video-fillers", "video-silence", "video-idle", "video-export"])
            Assert.True(Toolbar(id).Disabled, id);
        Assert.Null(W.VideoView.Tab);
        Assert.True(W.Commands.Execute("video.detect_idle"));
        Assert.True(W.Commands.Execute("video.play"));
        Assert.False(W.Message.Visible);
    }

    [Fact]
    public void DetectIdleIsDisabledWithoutASidecar()
    {
        var tab = OpenProject();
        Assert.False(W.VideoPanel.CanDetectIdle);
        Assert.True(Toolbar("video-idle").Disabled);
        Assert.Equal("Needs a Mainframe Engine recording (its .mfcap.json sidecar)", Toolbar("video-idle").Tooltip);
        Assert.False(Toolbar("video-fillers").Disabled); // it transcribes first

        W.Commands.Execute("video.detect_idle");
        Assert.Equal("This recording has no Mainframe Engine sidecar.", tab.Notice);
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void DetectIdleCutsAndSetsTheNotice()
    {
        var tab = OpenProject(sidecar: true);
        Assert.True(W.VideoPanel.CanDetectIdle);
        Assert.False(Toolbar("video-idle").Disabled);

        W.Commands.Execute("video.detect_idle");
        Assert.StartsWith("Cut ", tab.Notice, StringComparison.Ordinal);
        Assert.True(tab.Document.Segments.Count > 1);
        _editor.Tick();
        Assert.Equal(tab.Notice, W.VideoPanel.NoticeText);

        W.Commands.Execute("edit.undo");
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void SpaceTogglesPlaybackAndDeleteDissolvesTheHoveredRegion()
    {
        var tab = FakeTab();
        _editor.Key(Key.Space);
        Assert.True(tab.Preview.IsPlaying); // once: the Space shortcut did not toggle it back
        _editor.Key(Key.Space);
        Assert.False(tab.Preview.IsPlaying);

        tab.Document.Editor.CreateSection(10, 20, 10);
        var count = tab.Document.Segments.Count;
        tab.State.HoverSegment = 1;
        _editor.Key(Key.Delete);
        Assert.True(tab.Document.Segments.Count < count);
    }

    [Fact]
    public void HomeReturnsToTheStart()
    {
        var tab = FakeTab();
        tab.Seek(12);
        _editor.Key(Key.Home);
        Assert.Equal(0, tab.State.Playhead);
    }

    [Fact]
    public void TheKeptLabelFollowsEdits()
    {
        var tab = OpenProject();
        Assert.Equal("Kept 1:00.0 of 1:00.0", W.VideoPanel.Kept);
        Assert.Equal("Kept 1:00.0 of 1:00.0", Toolbar("video-kept").Text);
        tab.Document.Editor.CreateSection(10, 20, 10);
        _editor.Tick();
        Assert.Equal("Kept 0:50.0 of 1:00.0", W.VideoPanel.Kept);
        Assert.Equal("Kept 0:50.0 of 1:00.0", Toolbar("video-kept").Text);
    }

    [Fact]
    public void ThePositionShowsThePlayheadAndTheDuration()
    {
        var tab = FakeTab();
        Assert.Equal("0:00.0 / 1:00.0", Toolbar("video-pos").Text);
        tab.Seek(12.34);
        _editor.Tick();
        Assert.Equal("0:12.3 / 1:00.0", Toolbar("video-pos").Text);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.84)]
    [InlineData(59.96)]
    [InlineData(65.31)]
    [InlineData(3725.0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void TheSpanClockMatchesTheStringClock(double seconds)
    {
        Span<char> text = stackalloc char[24];
        var n = VideoFormat.FormatClock(text, seconds);
        Assert.Equal(VideoFormat.Clock(seconds), text[..n].ToString());
    }

    [Fact]
    public void HidingTheTranscriptGivesThePreviewTheWidth()
    {
        OpenProject();
        var before = W.VideoPanel.PreviewRect.Width;
        Assert.True(W.VideoPanel.TranscriptRect.Width > 0);
        W.Commands.Execute("video.toggle_transcript");
        _editor.Tick(2);
        Assert.Equal(0, W.VideoPanel.TranscriptRect.Width);
        Assert.True(W.VideoPanel.PreviewRect.Width > before);
        Assert.False(W.Layout.Settings.VideoShowTranscript);
    }

    [Fact]
    public void ViewOutputShowsTheOutputPanelInVideoMode()
    {
        OpenProject();
        Assert.False(W.OutputPanel.Visible);
        W.Commands.Execute("view.output");
        Assert.True(W.OutputPanel.Visible);
        Assert.True(W.Layout.Settings.VideoShowOutput);
        W.Commands.Execute("view.output");
        Assert.False(W.OutputPanel.Visible);
    }

    [Fact]
    public void TheVideoLayoutSettersClampAndPersist()
    {
        OpenProject();
        W.Layout.SetVideoTranscriptWidth(10);
        Assert.Equal(EditorLayout.MinVideoTranscriptWidth, W.Layout.Settings.VideoTranscriptWidth);
        W.Layout.SetVideoTranscriptWidth(400);
        W.Layout.SetVideoTimelineHeight(5000);
        Assert.True(W.Layout.Settings.VideoTimelineHeight < W.Layout.ViewportImage.Height);
        W.Layout.SetVideoTimelineHeight(220);
        W.Layout.SetVideoShowTranscript(false);
        W.SaveLayout();
        var saved = EditorLayout.Load(Path.Combine(_editor.Directory, "layout.json"));
        Assert.Equal(400, saved.VideoTranscriptWidth);
        Assert.Equal(220, saved.VideoTimelineHeight);
        Assert.False(saved.VideoShowTranscript);

        W.Layout.SetVideoShowTranscript(true);
        _editor.Tick(2);
        Assert.Equal(220, W.VideoPanel.TimelineRect.Height, 0.5);
        Assert.Equal(400, W.VideoPanel.TranscriptRect.Width, 0.5);
    }

    [Fact]
    public void TheViewportsRenderAgainAfterVisitingGameMode()
    {
        var tab = OpenProject();
        Assert.Equal(SubViewportUpdateMode.Always, tab.TimelineViewport.UpdateMode);
        Assert.Equal(SubViewportUpdateMode.Always, tab.Preview.Viewport.UpdateMode);
        W.SetMode(EditorMode.Game);
        _editor.Tick(2);
        Assert.Equal(SubViewportUpdateMode.Disabled, tab.TimelineViewport.UpdateMode);
        Assert.Equal(SubViewportUpdateMode.Disabled, tab.Preview.Viewport.UpdateMode);
        Assert.Null(W.VideoView.Tab);

        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        Assert.Same(tab, W.VideoView.Tab);
        Assert.Equal(SubViewportUpdateMode.Always, tab.TimelineViewport.UpdateMode);
        Assert.Equal(SubViewportUpdateMode.Always, tab.Preview.Viewport.UpdateMode);
    }

    [Fact]
    public void LeavingTheTimelineClearsTheHover()
    {
        var tab = OpenProject();
        Push(new InputEventMouseMotion { Position = InTimeline(100, 80) });
        Assert.Equal(0, tab.State.HoverSegment);
        Push(new InputEventMouseMotion { Position = InTimeline(100, -40) }); // over the preview
        Assert.Equal(-1, tab.State.HoverSegment);
        _editor.Key(Key.Delete);
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void ADragThatLeavesTheTimelineKeepsDrawingUntilRelease()
    {
        var tab = OpenProject();
        var width = W.VideoPanel.TimelineRect.Width;
        Push(new InputEventMouseMotion { Position = InTimeline(width * 0.25f, 80) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = InTimeline(width * 0.25f, 80) });
        Push(new InputEventMouseMotion { Position = InTimeline(width * 0.5f, -60) });
        Assert.Equal(VideoGesture.Section, tab.Controller.Gesture);
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = InTimeline(width * 0.5f, -60) });
        Assert.Equal(VideoGesture.None, tab.Controller.Gesture);
        Assert.Equal(3, tab.Document.Segments.Count);
        Assert.False(tab.Document.Segments.Segments[1].Enabled);
    }

    [Fact]
    public void OnlyTheLeftButtonStartsAGestureAndTheRightOneOpensTheSegmentMenu()
    {
        var tab = OpenProject();
        Push(new InputEventMouseMotion { Position = InTimeline(100, 80) });
        Push(new InputEventMouseButton { Button = MouseButton.Middle, Pressed = true, Position = InTimeline(100, 80) });
        Assert.Equal(VideoGesture.None, tab.Controller.Gesture);
        Push(new InputEventMouseButton { Button = MouseButton.Middle, Pressed = false, Position = InTimeline(100, 80) });

        Push(new InputEventMouseButton { Button = MouseButton.Right, Pressed = true, Position = InTimeline(100, 80) });
        Assert.Equal(VideoGesture.None, tab.Controller.Gesture);
        Assert.True(W.Popup.Visible);
        Assert.Equal(["edit.delete"], W.Popup.Items.Select(i => i.Command));
        W.Popup.Close();
        Assert.Single(tab.Document.Segments.Segments); // a right click never toggles
    }

    [Fact]
    public void TheWheelOverTheTimelineZooms()
    {
        var tab = OpenProject();
        var span = tab.View.Span;
        Push(new InputEventMouseMotion { Position = InTimeline(100, 80) });
        Push(new InputEventMouseWheel { Delta = new Vector2(0, 1) });
        Assert.NotEqual(span, tab.View.Span);
    }

    [Fact]
    public void DetectSilenceWithoutFfmpegReportsIt()
    {
        var tab = FakeTab();
        W.Commands.Execute("video.detect_silence");
        Assert.True(W.Message.Visible);
        Assert.Contains("ffmpeg not found", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Null(tab.SilenceJob);
    }

    [Fact]
    public void AFinishedSilenceJobCutsTheSilencesAndSetsTheNotice()
    {
        var tab = FakeTab();
        IReadOnlyList<SilenceSpan> spans = [new SilenceSpan(10, 20)];
        tab.SilenceJob = VideoJob.Start("silence", (p, ct) => Task.FromResult(spans));
        Assert.True(SpinWait.SpinUntil(() => tab.SilenceJob.IsDone, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.SilenceJob);
        Assert.StartsWith("Detected 1 silence", tab.Notice, StringComparison.Ordinal);
        Assert.Equal(3, tab.Document.Segments.Count);
        W.Commands.Execute("edit.undo");
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void DetectSilenceAgainCancelsTheRunningJob()
    {
        var tab = FakeTab();
        var job = VideoJob.Start("silence", async (p, ct) =>
        {
            p.Report(0.42);
            await Task.Delay(Timeout.Infinite, ct);
            return (IReadOnlyList<SilenceSpan>)[];
        });
        tab.SilenceJob = job;
        Assert.True(SpinWait.SpinUntil(() => job.Progress > 0.4, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Equal("Cancel 42 %", Toolbar("video-silence").Text);

        W.Commands.Execute("video.detect_silence");
        Assert.True(job.IsCancelled);
        Assert.True(SpinWait.SpinUntil(() => job.IsDone, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.SilenceJob);
        Assert.Equal("Silence detection cancelled.", tab.Notice);
        Assert.Equal("Detect silence", Toolbar("video-silence").Text);
        Assert.Single(tab.Document.Segments.Segments);
    }

    [Fact]
    public void AFailedSilenceJobIsReported()
    {
        var tab = FakeTab();
        tab.SilenceJob = VideoJob.Start<IReadOnlyList<SilenceSpan>>("silence", (p, ct) => throw new IOException("ffmpeg crashed"));
        Assert.True(SpinWait.SpinUntil(() => tab.SilenceJob.IsDone, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.SilenceJob);
        Assert.True(W.Message.Visible);
        Assert.Contains("Detect Silence failed", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg crashed", W.Message.Current!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptyStateListsTheRecentVideos()
    {
        var settings = W.Settings.Clone();
        settings.RecentVideos = ["/r/one.mp4", "/r/two.mfcut"];
        W.ApplySettings(settings);
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        var list = W.VideoPanel.Document.GetElementById("video-recent");
        Assert.Contains("one.mp4", list.InnerRml, StringComparison.Ordinal);
        Assert.Contains("two.mfcut", list.InnerRml, StringComparison.Ordinal);
        Assert.Contains("video.recent:1", list.InnerRml, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void IdleVideoFramesAllocateNothing()
    {
        FakeTab();
        _editor.Tick(120);
        var best = long.MaxValue;
        for (var window = 0; window < 3; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _editor.Tick(100);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best == 0, $"{best} B allocated over 100 idle video frames");
    }
}
