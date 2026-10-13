using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// A video-mode tab: one recording being trimmed. It owns the <see cref="VideoDocument"/>, the gapless
/// <see cref="VideoPreview"/>, the timeline (its view, state, controller and canvas, drawn in
/// <see cref="TimelineViewport"/>) and the long jobs that run on it. The session ticks only the active tab, and pauses
/// one that loses focus (<see cref="Deactivate"/>), so a hidden tab never plays or decodes.
/// </summary>
public sealed class VideoTab : IEditorTab
{
    /// <summary>A playhead this close to the end counts as at the end: Play then starts over from 0.</summary>
    private const double EndTolerance = 0.01;

    private int _previewVersion;
    private bool _disposed;

    internal VideoTab(VideoDocument document, VideoPreview preview, FfmpegLocation? ffmpeg, int number)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        Ffmpeg = ffmpeg;
        View = new VideoTimelineView();
        View.Reset(document.Duration);
        State = new VideoCanvasState();
        Controller = new VideoTimelineController(document, View, State) { Seek = Seek }; // exactly one per document
        Timeline = new VideoTimelineCanvas(document, View, State) { Name = "Timeline" };
        TimelineViewport = new SubViewport
        {
            Name = $"VideoTimeline{number}",
            Disable3D = true,
            UpdateMode = SubViewportUpdateMode.Disabled,
            ClearColor = System.Drawing.Color.FromArgb(255, 0x12, 0x12, 0x15),
            Width = 16,
            Height = 16,
        };
        TimelineViewport.AddChild(Timeline);
        Notice = document.Warnings.Count > 0 ? string.Join(" ", document.Warnings) : null;
        _previewVersion = document.Version;
        // A paused preview shows nothing until a frame is decoded: scrub to the start so the picture is there at once.
        // First, so a throw here leaks neither the waveform job nor the document subscription.
        Preview.Seek(0);
        if (ffmpeg is not null)
            WaveformJob = VideoJob.Start("waveform",
                (p, ct) => new WaveformExtractor(ffmpeg).ExtractAsync(document.SourcePath, document.Duration, p, ct));
        document.DirtyChanged += OnDirtyChanged;
    }

    public EditorMode Mode => EditorMode.Video;

    public VideoDocument Document { get; }

    public VideoPreview Preview { get; }

    /// <summary>The ffmpeg the tab was opened with; null in tests, where no job starts.</summary>
    public FfmpegLocation? Ffmpeg { get; }

    public VideoTimelineView View { get; }

    public VideoCanvasState State { get; }

    public VideoTimelineController Controller { get; }

    public VideoTimelineCanvas Timeline { get; }

    /// <summary>The timeline canvas's only parent; the session keeps it in the tree and the video view publishes it.</summary>
    public SubViewport TimelineViewport { get; }

    /// <summary>The notice line: open warnings and the last operation's result; null shows nothing.</summary>
    public string? Notice { get; set; }

    /// <summary>Extracting the waveform; null once it has been applied (or when the tab has no ffmpeg).</summary>
    public VideoJob<Waveform>? WaveformJob { get; private set; }

    public VideoJob<IReadOnlyList<SilenceSpan>>? SilenceJob { get; set; }

    /// <summary>
    /// The settings <see cref="SilenceJob"/> detects with, captured when it starts: its cuts are laid out with the same
    /// padding and minimum keep even if the settings change while it runs.
    /// </summary>
    public DetectionSettings? SilenceSettings { get; set; }

    public VideoJob<IReadOnlyList<Word>>? TranscribeJob { get; set; }

    /// <summary>What <see cref="TranscribeJob"/> was started with; null while none runs.</summary>
    public TranscribeRun? TranscribeRun { get; set; }

    public VideoJob<string>? ExportJob { get; set; }

    /// <summary>The transcript word under the playhead (<see cref="TranscriptView.IndexAtTime"/>), or -1.</summary>
    public int CurrentWord { get; private set; } = -1;

    public string? FilePath => Document.FilePath;

    public string DisplayName => Path.GetFileName(Document.FilePath ?? Document.DefaultProjectPath);

    public string Title => IsDirty ? DisplayName + "*" : DisplayName;

    public bool IsDirty => Document.IsDirty;

    public string IconClasses => "icon icon-video icon-audio";

    public string Tooltip => $"{DisplayName} — video ({(IsDirty ? "unsaved changes" : "saved")}) — {Document.FilePath ?? Document.SourcePath}";

    /// <summary>The dirty state flipped (the tab strip and the FileSystem badge follow).</summary>
    internal event Action<VideoTab>? DirtyChanged;

    /// <summary>
    /// Where a project is written: the path with any other extension (a video's, <c>.cutback</c>, none) replaced by
    /// <c>.mfcut</c>, and a doubled <c>name.mfcut.mfcut</c> (macOS save panels return these) collapsed. Saving can therefore
    /// never write over a recording: the source is opened read-only and never modified.
    /// </summary>
    public static string NormalizeProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var extension = ProjectSerializer.FileExtension;
        while (full.EndsWith(extension + extension, StringComparison.OrdinalIgnoreCase))
            full = full[..^extension.Length];
        if (Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase))
            return full;
        return Path.GetExtension(full).Length == 0 ? full + extension : Path.ChangeExtension(full, extension);
    }

    /// <summary>Pauses when playing; otherwise plays from the playhead (from 0 when the playhead is at the end).</summary>
    public void TogglePlay()
    {
        if (Preview.IsPlaying)
        {
            Preview.Pause();
            return;
        }

        var from = State.Playhead >= Document.Duration - EndTolerance ? 0 : State.Playhead;
        Preview.Play(Document.Segments.Segments, from, PreviewVersion());
        State.Playhead = Preview.Position;
    }

    /// <summary>Pauses and returns to the start.</summary>
    public void Stop()
    {
        Preview.Pause();
        Seek(0);
    }

    /// <summary>Moves the playhead (the ruler scrubs through here): playback restarts there, or the frame there is shown.</summary>
    public void Seek(double time)
    {
        State.Playhead = time;
        if (Preview.IsPlaying)
            Preview.Play(Document.Segments.Segments, time, PreviewVersion());
        else
            Preview.Seek(time);
    }

    /// <summary>Main thread, every frame while the tab is active: applies the waveform, drives the preview and the playhead.</summary>
    internal void Tick()
    {
        if (WaveformJob is { IsDone: true } job)
        {
            if (job.Task.IsCompletedSuccessfully)
                Document.Waveform = job.Task.Result;
            else if (job.Task.IsFaulted)
                AddNotice($"Waveform unavailable: {job.Task.Exception?.GetBaseException().Message}");
            job.Dispose();
            WaveformJob = null;
        }

        Preview.Update(Document.Segments.Segments, PreviewVersion());
        State.Playhead = Preview.Position;
        CurrentWord = TranscriptView.IndexAtTime(Document.Transcript, Preview.Position);
        if (Preview.IsPlaying)
            View.KeepVisible(Preview.Position);
    }

    /// <summary>The tab is no longer active (another tab, or the other mode): playback stops and the views pause.</summary>
    internal void Deactivate()
    {
        Preview.Pause();
        Controller.CancelGesture();
        TimelineViewport.UpdateMode = SubViewportUpdateMode.Disabled;
        Preview.Viewport.UpdateMode = SubViewportUpdateMode.Disabled;
    }

    /// <summary>
    /// The partition version the preview plays. It follows <see cref="VideoDocument.Version"/> except during a boundary
    /// drag, when it holds: every change of it while playing restarts the preview (decoders killed and respawned, a new
    /// audio ring), which must happen once when the drag ends, not on every pointer move.
    /// </summary>
    private int PreviewVersion()
    {
        if (!Document.Editor.IsDragging)
            _previewVersion = Document.Version;
        return _previewVersion;
    }

    private void AddNotice(string text) => Notice = string.IsNullOrEmpty(Notice) ? text : Notice + " " + text;

    private void OnDirtyChanged() => DirtyChanged?.Invoke(this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Document.DirtyChanged -= OnDirtyChanged;
        // Cancelling kills each job's ffmpeg; the export job deletes its partial file when it sees the cancellation.
        WaveformJob?.Dispose();
        SilenceJob?.Dispose();
        TranscribeJob?.Dispose();
        ExportJob?.Dispose();
        WaveformJob = null;
        SilenceJob = null;
        SilenceSettings = null;
        TranscribeJob = null;
        TranscribeRun = null;
        ExportJob = null;
        Preview.Dispose(); // kills every decoder and the voice, frees its viewport
        if (!TimelineViewport.IsFreed)
            TimelineViewport.Free();
    }
}

/// <summary>
/// The choices a transcription job was started with, captured when it starts so it finishes consistently even if the
/// settings change while it runs: the model, whether it downloads that model first (the toolbar says so, and the
/// progress bar's first half is the download), and whether Remove Filler Words follows.
/// </summary>
public sealed class TranscribeRun(WhisperModel model, bool downloads, bool thenRemoveFillers)
{
    public WhisperModel Model { get; } = model;

    public bool Downloads { get; } = downloads;

    /// <summary>Set by Remove Filler Words when it asks for a transcript: the filler cuts are made as soon as it arrives.</summary>
    public bool ThenRemoveFillers { get; set; } = thenRemoveFillers;
}
