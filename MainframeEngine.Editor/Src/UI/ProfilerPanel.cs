using System.Globalization;
using MainframeEngine.EditorLink;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>A bar of the Profiler tab's frame graph.</summary>
public sealed class ProfilerGraphBar
{
    public int Index { get; set; }
    public int Height { get; set; }
    public int GpuHeight { get; set; }
    public string Css { get; set; } = "bar";
    public string Tip { get; set; } = "";
}

/// <summary>A bar of the timeline (one scope or GPU zone), positioned in percent of the view.</summary>
public sealed class ProfilerTimelineBar
{
    public float Left { get; set; }
    public float Width { get; set; }
    public int Top { get; set; }
    public string Css { get; set; } = "ev";
    public string Label { get; set; } = "";
    public string Tip { get; set; } = "";
}

/// <summary>A lane label of the timeline (Main, Loader, Audio, GPU).</summary>
public sealed class ProfilerLane
{
    public int Top { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A row of the scope table.</summary>
public sealed class ProfilerScopeTableRow
{
    public ushort Marker { get; set; }
    public bool Gpu { get; set; }
    public string Name { get; set; } = "";
    public int Calls { get; set; }
    public double SelfMs { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
    public long Alloc { get; set; }
    public bool HasSource { get; set; }
}

/// <summary>A counter of the selected frame.</summary>
public sealed class ProfilerCounterRow
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Which column orders the scope table (descending).</summary>
public enum ProfilerSort
{
    Self,
    Total,
    Calls,
    Max,
    Alloc,
    Name,
}

/// <summary>
/// The bottom dock's Profiler tab (G10): a source picker (each running game, or the editor itself), Record, Pause, Clear,
/// GPU zones, the spike threshold with auto-freeze, Save and Open trace; a frame graph (CPU bars, GPU marks, 16.7/33.3 ms
/// budgets; click selects a frame, shift-click a range); a timeline of the selection (Main, other threads and GPU lanes of
/// nested bars by category, wheel to zoom, drag to pan, hover for name, duration and self time); the scope table (calls,
/// self, total, max, alloc; sortable; double-click opens the marker's source line) and the frame's counters. Its frames are
/// wire data in a <see cref="ProfilerCapture"/>, never game objects.
/// </summary>
public sealed class ProfilerPanel : EditorDocument
{
    /// <summary>Frames the graph shows at once (the newest, or the window around the selection).</summary>
    public const int GraphBars = 240;

    /// <summary>Timeline bars drawn at most (the shortest are left out past it).</summary>
    public const int MaxTimelineBars = 1500;

    /// <summary>Scope table rows at most.</summary>
    public const int MaxTableRows = 200;

    private const int GraphHeight = 64;
    private const int LaneRow = 16;
    private const int LaneHeader = 16;
    private const float RefreshSeconds = 0.1f;
    private const string ApproximateNote = " (approximate: this GPU samples timestamps at encoder boundaries)";

    private static readonly RmlStructType<ProfilerGraphBar> BarType = new RmlStructType<ProfilerGraphBar>()
        .Member("index", static b => b.Index)
        .Member("h", static b => b.Height)
        .Member("g", static b => b.GpuHeight)
        .Member("css", static b => b.Css)
        .Member("tip", static b => b.Tip);

    private static readonly RmlStructType<ProfilerTimelineBar> EventType = new RmlStructType<ProfilerTimelineBar>()
        .Member("left", static e => e.Left)
        .Member("width", static e => e.Width)
        .Member("top", static e => e.Top)
        .Member("css", static e => e.Css)
        .Member("label", static e => e.Label)
        .Member("tip", static e => e.Tip);

    private static readonly RmlStructType<ProfilerLane> LaneType = new RmlStructType<ProfilerLane>()
        .Member("top", static l => l.Top)
        .Member("name", static l => l.Name);

    private static readonly RmlStructType<ProfilerScopeTableRow> RowType = new RmlStructType<ProfilerScopeTableRow>()
        .Member("name", static r => r.Name)
        .Member("gpu", static r => r.Gpu)
        .Member("calls", static r => r.Calls)
        .Member("self", static r => (float)r.SelfMs)
        .Member("total", static r => (float)r.TotalMs)
        .Member("max", static r => (float)r.MaxMs)
        .Member("alloc", static r => (float)(r.Alloc / 1024.0))
        .Member("source", static r => r.HasSource);

    private static readonly RmlStructType<ProfilerCounterRow> CounterType = new RmlStructType<ProfilerCounterRow>()
        .Member("name", static c => c.Name)
        .Member("value", static c => c.Value);

    private readonly ProfilerStatistics _cpu = new();
    private readonly ProfilerStatistics _gpu = new();
    private readonly List<ProfilerGraphBar> _bars = [];
    private readonly List<ProfilerTimelineBar> _events = [];
    private readonly List<ProfilerLane> _lanes = [];
    private readonly List<ProfilerScopeTableRow> _rows = [];
    private readonly List<ProfilerCounterRow> _counters = [];
    private readonly List<RmlEventListener> _listeners = [];
    private RmlDataModel? _model;
    private PlayInstance? _instance;
    private bool _editorSource;
    private string? _traceName;
    private int _editorToken;
    private bool _recording;
    private bool _paused;
    private bool _gpuZones = true;
    private float _spikeMs;
    private bool _freeze;
    private int _selected = -1; // -1: follow the newest frame
    private int _rangeEnd = -1; // >= 0: a range selected with shift-click
    private int _shownVersion = -1;
    private float _sinceRefresh;
    private double _viewStart; // the timeline's view, as a fraction of the selected frame's span
    private double _viewEnd = 1;
    private float? _dragX;
    private double _dragViewStart;
    private int _timelineHeight;

    public ProfilerPanel(EditorWorkspace workspace)
        : base(workspace, "profiler.rml")
    {
        workspace.Play.Service.ProfileMessage += OnProfileMessage;
    }

    /// <summary>The frames received from the current source.</summary>
    public ProfilerCapture Capture { get; } = new();

    public bool IsRecording => _recording;

    public bool IsPaused => _paused;

    /// <summary>The game being profiled (null: the editor, a trace, or nothing).</summary>
    public PlayInstance? Instance => _instance;

    /// <summary>True when the source is the editor process itself.</summary>
    public bool IsEditorSource => _editorSource;

    /// <summary>The selected frame (index into <see cref="Capture"/>; the newest while following).</summary>
    public int SelectedIndex => _selected >= 0 ? Math.Min(_selected, Capture.Count - 1) : Capture.Count - 1;

    /// <summary>The scope table's rows (CPU scopes, then GPU zones), sorted by <see cref="Sort"/>.</summary>
    public IReadOnlyList<ProfilerScopeTableRow> Rows => _rows;

    public IReadOnlyList<ProfilerGraphBar> Bars => _bars;

    public IReadOnlyList<ProfilerTimelineBar> TimelineBars => _events;

    public IReadOnlyList<ProfilerCounterRow> Counters => _counters;

    public ProfilerSort Sort { get; private set; } = ProfilerSort.Self;

    /// <summary>The source picker's label.</summary>
    public string SourceLabel => _traceName is { } trace ? "Trace: " + trace
        : _editorSource ? "Editor"
        : _instance is { } instance ? $"#{instance.Number} {instance.Label}"
        : "No game running";

    private bool CanRecord => _editorSource || (_instance is { IsAlive: true, CanProfile: true });

    private string RecordTip => _instance is { CanProfile: false } ? "Game built with an older engine (it cannot stream profiler frames): rebuild it"
        : _recording ? "Stop recording" : "Record — stream profiler frames from the source";

    protected override void OnReady()
    {
        _model = CreateDataModel("profiler")
            .Bind("source", this, static p => p.SourceLabel)
            .Bind("recording", this, static p => p._recording)
            .Bind("can_record", this, static p => p.CanRecord)
            .Bind("record_tip", this, static p => p.RecordTip)
            .Bind("paused", this, static p => p._paused)
            .Bind("gpu", this, static p => p._gpuZones)
            .Bind("freeze", this, static p => p._freeze)
            .Bind("spike", this, static p => p._spikeMs.ToString("0.#", CultureInfo.InvariantCulture), static (p, v) => p.SetSpikeText(v))
            .Bind("frame", this, static p => p.SelectedFrameText)
            .Bind("cpu_ms", this, static p => p.SelectedFrame?.CpuMs ?? 0f)
            .Bind("gpu_ms", this, static p => p.SelectedFrame is { GpuMs: var g } && float.IsFinite(g) ? g : 0f)
            .Bind("wait_ms", this, static p => p.SelectedFrame?.WaitMs ?? 0f)
            .Bind("has_frames", this, static p => p.Capture.Count > 0)
            .Bind("dropped", this, static p => (int)Math.Min(int.MaxValue, p.Capture.DroppedFrames))
            .Bind("timeline_height", this, static p => p._timelineHeight)
            .Bind("sort", this, static p => (int)p.Sort)
            .BindList("bars", _bars, BarType)
            .BindList("events", _events, EventType)
            .BindList("lanes", _lanes, LaneType)
            .BindList("rows", _rows, RowType)
            .BindList("counters", _counters, CounterType)
            .Event("tab", e => Workspace.ShowBottomTab((BottomDockTab)e.GetArgument(0).GetInt32()))
            .Event("source", _ => ShowSources())
            .Event("record", _ => ToggleRecord())
            .Event("pause", _ => SetPaused(!_paused))
            .Event("clear", _ => Clear())
            .Event("gpu", _ => SetGpuZones(!_gpuZones))
            .Event("freeze", _ => SetFreeze(!_freeze))
            .Event("save", _ => ShowSave())
            .Event("open", _ => ShowOpen())
            .Event("pick", e => Pick(e.GetArgument(0).GetInt32(), e.Event.GetParameter("shift_key", 0) != 0))
            .Event("sort", e => SetSort((ProfilerSort)e.GetArgument(0).GetInt32()))
            .Event("open_source", e => OpenSource(e.GetArgument(0).GetInt32()));
        Rebuild();
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener.Remove();
        _listeners.Clear();
        var timeline = document.GetElementById("profiler-timeline");
        if (timeline.IsNull)
            return;
        Listen(timeline, "mousescroll", OnTimelineWheel);
        Listen(timeline, "mousedown", OnTimelineDown);
        Listen(timeline, "mousemove", OnTimelineMove);
        Listen(timeline, "mouseup", _ => _dragX = null);
        Listen(timeline, "mouseout", _ => _dragX = null);
    }

    private void Listen(RmlElement element, string type, RmlEventCallback handler)
    {
        if (element.AddEventListener(type, handler) is { } listener)
            _listeners.Add(listener);
    }

    private EditorLinkProfileFrame? SelectedFrame => Capture.Count > 0 ? Capture[SelectedIndex] : null;

    private string SelectedFrameText => SelectedFrame is { } frame ? frame.Frame.ToString(CultureInfo.InvariantCulture) : "—";

    // ── Source and recording ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Profiles <paramref name="instance"/> (a running game); stops the current stream first.</summary>
    public void SelectSource(PlayInstance? instance)
    {
        StopRecording();
        _instance = instance;
        _editorSource = false;
        _traceName = null;
        Capture.Reset();
        ResetSelection();
        Rebuild();
    }

    /// <summary>Profiles the editor process itself.</summary>
    public void SelectEditorSource()
    {
        StopRecording();
        _instance = null;
        _editorSource = true;
        _traceName = null;
        Capture.Reset();
        ResetSelection();
        Rebuild();
    }

    private void ShowSources()
    {
        var items = new List<MenuItem> { MenuItem.Header("Profile") };
        foreach (var instance in Workspace.Play.Service.Instances)
            if (instance.IsAlive)
                items.Add(new MenuItem($"#{instance.Number} {instance.Label}", "game:" + instance.Number.ToString(CultureInfo.InvariantCulture),
                    Enabled: instance.CanProfile, Icon: "device-gamepad-2"));
        items.Add(new MenuItem("Editor", "editor", Icon: "app-window"));
        var x = Rect?.X + 80 ?? 80;
        var y = Rect?.Y + 28 ?? 28;
        Workspace.Popup.Show(items, x, y, onCommand: command =>
        {
            if (command == "editor")
                SelectEditorSource();
            else if (command.StartsWith("game:", StringComparison.Ordinal) && int.TryParse(command[5..], CultureInfo.InvariantCulture, out var number))
                SelectSource(Workspace.Play.Service.Instances.FirstOrDefault(i => i.Number == number));
        });
    }

    /// <summary>Record: starts streaming from the source (the first live game when none is chosen), or stops.</summary>
    public void ToggleRecord()
    {
        if (_recording)
        {
            StopRecording();
            Rebuild();
            return;
        }

        if (!_editorSource && _instance is not { IsAlive: true })
            _instance = Workspace.Play.Service.Instances.FirstOrDefault(i => i.IsAlive && i.CanProfile);
        if (!CanRecord)
        {
            Rebuild();
            return;
        }

        if (_traceName is not null)
        {
            _traceName = null;
            Capture.Reset();
        }

        _paused = false;
        if (_editorSource)
        {
            _editorToken = Profiler.Acquire();
            Profiler.SpikeThresholdMs = _spikeMs;
            Profiler.FreezeOnSpike = _freeze;
            _recording = _editorToken != 0;
        }
        else
        {
            _recording = Workspace.Play.Service.StartProfiler(_instance!, Argument);
        }

        Rebuild();
    }

    private string Argument => string.Create(CultureInfo.InvariantCulture,
        $"gpu={(_gpuZones ? 1 : 0)};spike={_spikeMs};freeze={(_freeze ? 1 : 0)}");

    private void StopRecording()
    {
        if (!_recording)
            return;
        _recording = false;
        if (_editorSource)
        {
            Profiler.Release(_editorToken);
            _editorToken = 0;
        }
        else if (_instance is { IsAlive: true } instance)
        {
            Workspace.Play.Service.StopProfiler(instance);
        }
    }

    /// <summary>Pause freezes the view (the game keeps running; frames arriving meanwhile are not kept).</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused && _selected < 0 && Capture.Count > 0)
            _selected = Capture.Count - 1;
        Rebuild();
    }

    public void Clear()
    {
        Capture.Clear();
        ResetSelection();
        Rebuild();
    }

    public void SetGpuZones(bool on)
    {
        _gpuZones = on;
        Reconfigure();
    }

    public void SetFreeze(bool on)
    {
        _freeze = on;
        Reconfigure();
    }

    /// <summary>The spike threshold in ms (0 = off); applies to the stream at once.</summary>
    public void SetSpike(float ms)
    {
        _spikeMs = float.IsFinite(ms) ? Math.Clamp(ms, 0f, 10_000f) : 0f;
        Reconfigure();
    }

    private void SetSpikeText(string? text)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            SetSpike(ms);
    }

    // A running stream takes the new settings (ProfilerStart again reconfigures it).
    private void Reconfigure()
    {
        if (_recording && !_editorSource && _instance is { IsAlive: true } instance)
            Workspace.Play.Service.StartProfiler(instance, Argument);
        if (_recording && _editorSource)
        {
            Profiler.SpikeThresholdMs = _spikeMs;
            Profiler.FreezeOnSpike = _freeze;
        }

        _model?.DirtyAll();
    }

    private void OnProfileMessage(PlayInstance instance, EditorLinkMessage message)
    {
        if (!ReferenceEquals(instance, _instance) || !_recording)
            return;
        Feed(message);
    }

    /// <summary>Takes one profiler message (the game's stream; tests feed it directly).</summary>
    internal void Feed(in EditorLinkMessage message)
    {
        if (_paused && message.Type == EditorLinkMessageType.ProfileFrame)
            return;
        Capture.Add(message);
        if (message.ProfileFrame is { IsSpike: true } && _freeze)
            SetPaused(true); // auto-freeze: the game froze itself too (freeze=1)
    }

    /// <summary>Called every editor frame: pulls the editor's own frames, follows the game's state, refreshes at 10 Hz.</summary>
    public void Tick(float delta)
    {
        if (_recording && _editorSource && !_paused)
        {
            Capture.AddLocal(Profiler.Snapshot, Workspace.Tree?.Servers.Render?.Vulkan?.GpuProfiler.SamplesAtEncoderBoundaries ?? false);
            if (_freeze && Profiler.Paused)
                SetPaused(true);
        }

        if (_recording && !_editorSource && _instance is { IsAlive: false })
        {
            _recording = false; // the game exited: its frames stay for inspection
            Rebuild();
        }

        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds || Capture.Version == _shownVersion || !Visible)
            return;
        _sinceRefresh = 0f;
        Rebuild();
    }

