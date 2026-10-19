using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// A built-in effect's controls inside a music window: knobs, toggles and choice menus built from its
/// <see cref="BuiltinParam"/>s (<see cref="KnobMarkup"/>), the Channel EQ's curve with draggable band handles
/// (<see cref="EqGraphCanvas"/> in its own sub-viewport), the gain-reduction meter of the dynamics effects or the delay's
/// time at the song's tempo. The plugin window shows one; the synth window's FX tab shows the selected effect of the synth's
/// chain. The host document holds the elements <c>{Prefix}-graph</c> (with an <c>img</c>), <c>-meter</c>, <c>-meter-fill</c>,
/// <c>-meter-db</c>, <c>-readout</c> and <c>-body</c>, forwards its input events, and ticks it every frame. Every edit is a
/// <see cref="SongDocument"/> step (a knob or handle drag is one).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The graph's sub-viewport is a node of the workspace's scenes host: the tree frees it with the editor.")]
internal sealed class EffectPanel(EditorWorkspace workspace, string prefix, string knobPrefix, string texture)
{
    private const float KnobDragDp = 200; // vertical travel for the whole range
    private const double WheelMergeSeconds = 0.6;

    private static readonly string[] Percent = [.. Enumerable.Range(0, 101).Select(i => i.ToString(CultureInfo.InvariantCulture) + "%")];

    private readonly string _graphId = prefix + "-graph";
    private float[] _shown = [];
    private string?[] _shownText = [];
    private SubViewport? _graphViewport;
    private EqGraphCanvas? _graph;
    private bool _graphRegistered;
    private bool _graphDirty;
    private int _shownGr = -1;
    private float _gr;
    private double _shownTempo = double.NaN;
    private int _dragParam = -1;
    private float _dragStartNormalized;
    private int _dragBand = -1;
    private int _dragCounter;
    private string? _wheelKey;
    private long _wheelAt;

    public SongTab? Tab { get; private set; }

    /// <summary>The insert's track (null: the master).</summary>
    public SongTrack? Track { get; private set; }

    public PluginInsert? Insert { get; private set; }

    public BuiltinEffectInfo? Info { get; private set; }

    /// <summary>The EQ curve (null before an EQ was shown).</summary>
    public EqGraphCanvas? Graph => _graph;

    /// <summary>Whether a knob or EQ handle drag is under way.</summary>
    public bool Dragging => _dragParam >= 0 || _dragBand >= 0;

    private Dictionary<string, float>? Values => Insert?.Params;

    /// <summary>Builds the controls of <paramref name="insert"/> (a built-in of <paramref name="track"/>, null: the master).</summary>
    public void Show(RmlDocument document, SongTab tab, SongTrack? track, PluginInsert insert, BuiltinEffectInfo info)
    {
        EndEdits();
        Tab = tab;
        Track = track;
        Insert = insert;
        Info = info;
        _graphDirty = true;
        _shownTempo = double.NaN;
        _shownGr = -1;
        _gr = 0;
        _shown = new float[info.Params.Length];
        Array.Fill(_shown, float.NaN);
        _shownText = new string?[info.Params.Length];
        document.GetElementById(_graphId).SetClass("hidden", info.Graph != BuiltinGraph.Eq);
        document.GetElementById(prefix + "-meter").SetClass("hidden", info.Graph != BuiltinGraph.GainReduction);
        document.GetElementById(prefix + "-readout").SetClass("hidden", info.Graph != BuiltinGraph.DelayReadout);
        document.GetElementById(prefix + "-body").SetInnerRml(BodyRml(info, knobPrefix));
        if (info.Graph == BuiltinGraph.Eq)
            EnsureGraph(document);
    }

    /// <summary>Shows nothing (the effect left, or its window closed).</summary>
    public void Clear(RmlDocument? document = null)
    {
        EndEdits();
        _dragParam = -1;
        _dragBand = -1;
        Tab = null;
        Track = null;
        Insert = null;
        Info = null;
        if (_graphViewport is { } viewport)
            viewport.UpdateMode = SubViewportUpdateMode.Disabled;
        if (document is { } d)
        {
            d.GetElementById(prefix + "-body").SetInnerRml("");
            foreach (var part in (ReadOnlySpan<string>)["-graph", "-meter", "-readout"])
                d.GetElementById(prefix + part).SetClass("hidden", true);
        }
    }

