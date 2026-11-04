using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// A video-mode tab: one <c>.mftimeline</c> (docs/design/future/video-nle.md). It owns the document, the timeline canvas
/// and its controller (drawn in <see cref="CanvasViewport"/>), the Timeline viewer (a <see cref="VideoPreview"/> of the
/// timeline through its export graph), the Source viewer, the cut editor of the selected clip and the export job. The
/// session ticks only the active tab, and pauses one that loses focus.
/// </summary>
public sealed class TimelineTab : IEditorTab
{
    private static int _counter;

    private readonly IVideoDecoderFactory _decoders;
    private readonly Func<AudioServer?> _audio;
    private readonly Action<SubViewport> _attach;
    private readonly int _number;
    private IReadOnlyList<Segment> _playSegments = [];
    private int _playVersion = -1;
    private bool _disposed;

    internal TimelineTab(TimelineDocument document, IVideoDecoderFactory decoders, Func<AudioServer?> audio, MediaCache? cache,
        FfmpegLocation? ffmpeg, Action<SubViewport> attach)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
        Cache = cache;
        Ffmpeg = ffmpeg;
        _number = ++_counter;

        View = new TimelineViewState();
        View.Fit(TimelineLayout.Duration(document.Timeline));
        Controller = new TimelineController(document, View) { Seek = Seek };
        Canvas = new TimelineCanvas(() => Document.Timeline, View, cache, document.Resolve) { Name = "Timeline" };
        CanvasViewport = NewViewport($"Timeline{_number}");
        CanvasViewport.AddChild(Canvas);

        Cut = new ClipCutTarget(document, cache);
        CutView = new VideoTimelineView();
        CutState = new VideoCanvasState();
        CutController = new VideoTimelineController(Cut, CutView, CutState) { Seek = SeekSource };
        CutCanvas = new VideoTimelineCanvas(Cut, CutView, CutState) { Name = "Cuts" };
        CutViewport = NewViewport($"Cuts{_number}");
        CutViewport.AddChild(CutCanvas);