    // ── Selection ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Selects frame <paramref name="index"/> (a range from the current one with <paramref name="extend"/>).</summary>
    public void Pick(int index, bool extend = false)
    {
        if ((uint)index >= (uint)Capture.Count)
            return;
        if (extend && Capture.Count > 0)
        {
            if (_selected < 0)
                _selected = Capture.Count - 1;
            _rangeEnd = index;
        }
        else
        {
            _selected = index;
            _rangeEnd = -1;
        }

        _viewStart = 0;
        _viewEnd = 1;
        Rebuild();
    }

    /// <summary>Selects the slowest frame held (CPU or GPU), as the QA walkthrough does.</summary>
    public void PickSlowest()
    {
        var slowest = -1;
        var worst = -1f;
        for (var i = 0; i < Capture.Count; i++)
        {
            var frame = Capture[i];
            var ms = MathF.Max(frame.CpuMs, float.IsFinite(frame.GpuMs) ? frame.GpuMs : 0f);
            if (ms > worst)
                (worst, slowest) = (ms, i);
        }

        if (slowest >= 0)
            Pick(slowest);
    }

    private void ResetSelection()
    {
        _selected = -1;
        _rangeEnd = -1;
        _viewStart = 0;
        _viewEnd = 1;
    }

    public void SetSort(ProfilerSort sort)
    {
        Sort = sort;
        Rebuild();
    }