    /// <summary>The host's document was (re)loaded: the graph's texture registers again.</summary>
    public void Attached() => _graphRegistered = false;

    /// <summary>Every frame while shown: values that changed, the curve, the meter, the readout.</summary>
    public void Tick(RmlDocument document)
    {
        if (Info is not { } info || Tab is not { } tab)
            return;
        if (_wheelKey is not null && (System.Diagnostics.Stopwatch.GetTimestamp() - _wheelAt) > WheelMergeSeconds * System.Diagnostics.Stopwatch.Frequency)
            EndEdits();

        var parameters = info.Params;
        for (var i = 0; i < parameters.Length; i++)
        {
            var value = info.Value(Values, parameters[i].Id);
            if (value == _shown[i])
                continue;
            _shown[i] = value;
            KnobMarkup.Show(document, knobPrefix, i, parameters[i], value, _shownText);
            _graphDirty = true;
        }

        switch (info.Graph)
        {
            case BuiltinGraph.Eq:
                TickGraph(document);
                break;
            case BuiltinGraph.GainReduction:
                if (Insert is { } metered)
                    TickMeter(document, tab, metered);
                break;
            case BuiltinGraph.DelayReadout when _graphDirty || tab.Document.Song.Tempo != _shownTempo:
                _shownTempo = tab.Document.Song.Tempo;
                document.GetElementById(prefix + "-readout").SetInnerRml(RmlText.Escape(DelayEffect.Describe(Values, _shownTempo)));
                _graphDirty = false;
                break;
        }
    }

