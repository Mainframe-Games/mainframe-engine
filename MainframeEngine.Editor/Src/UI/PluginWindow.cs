using System.Numerics;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The window of a built-in effect insert in music mode (Logic's plugin window): one reused, floating, non-modal window
/// over the song view. Its controls are an <see cref="EffectPanel"/> (knobs, toggles and menus built from the effect's
/// <see cref="BuiltinParam"/>s, the Channel EQ's curve with draggable band handles, a gain-reduction meter, the delay's
/// time readout); the window adds the title bar (bypass, presets, reset, close; drag to move). Every edit goes through the
/// song's <see cref="SongDocument"/> (a knob drag is one undo step); undo, redo and edits from elsewhere show on the next
/// tick. It closes when its insert leaves the song (removed, undone, the tab closed or switched) or music mode is left.
/// </summary>
public sealed class PluginWindow : EditorDocument
{
    public const string GraphTexture = "plugin-graph";

    // The window's chrome in dp, as plugin_window.rml lays it out.
    private const float Border = 1;
    private const float TitleHeight = 30;

    private readonly List<RmlEventListener?> _listeners = [];
    private readonly EffectPanel _panel;
    private bool _built;
    private bool _shownBypass;
    private bool _shownReadOnly;
    private (float X, float Y) _position = (float.NaN, float.NaN);
    private float _width = 480;
    private (float X, float Y) _moveOffset;
    private bool _moving;
    private float _dragStartY;

    public PluginWindow(EditorWorkspace workspace)
        : base(workspace, "plugin_window.rml")
    {
        Visible = false;
        _panel = new EffectPanel(workspace, "pw", "", GraphTexture);
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

    /// <summary>
    /// Shows <paramref name="insert"/> (a built-in) of <paramref name="track"/> (null: the master) in <paramref name="tab"/>'s
    /// song: one of the track's inserts, or an effect of its wavetable synth's own chain.
    /// </summary>
    public void Open(SongTab tab, SongTrack? track, PluginInsert insert)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(insert);
        if (BuiltinEffects.Find(insert.Builtin ?? "") is not { } info)
        {
            Log.Warning($"[Music] {insert.DisplayName} has no window (not a built-in effect this editor knows).");
            return;
        }

        _panel.EndDrag();
        Tab = tab;
        Track = track;
        Insert = insert;
        Info = info;
        _width = WidthFor(info);
        _built = false;
        Visible = true;
        if (EnsureLoaded())
            Build();
    }

    /// <summary>Hides the window (keeps its position for the next one).</summary>
    public void Close()
    {
        if (!IsOpen)
            return;
        _panel.Clear();
        Tab = null;
        Track = null;
        Insert = null;
        Info = null;
        HideAndReleaseFocus();
    }

    /// <summary>Every frame from the song view: <paramref name="tab"/> is music mode's active song tab (null otherwise).</summary>
    public void Tick(SongTab? tab)
    {
        if (!IsOpen || Tab is not { } shown)
            return;
        if (!ReferenceEquals(tab, shown) || !StillInSong(shown, Track, Insert))
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

        var document = Document;
        _panel.Tick(document);
        var bypass = Insert?.Bypass ?? false;
        if (bypass != _shownBypass)
        {
            _shownBypass = bypass;
            document.GetElementById("pw-window").SetClass("bypassed", bypass);
            document.GetElementById("pw-power").SetClass("active", !bypass);
        }

        var readOnly = shown.Document.IsReadOnly;
        if (readOnly != _shownReadOnly)
        {
            _shownReadOnly = readOnly;
            document.GetElementById("pw-window").SetClass("readonly", readOnly);
        }
    }

    private static bool StillInSong(SongTab tab, SongTrack? track, PluginInsert? insert)
    {
        var song = tab.Document.Song;
        if (insert is null || (track is not null && !song.Tracks.Contains(track)))
            return false;
        return (track?.Inserts ?? song.Master.Inserts).Contains(insert) || track?.Instrument?.Patch?.Fx?.Contains(insert) == true;
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
        if (!IsOpen || Info is not { } info || Tab is not { } tab || Insert is not { } insert)
            return;
        var document = Document;
        _built = true;
        _shownBypass = !insert.Bypass;
        _shownReadOnly = !tab.Document.IsReadOnly;
        var trackName = Track?.Name ?? "Master";
        var synth = Track?.Instrument?.Patch?.Fx?.Contains(insert) == true ? " — " + SynthParams.ProductName : "";
        document.GetElementById("pw-name").SetInnerRml(RmlText.Escape($"{info.Name} — {trackName}{synth} — {tab.Document.Name}"));
        document.GetElementById("pw-icon").SetClassNames("icon icon-sm icon-" + info.Icon);
        document.GetElementById("pw-presets").SetClass("hidden", info.Presets is not { Length: > 0 });
        Place();
        _panel.Show(document, tab, Track, insert, info);
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

    // --- input --------------------------------------------------------------------------------------------------------

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        _listeners.Clear();
        _built = false;
        _panel.Attached();
        var body = document.AsElement();
        _listeners.Add(body.AddEventListener("dragstart", OnDragStart));
        _listeners.Add(body.AddEventListener("drag", OnDrag));
        _listeners.Add(body.AddEventListener("dragend", OnDragEnd));
        _listeners.Add(body.AddEventListener("dblclick", e => _panel.DoubleClick(Document, e)));
        _listeners.Add(body.AddEventListener("mousescroll", e => _panel.Wheel(e)));
        _listeners.Add(body.AddEventListener("mousedown", OnMouseDown));
        _listeners.Add(body.AddEventListener("keydown", e =>
        {
            if ((RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape)
                Close();
        }));
        if (IsOpen)
            Build();
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (!IsOpen || Tab is not { } tab)
            return;
        for (var el = e.Target; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            switch (el.Id)
            {
                case "pw-close":
                    Close();
                    return;
                case "pw-power":
                    if (Insert is { } bypassed)
                    {
                        if (!tab.Document.IsReadOnly)
                            tab.Document.SetInsertBypass(bypassed, !bypassed.Bypass);
                    }

                    return;
                case "pw-reset":
                    _panel.Reset();
                    return;
                case "pw-presets":
                    _panel.PresetMenu(_position.X + _width - 200, _position.Y + TitleHeight);
                    return;
            }
        }

        _panel.Click(e);
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (!IsOpen)
            return;
        // A press on the window brings it in front of the song view's other documents.
        Document.PullToFront();
        _panel.MouseDown(Document, e);
    }

    private void OnDragStart(RmlEvent e)
    {
        if (!IsOpen)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var mouse = ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
        _dragStartY = mouse.Item2;
        if (_panel.DragStart(Document, e))
            return;
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
            return;
        }

        _panel.Drag(Document, e, _dragStartY - y);
    }

    private void OnDragEnd(RmlEvent e)
    {
        if (_moving)
            Workspace.SaveLayout();
        _moving = false;
        _panel.DragEnd(Document);
    }

    /// <summary>Starts a knob drag on parameter <paramref name="index"/> (the mouse, and tests).</summary>
    internal void BeginKnob(int index) => _panel.BeginKnob(index);

    /// <summary>The knob being dragged, <paramref name="upDp"/> above where the drag started (Shift: a tenth as far).</summary>
    internal void DragKnob(float upDp, bool fine) => _panel.DragKnob(upDp, fine);

    /// <summary>Starts dragging EQ band <paramref name="band"/>'s handle.</summary>
    internal void BeginBand(int band) => _panel.BeginBand(band);

    /// <summary>Moves the dragged EQ handle to <paramref name="point"/> (the graph's dp).</summary>
    internal void DragBand(Vector2 point) => _panel.DragBand(point);

    /// <summary>Ends a knob or handle drag (one undo step).</summary>
    internal void EndDrag() => _panel.EndDrag();

    /// <summary>The EQ graph (null before an EQ was shown).</summary>
    internal EqGraphCanvas? Graph => _panel.Graph;
}
