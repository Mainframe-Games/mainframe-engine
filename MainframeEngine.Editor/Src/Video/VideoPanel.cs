using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Video mode's view (<c>Assets/Editor/video.rml</c>), over the viewport panel's view area below its tab strip and shown
/// only in video mode (<see cref="EditorWorkspace.SetMode"/>). Without a video tab it shows the empty state: how to open
/// or record a recording, and the recent videos. With one: the preview (fitted to the video's aspect, with black bars)
/// beside the transcript, the notice line, and the timeline, both images of the tab's viewports (<see cref="VideoView"/>).
/// The transcript column is edit-by-text (<see cref="TranscriptPanel"/>). The transcript and timeline splitters resize
/// through <see cref="EditorLayout"/>. Steady-state frames read three element rectangles and allocate nothing; text and
/// sizes are written only when they change.
/// </summary>
public sealed class VideoPanel : EditorDocument
{
    private sealed class RecentRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
    }

    private static readonly RmlStructType<RecentRow> RecentType = new RmlStructType<RecentRow>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name)
        .Member("path", static r => r.Path);

    private readonly List<RecentRow> _recent = [];
    private readonly TranscriptPanel _transcript;
    private readonly RmlEventListener?[] _listeners = new RmlEventListener?[6];
    private RmlDataModel? _model;
    private VideoTab? _tab;
    private int _docVersion = -1;
    private long _keptTenths = -1;
    private long _durationTenths = -1;
    private bool _previewImage;
    private bool _timelineImage;
    private float _appliedTranscriptWidth = float.NaN;
    private float _appliedTimelineHeight = float.NaN;
    private LayoutRect _fittedFor;
    private float _fittedAspect = float.NaN;

    // Bound values.
    private bool _hasTab;
    private bool _showTranscript = true;
    private string _notice = "";
    private bool _noticeFfmpeg;
    private string _kept = "";

    public VideoPanel(EditorWorkspace workspace)
        : base(workspace, "video.rml")
    {
        _transcript = new TranscriptPanel(workspace);
        Visible = false;
    }

    /// <summary>The transcript column's behaviour (edit by text); it drives <c>#video-transcript-words</c> in this document.</summary>
    internal TranscriptPanel Transcript => _transcript;

    /// <summary>The preview area in window dp (empty without a tab).</summary>
    public LayoutRect PreviewRect { get; private set; }

    /// <summary>The timeline area in window dp (empty without a tab).</summary>
    public LayoutRect TimelineRect { get; private set; }

    /// <summary>The transcript area in window dp (empty without a tab, or while View › Transcript hides it).</summary>
    public LayoutRect TranscriptRect { get; private set; }

    /// <summary>"Kept m:ss.t of m:ss.t" for the toolbar; the same string until the kept time changes. Empty without a tab.</summary>
    public string Kept => _kept;

    /// <summary>The recording has a Mainframe Engine sidecar, so Detect Idle Gameplay can run.</summary>
    public bool CanDetectIdle => _tab?.Document.Capture is not null;

    /// <summary>The notice line's text (open warnings, the last operation's result); empty shows no line.</summary>
    public string NoticeText => _notice;

    /// <summary>The notice line shows its Editor Settings link: the notice says ffmpeg was not found.</summary>
    public bool NoticeHasSettingsLink => _noticeFfmpeg;

    protected override void OnReady()
    {
        _model = CreateDataModel("video")
            .BindList("recent", _recent, RecentType)
            .Bind("has_tab", this, static p => p._hasTab)
            .Bind("show_transcript", this, static p => p._showTranscript)
            .Bind("notice", this, static p => p._notice)
            .Bind("notice_ffmpeg", this, static p => p._noticeFfmpeg);
    }

    // After every load and hot reload: the splitters' listeners, and every value written again on the next tick.
    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        var vertical = document.GetElementById("video-split-v");
        var horizontal = document.GetElementById("video-split-h");
        _listeners[0] = vertical.AddEventListener("drag", e => DragSplitter(vertical: true, e));
        _listeners[1] = horizontal.AddEventListener("drag", e => DragSplitter(vertical: false, e));
        _listeners[2] = vertical.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
        _listeners[3] = horizontal.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
        _listeners[4] = vertical.AddEventListener("dragend", EndSplitterDrag);
        _listeners[5] = horizontal.AddEventListener("dragend", EndSplitterDrag);
        _transcript.Attach(document);
        _previewImage = false;
        _timelineImage = false;
        _appliedTranscriptWidth = float.NaN;
        _appliedTimelineHeight = float.NaN;
        _fittedFor = default;
        _fittedAspect = float.NaN;
        _model?.DirtyAll();
    }

    private void DragSplitter(bool vertical, RmlEvent e)
    {
        if (Rect is not { } rect)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var half = EditorLayout.SplitterSize * 0.5f;
        if (vertical)
            Workspace.Layout.SetVideoTranscriptWidth(rect.Right - (float)(e.GetParameter("mouse_x", 0.0) / scale) - half);
        else
            Workspace.Layout.SetVideoTimelineHeight(rect.Bottom - (float)(e.GetParameter("mouse_y", 0.0) / scale) - half);
        ApplySizes();
    }

    private void EndSplitterDrag(RmlEvent e)
    {
        e.CurrentElement.SetClass("dragging", false);
        Workspace.SaveLayout();
    }

    /// <summary>Shows the preview's and the timeline's images once their textures are published.</summary>
    public void SetImages(bool preview, bool timeline)
    {
        if (!IsLoaded)
            return;
        if (preview != _previewImage)
        {
            _previewImage = preview;
            SetImage("video-preview-image", preview ? "engine://" + VideoView.PreviewTexture : null);
        }

        if (timeline != _timelineImage)
        {
            _timelineImage = timeline;
            SetImage("video-timeline-image", timeline ? "engine://" + VideoView.TimelineTexture : null);
        }
    }

    private void SetImage(string id, string? source)
    {
        var image = Document.GetElementById(id);
        if (image.IsNull)
            return;
        if (source is null)
            image.RemoveAttribute("src");
        else
            image.SetAttribute("src", source);
    }

    /// <summary>Every frame (from the <see cref="VideoView"/>): follows the shown video tab, or shows the empty state.</summary>
    public void Tick(VideoTab? tab)
    {
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _docVersion = -1;
            _keptTenths = -1;
            _durationTenths = -1;
            _fittedFor = default;
        }

        UpdateKept(tab);
        if (!IsLoaded)
            return;
        var dirty = false;
        if (_hasTab != tab is not null)
        {
            _hasTab = tab is not null;
            _model?.Dirty("has_tab");
            dirty = true;
        }

        var notice = tab?.Notice ?? "";
        if (!ReferenceEquals(notice, _notice) && !string.Equals(notice, _notice, StringComparison.Ordinal))
        {
            _notice = notice;
            _model?.Dirty("notice");
        }

        var noticeFfmpeg = tab?.NoticeNeedsFfmpegSetting == true;
        if (noticeFfmpeg != _noticeFfmpeg)
        {
            _noticeFfmpeg = noticeFfmpeg;
            _model?.Dirty("notice_ffmpeg");
        }

        if (_showTranscript != Workspace.Layout.Settings.VideoShowTranscript)
        {
            _showTranscript = Workspace.Layout.Settings.VideoShowTranscript;
            _model?.Dirty("show_transcript");
            dirty = true;
        }

        if (_showTranscript)
            _transcript.Update(tab);
        else
            _transcript.Follow(tab); // hidden: builds nothing, but lets go of a closed tab
        if (tab is null)
        {
            PreviewRect = default;
            TimelineRect = default;
            TranscriptRect = default;
            UpdateRecent();
            return;
        }

        ApplySizes();
        if (dirty)
            return; // the layout changes on this frame's update; the rectangles are read on the next
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        PreviewRect = RectOf("video-preview", scale);
        TimelineRect = RectOf("video-timeline", scale);
        TranscriptRect = _showTranscript ? RectOf("video-transcript", scale) : default;
        FitPreview(tab);
    }

    // The kept duration and its label, rebuilt only when the partition changed and the label would read differently.
    private void UpdateKept(VideoTab? tab)
    {
        if (tab is null)
        {
            _kept = "";
            return;
        }

        var document = tab.Document;
        if (document.Version == _docVersion)
            return;
        _docVersion = document.Version;
        var segments = document.Segments.Segments;
        var kept = 0.0;
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Enabled)
                kept += segments[i].Duration;
        var keptTenths = (long)Math.Round(kept * 10, MidpointRounding.AwayFromZero);
        var durationTenths = (long)Math.Round(document.Duration * 10, MidpointRounding.AwayFromZero);
        if (keptTenths == _keptTenths && durationTenths == _durationTenths)
            return;
        _keptTenths = keptTenths;
        _durationTenths = durationTenths;
        _kept = $"Kept {VideoFormat.Clock(kept)} of {VideoFormat.Clock(document.Duration)}";
    }

    // The transcript's width and the timeline's height, from the layout settings (splitter drags, a loaded layout).
    private void ApplySizes()
    {
        var settings = Workspace.Layout.Settings;
        if (settings.VideoTranscriptWidth != _appliedTranscriptWidth && Document.GetElementById("video-transcript") is { IsNull: false } transcript)
        {
            _appliedTranscriptWidth = settings.VideoTranscriptWidth;
            transcript.SetProperty("width", RmlText.Dp(_appliedTranscriptWidth));
        }

        if (settings.VideoTimelineHeight != _appliedTimelineHeight && Document.GetElementById("video-timeline") is { IsNull: false } timeline)
        {
            _appliedTimelineHeight = settings.VideoTimelineHeight;
            timeline.SetProperty("height", RmlText.Dp(_appliedTimelineHeight));
        }
    }

    // Letterboxes the preview image to the video's aspect inside the preview area (black bars on the area's background).
    private void FitPreview(VideoTab tab)
    {
        var area = PreviewRect;
        var aspect = tab.Preview.Height > 0 ? (float)tab.Preview.Width / tab.Preview.Height : 16f / 9f;
        if (area == _fittedFor && aspect == _fittedAspect)
            return;
        _fittedFor = area;
        _fittedAspect = aspect;
        if (area.Width <= 1 || area.Height <= 1 || Document.GetElementById("video-preview-image") is not { IsNull: false } image)
            return;
        var (w, h) = area.Width / area.Height > aspect ? (area.Height * aspect, area.Height) : (area.Width, area.Width / aspect);
        image.SetProperty("left", RmlText.Dp((area.Width - w) * 0.5f));
        image.SetProperty("top", RmlText.Dp((area.Height - h) * 0.5f));
        image.SetProperty("width", RmlText.Dp(w));
        image.SetProperty("height", RmlText.Dp(h));
    }

    private LayoutRect RectOf(string id, float scale)
    {
        var element = Document.GetElementById(id);
        if (element.IsNull)
            return default;
        var b = element.Bounds;
        return new LayoutRect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale);
    }

    // The empty state's rows follow EditorSettings.RecentVideos; compared in place, so an unchanged list costs no allocation.
    private void UpdateRecent()
    {
        var recent = Workspace.Settings.RecentVideos;
        var same = recent.Count == _recent.Count;
        for (var i = 0; same && i < recent.Count; i++)
            same = string.Equals(recent[i], _recent[i].Path, StringComparison.Ordinal);
        if (same)
            return;
        _recent.Clear();
        for (var i = 0; i < recent.Count; i++)
            _recent.Add(new RecentRow { Index = i, Name = Path.GetFileName(recent[i]), Path = recent[i] });
        _model?.Dirty("recent");
    }
}
