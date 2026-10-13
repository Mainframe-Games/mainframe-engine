using System.Text;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Preview;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// Transcribe, Remove Filler Words and Export Transcript on the headless editor. The speech recogniser is a fake behind
/// <see cref="VideoView.TranscriberFactory"/> and the model file is planted in a temp folder, so nothing is downloaded
/// and no model or ffmpeg ever runs.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class TranscriptionFlowTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly string _dir = Directory.CreateTempSubdirectory("transcription-").FullName;
    private readonly string _models;
    private readonly HeadlessEditor _editor;
    private int _tabs;

    public TranscriptionFlowTests()
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

    // The base.en model "downloaded" already: ModelStore treats any existing file as cached, so nothing is fetched.
    private void PlantModel() => File.WriteAllBytes(Path.Combine(_models, WhisperModelInfo.Default.FileName), [1, 2, 3]);

    // A tab on a real one-byte recording in the temp folder, with an ffmpeg location (jobs never reach it: the factory is a fake).
    private VideoTab OpenTab(FfmpegLocation? ffmpeg = null)
    {
        var video = Path.Combine(_dir, $"talk{++_tabs}.mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(_dir, $"talk{_tabs}.mfcut");
        SaveProject(video, path);
        var tab = W.Session.OpenVideo(path, ffmpeg ?? NoFfmpeg, 100);
        // The waveform job fails on the missing ffmpeg and adds a notice; let it finish so it cannot land after ours.
        Assert.True(SpinWait.SpinUntil(() => { _editor.Tick(); return tab.WaveformJob is null; }, TimeSpan.FromSeconds(10)));
        return tab;
    }

    private static void SaveProject(string video, string path) =>
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();

    // Ticks until the tab's transcription has been applied or reported.
    private void Finish(VideoTab tab)
    {
        Assert.True(SpinWait.SpinUntil(() => tab.TranscribeJob is not { IsDone: false }, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.TranscribeJob);
    }

    private sealed class FakeTranscriber(Func<IProgress<double>?, CancellationToken, Task<IReadOnlyList<Word>>> run) : ITranscriber
    {
        public Task<IReadOnlyList<Word>> TranscribeAsync(string mediaPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
            run(progress, cancellationToken);
    }

    private void UseWords(IReadOnlyList<Word> words) =>
        W.VideoView.TranscriberFactory = (_, _, _) => new FakeTranscriber((_, _) => Task.FromResult(words));

    [Fact]
    public void TranscribingStoresTheWordsAndMarksTheProjectDirty()
    {
        PlantModel();
        string? usedPath = null;
        WhisperModel? usedModel = null;
        W.VideoView.TranscriberFactory = (_, path, m) =>
        {
            usedPath = path;
            usedModel = m;
            return new FakeTranscriber((_, _) => Task.FromResult<IReadOnlyList<Word>>(VideoDocumentTests.Words()));
        };
        var tab = OpenTab();
        W.Layout.SetVideoShowTranscript(false);
        Assert.False(tab.IsDirty);

        W.Commands.Execute("video.transcribe");
        Assert.NotNull(tab.TranscribeJob);
        Finish(tab);

        Assert.Equal(4, tab.Document.Transcript.Count);
        Assert.True(tab.IsDirty);
        Assert.Equal("Transcribed 4 words.", tab.Notice);
        Assert.Equal(Path.Combine(_models, "ggml-base.en.bin"), usedPath);
        Assert.Equal(WhisperModel.BaseEn, usedModel);
        Assert.True(W.Layout.Settings.VideoShowTranscript); // a hidden transcript panel opens to show the result
        Assert.False(W.Message.Visible);
    }

    [Fact]
    public void NoSpeechSaysSoAndLeavesTheTranscriptEmpty()
    {
        PlantModel();
        UseWords([]);
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        Finish(tab);
        Assert.Equal("No speech was recognised.", tab.Notice);
        Assert.Empty(tab.Document.Transcript);
    }

    [Fact]
    public void RemoveFillersTranscribesFirstWhenThereIsNoTranscript()
    {
        PlantModel();
        UseWords(VideoDocumentTests.Words());
        var tab = OpenTab();

        W.Commands.Execute("video.remove_fillers");
        Finish(tab);

        Assert.Equal(4, tab.Document.Transcript.Count);
        Assert.True(tab.Document.Segments.Count > 1);
        Assert.Contains(tab.Document.Segments.Segments, s => !s.Enabled && s.Start < 1.8 && s.End > 1.4);
        Assert.StartsWith("Removed 1 filler word", tab.Notice, StringComparison.Ordinal);

        W.Commands.Execute("edit.undo"); // the cut is one step; the transcript stays
        Assert.Single(tab.Document.Segments.Segments);
        Assert.Equal(4, tab.Document.Transcript.Count);
    }

    [Fact]
    public void RemoveFillersWithATranscriptCutsAtOnceWithoutTranscribing()
    {
        var tab = OpenTab();
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        W.Layout.SetVideoShowTranscript(false);

        W.Commands.Execute("video.remove_fillers");

        Assert.Null(tab.TranscribeJob);
        Assert.StartsWith("Removed 1 filler word", tab.Notice, StringComparison.Ordinal);
        Assert.True(W.Layout.Settings.VideoShowTranscript);
    }

    [Fact]
    public void RemoveFillersWhileTranscribingWaitsForThatRunInsteadOfCancellingIt()
    {
        PlantModel();
        var release = new TaskCompletionSource();
        W.VideoView.TranscriberFactory = (_, _, _) => new FakeTranscriber(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return VideoDocumentTests.Words();
        });
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        var job = tab.TranscribeJob!;

        W.Commands.Execute("video.remove_fillers");
        Assert.Same(job, tab.TranscribeJob);
        Assert.False(job.IsCancelled);

        release.SetResult();
        Finish(tab);
        Assert.StartsWith("Removed 1 filler word", tab.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingTranscriptionLeavesNoTranscript()
    {
        PlantModel();
        W.VideoView.TranscriberFactory = (_, _, _) => new FakeTranscriber(async (p, ct) =>
        {
            p?.Report(0.25);
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        var job = tab.TranscribeJob!;
        Assert.True(SpinWait.SpinUntil(() => job.Progress > 0.2, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Equal("Cancel 25 %", W.Toolbar.Document.GetElementById("video-transcribe").InnerRml);

        W.Commands.Execute("video.transcribe"); // a second press cancels
        Assert.True(job.IsCancelled);
        Finish(tab);

        Assert.Equal("Transcription cancelled.", tab.Notice);
        Assert.Empty(tab.Document.Transcript);
        Assert.False(tab.IsDirty);
        Assert.Equal("Transcribe", W.Toolbar.Document.GetElementById("video-transcribe").InnerRml);
        Assert.False(W.Message.Visible);
    }

    [Fact]
    public void WhileTheModelDownloadsTheButtonSaysSoThenCancel()
    {
        var tab = OpenTab();
        var release = new TaskCompletionSource();
        tab.TranscribeRun = new TranscribeRun(WhisperModel.BaseEn, downloads: true, thenRemoveFillers: false);
        tab.TranscribeJob = VideoJob.Start<IReadOnlyList<Word>>("transcribe", async (p, ct) =>
        {
            p.Report(0.3);
            await release.Task.WaitAsync(ct);
            return [];
        });
        Assert.True(SpinWait.SpinUntil(() => tab.TranscribeJob.Progress > 0.25, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Equal("Downloading model 60 %", W.Toolbar.Document.GetElementById("video-transcribe").InnerRml);
        release.SetResult();
        Finish(tab);
    }

    [Fact]
    public void AFinishedTranscriptionClearsItsRun()
    {
        var tab = OpenTab();
        var job = VideoJob.Start<IReadOnlyList<Word>>("transcribe", (p, ct) => Task.FromResult<IReadOnlyList<Word>>([]));
        tab.TranscribeRun = new TranscribeRun(WhisperModel.BaseEn, downloads: true, thenRemoveFillers: false);
        tab.TranscribeJob = job;
        Assert.True(SpinWait.SpinUntil(() => job.IsDone, TimeSpan.FromSeconds(10)));
        _editor.Tick();
        Assert.Null(tab.TranscribeRun);
    }

    [Fact]
    public void AFailedTranscriptionIsReported()
    {
        PlantModel();
        W.VideoView.TranscriberFactory = (_, _, _) => new FakeTranscriber((_, _) => throw new IOException("audio could not be decoded"));
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        Finish(tab);
        Assert.True(W.Message.Visible);
        Assert.Contains("Transcription failed", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Contains("audio could not be decoded", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Empty(tab.Document.Transcript);
    }

    [Fact]
    public void AModelThatWillNotLoadAndLooksTruncatedIsRemoved()
    {
        PlantModel(); // three bytes: far below the size of base.en
        var modelFile = Path.Combine(_models, WhisperModelInfo.Default.FileName);
        W.VideoView.TranscriberFactory = (_, path, _) =>
            new FakeTranscriber((_, _) => throw new ModelLoadException("The speech model could not be loaded.", path, new IOException("bad")));
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        Finish(tab);
        Assert.Contains("Transcription failed", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Contains("has been removed", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(modelFile));
    }

    [Fact]
    public void TranscribingWithoutFfmpegReportsIt()
    {
        // A tab without an ffmpeg location (as opened when none is installed), its preview decoding from memory.
        var document = VideoDocument.FromProject(VideoDocumentTests.Project("/r/a.mp4"), null, null, []);
        var preview = new VideoPreview(new VideoTabTests.CountingFactory(320 * 180 * 4), () => null, "/r/a.mp4", 320, 180, 30, 800);
        var tab = new VideoTab(document, preview, null, 800);
        W.ScenesHost.AddChild(tab.TimelineViewport);
        W.ScenesHost.AddChild(preview.Viewport);
        W.Session.Add(tab);
        _editor.Tick(2);
        W.Commands.Execute("video.transcribe");
        Assert.True(W.Message.Visible);
        Assert.Contains("ffmpeg not found", W.Message.Current!.Message, StringComparison.Ordinal);
        Assert.Null(tab.TranscribeJob);
    }

    [Fact]
    public void ClosingATabWhileItTranscribesIsClean()
    {
        PlantModel();
        W.VideoView.TranscriberFactory = (_, _, _) => new FakeTranscriber(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        var tab = OpenTab();
        W.Commands.Execute("video.transcribe");
        var job = tab.TranscribeJob!;
        _editor.Tick();
        W.Session.Close(tab);
        _editor.Tick(3);
        Assert.True(job.IsCancelled);
        Assert.Null(tab.TranscribeJob);
        Assert.Null(W.VideoView.Tab);
        Assert.False(W.Message.Visible);
    }

    [Fact]
    public void ExportTranscriptWritesTheEditedText()
    {
        var tab = OpenTab();
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        tab.Document.CutWords(1, 1, 1); // "um" is cut, so it is left out
        var expected = tab.Document.TranscriptText(srt: false);
        Assert.DoesNotContain("um", expected, StringComparison.Ordinal);

        W.Commands.Execute("video.export_transcript");
        Assert.True(W.FilePicker.Visible);
        Assert.Equal(["*.txt", "*.srt"], W.FilePicker.Model!.Filter);
        Assert.Equal(Path.GetFileNameWithoutExtension(tab.Document.SourcePath) + ".txt", W.FilePicker.Model.FileName);
        Assert.Equal(Path.GetDirectoryName(tab.Document.SourcePath), W.FilePicker.Model.CurrentDirectory);
        W.FilePicker.Accept();

        var file = Path.ChangeExtension(tab.Document.SourcePath, ".txt");
        var bytes = File.ReadAllBytes(file);
        Assert.NotEqual(0xEF, bytes[0]); // no byte order mark
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal($"Exported {Path.GetFileName(file)}.", tab.Notice);
    }

    [Fact]
    public void ExportTranscriptAsSrtWritesSubtitles()
    {
        var tab = OpenTab();
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        W.Commands.Execute("video.export_transcript");
        W.FilePicker.Model!.FileName = "subs.SRT";
        W.FilePicker.Accept();
        var file = Path.Combine(Path.GetDirectoryName(tab.Document.SourcePath)!, "subs.SRT");
        Assert.Equal(tab.Document.TranscriptText(srt: true), File.ReadAllText(file));
        Assert.Contains("-->", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportTranscriptWithoutATranscriptDoesNothing()
    {
        var tab = OpenTab();
        W.Commands.Execute("video.export_transcript");
        Assert.False(W.FilePicker.Visible);
        Assert.Equal("There is no transcript to export. Transcribe the recording first.", tab.Notice);
    }

    [Fact]
    public void ATranscriptNeverOverwritesTheRecording()
    {
        var txt = Path.Combine(_dir, "clip.txt"); // a recording whose own name ends in .txt
        File.WriteAllBytes(txt, [0]);
        var project = Path.Combine(_dir, "clip.mfcut");
        SaveProject(txt, project);
        var clip = W.Session.OpenVideo(project, NoFfmpeg, 100);
        clip.Document.SetTranscript(VideoDocumentTests.Words());

        Assert.False(W.Commands.WriteTranscript(clip, txt));
        Assert.Equal([0], File.ReadAllBytes(txt));
        Assert.Equal("Error", W.Message.Current?.Title);
    }

    [Fact]
    public void AFailedWriteIsReported()
    {
        var tab = OpenTab();
        tab.Document.SetTranscript(VideoDocumentTests.Words());
        Assert.False(W.Commands.WriteTranscript(tab, Path.Combine(_dir, "missing-folder", "out.txt")));
        Assert.Equal("Error", W.Message.Current?.Title);
        Assert.Contains("Could not write the transcript", W.Message.Current!.Message, StringComparison.Ordinal);
    }
}
