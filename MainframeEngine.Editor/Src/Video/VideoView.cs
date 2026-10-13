using System.Numerics;
using MainframeEngine.Editor.Music;
using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Export;
using MainframeEngine.Video.Models;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The video tab's per-frame driver, a <c>[Tool]</c> node of the workspace beside <see cref="SongView"/>: it sizes the
/// active tab's timeline viewport to the <see cref="VideoPanel"/>'s timeline area, keeps the preview viewport rendering,
/// publishes both to the UI as <c>engine://video-timeline</c> and <c>engine://video-preview</c>, routes the pointer over
/// the timeline and the tab's keys to its <see cref="VideoTimelineController"/>, keeps the toolbar current and applies
/// finished detection jobs on the main thread. Steady-state frames allocate nothing.
/// </summary>
[Tool]
public sealed class VideoView : Node
{
    public const string TimelineTexture = "video-timeline";
    public const string PreviewTexture = "video-preview";

    /// <summary>An off-canvas point: hovering there clears the hovered segment and boundary.</summary>
    private static readonly Vector2 Away = new(-1, -1);

    private readonly EditorWorkspace _workspace;
    private VideoTab? _tab;
    private SubViewport? _timelineRegistered;
    private SubViewport? _previewRegistered;
    private Vector2 _mouse;

    // A left press on the timeline started a gesture: every motion and the release go to the controller until then,
    // wherever the pointer is.
    private bool _dragging;

    // The pointer was over the timeline at the last motion; leaving clears the hover so Delete cannot hit a region the
    // pointer has left.
    private bool _hovering;

    public VideoView(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>The video tab shown (the active tab in video mode), or null.</summary>
    public VideoTab? Tab => _tab;

    /// <summary>True while either texture is published to the UI (tests: a switch away unpublishes both).</summary>
    internal bool PublishesTextures => _timelineRegistered is not null || _previewRegistered is not null;

    // Editor Settings › Video, read when an operation starts (jobs keep what they started with).
    private DetectionSettings Detection => _workspace.Settings.VideoDetection;

    private WhisperModel SpeechModel => WhisperModelInfo.Parse(_workspace.Settings.VideoWhisperModel);

    private IReadOnlyList<string> FillerWords => _workspace.Settings.VideoFillerWords;

    /// <summary>Creates the speech recogniser for a downloaded model file; tests swap it so no model is ever loaded.</summary>
    public Func<FfmpegLocation, string, WhisperModel, ITranscriber> TranscriberFactory { get; set; } =
        static (ffmpeg, modelPath, model) => new WhisperTranscriber(ffmpeg, modelPath, model);

    /// <summary>Creates the runner of one export; tests swap it so ffmpeg never starts.</summary>
    internal Func<FfmpegLocation, TempSession, VideoExportRunner> ExporterFactory { get; set; } =
        static (ffmpeg, temp) => new Exporter(ffmpeg, temp).ExportAsync;

    // Scratch space for filter scripts and fast-export pieces: made by the first export, removed when the editor closes.
    private TempSession? _temp;

    /// <summary>The temp session's folder, or null until an export has started.</summary>
    internal string? TempDirectory => _temp?.Directory;

    protected override void OnProcess(in GameTime gameTime)
    {
        var tabs = _workspace.Session.Tabs;
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i] is VideoTab video)
                PollJobs(video);

        var tab = _workspace.Mode == EditorMode.Video ? _workspace.Session.ActiveTab as VideoTab : null;
        if (!ReferenceEquals(tab, _tab))
            Switch(tab);
        var panel = _workspace.VideoPanel;
        panel.Tick(tab);
        _workspace.Toolbar.UpdateVideo(tab);
        if (tab is null)
            return;