        Source = new SourceViewer(decoders, audio, attach);
        Preview = NewPreview();
        document.DirtyChanged += OnDirtyChanged;
        document.Changed += OnChanged;
    }

    public EditorMode Mode => EditorMode.Video;

    public TimelineDocument Document { get; }

    public Timeline Timeline => Document.Timeline;

    public MediaCache? Cache { get; }

    /// <summary>The ffmpeg the tab opened with; null in tests (no job starts).</summary>
    public FfmpegLocation? Ffmpeg { get; }

    public TimelineViewState View { get; }

    public TimelineController Controller { get; }

    public TimelineCanvas Canvas { get; }

    public SubViewport CanvasViewport { get; }

    /// <summary>The Timeline viewer: the timeline through its export graph at preview size.</summary>
    public VideoPreview Preview { get; private set; }

    public TimelineProgram Program { get; private set; } = null!;

    public SourceViewer Source { get; }

    /// <summary>The cut editor's target: the selected clip's cut list.</summary>
    public ClipCutTarget Cut { get; }

    public VideoTimelineView CutView { get; }

    public VideoCanvasState CutState { get; }

    public VideoTimelineController CutController { get; }

    public VideoTimelineCanvas CutCanvas { get; }

    public SubViewport CutViewport { get; }

    /// <summary>Which viewer the transport keys and I/O drive: the last one clicked.</summary>
    public bool SourceFocused { get; set; }

    /// <summary>The notice line (the last operation's result, warnings); null shows nothing.</summary>
    public string? Notice
    {
        get;
        set
        {
            field = value;
            NoticeNeedsFfmpegSetting = false;
        }
    }

    public bool NoticeNeedsFfmpegSetting { get; private set; }

    public VideoJob<string>? ExportJob { get; set; }

    /// <summary>Detect Silence on a clip (G20d), and what it started on.</summary>
    public VideoJob<IReadOnlyList<MainframeEngine.Video.Detection.SilenceSpan>>? SilenceJob { get; set; }

    internal ClipJob? SilenceRun { get; set; }

    /// <summary>Transcribe on a clip's media, and what it started on.</summary>
    public VideoJob<IReadOnlyList<Word>>? TranscribeJob { get; set; }

    internal TranscribeRun? TranscribeRun { get; set; }

    /// <summary>The Inspector column shows the Transcript tab (else the Inspector).</summary>
    public bool ShowsTranscript { get; set; }

    public string? FilePath => Document.FilePath;

    public string DisplayName => Path.GetFileName(Document.FilePath);

    public string Title => IsDirty ? DisplayName + "*" : DisplayName;

    public bool IsDirty => Document.IsDirty;

    public string IconClasses => "icon icon-video icon-audio";

    public string Tooltip => $"{DisplayName} — timeline {Timeline.Settings.Width}×{Timeline.Settings.Height} ({(IsDirty ? "unsaved changes" : "saved")}) — {Document.FilePath}";

    internal event Action<TimelineTab>? DirtyChanged;

    internal void ReportNoFfmpeg(string what)
    {
        Notice = VideoFfmpeg.NoticeText(what);
        NoticeNeedsFfmpegSetting = true;
    }

    // ── Transport ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Plays the timeline from the playhead (from the in point, or 0, when nothing follows it); pauses when playing.</summary>
    public void TogglePlay()
    {
        Source.Pause();
        if (Preview.IsPlaying)
        {
            Preview.Pause();
            return;
        }

        var (start, end) = TimelineLayout.Range(Timeline);
        var from = View.Playhead >= end - 0.01 || View.Playhead < start ? start : View.Playhead;
        Preview.Play(PlaySegments(), from, Document.Version);
    }

    public void Pause() => Preview.Pause();

    /// <summary>Moves the playhead: playback restarts there, or the frame there is shown.</summary>
    public void Seek(double time)
    {
        time = Timeline.Rate.Snap(Math.Max(0, time));
        View.Playhead = time;
        if (Preview.IsPlaying)
            Preview.Play(PlaySegments(), time, Document.Version);
        else
            Preview.Seek(time);
    }

    private void SeekSource(double sourceTime)
    {
        // The cut editor's ruler: the playhead goes to where that source time plays, when it does.
        if (Cut.Clip is { } clip && TimelineLayout.TimelineTimeOf(clip, sourceTime) is { } at)
            Seek(at);
    }

    // The preview plays program time [0, end of range): one kept segment over it (a timeline is one continuous program).
    private IReadOnlyList<Segment> PlaySegments()
    {
        if (_playVersion == Document.Version)
            return _playSegments;
        var end = TimelineLayout.Range(Timeline).End;
        _playSegments = end > 0 ? [Segment.Create(0, end, enabled: true, SegmentOrigin.Auto)] : [];
        _playVersion = Document.Version;
        return _playSegments;
    }

    /// <summary>Main thread, every frame while the tab is active: the viewers follow the timeline and the playhead.</summary>
    internal void Tick()
    {
        Program.Timeline = Timeline;
        Preview.Update(PlaySegments(), Document.Version);
        if (Preview.IsPlaying)
        {
            View.Playhead = Preview.Position;
            View.KeepVisible(View.Playhead);
        }

        Source.Tick();
        CutState.Playhead = Cut.Clip is { } clip ? TimelineLayout.SourceTimeAt(clip, Timeline.Rate, View.Playhead) ?? -1 : -1;
    }

    /// <summary>The tab is no longer active: playback stops and the views stop rendering.</summary>
    internal void Deactivate()
    {
        Preview.Pause();
        Source.Pause();
        Controller.CancelGesture();
        CutController.CancelGesture();
        CanvasViewport.UpdateMode = SubViewportUpdateMode.Disabled;
        CutViewport.UpdateMode = SubViewportUpdateMode.Disabled;
        Preview.Viewport.UpdateMode = SubViewportUpdateMode.Disabled;
        if (Source.Preview is { } source)
            source.Viewport.UpdateMode = SubViewportUpdateMode.Disabled;
    }

    // ── Selection and the cut editor ─────────────────────────────────────────────────────────────────────────

    /// <summary>The selected clip the Inspector and the cut editor show: the video one of a linked pair, else the first.</summary>
    public Clip? Primary
    {
        get
        {
            Clip? first = null;
            foreach (var id in View.Selection)
            {
                if (Timeline.FindClip(id) is not { } clip)
                    continue;
                if (Timeline.FindTrack(clip.TrackId)?.Kind == TrackKind.Video)
                    return clip;
                first ??= clip;
            }

            return first;
        }
    }

    /// <summary>Keeps the cut editor on the primary selected clip, framed on its window.</summary>
    internal void SyncCutEditor()
    {
        var primary = Primary is { IsStill: true } ? null : Primary; // a title or image has no cuts
        if (primary?.Id == Cut.ClipId)
            return;
        Cut.Show(primary?.Id);
        if (primary is { } clip)
        {
            CutView.Reset(clip.Media.Duration);
            var pad = Math.Max(0.5, (clip.SourceOut - clip.SourceIn) * 0.05);
            CutView.SetView(clip.SourceIn - pad, clip.SourceOut - clip.SourceIn + 2 * pad);
        }
        else
        {
            CutView.Reset(0);
        }
    }

    // ── Program ──────────────────────────────────────────────────────────────────────────────────────────────

    private VideoPreview NewPreview()
    {
        Program = new TimelineProgram(Timeline, Document.Resolve);
        var preview = new VideoPreview(_decoders, _audio, Program, _number);
        _attach(preview.Viewport);
        preview.Seek(View.Playhead);
        return preview;
    }

    private void OnChanged()
    {
        View.PruneSelection(Timeline);
        // New settings (size, rate) need a preview of that size.
        if (Timeline.Settings != Program.Settings)
        {
            var playing = Preview.IsPlaying;
            Preview.Dispose();
            Preview = NewPreview();
            if (playing)
                Preview.Play(PlaySegments(), View.Playhead, Document.Version);
        }
        else if (!Preview.IsPlaying)
        {
            Program.Timeline = Timeline;
            Preview.Seek(View.Playhead); // the frame under the playhead after the edit
        }
    }

    private void OnDirtyChanged() => DirtyChanged?.Invoke(this);

    private static SubViewport NewViewport(string name) => new()
    {
        Name = name,
        Disable3D = true,
        UpdateMode = SubViewportUpdateMode.Disabled,
        ClearColor = System.Drawing.Color.FromArgb(255, 0x12, 0x12, 0x15),
        Width = 16,
        Height = 16,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Document.DirtyChanged -= OnDirtyChanged;
        Document.Changed -= OnChanged;
        ExportJob?.Dispose();
        ExportJob = null;
        SilenceJob?.Dispose();
        SilenceJob = null;
        SilenceRun = null;
        TranscribeJob?.Dispose();
        TranscribeJob = null;
        TranscribeRun = null;
        Preview.Dispose();
        Source.Dispose();
        if (!CanvasViewport.IsFreed)
            CanvasViewport.Free();
        if (!CutViewport.IsFreed)
            CutViewport.Free();
    }
}
