using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Export;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Export Video on the headless editor: the dialog, starting the job, polling it and how it ends. The exporter is a fake
/// behind <see cref="VideoView.ExporterFactory"/>, so ffmpeg never runs and no file is ever encoded.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class ExportVideoDialogTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly string _dir = Directory.CreateTempSubdirectory("export-video-").FullName;
    private readonly List<string> _revealed = [];
    private readonly HeadlessEditor _editor;
    private int _tabs;

    public ExportVideoDialogTests() => _editor = new HeadlessEditor(configure: o => o with { Reveal = _revealed.Add });

    private EditorWorkspace W => _editor.Workspace;

    private ExportVideoDialog Dialog => W.ExportVideo;

    public void Dispose()
    {
        _editor.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static void SaveProject(string video, string path) =>
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();

    private VideoTab OpenTab()
    {
        var video = Path.Combine(_dir, $"talk{++_tabs}.mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(_dir, $"talk{_tabs}.mfcut");
        SaveProject(video, path);
        var tab = W.Session.OpenVideo(path, NoFfmpeg, 100);
        // The waveform job fails on the missing ffmpeg and adds a notice; let it finish so it cannot land after ours.
        Assert.True(SpinWait.SpinUntil(() => { _editor.Tick(); return tab.WaveformJob is null; }, TimeSpan.FromSeconds(10)));
        tab.Notice = null;
        return tab;
    }

    // The runner every export in these tests uses: records the request and waits for the test to release it.
    private sealed class Gate
    {
        public TaskCompletionSource Release { get; } = new();
        public ExportRequest? Request { get; private set; }
        public int Runs { get; private set; }

        public VideoExportRunner Runner(Func<IProgress<ExportProgress>, CancellationToken, Task>? body = null) => async (request, progress, ct) =>
        {
            Request = request;
            Runs++;
            if (body is not null)
                await body(progress, ct);
            await Release.Task.WaitAsync(ct);
        };
    }

    private Gate UseGate(Func<IProgress<ExportProgress>, CancellationToken, Task>? body = null)
    {
        var gate = new Gate();
        W.VideoView.ExporterFactory = (_, _) => gate.Runner(body);
        return gate;
    }

    private void FinishExport(VideoTab tab)
    {
        Assert.True(SpinWait.SpinUntil(() => tab.ExportJob is not { IsDone: false }, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.ExportJob);
    }

    [Fact]
    public void TheDialogDefaultsToPreciseNextToTheRecording()
    {
        var tab = OpenTab();
        W.Commands.Execute("video.export");
        _editor.Tick(2);

        Assert.True(Dialog.Visible);
        Assert.True(W.IsDialogOpen);
        Assert.Equal(Path.Combine(_dir, $"talk{_tabs}-cut.mp4"), Dialog.OutputPath);
        Assert.False(Dialog.Fast);
        Assert.Equal("Kept 1:00.0 of 1:00.0 · 0 cuts", Dialog.Summary);
        Assert.Equal("", Dialog.Error);
        Assert.Null(tab.ExportJob);
        Assert.Empty(_editor.RmlMessages);
        foreach (var id in (string[])["ev-path", "ev-browse", "ev-precise", "ev-fast", "ev-export", "ev-cancel"])
            Assert.False(Dialog.Document.GetElementById(id).IsNull, id);
    }

    [Fact]
    public void TheDefaultSkipsAnExistingExport()
    {
        OpenTab();
        File.WriteAllBytes(Path.Combine(_dir, $"talk{_tabs}-cut.mp4"), [0]);
        W.Commands.Execute("video.export");
        Assert.Equal(Path.Combine(_dir, $"talk{_tabs}-cut-2.mp4"), Dialog.OutputPath);
    }

    [Fact]
    public void ChoosingWebmTurnsFastOff()
    {
        OpenTab();
        W.Commands.Execute("video.export");
        _editor.Tick(2);
        Dialog.Fast = true;
        Assert.True(Dialog.Fast);

        Dialog.OutputPath = Path.Combine(_dir, "out.webm");
        _editor.Tick();
        Assert.False(Dialog.Fast);
        Assert.False(Dialog.FastAllowed);
        Assert.True(Dialog.Document.GetElementById("ev-fast").IsClassSet("disabled"));

        Dialog.Fast = true; // refused while the output is WebM
        Assert.False(Dialog.Fast);

        Dialog.OutputPath = Path.Combine(_dir, "out.mkv");
        _editor.Tick();
        Assert.True(Dialog.FastAllowed);
        Assert.False(Dialog.Document.GetElementById("ev-fast").IsClassSet("disabled"));
    }

    [Fact]
    public void ExportingWithEverythingCutOnlyWarns()
    {
        var tab = OpenTab();
        tab.Document.Editor.Toggle(0);
        W.Commands.Execute("video.export");
        Assert.Equal("Every segment is removed; there is nothing to export.", tab.Notice);
        Assert.False(Dialog.Visible);
        Assert.False(W.Message.Visible);
        Assert.Null(tab.ExportJob);
    }

    [Fact]
    public void ExportingWithoutFfmpegReportsIt()
    {
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, []);
        var preview = new VideoPreview(new VideoTabTests.CountingFactory(320 * 180 * 4), () => null, "/r/a.mp4", 320, 180, 30, 801);
        var tab = new VideoTab(document, preview, null, 801);
        W.ScenesHost.AddChild(tab.TimelineViewport);
        W.ScenesHost.AddChild(preview.Viewport);
        W.Session.Add(tab);
        _editor.Tick(2);

        W.Commands.Execute("video.export");

        Assert.True(W.Message.Visible);
        Assert.Contains("ffmpeg not found", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.False(Dialog.Visible);
        Assert.Null(tab.ExportJob);
    }

    [Fact]
    public void ExportStartsAJobOnASnapshotAndClosesTheDialog()
    {
        var tab = OpenTab();
        var gate = UseGate();
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Commands.Execute("video.export");
        _editor.Tick();
        Dialog.Fast = true;
        Dialog.OutputPath = Path.Combine(_dir, "result.mkv");

        Dialog.Export();

        Assert.False(Dialog.Visible);
        Assert.False(W.IsDialogOpen);
        Assert.NotNull(tab.ExportJob);
        Assert.False(tab.Preview.IsPlaying);
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));
        var request = gate.Request!;
        Assert.Equal(ExportMode.Fast, request.Mode);
        Assert.Equal(Path.Combine(_dir, "result.mkv"), request.OutputPath);
        Assert.Equal(tab.Document.SourcePath, request.SourcePath);
        Assert.Equal(3, request.Segments.Count);

        gate.Release.SetResult();
        FinishExport(tab);
    }

    [Fact]
    public void EditsMadeWhileExportingDoNotChangeTheRunningExport()
    {
        var tab = OpenTab();
        var gate = UseGate();
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        var expected = tab.Document.Segments.Segments.ToArray();
        W.Commands.Execute("video.export");
        Dialog.Export();
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));

        tab.Document.Editor.Toggle(0);
        tab.Document.Editor.CreateSection(30, 40, anchor: 30);
        tab.Document.Editor.Dissolve(1);
        _editor.Tick(2);

        Assert.NotEqual(expected.Length, tab.Document.Segments.Count);
        Assert.Equal(expected, gate.Request!.Segments.ToArray());
        gate.Release.SetResult();
        FinishExport(tab);
    }

    [Fact]
    public void TheJobReportsProgressAndThePressOfExportCancelsIt()
    {
        var tab = OpenTab();
        var gate = UseGate((progress, _) =>
        {
            progress.Report(new ExportProgress(0.4, "Encoding"));
            return Task.CompletedTask;
        });
        W.Commands.Execute("video.export");
        Dialog.Export();
        var job = tab.ExportJob!;
        Assert.True(SpinWait.SpinUntil(() => job.Progress > 0.3, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Equal("Cancel 40 %", W.Toolbar.Document.GetElementById("video-export").InnerRml);

        W.Commands.Execute("video.export"); // pressing Export while it runs cancels; no dialog opens
        Assert.True(job.IsCancelled);
        Assert.False(Dialog.Visible);
        FinishExport(tab);

        Assert.Equal("Export cancelled.", tab.Notice);
        Assert.False(W.Message.Visible);
        Assert.Equal("Export", W.Toolbar.Document.GetElementById("video-export").InnerRml);
        Assert.Equal(1, gate.Runs);
    }

    [Fact]
    public void AFinishedExportOffersShowInFolder()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        var output = Dialog.OutputPath;
        Dialog.Export();
        gate.Release.SetResult();
        FinishExport(tab);

        Assert.True(W.Message.Visible);
        var request = W.Message.Current!;
        Assert.Equal("Export finished", request.Title);
        Assert.Contains(Path.GetFileName(output), request.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetDirectoryName(output)!, request.Message, StringComparison.Ordinal);
        Assert.Equal(["Show in Folder", "Close"], request.Buttons);
        Assert.Empty(_revealed);

        W.Message.Answer(0);
        Assert.Equal([output], _revealed);
        Assert.Equal($"Exported {Path.GetFileName(output)}.", tab.Notice);
    }

    [Fact]
    public void ClosingTheFinishedMessageRevealsNothing()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        Dialog.Export();
        gate.Release.SetResult();
        FinishExport(tab);
        W.Message.Answer(1);
        Assert.Empty(_revealed);
    }

    [Fact]
    public void AFailedExportIsReportedWithFfmpegsOwnMessage()
    {
        var tab = OpenTab();
        W.VideoView.ExporterFactory = (_, _) => (_, _, _) => throw new FfmpegException("ffmpeg exited with code 1: no encoder", 1, "stderr tail");
        W.Commands.Execute("video.export");
        Dialog.Export();
        FinishExport(tab);

        Assert.True(W.Message.Visible);
        Assert.Contains("Export failed", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Contains("no encoder", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.NotEqual("Export finished", W.Message.Current.Title);
    }

    [Fact]
    public void TheRecordingItselfIsRefusedInTheDialog()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        _editor.Tick();
        Dialog.OutputPath = tab.Document.SourcePath;

        Dialog.Export();

        Assert.True(Dialog.Visible); // stays open to fix the name
        Assert.Contains("recording itself", Dialog.Error, StringComparison.Ordinal);
        Assert.Null(tab.ExportJob);
        Assert.Equal(0, gate.Runs);
        Assert.False(W.Message.Visible);
        Assert.Equal([0], File.ReadAllBytes(tab.Document.SourcePath)); // untouched
    }

    [Fact]
    public void TheRecordingByAnotherSpellingIsRefusedToo()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        var source = tab.Document.SourcePath;
        Dialog.OutputPath = Path.Combine(Path.GetDirectoryName(source)!, "sub", "..", Path.GetFileName(source));
        Dialog.Export();
        Assert.Contains("recording itself", Dialog.Error, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Dialog.OutputPath = source.ToUpperInvariant();
            Dialog.Export();
            Assert.Contains("recording itself", Dialog.Error, StringComparison.Ordinal);
        }

        Assert.Null(tab.ExportJob);
        Assert.Equal(0, gate.Runs);
    }

    [Fact]
    public void StartingAnExportToTheRecordingIsRefusedEvenWithoutTheDialog()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.VideoView.StartExport(tab, tab.Document.SourcePath, ExportMode.Precise);
        Assert.Null(tab.ExportJob);
        Assert.Equal(0, gate.Runs);
        Assert.True(W.Message.Visible);
        Assert.Contains("recording itself", W.Message.Current!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("clip.avi")]
    [InlineData("clip.txt")]
    public void AnUnsupportedExtensionIsAReadableErrorAndStartsNothing(string name)
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        Dialog.OutputPath = Path.Combine(_dir, name);
        Dialog.Export();
        Assert.True(Dialog.Visible);
        Assert.Contains("not a supported output format", Dialog.Error, StringComparison.Ordinal);
        Assert.Contains(".mp4, .mov, .mkv, .webm", Dialog.Error, StringComparison.Ordinal);
        Assert.Null(tab.ExportJob);
        Assert.Equal(0, gate.Runs);
    }

    [Fact]
    public void AnExtensionlessNameGetsMp4()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        Dialog.OutputPath = "mycut";
        Dialog.Export();
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(Path.Combine(_dir, "mycut.mp4"), gate.Request!.OutputPath);
        gate.Release.SetResult();
        FinishExport(tab);
    }

    [Fact]
    public void ATypedPathThatExistsAsksBeforeReplacingIt()
    {
        var tab = OpenTab();
        var gate = UseGate();
        var existing = Path.Combine(_dir, "taken.mp4");
        File.WriteAllBytes(existing, [7]);
        W.Commands.Execute("video.export");
        Dialog.OutputPath = existing;

        Dialog.Export();
        Assert.True(W.Message.Visible);
        Assert.Equal("Replace file?", W.Message.Current!.Title);
        Assert.Equal(["Replace", "Cancel"], W.Message.Current.Buttons);
        Assert.Null(tab.ExportJob);

        W.Message.Answer(1); // Cancel: back to the dialog, nothing started
        Assert.True(Dialog.Visible);
        Assert.Null(tab.ExportJob);
        Assert.Equal(0, gate.Runs);

        Dialog.Export();
        W.Message.Answer(0); // Replace
        Assert.False(Dialog.Visible);
        Assert.NotNull(tab.ExportJob);
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(existing, gate.Request!.OutputPath);
        gate.Release.SetResult();
        FinishExport(tab);
    }

    [Fact]
    public void APickedPathIsNotConfirmedTwice()
    {
        var tab = OpenTab();
        var gate = UseGate();
        var existing = Path.Combine(_dir, "taken.mp4");
        File.WriteAllBytes(existing, [7]);
        W.Commands.Execute("video.export");
        _editor.Tick();

        Dialog.Browse();
        Assert.True(W.FilePicker.Visible);
        Assert.Equal(["*.mp4", "*.mov", "*.mkv", "*.webm"], W.FilePicker.Model!.Filter);
        Assert.Equal(_dir, W.FilePicker.Model.CurrentDirectory);
        W.FilePicker.Model.FileName = "taken.mp4";
        W.FilePicker.Accept();
        Assert.Equal("Replace file?", W.Message.Current!.Title); // the picker's own confirmation
        W.Message.Answer(0);
        Assert.False(W.FilePicker.Visible);
        Assert.Equal(existing, Dialog.OutputPath);
        Assert.True(Dialog.Visible);

        Dialog.Export();

        Assert.False(W.Message.Visible); // not asked a second time
        Assert.NotNull(tab.ExportJob);
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));
        gate.Release.SetResult();
        FinishExport(tab);
    }

    [Fact]
    public void EditingAPickedPathAsksAgain()
    {
        OpenTab();
        UseGate();
        var existing = Path.Combine(_dir, "taken.mp4");
        var other = Path.Combine(_dir, "other.mkv");
        File.WriteAllBytes(existing, [7]);
        File.WriteAllBytes(other, [7]);
        W.Commands.Execute("video.export");
        Dialog.Browse();
        W.FilePicker.Model!.FileName = "taken.mp4";
        W.FilePicker.Accept();
        W.Message.Answer(0);

        Dialog.OutputPath = other; // a different existing file than the one the picker confirmed
        Dialog.Export();
        Assert.True(W.Message.Visible);
        Assert.Equal("Replace file?", W.Message.Current!.Title);
    }

    [Fact]
    public void BrowsingBeginsAtTheDefaultName()
    {
        var tab = OpenTab();
        W.Commands.Execute("video.export");
        Dialog.Browse();
        Assert.Equal(Path.GetFileName(Dialog.OutputPath), W.FilePicker.Model!.FileName);
        Assert.Equal(Path.GetDirectoryName(tab.Document.SourcePath), W.FilePicker.Model.CurrentDirectory);
        W.FilePicker.Cancel();
        Assert.True(Dialog.Visible);
    }

    [Fact]
    public void TheButtonsAndRadioRowsWork()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        _editor.Tick(2);
        Element("ev-fast").Click();
        Assert.True(Dialog.Fast);
        _editor.Tick();
        Assert.True(Element("ev-fast").IsClassSet("selected"));
        Assert.False(Element("ev-precise").IsClassSet("selected"));
        Element("ev-precise").Click();
        Assert.False(Dialog.Fast);
        _editor.Tick();
        Assert.True(Element("ev-precise").IsClassSet("selected"));

        Dialog.OutputPath = Path.Combine(_dir, "clip.webm");
        Element("ev-fast").Click(); // disabled for WebM
        Assert.False(Dialog.Fast);
        _editor.Tick();
        Assert.Contains("WebM", Dialog.Document.GetElementById("ev-hint").InnerRml, StringComparison.Ordinal);

        Element("ev-cancel").Click();
        Assert.False(Dialog.Visible);
        Assert.Null(tab.ExportJob);

        W.Commands.Execute("video.export");
        Element("ev-export").Click();
        Assert.False(Dialog.Visible);
        Assert.NotNull(tab.ExportJob);
        gate.Release.SetResult();
        FinishExport(tab);
    }

    private MainframeEngine.UI.Rml.RmlElement Element(string id) => Dialog.Document.GetElementById(id);

    [Fact]
    public void ACloseRequestWhileTheDialogIsOpenDismissesIt()
    {
        OpenTab();
        W.Commands.Execute("video.export");
        Assert.True(Dialog.Visible);
        W.OnCloseRequested();
        Assert.False(Dialog.Visible);
    }

    [Fact]
    public void ClosingTheTabMidExportCancelsItAndIsClean()
    {
        var tab = OpenTab();
        var gate = UseGate();
        W.Commands.Execute("video.export");
        Dialog.Export();
        var job = tab.ExportJob!;
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));

        W.Session.Close(tab);
        _editor.Tick(3);

        Assert.True(job.IsCancelled);
        Assert.Null(tab.ExportJob);
        Assert.False(W.Message.Visible);
    }

    [Fact]
    public void PollingWithNoExportDoesNotAllocate()
    {
        var tab = OpenTab();
        _editor.Tick(3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            W.VideoView.PollJobs(tab);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    [Fact]
    public void TheTempSessionIsCreatedForTheFirstExportAndRemovedWhenTheViewLeavesTheTree()
    {
        var tab = OpenTab();
        var gate = UseGate();
        Assert.Null(W.VideoView.TempDirectory);

        W.VideoView.StartExport(tab, Path.Combine(_dir, "solo-cut.mp4"), ExportMode.Precise);
        var temp = W.VideoView.TempDirectory;
        Assert.NotNull(temp);
        Assert.True(Directory.Exists(temp));
        Assert.True(SpinWait.SpinUntil(() => gate.Request is not null, TimeSpan.FromSeconds(10)));

        var job = tab.ExportJob!;
        W.RemoveChild(W.VideoView); // what closing the editor does to it
        Assert.False(Directory.Exists(temp));
        Assert.Null(W.VideoView.TempDirectory);
        Assert.True(job.IsCancelled); // the running export was told to stop before its folder went
        job.Dispose();
        tab.ExportJob = null;
    }
}