    /// <summary>Opens the source line of table row <paramref name="index"/>'s marker; false when it has none.</summary>
    public bool OpenSource(int index)
    {
        if ((uint)index >= (uint)_rows.Count || !Capture.Markers.TryGetValue(_rows[index].Marker, out var marker) || marker.File.Length == 0)
            return false;
        return Workspace.SourceOpener(marker.File, marker.Line);
    }

    // ── Save / open ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the held frames (the selected range, else all) as a Chrome trace.</summary>
    public void SaveTrace(string path)
    {
        var (first, last) = _rangeEnd >= 0 ? (Math.Min(SelectedIndex, _rangeEnd), Math.Max(SelectedIndex, _rangeEnd)) : (0, Capture.Count - 1);
        try
        {
            ChromeTraceWriter.Save(path, Capture.ToTrace(SourceLabel, first, last));
            Log.Info($"[Profiler] Trace of {last - first + 1} frames saved to {path}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[Profiler] Could not save the trace '{path}': {e.Message}");
        }
    }

    /// <summary>Loads a trace written by the profiler (the source becomes the trace).</summary>
    public bool OpenTrace(string path)
    {
        try
        {
            var trace = ChromeTraceReader.Load(path);
            StopRecording();
            _instance = null;
            _editorSource = false;
            _traceName = Path.GetFileName(path);
            Capture.Load(trace);
            ResetSelection();
            Rebuild();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"[Profiler] Could not open the trace '{path}': {e.Message}");
            return false;
        }
    }

    private string TraceFolder
    {
        get
        {
            var root = Workspace.Session.ProjectRoot;
            return root is not null && Directory.Exists(root) ? root : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    private void ShowSave()
    {
        if (Capture.Count == 0)
            return;
        var name = "profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Save, TraceFolder, ["*.json"], name), "Save Trace", "Save", SaveTrace);
    }

    private void ShowOpen() =>
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Open, TraceFolder, ["*.json"]), "Open Trace", "Open", path => OpenTrace(path));

    // ── Timeline input ──────────────────────────────────────────────────────────────────────────────────────

    private (float X, float Width) TimelineMetrics()
    {
        var timeline = Document.GetElementById("profiler-timeline");
        if (timeline.IsNull)
            return (0f, 1f);
        var bounds = timeline.Bounds;
        return (bounds.X, MathF.Max(1f, bounds.Width));
    }

    private void OnTimelineWheel(RmlEvent e)
    {
        var (x, width) = TimelineMetrics();
        var at = Math.Clamp(((float)e.GetParameter("mouse_x", 0.0) - x) / width, 0f, 1f);
        var delta = (float)e.GetParameter("wheel_delta_y", 0.0);
        Zoom(at, delta < 0 ? 0.8 : 1.25);
        e.StopPropagation();
    }

    /// <summary>Zooms the timeline by <paramref name="factor"/> around <paramref name="at"/> (0–1 of the view).</summary>
    public void Zoom(double at, double factor)
    {
        var span = _viewEnd - _viewStart;
        var pivot = _viewStart + at * span;
        var next = Math.Clamp(span * factor, 0.0005, 1.0);
        _viewStart = Math.Clamp(pivot - at * next, 0, 1 - next);
        _viewEnd = _viewStart + next;
        RebuildTimeline();
        _model?.DirtyAll();
    }

    private void OnTimelineDown(RmlEvent e)
    {
        if (e.GetParameter("button", 0) != 0)
            return;
        _dragX = (float)e.GetParameter("mouse_x", 0.0);
        _dragViewStart = _viewStart;
    }

    private void OnTimelineMove(RmlEvent e)
    {
        if (_dragX is not { } start)
            return;
        var (_, width) = TimelineMetrics();
        var span = _viewEnd - _viewStart;
        var moved = ((float)e.GetParameter("mouse_x", 0.0) - start) / width * span;
        _viewStart = Math.Clamp(_dragViewStart - moved, 0, 1 - span);
        _viewEnd = _viewStart + span;
        RebuildTimeline();
        _model?.DirtyAll();
    }

    // ── Views ───────────────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        _shownVersion = Capture.Version;
        RebuildGraph();
        RebuildTimeline();
        RebuildTable();
        RebuildCounters();
        _model?.DirtyAll();
    }

    private void RebuildGraph()
    {
        _bars.Clear();
        var count = Capture.Count;
        if (count == 0)
            return;
        var selected = SelectedIndex;
        var (rangeFirst, rangeLast) = _rangeEnd >= 0 ? (Math.Min(selected, _rangeEnd), Math.Max(selected, _rangeEnd)) : (selected, selected);
        // The newest GraphBars frames while following; else the window holding the selection.
        var last = _selected < 0 ? count - 1 : Math.Min(count - 1, Math.Max(rangeLast, Math.Min(count - 1, rangeFirst + GraphBars / 2)));
        var first = Math.Max(0, last - GraphBars + 1);
        for (var i = first; i <= last; i++)
        {
            var frame = Capture[i];
            var gpu = float.IsFinite(frame.GpuMs) ? frame.GpuMs : 0f;
            var ms = MathF.Max(frame.CpuMs, gpu);
            var css = "bar";
            if (ms > 33.333f || frame.IsSpike)
                css += " spike";
            else if (ms > 16.667f)
                css += " over";
            if (i >= rangeFirst && i <= rangeLast)
                css += " selected";
            _bars.Add(new ProfilerGraphBar
            {
                Index = i,
                Height = Scale(frame.CpuMs),
                GpuHeight = gpu > 0f ? Scale(gpu) : 0,
                Css = css,
                Tip = string.Create(CultureInfo.InvariantCulture, $"Frame {frame.Frame}: CPU {frame.CpuMs:0.00} ms, GPU {gpu:0.00} ms, wait {frame.WaitMs:0.00} ms"),
            });
        }

        static int Scale(float ms) => (int)Math.Clamp(MathF.Round(ms / 33.333f * GraphHeight), 1f, GraphHeight);
    }

    private void RebuildTimeline()
    {
        _events.Clear();
        _lanes.Clear();
        _timelineHeight = 0;
        if (SelectedFrame is not { } frame)
            return;
        var start = frame.StartMicroseconds;
        var end = Math.Max(frame.EndMicroseconds, start + 1);
        foreach (var e in frame.GpuZones)
            end = Math.Max(end, e.End);
        var span = (double)(end - start);
        var viewStart = start + _viewStart * span;
        var viewSpan = Math.Max(1, (_viewEnd - _viewStart) * span);

        // Self times for the tooltips.
        var self = new Dictionary<int, long>();
        ComputeSelf(frame.CpuEvents, self);

        var top = 0;
        var threads = frame.CpuEvents.Select(e => e.Thread).Distinct().Order().ToArray();
        foreach (var thread in threads)
            top = AddLane(Capture.Threads.TryGetValue(thread, out var name) ? name : "Thread " + thread, frame.CpuEvents, thread, top, viewStart, viewSpan, self, gpu: false, frame.EncoderBoundaries);
        if (frame.GpuZones.Length > 0)
            top = AddLane(Capture.EncoderBoundaries ? "GPU (aligned to submit; ≈ = at encoder boundaries)" : "GPU (aligned to submit)",
                frame.GpuZones, ProfilerEvent.GpuThread, top, viewStart, viewSpan, null, gpu: true, frame.EncoderBoundaries);
        _timelineHeight = top;

        if (_events.Count > MaxTimelineBars)
        {
            _events.Sort(static (a, b) => b.Width.CompareTo(a.Width)); // keep the widest
            _events.RemoveRange(MaxTimelineBars, _events.Count - MaxTimelineBars);
        }
    }

    private int AddLane(string name, ProfilerEvent[] events, byte thread, int top, double viewStart, double viewSpan,
        Dictionary<int, long>? self, bool gpu, bool encoderBoundaries)
    {
        _lanes.Add(new ProfilerLane { Top = top, Name = name });
        var maxDepth = 0;
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            if (e.Thread != thread)
                continue;
            maxDepth = Math.Max(maxDepth, e.Depth);
            var left = (e.Start - viewStart) / viewSpan * 100.0;
            var width = Math.Max(0.05, (e.End - e.Start) / viewSpan * 100.0);
            if (left > 100 || left + width < 0)
                continue;
            var marker = Capture.Markers.TryGetValue(e.Marker, out var m) ? m : new EditorLinkProfileMarker(e.Marker, ProfilerCategory.Game, ProfilerMarkerKind.Cpu, "#" + e.Marker, "", 0);
            var approximate = gpu && encoderBoundaries && marker.Kind == ProfilerMarkerKind.GpuInPass;
            var label = (approximate ? "≈ " : "") + marker.Name;
            var duration = (e.End - e.Start) / 1000.0;
            var tip = self is not null && self.TryGetValue(i, out var selfUs)
                ? string.Create(CultureInfo.InvariantCulture, $"{marker.Name} — {duration:0.000} ms (self {selfUs / 1000.0:0.000} ms)")
                : string.Create(CultureInfo.InvariantCulture, $"{label} — {duration:0.000} ms{(approximate ? ApproximateNote : "")}");
            _events.Add(new ProfilerTimelineBar
            {
                Left = (float)Math.Max(0, left),
                Width = (float)Math.Min(width, 100 - Math.Max(0, left)),
                Top = top + LaneHeader + e.Depth * LaneRow,
                Css = "ev cat-" + ChromeTraceWriter.CategoryName(marker.Category).ToLowerInvariant() + (gpu ? " gpu" : ""),
                Label = width > 4 ? label : "",
                Tip = tip,
            });
        }

        return top + LaneHeader + (maxDepth + 1) * LaneRow + 4;
    }

    // Self time (µs) per event index: duration minus the durations of its direct children (events in begin order per thread).
    private static void ComputeSelf(ProfilerEvent[] events, Dictionary<int, long> self)
    {
        var stack = new Stack<int>();
        var thread = -1;
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            if (e.Thread != thread)
            {
                stack.Clear();
                thread = e.Thread;
            }

            while (stack.Count > 0 && events[stack.Peek()].Depth >= e.Depth)
                stack.Pop();
            self[i] = e.End - e.Start;
            if (stack.Count > 0)
                self[stack.Peek()] -= e.End - e.Start;
            stack.Push(i);
        }
    }

    private void RebuildTable()
    {
        _rows.Clear();
        if (Capture.Count == 0)
            return;
        var selected = SelectedIndex;
        var (first, last) = _rangeEnd >= 0 ? (Math.Min(selected, _rangeEnd), Math.Max(selected, _rangeEnd)) : (selected, selected);
        Capture.Accumulate(first, last, _cpu, _gpu);
        AddRows(_cpu, gpu: false);
        AddRows(_gpu, gpu: true);
        Comparison<ProfilerScopeTableRow> order = Sort switch
        {
            ProfilerSort.Total => static (a, b) => b.TotalMs.CompareTo(a.TotalMs),
            ProfilerSort.Calls => static (a, b) => b.Calls.CompareTo(a.Calls),
            ProfilerSort.Max => static (a, b) => b.MaxMs.CompareTo(a.MaxMs),
            ProfilerSort.Alloc => static (a, b) => b.Alloc.CompareTo(a.Alloc),
            ProfilerSort.Name => static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            _ => static (a, b) => b.SelfMs.CompareTo(a.SelfMs),
        };
        _rows.Sort((a, b) => a.Gpu != b.Gpu ? a.Gpu.CompareTo(b.Gpu) : order(a, b)); // CPU scopes, then GPU zones
        if (_rows.Count > MaxTableRows)
            _rows.RemoveRange(MaxTableRows, _rows.Count - MaxTableRows);
    }

    private void AddRows(ProfilerStatistics statistics, bool gpu)
    {
        var frames = Math.Max(1, statistics.Frames);
        foreach (var marker in statistics.Markers)
        {
            var info = Capture.Markers.TryGetValue(marker, out var m) ? m : default;
            _rows.Add(new ProfilerScopeTableRow
            {
                Marker = marker,
                Gpu = gpu,
                Name = (gpu ? "GPU · " : "") + (info.Name ?? "#" + marker),
                Calls = statistics.Calls[marker] / frames,
                SelfMs = statistics.SelfTicks[marker] / 1000.0 / frames, // ticks are µs
                TotalMs = statistics.TotalTicks[marker] / 1000.0 / frames,
                MaxMs = statistics.MaxTicks[marker] / 1000.0,
                Alloc = statistics.AllocatedBytes[marker] / frames,
                HasSource = info.File is { Length: > 0 },
            });
        }
    }

    private void RebuildCounters()
    {
        _counters.Clear();
        if (SelectedFrame is not { } frame)
            return;
        foreach (var (id, value) in frame.Counters)
        {
            var counter = Capture.Counters.TryGetValue(id, out var c) ? c : new ProfilerCounterName(id, "#" + id, ProfilerCounterUnit.Count);
            _counters.Add(new ProfilerCounterRow { Name = counter.Name, Value = FormatCounter(value, counter.Unit) });
        }
    }

    /// <summary>A counter value as the table shows it (bytes as KiB/MiB, µs as ms).</summary>
    public static string FormatCounter(long value, ProfilerCounterUnit unit) => unit switch
    {
        ProfilerCounterUnit.Bytes when Math.Abs(value) >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{value / (1024.0 * 1024.0):0.0} MiB"),
        ProfilerCounterUnit.Bytes when Math.Abs(value) >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{value / 1024.0:0.0} KiB"),
        ProfilerCounterUnit.Bytes => string.Create(CultureInfo.InvariantCulture, $"{value} B"),
        ProfilerCounterUnit.Microseconds => string.Create(CultureInfo.InvariantCulture, $"{value / 1000.0:0.000} ms"),
        ProfilerCounterUnit.Milliseconds => string.Create(CultureInfo.InvariantCulture, $"{value} ms"),
        ProfilerCounterUnit.Percent => string.Create(CultureInfo.InvariantCulture, $"{value} %"),
        _ => value.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>Stops a running stream (the workspace leaves, a code reload).</summary>
    public void Shutdown()
    {
        StopRecording();
        Workspace.Play.Service.ProfileMessage -= OnProfileMessage;
    }
}
