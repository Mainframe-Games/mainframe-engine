using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The window of a built-in effect insert in music mode (Logic's plugin window): one reused, floating, non-modal window
/// over the song view. Its knobs, toggles and menus are built from the effect's <see cref="BuiltinParam"/>s; the Channel
/// EQ adds its curve with draggable band handles (<see cref="EqGraphCanvas"/>), the compressor, limiter and gate a
/// gain-reduction meter, the delay its time readout. Every edit goes through the song's <see cref="SongDocument"/> (a
/// knob drag is one undo step); undo, redo and edits from elsewhere show on the next tick. It closes when its insert
/// leaves the song (removed, undone, the tab closed or switched) or music mode is left.
/// </summary>
public sealed class PluginWindow : EditorDocument
{
    public const string GraphTexture = "plugin-graph";

    // The window's chrome in dp, as plugin_window.rml lays it out (mouse positions are mapped with these).
    private const float Border = 1;
    private const float TitleHeight = 30;
    private const float GraphMargin = 10;
    private const float GraphHeight = 190;
    private const float KnobDragDp = 200; // vertical travel for the whole range
    private const double WheelMergeSeconds = 0.6;

    private static readonly string[] Percent = [.. Enumerable.Range(0, 101).Select(i => i.ToString(CultureInfo.InvariantCulture) + "%")];
    private static readonly string[] Degrees = [.. Enumerable.Range(0, 271).Select(i => $"rotate({(i - 135).ToString(CultureInfo.InvariantCulture)}deg)")];

    private readonly List<RmlEventListener?> _listeners = [];
    private float[] _shown = [];
    private string?[] _shownText = [];
    private SubViewport? _graphViewport;
    private EqGraphCanvas? _graph;
    private bool _graphRegistered;
    private bool _graphDirty;
    private bool _built;
    private bool _shownBypass;
    private bool _shownReadOnly;
    private int _shownGr = -1;
    private float _gr;
    private double _shownTempo = double.NaN;
    private (float X, float Y) _position = (float.NaN, float.NaN);
    private float _width = 480;

    // Drags: a knob (its parameter, start value), the title bar (window move) or an EQ handle.
    private int _dragParam = -1;
    private float _dragStartNormalized;
    private float _dragStartY;
    private int _dragBand = -1;
    private (float X, float Y) _moveOffset;
    private bool _moving;
    private int _dragCounter;
    private string? _wheelKey;
    private long _wheelAt;

    public PluginWindow(EditorWorkspace workspace)
        : base(workspace, "plugin_window.rml")
    {
        Visible = false;
    }

    /// <summary>The song tab whose insert is shown, or null when closed.</summary>
    public SongTab? Tab { get; private set; }

    /// <summary>The insert's track (null: the master).</summary>
    public SongTrack? Track { get; private set; }

    public PluginInsert? Insert { get; private set; }

    public BuiltinEffectInfo? Info { get; private set; }

    public bool IsOpen => Insert is not null;

    /// <summary>The window's top-left corner and width (dp).</summary>
    public (float X, float Y, float Width) Placement => (_position.X, _position.Y, _width);

    /// <summary>Shows <paramref name="insert"/> (a built-in) of <paramref name="track"/> (null: the master) in <paramref name="tab"/>'s song.</summary>
    public void Open(SongTab tab, SongTrack? track, PluginInsert insert)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(insert);
        if (BuiltinEffects.Find(insert.Builtin ?? "") is not { } info)
        {
            Log.Warning($"[Music] {insert.DisplayName} has no window (not a built-in effect this editor knows).");
            return;
        }

        Tab = tab;
        Track = track;
        Insert = insert;
        Info = info;
        _width = WidthFor(info);
        _built = false;
        _graphDirty = true;
        _shownTempo = double.NaN;
        _shownGr = -1;
        _gr = 0;
        Visible = true;
        if (EnsureLoaded())
            Build();
    }

    /// <summary>Hides the window (keeps its position for the next one).</summary>
    public void Close()
    {
        if (Insert is null)
            return;
        EndEdits();
        Tab = null;
        Track = null;
        Insert = null;
        Info = null;
        HideAndReleaseFocus();
        if (_graphViewport is { } viewport)
            viewport.UpdateMode = SubViewportUpdateMode.Disabled;
    }

    /// <summary>Every frame from the song view: <paramref name="tab"/> is music mode's active song tab (null otherwise).</summary>
    public void Tick(SongTab? tab)
    {
        if (Insert is not { } insert || Info is not { } info || Tab is not { } shown)
            return;
        if (!ReferenceEquals(tab, shown) || !StillInSong(shown, Track, insert))
        {
            Close();
            return;
        }

        if (!IsLoaded || !_built)
        {
            if (EnsureLoaded())
                Build();
            return;
        }

        if (_wheelKey is not null && (System.Diagnostics.Stopwatch.GetTimestamp() - _wheelAt) > WheelMergeSeconds * System.Diagnostics.Stopwatch.Frequency)
            EndEdits();

        var document = Document;
        var parameters = info.Params;
        for (var i = 0; i < parameters.Length; i++)
        {
            var value = info.Value(insert.Params, parameters[i].Id);
            if (value == _shown[i])
                continue;
            _shown[i] = value;
            ShowValue(document, i, parameters[i], value);
            _graphDirty = true;
        }

        if (insert.Bypass != _shownBypass)
        {
            _shownBypass = insert.Bypass;
            document.GetElementById("pw-window").SetClass("bypassed", insert.Bypass);
            document.GetElementById("pw-power").SetClass("active", !insert.Bypass);
        }

        var readOnly = shown.Document.IsReadOnly;
        if (readOnly != _shownReadOnly)
        {
            _shownReadOnly = readOnly;
            document.GetElementById("pw-window").SetClass("readonly", readOnly);
        }

        switch (info.Graph)
        {
            case BuiltinGraph.Eq:
                TickGraph(insert);
                break;
            case BuiltinGraph.GainReduction:
                TickMeter(shown, insert);
                break;
            case BuiltinGraph.DelayReadout when _graphDirty || shown.Document.Song.Tempo != _shownTempo:
                _shownTempo = shown.Document.Song.Tempo;
                document.GetElementById("pw-readout").SetInnerRml(RmlText.Escape(DelayEffect.Describe(insert.Params, _shownTempo)));
                _graphDirty = false;
                break;
        }
    }

    private static bool StillInSong(SongTab tab, SongTrack? track, PluginInsert insert)
    {
        var song = tab.Document.Song;
        if (track is not null && !song.Tracks.Contains(track))
            return false;
        return (track?.Inserts ?? song.Master.Inserts).Contains(insert);
    }

    // --- building the window ------------------------------------------------------------------------------------------

    // Wide enough for every section on one row (as plugin_window.rml sizes them: 62dp per control, a band column 70dp,
    // 18dp of section margin, padding and border), within reason.
    private static float WidthFor(BuiltinEffectInfo info)
    {
        var width = 2 * Border + 12f;
        foreach (var group in info.Params.GroupBy(p => p.Group ?? ""))
        {
            var band = info.Graph == BuiltinGraph.Eq && Array.Exists(EqEffect.Bands, b => b.Name == group.Key);
            width += (band ? 70 : 62 * group.Count()) + 18;
        }

        return Math.Clamp(width, 340, 900);
    }

    private void Build()
    {
        if (Insert is not { } insert || Info is not { } info || Tab is not { } tab)
            return;
        var document = Document;
        _built = true;
        _shown = new float[info.Params.Length];
        Array.Fill(_shown, float.NaN);
        _shownText = new string?[info.Params.Length];
        _shownBypass = !insert.Bypass;
        _shownReadOnly = !tab.Document.IsReadOnly;
        var trackName = Track?.Name ?? "Master";
        document.GetElementById("pw-name").SetInnerRml(RmlText.Escape($"{info.Name} — {trackName} — {tab.Document.Name}"));
        document.GetElementById("pw-icon").SetClassNames("icon icon-sm icon-" + info.Icon);
        document.GetElementById("pw-presets").SetClass("hidden", info.Presets is not { Length: > 0 });
        document.GetElementById("pw-graph").SetClass("hidden", info.Graph != BuiltinGraph.Eq);
        document.GetElementById("pw-meter").SetClass("hidden", info.Graph != BuiltinGraph.GainReduction);
        document.GetElementById("pw-readout").SetClass("hidden", info.Graph != BuiltinGraph.DelayReadout);
        document.GetElementById("pw-body").SetInnerRml(BodyRml(info));
        if (info.Graph == BuiltinGraph.Eq)
            EnsureGraph(document);
        Place();
    }

    // Sections: the EQ's bands as columns (their colour swatch on top); other effects' groups as boxes of knobs.
    private static string BodyRml(BuiltinEffectInfo info)
    {
        var sb = new StringBuilder();
        var groups = info.Params.Select((p, i) => (p, i)).GroupBy(x => x.p.Group ?? "");
        foreach (var group in groups)
        {
            var band = info.Graph == BuiltinGraph.Eq ? Array.FindIndex(EqEffect.Bands, b => b.Name == group.Key) : -1;
            sb.Append(band >= 0 ? "<div class=\"pw-section band\">" : "<div class=\"pw-section\">");
            if (group.Key.Length > 0)
            {
                sb.Append("<div class=\"pw-section-title\">");
                if (band >= 0)
                    sb.Append("<span class=\"swatch\" style=\"background-color: ").Append(EqGraphCanvas.BandColors[band]).Append(";\"></span>");
                sb.Append(RmlText.Escape(group.Key)).Append("</div>");
            }

            sb.Append("<div class=\"pw-knobs\">");
            foreach (var (p, i) in group)
                AppendControl(sb, p, i);
            sb.Append("</div></div>");
        }

        return sb.ToString();
    }

    private static void AppendControl(StringBuilder sb, BuiltinParam p, int i)
    {
        var n = i.ToString(CultureInfo.InvariantCulture);
        var name = RmlText.Escape(p.Name);
        switch (p.Curve)
        {
            case ParamCurve.Toggle:
                sb.Append("<div class=\"pw-choice-wrap\"><div class=\"pw-toggle\" id=\"kt-").Append(n).Append("\" data-p=\"").Append(n)
                    .Append("\" data-tooltip=\"").Append(name).Append(" — click to switch\">").Append(name).Append("</div></div>");
                break;
            case ParamCurve.Choice:
                sb.Append("<div class=\"pw-choice-wrap\"><div class=\"pw-choice\" id=\"kc-").Append(n).Append("\" data-p=\"").Append(n)
                    .Append("\" data-tooltip=\"").Append(name).Append(" — click for the choices\"></div><span class=\"pw-param-name\">")
                    .Append(name).Append("</span></div>");
                break;
            default:
                sb.Append("<div class=\"knob\" data-tooltip=\"").Append(name)
                    .Append(" — drag up or down (Shift: fine) · wheel steps · double-click resets\"><div class=\"knob-dial\" data-p=\"").Append(n)
                    .Append("\"><div class=\"knob-pointer\" id=\"kp-").Append(n).Append("\"></div></div><div class=\"knob-name\">").Append(name)
                    .Append("</div><div class=\"knob-value mono\" id=\"kv-").Append(n).Append("\"></div></div>");
                break;
        }
    }

    private void ShowValue(RmlDocument document, int index, BuiltinParam p, float value)
    {
        var n = index.ToString(CultureInfo.InvariantCulture);
        switch (p.Curve)
        {
            case ParamCurve.Toggle:
                document.GetElementById("kt-" + n).SetClass("active", value >= 0.5f);
                return;
            case ParamCurve.Choice:
                document.GetElementById("kc-" + n).SetInnerRml(RmlText.Escape(p.Format(value)));
                return;
        }

        var angle = (int)MathF.Round(p.ToNormalized(value) * 270);
        document.GetElementById("kp-" + n).SetProperty("transform", Degrees[Math.Clamp(angle, 0, 270)]);
        var text = p.Format(value);
        if (text == _shownText[index])
            return;
        _shownText[index] = text;
        document.GetElementById("kv-" + n).SetInnerRml(RmlText.Escape(text));
    }

    private void Place()
    {
        if (!IsLoaded)
            return;
        var view = Workspace.Layout.ViewportImage;
        if (float.IsNaN(_position.X))
            _position = (view.X + (view.Width - _width) * 0.5f, view.Y + 40);
        _position = (Math.Clamp(_position.X, 0, Math.Max(0, Workspace.Layout.WindowWidth - 80)),
            Math.Clamp(_position.Y, 0, Math.Max(0, Workspace.Layout.WindowHeight - 40)));
        var window = Document.GetElementById("pw-window");
        window.SetProperty("left", RmlText.Dp(_position.X));
        window.SetProperty("top", RmlText.Dp(_position.Y));
        window.SetProperty("width", RmlText.Dp(_width));
    }

    // --- the EQ graph -------------------------------------------------------------------------------------------------

    private void EnsureGraph(RmlDocument document)
    {
        if (_graphViewport is null)
        {
            _graphViewport = new SubViewport
            {
                Name = "PluginGraph",
                Disable3D = true,
                UpdateMode = SubViewportUpdateMode.Disabled,
                ClearColor = System.Drawing.Color.FromArgb(255, 21, 23, 28),
            };
            _graph = new EqGraphCanvas { Name = "EqGraph" };
            _graphViewport.AddChild(_graph);
            Workspace.ScenesHost.AddChild(_graphViewport);
        }

        if (!_graphRegistered && Workspace.Tree?.Servers.Get<UiServer>() is { } ui)
        {
            ui.RegisterTexture(GraphTexture, _graphViewport);
            _graphRegistered = true;
        }

        document.GetElementById("pw-graph-image").SetAttribute("src", "engine://" + GraphTexture);
        _graphDirty = true;
    }

    private (float Width, float Height) GraphSize => (_width - 2 * Border - 2 * GraphMargin - 2, GraphHeight - 2);

    private void TickGraph(PluginInsert insert)
    {
        if (_graphViewport is not { } viewport || _graph is not { } graph)
            return;
        var scale = Workspace.Host.PixelScale;
        var (w, h) = GraphSize;
        var pw = Math.Max(1, (int)MathF.Round(w * scale));
        var ph = Math.Max(1, (int)MathF.Round(h * scale));
        if (viewport.Width != pw || viewport.Height != ph)
        {
            viewport.Width = pw;
            viewport.Height = ph;
            _graphDirty = true;
        }

        if (!_graphDirty)
        {
            if (viewport.UpdateMode == SubViewportUpdateMode.Once)
                return;
            if (viewport.UpdateMode != SubViewportUpdateMode.Disabled)
                viewport.UpdateMode = SubViewportUpdateMode.Disabled;
            return;
        }

        _graphDirty = false;
        graph.Values = insert.Params;
        graph.PixelScale = scale;
        graph.ViewSize = new Vector2(w, h);
        graph.QueueRedraw();
        viewport.UpdateMode = SubViewportUpdateMode.Once;
    }

    // Mouse position (window pixels) → the graph's dp coordinates.
    private Vector2 GraphPoint(RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var x = (float)(e.GetParameter("mouse_x", 0.0) / scale) - _position.X - Border - GraphMargin - 1;
        var y = (float)(e.GetParameter("mouse_y", 0.0) / scale) - _position.Y - Border - TitleHeight - GraphMargin - 1;
        return new Vector2(x, y);
    }

    // --- the meter ----------------------------------------------------------------------------------------------------

    private void TickMeter(SongTab tab, PluginInsert insert)
    {
        var taken = tab.Player.FindBuiltin(insert)?.TakeGainReductionDb() ?? 0;
        // Falls 20 dB/s, like the mixer's meters.
        _gr = MathF.Max(taken, _gr - 20f / 60f);
        var tenths = (int)MathF.Round(Math.Clamp(_gr, 0, 30) * 10);
        if (tenths == _shownGr)
            return;
        _shownGr = tenths;
        var document = Document;
        document.GetElementById("pw-meter-fill").SetProperty("width", Percent[Math.Clamp(tenths * 100 / 300, 0, 100)]);
        document.GetElementById("pw-meter-db").SetInnerRml(tenths == 0 ? "0.0 dB" : "-" + (tenths / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " dB");
    }

    // --- input --------------------------------------------------------------------------------------------------------

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        _listeners.Clear();
        _built = false;
        _graphRegistered = false;
        var body = document.AsElement();
        _listeners.Add(body.AddEventListener("dragstart", OnDragStart));
        _listeners.Add(body.AddEventListener("drag", OnDrag));
        _listeners.Add(body.AddEventListener("dragend", OnDragEnd));
        _listeners.Add(body.AddEventListener("dblclick", OnDoubleClick));
        _listeners.Add(body.AddEventListener("mousescroll", OnWheel));
        _listeners.Add(body.AddEventListener("mousedown", OnMouseDown));
        _listeners.Add(body.AddEventListener("keydown", e =>
        {
            if ((RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape)
                Close();
        }));
        if (Insert is not null)
            Build();
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (Insert is not { } insert || Info is not { } info || Tab is not { } tab)
            return;
        var target = e.Target;
        for (var el = target; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            switch (el.Id)
            {
                case "pw-close":
                    Close();
                    return;
                case "pw-power":
                    Edit(tab, "Could not bypass the effect", () => tab.Document.SetInsertBypass(insert, !insert.Bypass));
                    return;
                case "pw-reset":
                    Edit(tab, "Could not reset the effect", () => tab.Document.SetInsertParams(insert, info.Defaults(), "Reset " + info.Name));
                    return;
                case "pw-presets":
                    PresetMenu(tab, insert, info);
                    return;
            }
        }

        if (FindAttribute(target, "data-p") is not { } attr || !int.TryParse(attr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
            (uint)index >= (uint)info.Params.Length)
            return;
        var p = info.Params[index];
        if (p.Curve == ParamCurve.Toggle)
        {
            var value = info.Value(insert.Params, p.Id) >= 0.5f ? 0 : 1;
            Edit(tab, "Could not change the effect", () => tab.Document.SetInsertParam(insert, p.Id, value));
        }
        else if (p.Curve == ParamCurve.Choice)
        {
            ChoiceMenu(tab, insert, p, e);
        }
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (Insert is null)
            return;
        // A press on the window brings it in front of the song view's other documents.
        Document.PullToFront();
        if (Info?.Graph == BuiltinGraph.Eq && _graph is { } graph && IsInGraph(e.Target))
        {
            var band = graph.HitBand(GraphPoint(e));
            if (band != graph.SelectedBand)
            {
                graph.SelectedBand = band;
                _graphDirty = true;
            }
        }
    }

    private static bool IsInGraph(RmlElement element)
    {
        for (var el = element; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            if (el.Id == "pw-graph")
                return true;
        }

        return false;
    }

    private void OnDragStart(RmlEvent e)
    {
        if (Insert is null || Info is null)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var mouse = ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
        if (FindWithAttribute(e.Target, "data-p") is { IsNull: false } dial && dial.IsClassSet("knob-dial") &&
            int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            BeginKnob(index);
            _dragStartY = mouse.Item2;
            dial.SetClass("dragging", true);
            return;
        }

        if (IsInGraph(e.Target) && _graph is { } graph)
        {
            BeginBand(graph.HitBand(GraphPoint(e)));
            return;
        }

        if (e.Target.Id is "pw-title" or "pw-name" or "pw-icon")
        {
            _moving = true;
            _moveOffset = (mouse.Item1 - _position.X, mouse.Item2 - _position.Y);
        }
    }

    private void OnDrag(RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var x = (float)(e.GetParameter("mouse_x", 0.0) / scale);
        var y = (float)(e.GetParameter("mouse_y", 0.0) / scale);
        if (_moving)
        {
            _position = (x - _moveOffset.X, y - _moveOffset.Y);
            Place();
        }
        else if (_dragParam >= 0)
        {
            DragKnob(_dragStartY - y, e.GetParameter("shift_key", 0) != 0);
        }
        else if (_dragBand >= 0)
        {
            DragBand(GraphPoint(e));
        }
    }

    /// <summary>Starts a knob drag on parameter <paramref name="index"/> (the mouse, and tests).</summary>
    internal void BeginKnob(int index)
    {
        if (Insert is not { } insert || Info is not { } info || (uint)index >= (uint)info.Params.Length)
            return;
        EndEdits();
        _dragCounter++;
        _dragParam = index;
        _dragStartNormalized = info.Params[index].ToNormalized(info.Value(insert.Params, info.Params[index].Id));
    }

    /// <summary>The knob being dragged, <paramref name="upDp"/> above where the drag started (Shift: a tenth as far).</summary>
    internal void DragKnob(float upDp, bool fine)
    {
        if (_dragParam < 0 || Insert is not { } insert || Info is not { } info || Tab is not { } tab)
            return;
        var p = info.Params[_dragParam];
        var value = p.FromNormalized(_dragStartNormalized + upDp / KnobDragDp * (fine ? 0.1f : 1f));
        Edit(tab, "Could not change the effect", () => tab.Document.SetInsertParam(insert, p.Id, value, MergeKey(p.Id)));
    }

    /// <summary>Starts dragging EQ band <paramref name="band"/>'s handle.</summary>
    internal void BeginBand(int band)
    {
        EndEdits();
        _dragCounter++;
        _dragBand = band;
        if (_graph is { } graph)
            graph.SelectedBand = band;
        _graphDirty = true;
    }

    /// <summary>Moves the dragged EQ handle to <paramref name="point"/> (the graph's dp): frequency, and gain where the band has one.</summary>
    internal void DragBand(Vector2 point)
    {
        if (_dragBand < 0 || Insert is not { } insert || Info is not { } info || Tab is not { } tab || _graph is not { } graph)
            return;
        var band = EqEffect.Bands[_dragBand];
        var values = new Dictionary<string, float>(StringComparer.Ordinal) { [band.FreqId] = graph.Frequency(point.X) };
        if (band.GainId is not null)
            values[band.GainId] = MathF.Round(graph.Gain(point.Y) * 10) / 10;
        if (info.Value(insert.Params, band.OnId) < 0.5f)
            values[band.OnId] = 1; // dragging a band switches it on
        Edit(tab, "Could not change the EQ", () => tab.Document.SetInsertParams(insert, values, "EQ " + band.Name, MergeKey(band.Name)));
    }

    /// <summary>Ends a knob or handle drag (one undo step).</summary>
    internal void EndDrag()
    {
        _dragBand = -1;
        _dragParam = -1;
        EndEdits();
    }

    /// <summary>The EQ graph (null before an EQ was shown).</summary>
    internal EqGraphCanvas? Graph => _graph;

    private void OnDragEnd(RmlEvent e)
    {
        if (_moving)
            Workspace.SaveLayout();
        _moving = false;
        if (_dragParam >= 0)
            Document.QuerySelector(".knob-dial.dragging").SetClass("dragging", false);
        EndDrag();
    }

    private void OnDoubleClick(RmlEvent e)
    {
        if (Insert is not { } insert || Info is not { } info || Tab is not { } tab || tab.Document.IsReadOnly)
            return;
        if (FindWithAttribute(e.Target, "data-p") is { IsNull: false } dial && dial.IsClassSet("knob-dial") &&
            int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            var p = info.Params[index];
            Edit(tab, "Could not reset the parameter", () => tab.Document.SetInsertParam(insert, p.Id, p.Default));
            return;
        }

        if (IsInGraph(e.Target) && _graph is { } graph && graph.HitBand(GraphPoint(e)) is >= 0 and var band)
        {
            var on = EqEffect.Bands[band].OnId;
            Edit(tab, "Could not switch the band", () => tab.Document.SetInsertParam(insert, on, info.Value(insert.Params, on) >= 0.5f ? 0 : 1));
        }
    }

    private void OnWheel(RmlEvent e)
    {
        if (Insert is not { } insert || Info is not { } info || Tab is not { } tab || tab.Document.IsReadOnly)
            return;
        var delta = (float)e.GetParameter("wheel_delta_y", 0.0);
        if (delta == 0)
            return;
        string? id = null;
        float value = 0;
        if (FindWithAttribute(e.Target, "data-p") is { IsNull: false } dial && dial.IsClassSet("knob-dial") &&
            int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            var p = info.Params[index];
            var step = e.GetParameter("shift_key", 0) != 0 ? 0.005f : 0.02f;
            value = p.FromNormalized(p.ToNormalized(info.Value(insert.Params, p.Id)) - MathF.Sign(delta) * step);
            id = p.Id;
        }
        else if (IsInGraph(e.Target) && _graph is { } graph && graph.SelectedBand is >= 0 and var band && EqEffect.Bands[band].QId is { } q)
        {
            var p = info.Find(q)!;
            value = p.FromNormalized(p.ToNormalized(info.Value(insert.Params, q)) - MathF.Sign(delta) * 0.03f);
            id = q;
        }

        if (id is null)
            return;
        e.StopPropagation();
        var key = "pw-wheel-" + id;
        if (_wheelKey != key)
            EndEdits();
        _wheelKey = key;
        _wheelAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Edit(tab, "Could not change the effect", () => tab.Document.SetInsertParam(insert, id, value, key));
    }

    private string MergeKey(string id) => "pw-" + _dragCounter.ToString(CultureInfo.InvariantCulture) + "-" + id;

    private void EndEdits()
    {
        _wheelKey = null;
        Tab?.Document.History.EndMerge();
    }

    // --- menus --------------------------------------------------------------------------------------------------------

    private void ChoiceMenu(SongTab tab, PluginInsert insert, BuiltinParam p, RmlEvent e)
    {
        if (p.Choices is not { } choices || tab.Document.IsReadOnly)
            return;
        var current = (int)(Info?.Value(insert.Params, p.Id) ?? 0);
        var items = choices.Select((c, i) => new MenuItem(c, i.ToString(CultureInfo.InvariantCulture), Css: i == current ? "current" : null)).ToList();
        var (x, y) = MenuPoint(e);
        Workspace.Popup.Show(items, x, y, command =>
        {
            if (int.TryParse(command, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && ReferenceEquals(Insert, insert))
                Edit(tab, "Could not change the effect", () => tab.Document.SetInsertParam(insert, p.Id, index));
        });
    }

    private void PresetMenu(SongTab tab, PluginInsert insert, BuiltinEffectInfo info)
    {
        if (info.Presets is not { Length: > 0 } presets || tab.Document.IsReadOnly)
            return;
        var items = presets.Select((preset, i) => new MenuItem(preset.Name, i.ToString(CultureInfo.InvariantCulture), Icon: "adjustments")).ToList();
        var x = _position.X + _width - 200;
        var y = _position.Y + TitleHeight;
        Workspace.Popup.Show(items, x, y, command =>
        {
            if (!int.TryParse(command, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || !ReferenceEquals(Insert, insert))
                return;
            var preset = presets[index];
            var values = info.Defaults();
            foreach (var (id, value) in preset.Values)
                values[id] = value;
            Edit(tab, "Could not load the preset", () => tab.Document.SetInsertParams(insert, values, "Preset: " + preset.Name));
        });
    }

    private (float X, float Y) MenuPoint(RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        return ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
    }

    private static void Edit(SongTab tab, string what, Action edit)
    {
        if (tab.Document.IsReadOnly)
            return;
        try
        {
            edit();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warning($"[Music] {what}: {ex.Message}");
        }
    }
}
