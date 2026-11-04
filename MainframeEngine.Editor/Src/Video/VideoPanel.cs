using System.Globalization;
using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Video mode's view (<c>Assets/Editor/video.rml</c>, docs/design/future/video-nle.md): the media pool, the Source and
/// Timeline viewers, the Inspector, the notice line, the track headers beside the timeline canvas, and the cut editor. The
/// viewers and canvases are images of the tab's sub-viewports (<see cref="VideoView"/> publishes them and routes the
/// pointer over them); everything else is RmlUi. Without a timeline tab the pool stays and the rest is the empty state.
/// Steady frames read a few element rectangles and write nothing: values are written only when they change.
/// </summary>
public sealed class VideoPanel : EditorDocument
{
    private const float DragThreshold = 4;

    private sealed class PoolRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Meta { get; init; }
        public required string Icon { get; init; }
        public required string Thumb { get; set; }
        public required string Tip { get; init; }
        public required bool Selected { get; set; }
    }

    private sealed class TrackRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required bool Video { get; init; }
        public required bool Locked { get; init; }
        public required bool Enabled { get; init; }
        public required bool Muted { get; init; }
        public required bool Solo { get; init; }
        public required bool Target { get; init; }
        public required float Height { get; init; }
    }

    private sealed class RateRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required bool On { get; set; }
    }

    private static readonly RmlStructType<PoolRow> PoolType = new RmlStructType<PoolRow>()
        .Member("index", static r => r.Index).Member("name", static r => r.Name).Member("meta", static r => r.Meta)
        .Member("icon", static r => r.Icon).Member("thumb", static r => r.Thumb).Member("tip", static r => r.Tip)
        .Member("selected", static r => r.Selected);

    private static readonly RmlStructType<TrackRow> TrackType = new RmlStructType<TrackRow>()
        .Member("id", static r => r.Id).Member("name", static r => r.Name).Member("video", static r => r.Video)
        .Member("locked", static r => r.Locked).Member("enabled", static r => r.Enabled).Member("muted", static r => r.Muted)
        .Member("solo", static r => r.Solo).Member("target", static r => r.Target).Member("height", static r => r.Height);

    private static readonly RmlStructType<RateRow> RateType = new RmlStructType<RateRow>()
        .Member("index", static r => r.Index).Member("name", static r => r.Name).Member("on", static r => r.On);

    /// <summary>Frame rates the Inspector offers.</summary>
    public static readonly FrameRate[] Rates = [new(24, 1), new(25, 1), new(30, 1), new(50, 1), new(60, 1)];

    /// <summary>Size presets the Inspector offers (9:16, 16:9, 1:1, 4K).</summary>
    public static readonly (int Width, int Height)[] SizePresets = [(1080, 1920), (1920, 1080), (1080, 1080), (3840, 2160)];

    private readonly List<PoolRow> _pool = [];
    private readonly List<TrackRow> _tracks = [];
    private readonly List<RateRow> _rates = [.. Rates.Select((r, i) => new RateRow { Index = i, Name = r.ToFfmpeg(), On = false })];
    private readonly List<RmlEventListener> _listeners = [];
    private RmlDataModel? _model;
    private TimelineTab? _tab;
    private MediaPool? _mediaPool;
    private DebouncedFileWatcher? _watcher;
    private volatile bool _poolChanged;
    private int _poolShown = -1;
    private int _cacheShown = -1;
    private int _selected = -1;
    private int _pressedRow = -1;
    private (float X, float Y) _pressedAt;
    private Timeline? _shownTimeline;
    private MediaRef? _sourceMedia;
    private int _shownSelection = -1;
    private long _sourceTenths = -2;
    private long _programTenths = -2;
    private float _appliedTimelineHeight = float.NaN;
    private float _shownScroll = float.NaN;
    private (LayoutRect Area, float Aspect) _fittedSource;
    private (LayoutRect Area, float Aspect) _fittedProgram;
    private bool _sourceImage;
    private bool _programImage;
    private bool _timelineImage;
    private bool _cutsImage;

    // Bound values.
    private bool _hasTab;
    private string _notice = "";
    private bool _noticeFfmpeg;
    private string _poolPath = "";
    private bool _poolUp;
    private string _sourceName = "";
    private string _sourceTime = "";
    private float _sourceInPct;
    private float _sourceRangePct;
    private float _sourcePosPct;
    private bool _sourcePlaying;
    private bool _sourceFocused;
    private string _programTitle = "";
    private string _programTime = "";
    private bool _programPlaying;
    private string _timecode = "";
    private float _headersTop;
    private string _cutsTitle = "";
    private string _inspMode = "none";
    private string _inspTitle = "";
    private string _tWidth = "", _tHeight = "", _tBackground = "", _tInOut = "";
    private int _cFit = 1;
    private string _cScale = "", _cX = "", _cY = "", _cCropL = "", _cCropR = "", _cCropT = "", _cCropB = "", _cOpacity = "", _cWindow = "", _cGain = "";
    private bool _cEnabled = true;

    public VideoPanel(EditorWorkspace workspace)
        : base(workspace, "video.rml")
    {
        Visible = false;
    }

    public LayoutRect SourceRect { get; private set; }

    public LayoutRect SourceBarRect { get; private set; }

    public LayoutRect ProgramRect { get; private set; }

    /// <summary>The timeline canvas area in window dp (empty without a tab).</summary>
    public LayoutRect TimelineRect { get; private set; }

    /// <summary>The cut editor's canvas area in window dp.</summary>
    public LayoutRect CutsRect { get; private set; }

    public LayoutRect PoolRect { get; private set; }

    /// <summary>
    /// The media pool's listing for the open project (null without one). The pool follows the project only in video mode
    /// or when a video command asks for it here, so a project opened in game mode never gets an <c>Assets/Video</c>.
    /// </summary>
    public MediaPool? Pool
    {
        get
        {
            FollowProject();
            return _mediaPool;
        }
    }

    public string NoticeText => _notice;

    /// <summary>The cut editor's header text.</summary>
    public string CutsTitle => _cutsTitle;

    public bool NoticeHasSettingsLink => _noticeFfmpeg;

    /// <summary>The pool row selected, or null.</summary>
    public MediaPoolItem? SelectedItem => _mediaPool is { } pool && _selected >= 0 && _selected < pool.Items.Count ? pool.Items[_selected] : null;

    protected override void OnReady()
    {
        _model = CreateDataModel("video")
            .Bind("has_tab", this, static p => p._hasTab)
            .Bind("notice", this, static p => p._notice)
            .Bind("notice_ffmpeg", this, static p => p._noticeFfmpeg)
            .BindList("pool", _pool, PoolType)
            .Bind("pool_empty", this, static p => p._pool.Count == 0)
            .Bind("pool_path", this, static p => p._poolPath)
            .Bind("pool_up", this, static p => p._poolUp)
            .Bind("source_name", this, static p => p._sourceName)
            .Bind("source_time", this, static p => p._sourceTime)
            .Bind("source_in_pct", this, static p => p._sourceInPct)
            .Bind("source_range_pct", this, static p => p._sourceRangePct)
            .Bind("source_pos_pct", this, static p => p._sourcePosPct)
            .Bind("source_playing", this, static p => p._sourcePlaying)
            .Bind("source_focused", this, static p => p._sourceFocused)
            .Bind("program_title", this, static p => p._programTitle)
            .Bind("program_time", this, static p => p._programTime)
            .Bind("program_playing", this, static p => p._programPlaying)
            .Bind("timecode", this, static p => p._timecode)
            .Bind("headers_top", this, static p => p._headersTop)
            .BindList("tracks", _tracks, TrackType)
            .BindList("rates", _rates, RateType)
            .Bind("cuts_title", this, static p => p._cutsTitle)
            .Bind("insp_mode", this, static p => p._inspMode)
            .Bind("insp_title", this, static p => p._inspTitle)
            .Bind("t_width", this, static p => p._tWidth)
            .Bind("t_height", this, static p => p._tHeight)
            .Bind("t_background", this, static p => p._tBackground)
            .Bind("t_inout", this, static p => p._tInOut)
            .Bind("c_fit", this, static p => p._cFit)
            .Bind("c_scale", this, static p => p._cScale)
            .Bind("c_x", this, static p => p._cX)
            .Bind("c_y", this, static p => p._cY)
            .Bind("c_crop_l", this, static p => p._cCropL)
            .Bind("c_crop_r", this, static p => p._cCropR)
            .Bind("c_crop_t", this, static p => p._cCropT)
            .Bind("c_crop_b", this, static p => p._cCropB)
            .Bind("c_opacity", this, static p => p._cOpacity)
            .Bind("c_window", this, static p => p._cWindow)
            .Bind("c_gain", this, static p => p._cGain)
            .Bind("c_enabled", this, static p => p._cEnabled)
            .Event("preset", e => SetSize(SizePresets[Math.Clamp(e.GetArgument(0).GetInt32(), 0, SizePresets.Length - 1)]))
            .Event("rate", e => SetRate(Rates[Math.Clamp(e.GetArgument(0).GetInt32(), 0, Rates.Length - 1)]))
            .Event("fit", e => SetFit(e.GetArgument(0).GetInt32()))
            .Event("toggle_enabled", _ => ToggleEnabled());
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener.Remove();
        _listeners.Clear();
        var root = document.AsElement();
        Add(root.AddEventListener("dblclick", OnDoubleClick));
        Add(root.AddEventListener("mousedown", OnPress));
        Add(root.AddEventListener("mousemove", OnMove));
        Add(root.AddEventListener("mouseup", _ => _pressedRow = -1));
        Add(root.AddEventListener("change", e =>
        {
            if (e.GetParameter("linebreak", false))
                CommitField(e.Target);
        }));
        Add(root.AddEventListener("blur", e => CommitField(e.Target), inCapturePhase: true));
        var splitter = document.GetElementById("video-split-h");
        if (!splitter.IsNull)
        {
            Add(splitter.AddEventListener("drag", DragSplitter));
            Add(splitter.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true)));
            Add(splitter.AddEventListener("dragend", e =>
            {
                e.CurrentElement.SetClass("dragging", false);
                Workspace.SaveLayout();
            }));
        }

        _sourceImage = _programImage = _timelineImage = _cutsImage = false;
        _appliedTimelineHeight = float.NaN;
        _shownScroll = float.NaN;
        _fittedSource = _fittedProgram = default;
        _model?.DirtyAll();

        void Add(RmlEventListener? listener)
        {
            if (listener is not null)
                _listeners.Add(listener);
        }
    }

    // ── Every frame ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every frame (from the <see cref="VideoView"/>): follows the project's pool and the shown tab.</summary>
    public void Tick(TimelineTab? tab)
    {
        if (Workspace.Mode == EditorMode.Video)
            FollowProject();
        if (!IsLoaded)
            return;
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _shownTimeline = null;
            _shownSelection = -1;
            _sourceTenths = _programTenths = -2;
            _fittedSource = _fittedProgram = default;
        }

        if (Set(ref _hasTab, tab is not null, "has_tab"))
            return; // the layout changes on this frame's update; the rectangles are read on the next
        UpdatePool();
        Set(ref _notice, tab?.Notice ?? "", "notice");
        Set(ref _noticeFfmpeg, tab?.NoticeNeedsFfmpegSetting == true, "notice_ffmpeg");
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        PoolRect = RectOf("pool", scale);
        if (tab is null)
        {
            SourceRect = SourceBarRect = ProgramRect = TimelineRect = CutsRect = default;
            return;
        }

        ApplyTimelineHeight();
        UpdateTimeline(tab);
        UpdateViewers(tab);
        SourceRect = RectOf("source-view", scale);
        SourceBarRect = RectOf("source-bar", scale);
        ProgramRect = RectOf("program-view", scale);
        TimelineRect = RectOf("video-timeline-canvas", scale);
        CutsRect = RectOf("video-cuts-canvas", scale);
        Fit("source-image", SourceRect, tab.Source.Preview is { } s ? (float)s.Width / s.Height : 16f / 9f, ref _fittedSource);
        Fit("program-image", ProgramRect, (float)tab.Preview.Width / tab.Preview.Height, ref _fittedProgram);
    }

    /// <summary>Shows the viewers' and canvases' images once their textures are published.</summary>
    public void SetImages(bool source, bool program, bool timeline, bool cuts)
    {
        if (!IsLoaded)
            return;
        SetImage("source-image", source, VideoView.SourceTexture, ref _sourceImage);
        SetImage("program-image", program, VideoView.ProgramTexture, ref _programImage);
        SetImage("timeline-image", timeline, VideoView.TimelineTexture, ref _timelineImage);
        SetImage("cuts-image", cuts, VideoView.CutsTexture, ref _cutsImage);
    }

    private void SetImage(string id, bool shown, string texture, ref bool current)
    {
        if (shown == current)
            return;
        current = shown;
        var image = Document.GetElementById(id);
        if (image.IsNull)
            return;
        if (shown)
            image.SetAttribute("src", "engine://" + texture);
        else
            image.RemoveAttribute("src");
        image.SetClass("off", !shown);
    }

    // ── Media pool ───────────────────────────────────────────────────────────────────────────────────────────

    // The pool follows the open project: a new listing and a watcher on its Assets/Video.
    private void FollowProject()
    {
        var root = Workspace.Session.ProjectRoot;
        if (_mediaPool is { } pool && root is not null && EditorSession.PathsEqual(pool.ProjectRoot, root))
            return;
        _watcher?.Dispose();
        _watcher = null;
        _mediaPool = root is null ? null : new MediaPool(root);
        _selected = -1;
        _poolShown = -1;
        if (_mediaPool is { } created)
        {
            try
            {
                created.EnsureFolders();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Video] Could not make {created.RootFolder}: {e.Message}");
            }

            created.Refresh();
            try
            {
                _watcher = new DebouncedFileWatcher(created.RootFolder, "*", recursive: true);
                _watcher.Changed += _ => _poolChanged = true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Warning($"[Video] The media pool will not follow file changes: {e.Message}");
            }
        }
    }

    /// <summary>Re-reads the pool's folder (after an import, a new timeline, a new folder).</summary>
    public void RefreshPool()
    {
        if (_mediaPool?.Refresh() == true)
            _poolShown = -1;
    }

    private void UpdatePool()
    {
        if (_poolChanged)
        {
            _poolChanged = false;
            RefreshPool();
            foreach (var open in Workspace.Session.Tabs)
                if (open is TimelineTab timeline)
                    timeline.Document.ForgetResolvedPaths(); // a move in the pool: media paths are looked up again
        }

        var cache = Workspace.VideoView.Cache;
        var cacheVersion = cache?.Version ?? 0;
        if (_mediaPool is not { } pool)
        {
            if (_pool.Count > 0)
            {
                _pool.Clear();
                _model?.Dirty("pool");
                _model?.Dirty("pool_empty");
            }

            return;
        }

        if (_poolShown == pool.Version && _cacheShown == cacheVersion)
            return;
        var rebuilt = _poolShown != pool.Version;
        _poolShown = pool.Version;
        _cacheShown = cacheVersion;
        if (rebuilt)
        {
            _pool.Clear();
            for (var i = 0; i < pool.Items.Count; i++)
            {
                var item = pool.Items[i];
                _pool.Add(new PoolRow
                {
                    Index = i,
                    Name = item.Name,
                    Meta = MetaOf(item),
                    Icon = IconOf(item.Kind),
                    Thumb = "",
                    Tip = TipOf(item),
                    Selected = i == _selected,
                });
            }

            _poolPath = pool.DisplayPath;
            _poolUp = pool.CanGoUp;
            _model?.Dirty("pool_path");
            _model?.Dirty("pool_up");
            _model?.Dirty("pool_empty");
        }

        // Posters arrive in the background (the media cache makes them once per file).
        for (var i = 0; i < _pool.Count; i++)
            if (pool.Items[i].Kind == MediaPoolKind.Video && _pool[i].Thumb.Length == 0 && cache?.PosterOf(MediaCache.KeyFor(pool.Items[i].Path), pool.Items[i].Path, 60) is { } poster)
                _pool[i].Thumb = poster;
        _model?.Dirty("pool");
    }

    private static string MetaOf(MediaPoolItem item)
    {
        if (item.Kind == MediaPoolKind.Folder)
            return "Folder";
        long size;
        try
        {
            size = new FileInfo(item.Path).Length; // disk-io: the editor lists the project's own folder
        }
        catch (IOException)
        {
            size = 0;
        }

        var kind = item.Kind switch
        {
            MediaPoolKind.Video => "Video",
            MediaPoolKind.Audio => "Audio",
            MediaPoolKind.Image => "Image",
            _ => "Timeline",
        };
        return size >= 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} · {size / 1048576.0:0.0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{kind} · {Math.Max(1, size / 1024)} KB");
    }

    private static string IconOf(MediaPoolKind kind) => kind switch
    {
        MediaPoolKind.Folder => "folder",
        MediaPoolKind.Video => "movie",
        MediaPoolKind.Audio => "music",
        MediaPoolKind.Image => "photo",
        _ => "timeline",
    };

    private static string TipOf(MediaPoolItem item) => item.Kind switch
    {
        MediaPoolKind.Folder => $"{item.Name} — double-click to open",
        MediaPoolKind.Timeline => $"{item.Name} — double-click to open the timeline",
        _ => $"{item.Name} — double-click to show it in the Source viewer; drag it onto the timeline",
    };

    private static int PoolRowOf(RmlEvent e) =>
        FindAttribute(e.Target, "data-pool") is { } attr && int.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-pool-up") is "1")
        {
            _mediaPool?.Up();
            _selected = -1;
            return;
        }

        if (PoolRowOf(e) is >= 0 and var row)
        {
            Select(row);
            return;
        }

        if (_tab is not { } tab)
            return;
        if (FindAttribute(e.Target, "data-track-target") is { } target)
        {
            if (tab.Timeline.FindTrack(target) is { } track)
            {
                if (track.Kind == TrackKind.Video)
                    tab.View.TargetVideo = track.Id;
                else
                    tab.View.TargetAudio = track.Id;
                _shownTimeline = null;
            }

            return;
        }

        if (FindAttribute(e.Target, "data-track-lock") is { } locked)
            Track(tab, locked, "Lock Track", t => t with { Locked = !t.Locked });
        else if (FindAttribute(e.Target, "data-track-eye") is { } eye)
            Track(tab, eye, "Show/Hide Track", t => t with { Enabled = !t.Enabled });
        else if (FindAttribute(e.Target, "data-track-mute") is { } mute)
            Track(tab, mute, "Mute Track", t => t with { Muted = !t.Muted });
        else if (FindAttribute(e.Target, "data-track-solo") is { } solo)
            Track(tab, solo, "Solo Track", t => t with { Solo = !t.Solo });
    }

    private void Track(TimelineTab tab, string id, string name, Func<MainframeEngine.Video.Timelines.Track, MainframeEngine.Video.Timelines.Track> change) =>
        Guard(name, () => tab.Document.Apply(name, t => TimelineEdits.SetTrack(t, id, change)));

    private void Select(int row)
    {
        _selected = row;
        for (var i = 0; i < _pool.Count; i++)
            _pool[i].Selected = i == row;
        _model?.Dirty("pool");
    }

    private void OnDoubleClick(RmlEvent e)
    {
        if (PoolRowOf(e) is not (>= 0 and var row) || _mediaPool is not { } pool || row >= pool.Items.Count)
            return;
        var item = pool.Items[row];
        switch (item.Kind)
        {
            case MediaPoolKind.Folder:
                pool.Enter(item.Path);
                _selected = -1;
                break;
            case MediaPoolKind.Timeline:
                Workspace.Commands.OpenTimeline(item.Path);
                break;
            default:
                Workspace.VideoView.ShowInSource(item.Path);
                break;
        }
    }

    private void OnPress(RmlEvent e)
    {
        var button = e.GetParameter("button", 0);
        _pressedRow = button == 0 ? PoolRowOf(e) : -1;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        _pressedAt = ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
        if (button == 1 && FindAttribute(e.Target, "data-track-row") is { } track)
            ShowTrackMenu(track, _pressedAt.X, _pressedAt.Y);
    }

    /// <summary>A track header's right-click menu: add a track of either kind, or remove this one (with its clips).</summary>
    public void ShowTrackMenu(string trackId, float x, float y)
    {
        if (_tab is not { } tab || tab.Timeline.FindTrack(trackId) is not { } track)
            return;
        var kind = track.Kind == TrackKind.Video ? "Video" : "Audio";
        var last = tab.Timeline.TracksOf(track.Kind).Count <= 1;
        Workspace.Popup.Show(
        [
            new MenuItem("Add Video Track", "video.add_video_track", Icon: "plus"),
            new MenuItem("Add Audio Track", "video.add_audio_track", Icon: "plus"),
            MenuItem.Separator,
            new MenuItem($"Remove {kind} Track {track.Name}", "track.remove", null, !track.Locked && !last, Icon: "trash"),
        ], x, y, onCommand: id =>
        {
            if (id == "track.remove")
                RemoveTrack(tab, trackId);
            else if (id is not null)
                Workspace.Commands.Execute(id);
        });
    }

    /// <summary>Removes a track and its clips as one undo step (not the last track of its kind, nor a locked one).</summary>
    public void RemoveTrack(TimelineTab tab, string trackId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Timeline.FindTrack(trackId) is not { } track || tab.Timeline.TracksOf(track.Kind).Count <= 1)
            return;
        Guard("Remove Track", () => tab.Document.Apply("Remove Track", t => TimelineEdits.RemoveTrack(t, trackId)));
        if (tab.View.TargetVideo == trackId)
            tab.View.TargetVideo = null;
        if (tab.View.TargetAudio == trackId)
            tab.View.TargetAudio = null;
        _shownTimeline = null;
    }

    // Dragging a pool row starts the editor's file drag; the VideoView drops it on the timeline when it is released there.
    private void OnMove(RmlEvent e)
    {
        if (_pressedRow < 0 || _mediaPool is not { } pool || _pressedRow >= pool.Items.Count)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var dx = (float)(e.GetParameter("mouse_x", 0.0) / scale) - _pressedAt.X;
        var dy = (float)(e.GetParameter("mouse_y", 0.0) / scale) - _pressedAt.Y;
        if (dx * dx + dy * dy < DragThreshold * DragThreshold)
            return;
        var item = pool.Items[_pressedRow];
        _pressedRow = -1;
        if (item.Kind is MediaPoolKind.Video or MediaPoolKind.Audio)
            Workspace.BeginFileDrag(item.Path);
    }

    // ── Timeline, headers, Inspector ─────────────────────────────────────────────────────────────────────────

    private void UpdateTimeline(TimelineTab tab)
    {
        var timeline = tab.Timeline;
        var selection = SelectionKey(tab);
        if (!ReferenceEquals(timeline, _shownTimeline) || selection != _shownSelection)
        {
            _shownTimeline = timeline;
            _shownSelection = selection;
            RebuildTracks(tab);
            RebuildInspector(tab);
            var settings = timeline.Settings;
            _programTitle = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(tab.Document.FilePath)} · {settings.Width}×{settings.Height} · {settings.FrameRate.Fps:0.##} fps");
            _model?.Dirty("program_title");
            var clip = tab.Cut.Clip;
            _cutsTitle = clip is null ? "Cuts — select a clip to see and edit its cuts (click a region to cut or restore it)" : $"Cuts — {Path.GetFileName(clip.Media.Path)} (click a region to cut or restore it; drag to draw a section)";
            _model?.Dirty("cuts_title");
        }

        if (tab.View.ScrollY != _shownScroll)
        {
            _shownScroll = tab.View.ScrollY;
            _headersTop = -_shownScroll;
            _model?.Dirty("headers_top");
        }
    }

    // A cheap fingerprint of the selection (count and the primary clip), so the Inspector rebuilds when it changes.
    private static int SelectionKey(TimelineTab tab) => HashCode.Combine(tab.View.Selection.Count, tab.Primary?.Id, tab.View.TargetVideo, tab.View.TargetAudio);

    private void RebuildTracks(TimelineTab tab)
    {
        _tracks.Clear();
        var timeline = tab.Timeline;
        var targetVideo = tab.View.TargetOf(timeline, TrackKind.Video);
        var targetAudio = tab.View.TargetOf(timeline, TrackKind.Audio);
        foreach (var lane in tab.View.Lanes(timeline))
        {
            if (timeline.FindTrack(lane.TrackId) is not { } track)
                continue;
            _tracks.Add(new TrackRow
            {
                Id = track.Id,
                Name = track.Name,
                Video = track.Kind == TrackKind.Video,
                Locked = track.Locked,
                Enabled = track.Enabled,
                Muted = track.Muted,
                Solo = track.Solo,
                Target = track.Id == targetVideo || track.Id == targetAudio,
                Height = lane.Height,
            });
        }

        _model?.Dirty("tracks");
    }

    private void RebuildInspector(TimelineTab tab)
    {
        var timeline = tab.Timeline;
        var clip = tab.Primary;
        if (clip is null)
        {
            var s = timeline.Settings;
            _inspMode = "timeline";
            _inspTitle = "Timeline settings";
            _tWidth = I(s.Width);
            _tHeight = I(s.Height);
            _tBackground = s.Background;
            _tInOut = timeline.InPoint is null && timeline.OutPoint is null
                ? "none"
                : $"{VideoFormat.Clock(timeline.InPoint ?? 0)} – {VideoFormat.Clock(timeline.OutPoint ?? TimelineLayout.Duration(timeline))}";
            for (var i = 0; i < _rates.Count; i++)
                _rates[i].On = Rates[i] == s.FrameRate;
        }
        else
        {
            var video = timeline.FindTrack(clip.TrackId)?.Kind == TrackKind.Video;
            _inspMode = video ? "video" : "audio";
            _inspTitle = Path.GetFileName(clip.Media.Path);
            var t = clip.Transform;
            _cFit = (int)t.Fit;
            _cScale = N(t.Scale * 100);
            _cX = N(t.X);
            _cY = N(t.Y);
            _cCropL = N(t.Crop.Left);
            _cCropR = N(t.Crop.Right);
            _cCropT = N(t.Crop.Top);
            _cCropB = N(t.Crop.Bottom);
            _cOpacity = N(t.Opacity * 100);
            _cEnabled = clip.Enabled;
            _cWindow = $"{VideoFormat.Clock(clip.SourceIn)} – {VideoFormat.Clock(clip.SourceOut)} · {VideoFormat.Clock(TimelineLayout.Length(clip, timeline.Rate))} on the timeline";
            _cGain = N(timeline.FindTrack(clip.TrackId)?.GainDb ?? 0);
        }

        _model?.DirtyAll();
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private void CommitField(RmlElement target)
    {
        if (target.GetAttribute("data-field") is not { } field || _tab is not { } tab)
            return;
        Span<char> buffer = stackalloc char[64];
        var n = target.GetValue(buffer);
        var text = n >= 0 && n <= buffer.Length ? new string(buffer[..n]).Trim() : (target.GetAttribute("value") ?? "").Trim();
        if (field == "background")
        {
            var colour = text.StartsWith('#') ? text : "#" + text;
            SetSettings("Background", s => s with { Background = colour.ToUpperInvariant() });
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            _shownTimeline = null; // shows the current value again
            return;
        }

        switch (field)
        {
            case "width":
                SetSize(((int)Math.Round(value), tab.Timeline.Settings.Height));
                break;
            case "height":
                SetSize((tab.Timeline.Settings.Width, (int)Math.Round(value)));
                break;
            case "scale":
                SetTransform("Scale", t => t with { Scale = Math.Clamp(value / 100, 0.01, 100) });
                break;
            case "x":
                SetTransform("Position", t => t with { X = value });
                break;
            case "y":
                SetTransform("Position", t => t with { Y = value });
                break;
            case "crop_l":
                SetTransform("Crop", t => t with { Crop = t.Crop with { Left = Math.Max(0, value) } });
                break;
            case "crop_r":
                SetTransform("Crop", t => t with { Crop = t.Crop with { Right = Math.Max(0, value) } });
                break;
            case "crop_t":
                SetTransform("Crop", t => t with { Crop = t.Crop with { Top = Math.Max(0, value) } });
                break;
            case "crop_b":
                SetTransform("Crop", t => t with { Crop = t.Crop with { Bottom = Math.Max(0, value) } });
                break;
            case "opacity":
                SetTransform("Opacity", t => t with { Opacity = Math.Clamp(value / 100, 0, 1) });
                break;
            case "gain":
                if (tab.Primary is { } clip)
                    Track(tab, clip.TrackId, "Track Gain", t => t with { GainDb = Math.Clamp(value, -60, 24) });
                break;
        }

        _shownTimeline = null;
    }

    /// <summary>Sets the timeline's size (even, 16–8192); the preview follows.</summary>
    public void SetSize((int Width, int Height) size)
    {
        var width = Math.Clamp(size.Width / 2 * 2, 16, 8192);
        var height = Math.Clamp(size.Height / 2 * 2, 16, 8192);
        SetSettings("Timeline Size", s => s with { Width = width, Height = height });
    }

    public void SetRate(FrameRate rate) => SetSettings("Frame Rate", s => s with { FrameRate = rate });

    private void SetSettings(string name, Func<TimelineSettings, TimelineSettings> change)
    {
        if (_tab is not { } tab)
            return;
        Guard(name, () =>
        {
            var settings = change(tab.Timeline.Settings);
            settings.Validate();
            if (settings != tab.Timeline.Settings)
                tab.Document.Apply(name, t => t with { Settings = settings });
        });
        _shownTimeline = null;
    }

    /// <summary>Changes the primary video clip's reframe as one undo step.</summary>
    public void SetTransform(string name, Func<ClipTransform, ClipTransform> change)
    {
        if (_tab is not { } tab || tab.Primary is not { } clip)
            return;
        Guard(name, () => tab.Document.Apply(name, t => TimelineEdits.SetTransform(t, clip.Id, change(clip.Transform))));
        _shownTimeline = null;
    }

    private void SetFit(int fit) => SetTransform("Fit", t => t with { Fit = (FrameFit)Math.Clamp(fit, 0, 2) });

    private void ToggleEnabled()
    {
        if (_tab is not { } tab || tab.Primary is not { } clip)
            return;
        var ids = tab.Timeline.LinkedWith(clip).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var enabled = !clip.Enabled;
        Guard("Enable Clip", () => tab.Document.Apply(enabled ? "Enable Clip" : "Disable Clip",
            t => t with { Clips = [.. t.Clips.Select(c => ids.Contains(c.Id) ? c with { Enabled = enabled } : c)] }));
        _shownTimeline = null;
    }

    // ── Viewers ──────────────────────────────────────────────────────────────────────────────────────────────

    private void UpdateViewers(TimelineTab tab)
    {
        var source = tab.Source;
        Set(ref _sourceFocused, tab.SourceFocused, "source_focused");
        Set(ref _sourcePlaying, source.IsPlaying, "source_playing");
        Set(ref _programPlaying, tab.Preview.IsPlaying, "program_playing");
        if (!ReferenceEquals(source.Media, _sourceMedia))
        {
            _sourceMedia = source.Media;
            Set(ref _sourceName, source.Media is { } m ? Path.GetFileName(m.Path) : "double-click a clip in the media pool", "source_name");
            _sourceTenths = -2;
        }

        var sourceTenths = source.Media is null ? -1 : (long)Math.Round(source.Position * 10);
        if (sourceTenths != _sourceTenths)
        {
            _sourceTenths = sourceTenths;
            var duration = source.Media?.Duration ?? 0;
            _sourceTime = source.Media is null ? "" : VideoFormat.Clock(source.Position);
            var (i, o) = source.Range;
            _sourceInPct = duration > 0 ? (float)(i / duration * 100) : 0;
            _sourceRangePct = duration > 0 ? (float)((o - i) / duration * 100) : 0;
            _sourcePosPct = duration > 0 ? (float)(source.Position / duration * 100) : 0;
            _model?.Dirty("source_time");
            _model?.Dirty("source_in_pct");
            _model?.Dirty("source_range_pct");
            _model?.Dirty("source_pos_pct");
        }

        var programTenths = (long)Math.Round(tab.View.Playhead * 10);
        if (programTenths != _programTenths)
        {
            _programTenths = programTenths;
            _programTime = VideoFormat.Clock(tab.View.Playhead);
            _timecode = VideoFormat.Timecode(tab.View.Playhead, tab.Timeline.Rate);
            _model?.Dirty("program_time");
            _model?.Dirty("timecode");
        }
    }

    /// <summary>The source marks changed (I, O, a newly loaded file): the bar is drawn again.</summary>
    public void SourceChanged() => _sourceTenths = -2;

    // ── Layout ───────────────────────────────────────────────────────────────────────────────────────────────

    private void DragSplitter(RmlEvent e)
    {
        if (Rect is not { } rect)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        Workspace.Layout.SetVideoTimelineHeight(rect.Bottom - (float)(e.GetParameter("mouse_y", 0.0) / scale) - EditorLayout.SplitterSize * 0.5f - 96);
        ApplyTimelineHeight();
    }

    private void ApplyTimelineHeight()
    {
        var height = Workspace.Layout.Settings.VideoTimelineHeight;
        if (height == _appliedTimelineHeight || Document.GetElementById("video-timeline") is not { IsNull: false } element)
            return;
        _appliedTimelineHeight = height;
        element.SetProperty("height", RmlText.Dp(height));
    }

    // Letterboxes a viewer's image to its picture's aspect.
    private void Fit(string id, LayoutRect area, float aspect, ref (LayoutRect Area, float Aspect) fitted)
    {
        if (fitted == (area, aspect))
            return;
        fitted = (area, aspect);
        if (area.Width <= 1 || area.Height <= 1 || Document.GetElementById(id) is not { IsNull: false } image)
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

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        _model?.Dirty(name);
        return true;
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError(what + " failed", e);
        }
    }

    protected override void OnExitTree()
    {
        _watcher?.Dispose();
        _watcher = null;
        base.OnExitTree();
    }
}