    // Sections: the EQ's bands as columns (their colour swatch on top); other effects' groups as boxes of knobs.
    private static string BodyRml(BuiltinEffectInfo info, string knobPrefix)
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
                sb.Append("<span>").Append(RmlText.Escape(group.Key)).Append("</span></div>");
            }

            sb.Append("<div class=\"pw-knobs\">");
            foreach (var (p, i) in group)
                KnobMarkup.Append(sb, p, i, knobPrefix);
            sb.Append("</div></div>");
        }

        return sb.ToString();
    }

    // --- the EQ graph -------------------------------------------------------------------------------------------------

    private void EnsureGraph(RmlDocument document)
    {
        if (_graphViewport is null)
        {
            _graphViewport = new SubViewport
            {
                Name = "EffectGraph-" + prefix,
                Disable3D = true,
                UpdateMode = SubViewportUpdateMode.Disabled,
                ClearColor = System.Drawing.Color.FromArgb(255, 21, 23, 28),
            };
            _graph = new EqGraphCanvas { Name = "EqGraph" };
            _graphViewport.AddChild(_graph);
            workspace.ScenesHost.AddChild(_graphViewport);
        }

        if (!_graphRegistered && workspace.Tree?.Servers.Get<UiServer>() is { } ui)
        {
            ui.RegisterTexture(texture, _graphViewport);
            _graphRegistered = true;
        }

        document.GetElementById(_graphId).QuerySelector("img").SetAttribute("src", "engine://" + texture);
        _graphDirty = true;
    }

    private void TickGraph(RmlDocument document)
    {
        if (_graphViewport is not { } viewport || _graph is not { } graph)
            return;
        var scale = MathF.Max(0.01f, workspace.Host.PixelScale);
        var b = document.GetElementById(_graphId).Bounds;
        if (b.Width < 4 || b.Height < 4)
            return; // not laid out (or hidden)
        // Inside the 1 dp border.
        var w = b.Width / scale - 2;
        var h = b.Height / scale - 2;
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
            if (viewport.UpdateMode != SubViewportUpdateMode.Once && viewport.UpdateMode != SubViewportUpdateMode.Disabled)
                viewport.UpdateMode = SubViewportUpdateMode.Disabled;
            return;
        }

        _graphDirty = false;
        graph.Values = Values;
        graph.PixelScale = scale;
        graph.ViewSize = new Vector2(w, h);
        graph.QueueRedraw();
        viewport.UpdateMode = SubViewportUpdateMode.Once;
    }

    /// <summary>Whether <paramref name="element"/> is in the EQ curve.</summary>
    public bool IsInGraph(RmlElement element)
    {
        for (var el = element; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            if (el.Id == _graphId)
                return true;
        }

        return false;
    }

    // Mouse position (window pixels) → the graph's dp coordinates (inside its 1 dp border).
    private Vector2 GraphPoint(RmlDocument document, RmlEvent e)
    {
        var scale = MathF.Max(0.01f, workspace.Host.PixelScale);
        var b = document.GetElementById(_graphId).Bounds;
        return new Vector2((float)((e.GetParameter("mouse_x", 0.0) - b.X) / scale) - 1, (float)((e.GetParameter("mouse_y", 0.0) - b.Y) / scale) - 1);
    }

    // --- the meter ----------------------------------------------------------------------------------------------------

    private void TickMeter(RmlDocument document, SongTab tab, PluginInsert insert)
    {
        var taken = tab.Player.FindBuiltin(insert)?.TakeGainReductionDb() ?? 0;
        // Falls 20 dB/s, like the mixer's meters.
        _gr = MathF.Max(taken, _gr - 20f / 60f);
        var tenths = (int)MathF.Round(Math.Clamp(_gr, 0, 30) * 10);
        if (tenths == _shownGr)
            return;
        _shownGr = tenths;
        document.GetElementById(prefix + "-meter-fill").SetProperty("width", Percent[Math.Clamp(tenths * 100 / 300, 0, 100)]);
        document.GetElementById(prefix + "-meter-db")
            .SetInnerRml(tenths == 0 ? "0.0 dB" : "-" + (tenths / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " dB");
    }

    // --- input (forwarded by the host; each returns whether the event was the panel's) --------------------------------

    /// <summary>A click: a toggle switches, a choice opens its menu.</summary>
    public bool Click(RmlEvent e)
    {
        if (Info is not { } info || Tab is not { } tab)
            return false;
        var index = KnobMarkup.IndexOf(e.Target, knobPrefix);
        if ((uint)index >= (uint)info.Params.Length)
            return false;
        var p = info.Params[index];
        if (p.Curve == ParamCurve.Toggle)
        {
            var value = info.Value(Values, p.Id) >= 0.5f ? 0 : 1;
            Edit(tab, "Could not change the effect", () => SetParam(tab, p.Id, value));
        }
        else if (p.Curve == ParamCurve.Choice)
        {
            ChoiceMenu(tab, p, e);
        }

        return true;
    }

    /// <summary>A press in the curve selects the band under it.</summary>
    public void MouseDown(RmlDocument document, RmlEvent e)
    {
        if (Info?.Graph != BuiltinGraph.Eq || _graph is not { } graph || !IsInGraph(e.Target))
            return;
        var band = graph.HitBand(GraphPoint(document, e));
        if (band != graph.SelectedBand)
        {
            graph.SelectedBand = band;
            _graphDirty = true;
        }
    }

    /// <summary>A drag starting on a knob or in the curve.</summary>
    public bool DragStart(RmlDocument document, RmlEvent e)
    {
        if (Info is null)
            return false;
        var index = KnobMarkup.IndexOf(e.Target, knobPrefix, dialOnly: true);
        if (index >= 0)
        {
            BeginKnob(index);
            FindDial(e.Target).SetClass("dragging", true);
            return true;
        }

        if (IsInGraph(e.Target) && _graph is { } graph)
        {
            BeginBand(graph.HitBand(GraphPoint(document, e)));
            return true;
        }

        return false;
    }

    private static RmlElement FindDial(RmlElement element)
    {
        for (var el = element; !el.IsNull; el = el.Parent)
        {
            if (el.IsClassSet("knob-dial"))
                return el;
        }

        return element;
    }

    /// <summary>The mouse moved during the panel's drag; <paramref name="upDp"/> is how far above the drag's start it is.</summary>
    public bool Drag(RmlDocument document, RmlEvent e, float upDp)
    {
        if (_dragParam >= 0)
        {
            DragKnob(upDp, e.GetParameter("shift_key", 0) != 0);
            return true;
        }

        if (_dragBand >= 0)
        {
            DragBand(GraphPoint(document, e));
            return true;
        }

        return false;
    }

    /// <summary>The panel's drag ended (one undo step).</summary>
    public void DragEnd(RmlDocument document)
    {
        if (_dragParam >= 0)
        {
            foreach (var dial in document.AsElement().QuerySelectorAll(".knob-dial.dragging"))
                dial.SetClass("dragging", false);
        }

        EndDrag();
    }

    /// <summary>Starts a knob drag on parameter <paramref name="index"/> (the mouse, and tests).</summary>
    public void BeginKnob(int index)
    {
        if (Info is not { } info || (uint)index >= (uint)info.Params.Length)
            return;
        EndEdits();
        _dragCounter++;
        _dragParam = index;
        _dragBand = -1;
        _dragStartNormalized = info.Params[index].ToNormalized(info.Value(Values, info.Params[index].Id));
    }

    /// <summary>The knob being dragged, <paramref name="upDp"/> above where the drag started (Shift: a tenth as far).</summary>
    public void DragKnob(float upDp, bool fine)
    {
        if (_dragParam < 0 || Info is not { } info || Tab is not { } tab)
            return;
        var p = info.Params[_dragParam];
        var value = p.FromNormalized(_dragStartNormalized + upDp / KnobDragDp * (fine ? 0.1f : 1f));
        Edit(tab, "Could not change the effect", () => SetParam(tab, p.Id, value, MergeKey(p.Id)));
    }

    /// <summary>Starts dragging EQ band <paramref name="band"/>'s handle.</summary>
    public void BeginBand(int band)
    {
        EndEdits();
        _dragCounter++;
        _dragBand = band;
        _dragParam = -1;
        if (_graph is { } graph)
            graph.SelectedBand = band;
        _graphDirty = true;
    }

    /// <summary>Moves the dragged EQ handle to <paramref name="point"/> (the graph's dp): frequency, and gain where the band has one.</summary>
    public void DragBand(Vector2 point)
    {
        if (_dragBand < 0 || Info is not { } info || Tab is not { } tab || _graph is not { } graph)
            return;
        var band = EqEffect.Bands[_dragBand];
        var values = new Dictionary<string, float>(StringComparer.Ordinal) { [band.FreqId] = graph.Frequency(point.X) };
        if (band.GainId is not null)
            values[band.GainId] = MathF.Round(graph.Gain(point.Y) * 10) / 10;
        if (info.Value(Values, band.OnId) < 0.5f)
            values[band.OnId] = 1; // dragging a band switches it on
        Edit(tab, "Could not change the EQ", () => SetParams(tab, values, "EQ " + band.Name, MergeKey(band.Name)));
    }

    /// <summary>Ends a knob or handle drag (one undo step).</summary>
    public void EndDrag()
    {
        _dragBand = -1;
        _dragParam = -1;
        EndEdits();
    }

    /// <summary>A double-click: a knob resets, a band handle switches its band on or off.</summary>
    public bool DoubleClick(RmlDocument document, RmlEvent e)
    {
        if (Info is not { } info || Tab is not { } tab || tab.Document.IsReadOnly)
            return false;
        var index = KnobMarkup.IndexOf(e.Target, knobPrefix, dialOnly: true);
        if (index >= 0)
        {
            var p = info.Params[index];
            Edit(tab, "Could not reset the parameter", () => SetParam(tab, p.Id, p.Default));
            return true;
        }

        if (IsInGraph(e.Target) && _graph is { } graph && graph.HitBand(GraphPoint(document, e)) is >= 0 and var band)
        {
            var on = EqEffect.Bands[band].OnId;
            Edit(tab, "Could not switch the band", () => SetParam(tab, on, info.Value(Values, on) >= 0.5f ? 0 : 1));
            return true;
        }

        return false;
    }

    /// <summary>The wheel: a knob steps, the curve sets the selected band's Q.</summary>
    public bool Wheel(RmlEvent e)
    {
        if (Info is not { } info || Tab is not { } tab || tab.Document.IsReadOnly)
            return false;
        var delta = (float)e.GetParameter("wheel_delta_y", 0.0);
        if (delta == 0)
            return false;
        string? id = null;
        float value = 0;
        var index = KnobMarkup.IndexOf(e.Target, knobPrefix, dialOnly: true);
        if (index >= 0)
        {
            var p = info.Params[index];
            var step = e.GetParameter("shift_key", 0) != 0 ? 0.005f : 0.02f;
            value = p.FromNormalized(p.ToNormalized(info.Value(Values, p.Id)) - MathF.Sign(delta) * step);
            id = p.Id;
        }
        else if (IsInGraph(e.Target) && _graph is { } graph && graph.SelectedBand is >= 0 and var band && EqEffect.Bands[band].QId is { } q)
        {
            var p = info.Find(q)!;
            value = p.FromNormalized(p.ToNormalized(info.Value(Values, q)) - MathF.Sign(delta) * 0.03f);
            id = q;
        }

        if (id is null)
            return false;
        e.StopPropagation();
        var key = prefix + "-wheel-" + id;
        if (_wheelKey != key)
            EndEdits();
        _wheelKey = key;
        _wheelAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Edit(tab, "Could not change the effect", () => SetParam(tab, id, value, key));
        return true;
    }

    /// <summary>Every parameter to its default (one undo step).</summary>
    public void Reset()
    {
        if (Info is { } info && Tab is { } tab)
            Edit(tab, "Could not reset the effect", () => SetParams(tab, info.Defaults(), "Reset " + info.Name));
    }

    /// <summary>The effect's presets as a menu at (<paramref name="x"/>, <paramref name="y"/>) dp.</summary>
    public void PresetMenu(float x, float y)
    {
        if (Info is not { Presets: { Length: > 0 } presets } info || Tab is not { } tab || tab.Document.IsReadOnly)
            return;
        var shown = Insert;
        var items = presets.Select((preset, i) => new MenuItem(preset.Name, i.ToString(CultureInfo.InvariantCulture), Icon: "adjustments")).ToList();
        workspace.Popup.Show(items, x, y, command =>
        {
            if (!int.TryParse(command, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || !ReferenceEquals(Insert, shown))
                return;
            var preset = presets[index];
            var values = info.Defaults();
            foreach (var (id, value) in preset.Values)
                values[id] = value;
            Edit(tab, "Could not load the preset", () => SetParams(tab, values, "Preset: " + preset.Name));
        });
    }

    private void ChoiceMenu(SongTab tab, BuiltinParam p, RmlEvent e)
    {
        if (p.Choices is not { } choices || tab.Document.IsReadOnly)
            return;
        var current = (int)(Info?.Value(Values, p.Id) ?? 0);
        var shown = Insert;
        var items = choices.Select((c, i) => new MenuItem(c, i.ToString(CultureInfo.InvariantCulture), Css: i == current ? "current" : null)).ToList();
        var scale = MathF.Max(0.01f, workspace.Host.PixelScale);
        workspace.Popup.Show(items, (float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale), command =>
        {
            if (int.TryParse(command, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && ReferenceEquals(Insert, shown))
                Edit(tab, "Could not change the effect", () => SetParam(tab, p.Id, index));
        });
    }

    private void SetParam(SongTab tab, string id, float value, string? mergeKey = null)
    {
        if (Insert is { } insert)
            tab.Document.SetInsertParam(insert, id, value, mergeKey);
    }

    private void SetParams(SongTab tab, IReadOnlyDictionary<string, float> values, string name, string? mergeKey = null)
    {
        if (Insert is { } insert)
            tab.Document.SetInsertParams(insert, values, name, mergeKey);
    }

    private string MergeKey(string id) => prefix + "-" + _dragCounter.ToString(CultureInfo.InvariantCulture) + "-" + id;

    private void EndEdits()
    {
        _wheelKey = null;
        Tab?.Document.History.EndMerge();
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