        var scale = _workspace.Host.PixelScale;
        var timeline = panel.TimelineRect;
        var preview = panel.PreviewRect;
        var showTimeline = timeline.Width > 1 && timeline.Height > 1;
        var showPreview = preview.Width > 1 && preview.Height > 1;
        if (showTimeline)
            tab.View.Width = timeline.Width;
        SongView.Place(tab.TimelineViewport, tab.Timeline, timeline, scale, showTimeline);
        // The preview sizes its own viewport (the decode size); it only has to keep rendering while it is shown.
        var mode = showPreview ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        if (tab.Preview.Viewport.UpdateMode != mode)
            tab.Preview.Viewport.UpdateMode = mode;
        Publish(tab, showTimeline, showPreview);
    }

    private void Publish(VideoTab tab, bool timeline, bool preview)
    {
        if (Tree?.Servers.Get<UiServer>() is not { } ui)
            return;
        if (timeline && !ReferenceEquals(_timelineRegistered, tab.TimelineViewport))
        {
            ui.RegisterTexture(TimelineTexture, tab.TimelineViewport);
            _timelineRegistered = tab.TimelineViewport;
        }

        if (preview && !ReferenceEquals(_previewRegistered, tab.Preview.Viewport))
        {
            ui.RegisterTexture(PreviewTexture, tab.Preview.Viewport);
            _previewRegistered = tab.Preview.Viewport;
        }

        _workspace.VideoPanel.SetImages(_previewRegistered is not null && preview, _timelineRegistered is not null && timeline);
    }

    // The shown tab changed (another tab, a mode switch, a close): the old one's gesture ends, the UI stops sampling its
    // targets, and the new one's segment menu opens here.
    private void Switch(VideoTab? tab)
    {
        if (_dragging)
            _tab?.Controller.CancelGesture();
        _dragging = false;
        _hovering = false;
        if (_tab is { } old)
        {
            old.Controller.ShowSegmentMenu = null;
            old.Controller.Hover(Away); // no stale hovered region survives the switch (Delete would hit it on return)
        }

        _tab = tab;
        if (_timelineRegistered is not null || _previewRegistered is not null)
        {
            var ui = Tree?.Servers.Get<UiServer>();
            ui?.UnregisterTexture(TimelineTexture);
            ui?.UnregisterTexture(PreviewTexture);
            _timelineRegistered = null;
            _previewRegistered = null;
        }

        _workspace.VideoPanel.SetImages(false, false);
        if (tab is null)
            return;
        tab.Controller.ShowSegmentMenu = (_, x, y) => ShowSegmentMenu(tab, x, y);
    }

    // Built only when a right-click opens it.
    private void ShowSegmentMenu(VideoTab tab, float x, float y)
    {
        var rect = _workspace.VideoPanel.TimelineRect;
        _workspace.Popup.Show([new MenuItem("Delete", "edit.delete", "Del", Icon: "trash")], rect.X + x, rect.Y + y, onCommand: id =>
        {
            if (id == "edit.delete")
                Guard("Delete failed", () => tab.Controller.DeleteHovered());
        });
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_tab is not { } tab || _workspace.IsDialogOpen || _workspace.Popup.Visible)
            return;
        var scale = _workspace.Host.PointerScale;
        switch (inputEvent)
        {
            case InputEventMouseButton { Button: MouseButton.Left } button:
                _mouse = button.Position * scale;
                if (button.Pressed)
                {
                    if (!InTimeline(_mouse))
                        return;
                    _dragging = true;
                    Guard("Timeline edit failed", () => tab.Controller.Press(Local(_mouse), _workspace.Modifiers));
                    Handled();
                }
                else if (_dragging)
                {
                    _dragging = false;
                    Guard("Timeline edit failed", () => tab.Controller.Release(Local(_mouse)));
                    _hovering = InTimeline(_mouse);
                    if (!_hovering)
                        tab.Controller.Hover(Away);
                    Handled();
                }

                break;
            case InputEventMouseButton { Button: MouseButton.Right, Pressed: true } right:
                _mouse = right.Position * scale;
                if (_dragging || !InTimeline(_mouse))
                    return;
                Guard("Timeline edit failed", () => tab.Controller.RightClick(Local(_mouse)));
                Handled();
                break;
            case InputEventMouseMotion motion:
                _mouse = motion.Position * scale;
                if (_dragging)
                {
                    Guard("Timeline edit failed", () => tab.Controller.Drag(Local(_mouse), _workspace.Modifiers));
                    Handled();
                }
                else if (InTimeline(_mouse))
                {
                    _hovering = true;
                    tab.Controller.Hover(Local(_mouse));
                }
                else if (_hovering)
                {
                    _hovering = false;
                    tab.Controller.Hover(Away);
                }

                break;
            case InputEventMouseWheel wheel:
                if (!InTimeline(_mouse))
                    return;
                tab.Controller.Wheel(Local(_mouse), wheel.Delta, _workspace.Modifiers);
                Handled();
                break;
        }
    }

    /// <summary>
    /// The video tab's keys: Space plays and pauses, Home goes to the start, and the controller takes the rest
    /// (Delete/Backspace). False when no video tab is shown or the key is not one of them, so its shortcut runs instead.
    /// </summary>
    public bool HandleKey(Key key, EditorModifiers modifiers)
    {
        if (_workspace.Mode != EditorMode.Video || _workspace.Session.ActiveTab is not VideoTab tab)
            return false;
        if (modifiers == EditorModifiers.None)
        {
            if (key == Key.Space)
            {
                Guard("Playback failed", tab.TogglePlay);
                return true;
            }

            if (key == Key.Home)
            {
                Guard("Playback failed", () => tab.Seek(0));
                return true;
            }
        }

        var handled = false;
        Guard("Timeline edit failed", () => handled = tab.Controller.Key(key, modifiers));
        return handled;
    }

    private bool InTimeline(Vector2 p) => _workspace.VideoPanel.TimelineRect.Contains(p.X, p.Y);

    private Vector2 Local(Vector2 p)
    {
        var rect = _workspace.VideoPanel.TimelineRect;
        return new Vector2(p.X - rect.X, p.Y - rect.Y);
    }

    private void Handled() => GetViewport()?.SetInputAsHandled();

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            _workspace.Commands.ReportError(what, e);
        }
    }

    // ── Detection ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect Silence: runs ffmpeg's <c>silencedetect</c> over the recording in the background; <see cref="PollJobs"/> lays
    /// out the cuts when it finishes. A second call while it runs cancels it.
    /// </summary>
    public void DetectSilence(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.SilenceJob is { } running)
        {
            running.Cancel();
            return;
        }

        if (tab.Ffmpeg is not { } ffmpeg)
        {
            tab.ReportNoFfmpeg("Detect Silence");
            return;
        }

        var document = tab.Document;
        var settings = Detection;
        tab.SilenceSettings = settings;
        tab.SilenceJob = VideoJob.Start("silence",
            (p, ct) => new SilenceDetector(ffmpeg).DetectAsync(document.SourcePath, document.Duration, settings, p, ct));
        Log.Info($"[Video] Detecting silence in {Path.GetFileName(document.SourcePath)}…");
    }

    /// <summary>
    /// Main thread, every frame: applies <paramref name="tab"/>'s finished silence detection and transcription, or
    /// reports how they ended. A tab closed mid-run is no longer polled; closing it cancelled and disposed its jobs.
    /// </summary>
    internal void PollJobs(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.SilenceJob is { IsDone: true } silence)
            FinishSilence(tab, silence);
        if (tab.TranscribeJob is { IsDone: true } transcribe)
            FinishTranscribe(tab, transcribe);
        if (tab.ExportJob is { IsDone: true } export)
            FinishExport(tab, export);
    }

    private void FinishSilence(VideoTab tab, VideoJob<IReadOnlyList<SilenceSpan>> job)
    {
        tab.SilenceJob = null;
        var settings = tab.SilenceSettings ?? Detection; // the ones it detected with
        tab.SilenceSettings = null;
        var task = job.Task;
        if (task.IsCanceled || job.IsCancelled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Silence detection cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            _workspace.Commands.ReportError("Detect Silence failed", error);
        else
            ApplySilence(tab, task.Result, settings);
        job.Dispose();
    }

    // Kept out of PollJobs: a lambda capturing its parameter would allocate a closure on every call, every frame.
    private void ApplySilence(VideoTab tab, IReadOnlyList<SilenceSpan> spans, DetectionSettings settings) =>
        Guard("Detect Silence failed", () =>
        {
            tab.Controller.CancelGesture();
            tab.Notice = tab.Document.ApplySilence(spans, settings);
        });

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// File › Export Video: opens the export dialog. A second call while an export runs cancels it (the toolbar button reads
    /// "Cancel n %"); with every segment cut there is nothing to export; without ffmpeg it is reported.
    /// </summary>
    public void Export(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.ExportJob is { } running)
        {
            running.Cancel();
            return;
        }

        if (VideoExport.KeptSeconds(tab.Document) <= 0)
        {
            tab.Notice = VideoExport.NothingToExport;
            return;
        }

        if (tab.Ffmpeg is null)
        {
            tab.ReportNoFfmpeg("Export");
            return;
        }

        tab.Controller.CancelGesture();
        _workspace.ExportVideo.Open(tab);
    }

    /// <summary>
    /// Starts the export job: pauses the preview, builds the immutable <see cref="ExportRequest"/> here on the main thread
    /// (the job never reads the document, so edits made while it runs do not touch it) and runs it in the background;
    /// <see cref="PollJobs"/> reports how it ends. The output is refused when it is the recording itself.
    /// </summary>
    /// <param name="tab">The video tab to export.</param>
    /// <param name="path">The output file (full path, a supported extension).</param>
    /// <param name="mode">Precise re-encodes, Fast copies.</param>
    public void StartExport(VideoTab tab, string path, ExportMode mode)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (tab.ExportJob is not null || !_workspace.Session.Tabs.Contains(tab))
            return;
        if (tab.Ffmpeg is not { } ffmpeg)
        {
            tab.ReportNoFfmpeg("Export");
            return;
        }

        ExportRequest? snapshot = null;
        Guard("Export failed", () => snapshot = VideoExport.Request(tab.Document, path, mode));
        if (snapshot is not { } request)
            return;
        tab.Controller.CancelGesture();
        tab.Preview.Pause();
        var runner = ExporterFactory(ffmpeg, _temp ??= new TempSession());
        tab.ExportJob = VideoJob.Start("export", async (p, ct) =>
        {
            await runner(request, new ExportProgressSink(p), ct).ConfigureAwait(false);
            return request.OutputPath;
        });
        Log.Info($"[Video] Exporting {Path.GetFileName(request.SourcePath)} to {request.OutputPath} ({mode})…");
    }

    private void FinishExport(VideoTab tab, VideoJob<string> job)
    {
        tab.ExportJob = null;
        var task = job.Task;
        // By how the work ended, not by whether Cancel was pressed: a cancel that came after the file was written is a success.
        if (task.IsCompletedSuccessfully)
            ShowExported(tab, task.Result);
        else if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Export cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            _workspace.Commands.ReportError("Export failed", error);
        job.Dispose();
    }

    private void ShowExported(VideoTab tab, string path)
    {
        var name = Path.GetFileName(path);
        Log.Info($"[Video] Exported {path}");
        tab.Notice = $"Exported {name}.";
        var reveal = _workspace.Options.Reveal ?? OsShell.Reveal;
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Export finished",
            Message = $"{name}\n{Path.GetDirectoryName(path)}",
            Buttons = ["Show in Folder", "Close"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "check",
            Callback = (button, _) =>
            {
                if (button == 0)
                    reveal(path);
            },
        });
    }

    // Maps the exporter's progress to the job's 0…1. A sink of its own, not Progress<T>: that would post every report to a
    // synchronisation context, and the job only wants the latest value stored.
    private sealed class ExportProgressSink(IProgress<double> inner) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => inner.Report(value.Fraction);
    }

    protected override void OnExitTree()
    {
        // The jobs are cancelled first: an export still writing its filter script must not find its folder gone.
        var tabs = _workspace.Session.Tabs;
        var running = new List<Task>();
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i] is VideoTab { ExportJob: { } job })
            {
                job.Cancel();
                running.Add(job.Task);
            }

        // Bounded: ffmpeg is killed through the token, so they end at once; a stuck one must not hold the editor open.
        // WaitAny, not WaitAll: a cancelled or failed task would make WaitAll throw.
        if (running.Count > 0 && Task.WaitAny([Task.WhenAll(running)], TimeSpan.FromSeconds(2)) < 0)
            Log.Warning("[Video] An export did not stop within 2 s of closing the editor; its scratch folder is removed anyway.");
        _temp?.Dispose();
        _temp = null;
        base.OnExitTree();
    }

    // ── Transcription ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transcribe: downloads the speech model first if it is not cached (the first half of the progress), then runs
    /// Whisper over the recording's audio in the background; <see cref="PollJobs"/> installs the words when it finishes.
    /// A second call while it runs cancels it. Everything runs on this machine; the model download is the only network use.
    /// </summary>
    /// <param name="tab">The video tab to transcribe.</param>
    /// <param name="thenRemoveFillers">Cuts the filler words as soon as the transcript arrives.</param>
    public void Transcribe(VideoTab tab, bool thenRemoveFillers = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.TranscribeJob is { } running)
        {
            running.Cancel();
            return;
        }

        if (tab.Ffmpeg is not { } ffmpeg)
        {
            tab.ReportNoFfmpeg("Transcribe");
            return;
        }

        var model = SpeechModel;
        var store = new ModelStore(_workspace.Options.VideoModelsDirectory);
        var downloads = !store.IsDownloaded(model);
        var factory = TranscriberFactory;
        var path = tab.Document.SourcePath;
        tab.TranscribeRun = new TranscribeRun(model, downloads, thenRemoveFillers);
        tab.TranscribeJob = VideoJob.Start<IReadOnlyList<Word>>("transcribe", async (p, ct) =>
        {
            // With a download the first half of the bar is the model; the recognition takes the rest.
            var share = downloads ? 0.5 : 0.0;
            var modelPath = await store.EnsureAsync(model, downloads ? new PhaseProgress(p, 0, 0.5) : null, ct).ConfigureAwait(false);
            return await factory(ffmpeg, modelPath, model).TranscribeAsync(path, new PhaseProgress(p, share, 1 - share), ct).ConfigureAwait(false);
        });
        Log.Info($"[Video] Transcribing {Path.GetFileName(path)} with {WhisperModelInfo.For(model).Key}…");
    }

    /// <summary>
    /// Remove Filler Words: cuts the "um"s and "uh"s of the transcript (one undo step). With no transcript it
    /// transcribes first and cuts when that finishes; while a transcription is already running it just asks that one to.
    /// </summary>
    public void RemoveFillers(VideoTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Document.Transcript.Count == 0)
        {
            if (tab.TranscribeRun is { } run && tab.TranscribeJob is not null)
                run.ThenRemoveFillers = true; // not Transcribe(): a second call there would cancel the running job
            else
                Transcribe(tab, thenRemoveFillers: true);
            return;
        }

        ApplyFillers(tab);
    }

    private void ApplyFillers(VideoTab tab) =>
        Guard("Remove Filler Words failed", () =>
        {
            tab.Controller.CancelGesture();
            tab.Notice = tab.Document.RemoveFillerWords(FillerWords, Detection);
            ShowTranscript();
        });

    // The transcript panel is where the result is; show it if the user had hidden it.
    private void ShowTranscript()
    {
        if (!_workspace.Layout.Settings.VideoShowTranscript)
            _workspace.Layout.SetVideoShowTranscript(true);
    }

    private void FinishTranscribe(VideoTab tab, VideoJob<IReadOnlyList<Word>> job)
    {
        var run = tab.TranscribeRun;
        tab.TranscribeJob = null;
        tab.TranscribeRun = null;
        var task = job.Task;
        if (task.IsCanceled || job.IsCancelled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Transcription cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            _workspace.Commands.ReportError("Transcription failed", ExplainTranscribeFailure(error, run));
        else
            InstallTranscript(tab, task.Result, run?.ThenRemoveFillers == true);
        job.Dispose();
    }

    private void InstallTranscript(VideoTab tab, IReadOnlyList<Word> words, bool thenRemoveFillers) =>
        Guard("Transcription failed", () =>
        {
            tab.Document.SetTranscript(words);
            tab.Notice = words.Count == 0 ? "No speech was recognised." : $"Transcribed {words.Count} words.";
            ShowTranscript();
            if (words.Count > 0 && thenRemoveFillers)
                ApplyFillers(tab);
        });

    // Cutback's advice per failure: a model that would not load is deleted when it looks truncated so the next run fetches
    // it again; a missing native library says so. Anything else keeps its own message.
    private Exception ExplainTranscribeFailure(Exception error, TranscribeRun? run)
    {
        switch (error)
        {
            case ModelLoadException load:
                var store = new ModelStore(_workspace.Options.VideoModelsDirectory);
                var model = run?.Model ?? SpeechModel;
                if (!store.IsPlausiblyComplete(model))
                {
                    store.Delete(model);
                    return new InvalidOperationException(
                        load.Message + " The cached file looked incomplete and has been removed; run Transcribe again to download it.", load);
                }

                return new InvalidOperationException(
                    load.Message + $" If this keeps happening, delete the file at {load.ModelPath} to download it again.", load);
            case DllNotFoundException:
                return new InvalidOperationException("The speech recognition library could not be loaded. " + error.Message, error);
            default:
                return error;
        }
    }

    // One phase of a job's progress mapped into [offset, offset + scale] of the whole bar.
    private sealed class PhaseProgress(IProgress<double> inner, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => inner.Report(offset + Math.Clamp(value, 0, 1) * scale);
    }
}
