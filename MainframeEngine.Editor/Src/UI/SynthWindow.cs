using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The wavetable synth's window ("Mainframe Wavetable", <c>Content/Editor/synth_window.rml</c>): one floating, non-modal
/// window for a track's synth. Osc (oscillators with their wavetables, sub, noise, filters with their curves, voicing,
/// master, macros and the envelope / LFO editor), Matrix (every route) and FX (the synth's effect chain); the modulation
/// strip (drag a source onto a knob to modulate it) and a keyboard. Every edit is a <see cref="SongDocument"/> step (a knob
/// drag is one); undo, redo and edits elsewhere show on the next frame. It closes when the track's instrument changes,
/// another song tab becomes active, or music mode is left.
/// </summary>
public sealed class SynthWindow : EditorDocument
{
    private const float KnobDragDp = 200;
    private const float WindowWidth = 1010;
    private const float DragThreshold = 4;

    /// <summary>The FX tab's EQ curve texture.</summary>
    public const string FxGraphTexture = "synth-fx-graph";
    private static readonly int ParamCount = SynthParams.All.Length;
    private static readonly string[] Degrees = KnobMarkup.Degrees;
    private static readonly List<SynthModRoute> NoRoutes = [];
    private static readonly List<PluginInsert> NoFx = [];

    /// <summary>The modulation sources' names (by source index) and colours.</summary>
    public static readonly string[] SourceNames =
    [
        "Env 1", "Env 2", "Env 3", "Env 4", "LFO 1", "LFO 2", "LFO 3", "LFO 4", "Velocity", "Note", "Random",
        "Macro 1", "Macro 2", "Macro 3", "Macro 4", "Mod Wheel", "Aftertouch", "Pitch Bend",
    ];

    public static readonly string[] SourceColors =
    [
        "#a78bfa", "#c084fc", "#e879f9", "#f472b6", "#34d399", "#2dd4bf", "#22d3ee", "#38bdf8", "#facc15", "#fb923c", "#f87171",
        "#fbbf24", "#f59e0b", "#d97706", "#eab308", "#94a3b8", "#cbd5e1", "#e2e8f0",
    ];

    private readonly List<RmlEventListener?> _listeners = [];
    private readonly float[] _shown = new float[ParamCount];
    private readonly string?[] _shownText = new string?[ParamCount];
    private readonly int[] _shownLive = new int[ParamCount];
    private readonly View _waveA = new("synth-osc-a", "sw-wt-a", new WavetableCanvas { Name = "SynthOscA" });
    private readonly View _waveB = new("synth-osc-b", "sw-wt-b", new WavetableCanvas { Name = "SynthOscB", Color = new Vector4(52 / 255f, 211 / 255f, 153 / 255f, 1) });
    private readonly View _filter = new("synth-filter", "sw-filter", new SynthFilterCanvas { Name = "SynthFilter" });
    private readonly View _mod = new("synth-mod", "sw-mod-canvas", new SynthModCanvas { Name = "SynthMod" });
    private bool _built;
    private string _page = "osc";
    private int _modSource; // 0–3 envelopes, 4–7 LFOs, 8 macros
    private int? _matrixSignature;
    private int? _fxSignature;
    private string? _shownPreset;
    private (int Voices, int Load) _shownCpu = (-1, -1);
    private (float X, float Y) _position = (float.NaN, float.NaN);
    private (float X, float Y) _moveOffset;
    private bool _moving;
    private int _cpuFrames;
    private int _octave = 4;
    private int _heldKey = -1;

    // Gestures: a knob drag (its parameter, start value, or a route's amount), a mod-editor handle, a source tile drag.
    private int _dragParam = -1;
    private int _dragRoute = -1;
    private float _dragStart;
    private float _dragStartY;
    private int _dragCounter;
    private int _modHandle = -1;
    private bool _bending;
    private int _pressedSource = -1;
    private (float X, float Y) _pressedAt;
    private int _draggingSource = -1;
    private readonly Dictionary<int, int> _selectedRoute = [];
    private readonly EffectPanel _fxPanel;
    private PluginInsert? _fxSelected;
    private int _fxDrag = -1;
    private int _hoverSource = -1;

    public SynthWindow(EditorWorkspace workspace)
        : base(workspace, "synth_window.rml")
    {
        Visible = false;
        _fxPanel = new EffectPanel(workspace, "sx", "f", FxGraphTexture);
        Array.Fill(_shown, float.NaN);
        Array.Fill(_shownLive, -1);
    }

    public SongTab? Tab { get; private set; }

    public SongTrack? Track { get; private set; }

    /// <summary>The synth shown (its track's instrument), or null when closed.</summary>
    public InstrumentDescriptor? Instrument { get; private set; }

    public bool IsOpen => Instrument is not null;

    /// <summary>The shown tab: <c>osc</c>, <c>matrix</c> or <c>fx</c>.</summary>
    public string Page => _page;

    /// <summary>The modulation editor's source: 0–3 envelope 1–4, 4–7 LFO 1–4, 8 the macros.</summary>
    public int ModSource => _modSource;

    private Dictionary<string, float>? Values => Instrument?.Patch?.Params;

    private List<SynthModRoute> Routes => Instrument?.Patch?.Matrix ?? NoRoutes;

    /// <summary>Shows <paramref name="track"/>'s wavetable synth.</summary>
    public void Open(SongTab tab, SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(track);
        if (track.Instrument is not { IsWavetable: true } instrument)
            return;
        Tab = tab;
        Track = track;
        Instrument = instrument;
        _built = false;
        Visible = true;
        if (EnsureLoaded())
            Build();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        ReleaseKey();
        EndEdits();
        _fxPanel.Clear();
        _fxSelected = null;
        Tab = null;
        Track = null;
        Instrument = null;
        HideAndReleaseFocus();
        foreach (var v in (ReadOnlySpan<View>)[_waveA, _waveB, _filter, _mod])
        {
            if (v.Viewport is { } viewport)
                viewport.UpdateMode = SubViewportUpdateMode.Disabled;
        }
    }

    /// <summary>Every frame from the song view: <paramref name="tab"/> is music mode's active song tab (null otherwise).</summary>
    public void Tick(SongTab? tab)
    {
        if (Instrument is not { } synth || Tab is not { } shown || Track is not { } track)
            return;
        if (!ReferenceEquals(tab, shown) || !shown.Document.Song.Tracks.Contains(track) || !ReferenceEquals(track.Instrument, synth))
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
        var all = SynthParams.All;
        var values = Values;
        var filterDirty = false;
        var modDirty = false;
        for (var i = 0; i < ParamCount; i++)
        {
            var value = SynthParams.Value(values, i);
            if (value == _shown[i])
                continue;
            _shown[i] = value;
            KnobMarkup.Show(document, "", i, all[i], value, _shownText);
            filterDirty |= IsFilterParam(i);
            modDirty = true;
        }

        if (!string.Equals(synth.Preset, _shownPreset, StringComparison.Ordinal))
        {
            _shownPreset = synth.Preset;
            ShowTitle(document, shown, track);
        }

        var matrix = MatrixSignature();
        if (matrix != _matrixSignature)
        {
            _matrixSignature = matrix;
            ShowRoutes(document);
        }

        var fx = FxSignature();
        if (fx != _fxSignature)
        {
            _fxSignature = fx;
            ShowFx(document);
        }

        TickTables(document, synth);
        if (filterDirty)
            _filter.Dirty = true;
        if (modDirty)
            _mod.Dirty = true;
        TickLive(document, shown, track);
        foreach (var v in (ReadOnlySpan<View>)[_waveA, _waveB, _filter, _mod])
            TickView(v);
        if (_page == "fx")
            _fxPanel.Tick(document);
    }

    private static bool IsFilterParam(int i)
    {
        var id = SynthParams.All[i].Id;
        return id.StartsWith("f1.", StringComparison.Ordinal) || id.StartsWith("f2.", StringComparison.Ordinal);
    }

    // ── Building ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void Build()
    {
        if (Instrument is null || Tab is not { } tab || Track is not { } track)
            return;
        var document = Document;
        _built = true;
        Array.Fill(_shown, float.NaN);
        Array.Clear(_shownText);
        Array.Fill(_shownLive, -1);
        _matrixSignature = null;
        _fxSignature = null;
        _waveA.Table = null;
        _waveB.Table = null;
        _shownCpu = (-1, -1);
        _fxPanel.Clear();
        ShowTitle(document, tab, track);
        _shownPreset = Instrument.Preset;

        var a = SynthParams.A;
        var b = SynthParams.B;
        Fill(document, "sw-on-a", [a.On]);
        Fill(document, "sw-on-b", [b.On]);
        Fill(document, "sw-knobs-a", OscKnobs(a));
        Fill(document, "sw-knobs-b", OscKnobs(b));
        var f1 = SynthParams.F1;
        var f2 = SynthParams.F2;
        Fill(document, "sw-knobs-f1", [f1.On, f1.Type, f1.Cutoff, f1.Reso, f1.Drive, f1.KeyTrack, f1.Mix]);
        Fill(document, "sw-knobs-f2", [f2.On, f2.Type, f2.Cutoff, f2.Reso, f2.Drive, f2.KeyTrack, f2.Mix]);
        Fill(document, "sw-routing", [SynthParams.FilterRouting]);
        var sub = SynthParams.Sub;
        Fill(document, "sw-knobs-sub", [sub.On, sub.Shape, sub.Octave, sub.Level, sub.Pan, sub.ToF1, sub.ToF2]);
        var noise = SynthParams.Noise;
        Fill(document, "sw-knobs-noise", [noise.On, noise.Type, noise.Color, noise.Rate, noise.Level, noise.Pan, noise.ToF1, noise.ToF2]);
        Fill(document, "sw-knobs-voice", [SynthParams.Polyphony, SynthParams.VoiceMode, SynthParams.Glide, SynthParams.GlideMode, SynthParams.BendRange]);
        Fill(document, "sw-knobs-master", [SynthParams.MasterVolume, SynthParams.MasterPan]);
        BuildModEditor(document);
        BuildStrip(document);
        BuildKeyboard(document);
        ShowPage(document);
        foreach (var v in (ReadOnlySpan<View>)[_waveA, _waveB, _filter, _mod])
            EnsureView(document, v);
        Place();
    }

    private static int[] OscKnobs(in OscParams p) =>
    [
        p.Level, p.Pan, p.Octave, p.Semi, p.Fine, p.Coarse, p.Position, p.FrameMode, p.WarpMode, p.Warp, p.Unison, p.Detune, p.Range, p.Blend,
        p.Width, p.Phase, p.Random, p.Retrigger, p.ToF1, p.ToF2, p.Hq,
    ];

    private static void Fill(RmlDocument document, string container, int[] parameters)
    {
        var sb = new StringBuilder();
        foreach (var i in parameters)
        {
            var p = SynthParams.All[i];
            var target = SynthParams.IsModTarget(p);
            KnobMarkup.Append(sb, p, i, "",
                target ? " · drop a source from the strip to modulate it; Alt-drag sets the route's amount, right-click lists its routes" : null,
                target ? "mod:" + i.ToString(CultureInfo.InvariantCulture) : null);
        }

        document.GetElementById(container).SetInnerRml(sb.ToString());
    }

    private void ShowTitle(RmlDocument document, SongTab tab, SongTrack track)
    {
        var preset = PresetName(Instrument?.Preset);
        document.GetElementById("sw-name").SetInnerRml(RmlText.Escape(preset is null
            ? $"{SynthParams.ProductName} — {track.Name} — {tab.Document.Name}"
            : $"{SynthParams.ProductName} · {preset} — {track.Name} — {tab.Document.Name}"));
    }

    private void Place()
    {
        if (!IsLoaded)
            return;
        var view = Workspace.Layout.ViewportImage;
        if (float.IsNaN(_position.X))
            _position = (view.X + Math.Max(0, (view.Width - WindowWidth) * 0.5f), view.Y + 24);
        _position = (Math.Clamp(_position.X, 0, Math.Max(0, Workspace.Layout.WindowWidth - 80)), Math.Clamp(_position.Y, 0, Math.Max(0, Workspace.Layout.WindowHeight - 40)));
        var window = Document.GetElementById("sw-window");
        window.SetProperty("left", RmlText.Dp(_position.X));
        window.SetProperty("top", RmlText.Dp(_position.Y));
    }

    private void ShowPage(RmlDocument document)
    {
        foreach (var page in (ReadOnlySpan<string>)["osc", "matrix", "fx"])
            document.GetElementById("sw-page-" + page).SetClass("hidden", page != _page);
        foreach (var tab in document.AsElement().QuerySelectorAll(".sw-tab"))
            tab.SetClass("active", tab.GetAttribute("data-tab") == _page);
    }

    /// <summary>Shows tab <paramref name="page"/> (<c>osc</c>, <c>matrix</c> or <c>fx</c>).</summary>
    public void ShowPage(string page)
    {
        _page = page is "matrix" or "fx" ? page : "osc";
        if (IsLoaded && _built)
            ShowPage(Document);
    }

    // ── Modulation editor and strip ──────────────────────────────────────────────────────────────────────────────

    private void BuildModEditor(RmlDocument document)
    {
        var mod = (SynthModCanvas)_mod.Canvas;
        if (_modSource < 8)
            mod.Source = _modSource;
        document.GetElementById("sw-mod-canvas").SetClass("hidden", _modSource >= 8);
        string title;
        int[] knobs;
        if (_modSource < 4)
        {
            var e = SynthParams.Env[_modSource];
            title = _modSource == 0 ? "Envelope 1 (amp)" : $"Envelope {_modSource + 1}";
            knobs = _modSource == 0
                ? [e.Delay, e.Attack, e.Hold, e.Decay, e.Sustain, e.Release, e.AttackCurve, e.DecayCurve, e.ReleaseCurve, SynthParams.AmpLevel]
                : [e.Delay, e.Attack, e.Hold, e.Decay, e.Sustain, e.Release, e.AttackCurve, e.DecayCurve, e.ReleaseCurve];
        }
        else if (_modSource < 8)
        {
            var l = SynthParams.Lfo[_modSource - 4];
            title = $"LFO {_modSource - 3}";
            knobs = [l.Shape, l.Rate, l.Sync, l.Division, l.Feel, l.Mode, l.Global, l.Delay, l.Rise, l.Phase, l.Smooth];
        }
        else
        {
            title = "Macros";
            knobs = [.. SynthParams.Macro];
        }

        document.GetElementById("sw-mod-title").SetInnerRml(RmlText.Escape(title));
        document.GetElementById("sw-mod-draw").SetInnerRml(_modSource is >= 4 and < 8
            ? "<div class=\"pw-toggle\" id=\"sw-draw\" data-tooltip=\"Draw — edit this LFO's shape point by point (starts from its current shape)\">Draw</div>"
            : "");
        Fill(document, "sw-mod-knobs", knobs);
        Array.Fill(_shown, float.NaN);
        Array.Fill(_shownLive, -1);
        _matrixSignature = null;
        _mod.Dirty = true;
    }

    /// <summary>Shows <paramref name="source"/>'s editor (0–3 envelopes, 4–7 LFOs, 8 the macros).</summary>
    public void SelectModSource(int source)
    {
        _modSource = Math.Clamp(source, 0, 8);
        if (!IsLoaded || !_built)
            return;
        BuildModEditor(Document);
        BuildStrip(Document);
    }

    private void BuildStrip(RmlDocument document)
    {
        var sb = new StringBuilder();
        for (var s = 0; s < SynthParams.Sources.Length; s++)
        {
            var n = s.ToString(CultureInfo.InvariantCulture);
            var editor = s < 8 ? s : s is >= SynthParams.SourceMacro1 and < SynthParams.SourceModWheel ? 8 : -1;
            var selected = editor >= 0 && editor == _modSource && (s < 8 || s == SynthParams.SourceMacro1);
            if (s is > SynthParams.SourceMacro1 and < SynthParams.SourceModWheel)
                selected = false;
            sb.Append("<div class=\"mod-tile").Append(selected ? " selected" : "").Append("\" data-src=\"").Append(n).Append("\" data-tooltip=\"")
                .Append(SourceNames[s]).Append(editor >= 0 ? " — click to edit it; " : " — ").Append("drag the handle onto a knob to modulate it\">")
                .Append("<span class=\"swatch\" style=\"background-color: ").Append(SourceColors[s]).Append(";\"></span><span>").Append(SourceNames[s])
                .Append("</span><span class=\"count\" id=\"sc-").Append(n).Append("\"></span><span class=\"icon icon-sm icon-arrows-move handle\"></span></div>");
        }

        document.GetElementById("sw-strip").SetInnerRml(sb.ToString());
        _matrixSignature = null;
    }

    // What the marks, counts and Matrix tab show, as a hash (no allocation on steady frames).
    private int MatrixSignature()
    {
        var hash = new HashCode();
        foreach (var r in Routes)
        {
            hash.Add(r.Source, StringComparer.Ordinal);
            hash.Add(r.Target, StringComparer.Ordinal);
            hash.Add(r.Amount);
            hash.Add(r.Bipolar);
        }

        hash.Add(Routes.Count);
        return hash.ToHashCode();
    }

    // Knob modulation marks (a tick in the source's colour at base + amount, a live dot), the strip's counts, the Matrix tab.
    private void ShowRoutes(RmlDocument document)
    {
        var routes = Routes;
        var byTarget = new Dictionary<int, List<SynthModRoute>>();
        foreach (var r in routes)
        {
            var t = SynthParams.IndexOf(r.Target);
            if (t >= 0)
                (byTarget.TryGetValue(t, out var list) ? list : byTarget[t] = []).Add(r);
        }

        for (var i = 0; i < ParamCount; i++)
        {
            if (document.GetElementById("km-" + i.ToString(CultureInfo.InvariantCulture)) is not { IsNull: false } marks)
                continue;
            var dial = marks.Parent;
            dial.SetClass("modulated", byTarget.ContainsKey(i));
            if (!byTarget.TryGetValue(i, out var list))
            {
                marks.SetInnerRml("");
                continue;
            }

            var p = SynthParams.All[i];
            var basePos = p.ToNormalized(SynthParams.Value(Values, i));
            var sb = new StringBuilder();
            foreach (var r in list)
            {
                var source = SynthParams.SourceIndex(r.Source);
                var end = Math.Clamp(basePos + r.Amount, 0, 1);
                var color = source >= 0 ? SourceColors[source] : "#94a3b8";
                sb.Append("<div class=\"mod-tick\" style=\"background-color: ").Append(color).Append("; transform: ")
                    .Append(Degrees[(int)MathF.Round(end * 270)]).Append(";\"></div>");
                if (r.Bipolar)
                {
                    var start = Math.Clamp(basePos - r.Amount, 0, 1);
                    sb.Append("<div class=\"mod-tick\" style=\"background-color: ").Append(color).Append("; transform: ")
                        .Append(Degrees[(int)MathF.Round(start * 270)]).Append(";\"></div>");
                }
            }

            sb.Append("<div class=\"live-dot hidden\" id=\"kl-").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\"></div>");
            marks.SetInnerRml(sb.ToString());
        }

        Array.Fill(_shownLive, -1);
        for (var s = 0; s < SynthParams.Sources.Length; s++)
        {
            var count = routes.Count(r => r.Source == SynthParams.Sources[s]);
            if (document.GetElementById("sc-" + s.ToString(CultureInfo.InvariantCulture)) is { IsNull: false } badge)
                badge.SetInnerRml(count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "");
        }

        ShowMatrix(document);
        _filter.Dirty = true;
    }

    // While voices sound: each modulated knob's live dot at the newest voice's value; the CPU meter.
    private void TickLive(RmlDocument document, SongTab tab, SongTrack track)
    {
        var synth = tab.Player.FindInstrument(track) as WavetableSynth;
        if (++_cpuFrames % 10 == 0)
        {
            // Silent: no voices and no load to show (and no text to build on idle frames).
            var voices = synth?.ActiveVoices ?? 0;
            var load = voices == 0 ? 0 : (int)MathF.Round((synth?.Load ?? 0) * 100);
            if ((voices, load) != _shownCpu && document.GetElementById("sw-cpu") is { IsNull: false } cpu)
            {
                _shownCpu = (voices, load);
                cpu.SetInnerRml(voices == 0 ? "0 voices"
                    : voices.ToString(CultureInfo.InvariantCulture) + (voices == 1 ? " voice · " : " voices · ") + load.ToString(CultureInfo.InvariantCulture) + " %");
                cpu.SetClass("hot", load >= 70);
            }
        }

        var sounding = synth is { ActiveVoices: > 0 };
        foreach (var r in Routes)
        {
            var t = SynthParams.IndexOf(r.Target);
            if (t < 0)
                continue;
            var angle = sounding ? (int)MathF.Round(SynthParams.All[t].ToNormalized(synth!.LiveValue(t)) * 270) : -2;
            if (angle == _shownLive[t])
                continue;
            _shownLive[t] = angle;
            if (document.GetElementById("kl-" + t.ToString(CultureInfo.InvariantCulture)) is not { IsNull: false } dot)
                continue;
            dot.SetClass("hidden", angle < 0);
            if (angle >= 0)
                dot.SetProperty("transform", Degrees[Math.Clamp(angle, 0, 270)]);
        }

        // The wavetable displays follow the newest voice's (modulated) position.
        if (synth is not null)
        {
            Position(_waveA, sounding ? synth.LiveValue(SynthParams.A.Position) : SynthParams.Value(Values, SynthParams.A.Position));
            Position(_waveB, sounding ? synth.LiveValue(SynthParams.B.Position) : SynthParams.Value(Values, SynthParams.B.Position));
        }
    }

    private static void Position(View view, float position)
    {
        var canvas = (WavetableCanvas)view.Canvas;
        if (canvas.Table is not { } table)
            return;
        var frame = (int)MathF.Round(position * (table.Frames - 1));
        var shown = (int)MathF.Round(canvas.FramePosition * (table.Frames - 1));
        if (frame == shown)
            return;
        canvas.FramePosition = position;
        view.Dirty = true;
    }

    // ── Wavetables and canvases ──────────────────────────────────────────────────────────────────────────────────

    // Each oscillator's table and label: looked up again only when its source or frame size changed, or a project file's table may have.
    private void TickTables(RmlDocument document, InstrumentDescriptor synth)
    {
        var version = WavetableCache.Version;
        foreach (var (oscillator, view) in (ReadOnlySpan<(string, View)>)[("a", _waveA), ("b", _waveB)])
        {
            var reference = Table(synth.Patch, oscillator);
            // A factory table never changes; a project file's is read again when the cache saw it change (a new version).
            if (view.Table is { } shown && string.Equals(shown.Source, reference.Source, StringComparison.Ordinal) && shown.FrameSize == reference.FrameSize &&
                view.HasTable && (shown.Version == version || reference.Source.StartsWith(FactoryWavetables.Prefix, StringComparison.Ordinal)))
                continue;
            var table = WavetableCache.Get(reference.Source, out _, reference.FrameSize);
            view.Table = (reference.Source, reference.FrameSize, version);
            view.HasTable = table is not null;
            var canvas = (WavetableCanvas)view.Canvas;
            if (table is not null && !ReferenceEquals(table, canvas.Table))
            {
                canvas.Table = table;
                view.Dirty = true;
            }

            var label = TableLabel(synth.Patch, oscillator);
            document.GetElementById(oscillator == "a" ? "sw-table-a" : "sw-table-b").SetInnerRml(RmlText.Escape(label));
        }
    }

    private static SynthTableRef Table(SynthPatch? patch, string oscillator) =>
        patch?.Tables is { } tables && tables.TryGetValue(oscillator, out var t) ? t : new SynthTableRef();

    private static string TableLabel(SynthPatch? patch, string oscillator)
    {
        var source = Table(patch, oscillator).Source;
        if (source.StartsWith(FactoryWavetables.Prefix, StringComparison.Ordinal))
            return FactoryWavetables.DisplayName(source);
        var path = AssetUid.IsUid(source) ? AssetDatabase.Current.GetPath(source) : source;
        return path is null ? source : Path.GetFileNameWithoutExtension(path);
    }

    private sealed class View(string texture, string element, EditorCanvas canvas)
    {
        public string Texture { get; } = texture;
        public string Element { get; } = element;
        public EditorCanvas Canvas { get; } = canvas;
        public SubViewport? Viewport { get; set; }
        public bool Registered { get; set; }
        public bool Dirty { get; set; } = true;
        public (int W, int H) Size { get; set; }

        // The table source shown (wavetable displays): its source, frame size and the cache version it was looked up at.
        public (string Source, int FrameSize, int Version)? Table { get; set; }
        public bool HasTable { get; set; }
    }

    private void EnsureView(RmlDocument document, View view)
    {
        if (view.Viewport is null)
        {
            view.Viewport = new SubViewport
            {
                Name = "Synth" + view.Texture,
                Disable3D = true,
                UpdateMode = SubViewportUpdateMode.Disabled,
                ClearColor = System.Drawing.Color.FromArgb(255, 21, 23, 28),
                Width = 16,
                Height = 16,
            };
            view.Viewport.AddChild(view.Canvas);
            Workspace.ScenesHost.AddChild(view.Viewport);
        }

        if (!view.Registered && Workspace.Tree?.Servers.Get<UiServer>() is { } ui)
        {
            ui.RegisterTexture(view.Texture, view.Viewport);
            view.Registered = true;
        }

        if (document.GetElementById(view.Element + (view == _mod ? "" : "")).QuerySelector("img") is { IsNull: false } image)
            image.SetAttribute("src", "engine://" + view.Texture);
        view.Dirty = true;
    }

    private void TickView(View view)
    {
        if (view.Viewport is not { } viewport || !IsLoaded)
            return;
        var element = Document.GetElementById(view.Element);
        if (element.IsNull)
            return;
        var b = element.Bounds;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var size = (Math.Max(1, (int)MathF.Round(b.Width)), Math.Max(1, (int)MathF.Round(b.Height)));
        if (b.Width < 2 || b.Height < 2)
            return; // hidden (another tab)
        if (size != view.Size)
        {
            view.Size = size;
            viewport.Width = size.Item1;
            viewport.Height = size.Item2;
            view.Dirty = true;
        }

        if (!view.Dirty)
        {
            if (viewport.UpdateMode != SubViewportUpdateMode.Once && viewport.UpdateMode != SubViewportUpdateMode.Disabled)
                viewport.UpdateMode = SubViewportUpdateMode.Disabled;
            return;
        }

        view.Dirty = false;
        view.Canvas.PixelScale = scale;
        view.Canvas.ViewSize = new Vector2(b.Width / scale, b.Height / scale);
        switch (view.Canvas)
        {
            case SynthFilterCanvas filter:
                filter.Values = Values;
                break;
            case SynthModCanvas mod:
                mod.Values = Values;
                mod.Drawn = _modSource is >= 4 and < 8 && Instrument?.Patch?.LfoShapes is { } shapes && _modSource - 4 < shapes.Count ? shapes[_modSource - 4] : null;
                mod.Color = new Vector4(Convert.ToInt32(SourceColors[Math.Min(_modSource, 7)][1..3], 16) / 255f,
                    Convert.ToInt32(SourceColors[Math.Min(_modSource, 7)][3..5], 16) / 255f, Convert.ToInt32(SourceColors[Math.Min(_modSource, 7)][5..7], 16) / 255f, 1);
                break;
        }

        view.Canvas.QueueRedraw();
        viewport.UpdateMode = SubViewportUpdateMode.Once;
    }

    // Mouse (window pixels) → a canvas's local dp.
    private Vector2 Local(View view, RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var b = Document.GetElementById(view.Element).Bounds;
        return new Vector2((float)((e.GetParameter("mouse_x", 0.0) - b.X) / scale), (float)((e.GetParameter("mouse_y", 0.0) - b.Y) / scale));
    }

    /// <summary>Marks the knobs <paramref name="source"/> (a strip tile, −1: none) modulates.</summary>
    internal void HighlightSource(int source)
    {
        if (source == _hoverSource || !IsLoaded)
            return;
        _hoverSource = source;
        foreach (var dial in Document.AsElement().QuerySelectorAll(".source-target"))
            dial.SetClass("source-target", false);
        if ((uint)source >= (uint)SynthParams.Sources.Length)
            return;
        var name = SynthParams.Sources[source];
        foreach (var r in Routes)
        {
            if (r.Source == name && SynthParams.IndexOf(r.Target) is >= 0 and var t &&
                Document.GetElementById("km-" + t.ToString(CultureInfo.InvariantCulture)) is { IsNull: false } marks)
                marks.Parent.SetClass("source-target", true);
        }
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildKeyboard(RmlDocument document)
    {
        // Two octaves from C of the current octave: white keys evenly, black keys between.
        var sb = new StringBuilder();
        const int Whites = 15;
        var white = 0;
        for (var k = 0; k <= 24; k++)
        {
            var pitch = 12 * (_octave + 1) + k;
            var note = k % 12;
            var black = note is 1 or 3 or 6 or 8 or 10;
            var left = black ? (white - 0.3f) / Whites * 100 : white / (float)Whites * 100;
            var width = black ? 0.6f / Whites * 100 : 1f / Whites * 100;
            sb.Append("<div class=\"key").Append(black ? " black" : "").Append("\" data-key=\"").Append(pitch.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"left: ").Append(left.ToString("0.###", CultureInfo.InvariantCulture)).Append("%; width: ")
                .Append(width.ToString("0.###", CultureInfo.InvariantCulture)).Append("%;\"></div>");
            if (!black)
                white++;
        }

        document.GetElementById("sw-keyboard").SetInnerRml(sb.ToString());
    }

    /// <summary>Plays <paramref name="pitch"/> on the synth's track until <see cref="ReleaseKey"/>.</summary>
    internal void PressKey(int pitch)
    {
        if (Tab is not { } tab || Track is not { } track)
            return;
        ReleaseKey();
        _heldKey = pitch;
        tab.Player.PreviewNote(track, pitch, 100);
    }

    internal void ReleaseKey()
    {
        if (_heldKey < 0 || Tab is not { } tab || Track is not { } track)
            return;
        tab.Player.ReleaseNote(track, _heldKey);
        _heldKey = -1;
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        _listeners.Clear();
        _built = false;
        foreach (var v in (ReadOnlySpan<View>)[_waveA, _waveB, _filter, _mod])
            v.Registered = false;
        _fxPanel.Attached();
        var body = document.AsElement();
        _listeners.Add(body.AddEventListener("dragstart", OnDragStart));
        _listeners.Add(body.AddEventListener("drag", OnDrag));
        _listeners.Add(body.AddEventListener("dragend", OnDragEnd));
        _listeners.Add(body.AddEventListener("dblclick", OnDoubleClick));
        _listeners.Add(body.AddEventListener("mousescroll", OnWheel));
        _listeners.Add(body.AddEventListener("mousedown", OnMouseDown));
        _listeners.Add(body.AddEventListener("mousemove", OnMouseMove));
        _listeners.Add(body.AddEventListener("mouseup", OnMouseUp));
        _listeners.Add(body.AddEventListener("change", OnChange));
        _listeners.Add(body.AddEventListener("mouseover", e => HighlightSource(IntAttribute(e.Target, "data-src"))));
        _listeners.Add(body.AddEventListener("keydown", e =>
        {
            if ((RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape)
                Close();
        }));
        if (IsOpen)
            Build();
    }

    private static string? IdUp(RmlElement element, params ReadOnlySpan<string> ids)
    {
        for (var el = element; !el.IsNull && el.TagName != "body"; el = el.Parent)
        {
            var id = el.Id;
            foreach (var candidate in ids)
            {
                if (id == candidate)
                    return id;
            }
        }

        return null;
    }

    private static int IntAttribute(RmlElement element, string attribute) =>
        FindAttribute(element, attribute) is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    protected override void OnClickElement(RmlEvent e)
    {
        if (Tab is not { } tab || Track is not { } track || Instrument is not { } synth)
            return;
        var target = e.Target;
        switch (IdUp(target, "sw-close", "sw-reset", "sw-presets", "sw-prev", "sw-next", "sw-table-a", "sw-table-b", "sw-oct-up", "sw-oct-down", "sw-draw",
                    "sw-wt-a", "sw-wt-b", "sw-add-route", "sw-add-fx", "sx-power", "sx-presets", "sx-reset", "sx-open"))
        {
            case "sx-power":
                if (_fxSelected is { } bypassed)
                    Edit(tab, "Could not bypass the effect", () => tab.Document.SetInsertBypass(bypassed, !bypassed.Bypass));
                return;
            case "sx-presets":
                {
                    var b = Document.GetElementById("sx-presets").Bounds;
                    var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
                    _fxPanel.PresetMenu(b.X / scale, (b.Y + b.Height) / scale + 2);
                    return;
                }

            case "sx-reset":
                _fxPanel.Reset();
                return;
            case "sx-open":
                if (_fxSelected is { } open)
                    Workspace.PluginWindow.Open(tab, track, open);
                return;
            case "sw-close":
                Close();
                return;
            case "sw-reset":
                Edit(tab, "Could not reset the synth", () => tab.Document.SetSynthPatch(track, SynthPatch.Init(), "Init Patch"));
                return;
            case "sw-presets":
                PresetMenu(tab, track, synth);
                return;
            case "sw-prev" or "sw-next" when IdUp(target, "sw-next") is { } next:
                StepPreset(tab, 1);
                return;
            case "sw-prev":
                StepPreset(tab, -1);
                return;
            case "sw-table-a" or "sw-table-b":
                TableMenu(tab, track, synth, IdUp(target, "sw-table-a") is null ? "b" : "a");
                return;
            case "sw-oct-up" or "sw-oct-down":
                _octave = Math.Clamp(_octave + (IdUp(target, "sw-oct-up") is null ? -1 : 1), 0, 8);
                BuildKeyboard(Document);
                return;
            case "sw-draw":
                StartDrawing(tab, track);
                return;
            case "sw-wt-a" or "sw-wt-b":
                {
                    var view = IdUp(target, "sw-wt-a") is null ? _waveB : _waveA;
                    var canvas = (WavetableCanvas)view.Canvas;
                    canvas.Flat = !canvas.Flat;
                    view.Dirty = true;
                    return;
                }

            case "sw-add-route":
                Edit(tab, "Could not add the route", () => tab.Document.AddModRoute(track, new SynthModRoute { Source = "lfo1", Target = "a.wt_pos", Amount = 0.25f }));
                return;
            case "sw-add-fx":
                AddFxMenu(tab, track, target);
                return;
        }

        if (FindAttribute(target, "data-tab") is { } page)
        {
            ShowPage(page);
            return;
        }

        if (FindAttribute(target, "data-src") is not null && _draggingSource < 0)
        {
            var source = IntAttribute(target, "data-src");
            if (source < 8)
                SelectModSource(source);
            else if (source is >= SynthParams.SourceMacro1 and < SynthParams.SourceModWheel)
                SelectModSource(8);
            return;
        }

        if (FindAttribute(target, "data-route-action") is { } action)
        {
            RouteAction(tab, track, IntAttribute(target, "data-route"), action, target);
            return;
        }

        if (FindAttribute(target, "data-fx-action") is { } fxAction)
        {
            FxAction(tab, track, IntAttribute(target, "data-fx"), fxAction);
            return;
        }

        if (_page == "fx" && _fxPanel.Click(e))
            return;

        var index = IntAttribute(target, "data-p");
        if ((uint)index >= (uint)ParamCount || tab.Document.IsReadOnly)
            return;
        var p = SynthParams.All[index];
        if (p.Curve == ParamCurve.Toggle)
            Edit(tab, "Could not change the synth", () => tab.Document.SetSynthParam(track, p.Id, SynthParams.Value(Values, index) >= 0.5f ? 0 : 1));
        else if (p.Curve == ParamCurve.Choice)
            ChoiceMenu(tab, track, p, index, e);
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (!IsOpen)
            return;
        Document.PullToFront();
        var button = e.GetParameter("button", 0);
        var target = e.Target;
        if (_page == "fx")
            _fxPanel.MouseDown(Document, e);
        if (button == 0 && IntAttribute(target, "data-key") is >= 0 and var pitch)
        {
            PressKey(pitch);
            if (FindWithAttribute(target, "data-key") is { IsNull: false } key)
                key.SetClass("down", true);
            return;
        }

        if (button == 0 && FindAttribute(target, "data-src") is not null)
        {
            _pressedSource = IntAttribute(target, "data-src");
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            _pressedAt = ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
            return;
        }

        if (button == 1 && Tab is { } tab && Track is { } track && IntAttribute(target, "data-p") is >= 0 and var index && SynthParams.IsModTarget(SynthParams.All[index]))
            RoutesMenu(tab, track, index, e);
    }

    private void OnMouseMove(RmlEvent e)
    {
        if (_pressedSource < 0 || _draggingSource >= 0)
            return;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var dx = (float)(e.GetParameter("mouse_x", 0.0) / scale) - _pressedAt.X;
        var dy = (float)(e.GetParameter("mouse_y", 0.0) / scale) - _pressedAt.Y;
        if (dx * dx + dy * dy < DragThreshold * DragThreshold)
            return;
        BeginSourceDrag(_pressedSource);
    }

    /// <summary>Starts dragging modulation source <paramref name="source"/> (a strip tile) toward a knob.</summary>
    internal void BeginSourceDrag(int source)
    {
        _draggingSource = source;
        if (!IsLoaded)
            return;
        foreach (var dial in Document.AsElement().QuerySelectorAll("[data-drop]"))
            dial.SetClass("drop-target", true);
    }

    private void OnMouseUp(RmlEvent e)
    {
        ReleaseKey();
        foreach (var key in Document.AsElement().QuerySelectorAll(".key.down"))
            key.SetClass("down", false);
        _pressedSource = -1;
        if (_draggingSource < 0)
            return;
        var source = _draggingSource;
        EndSourceDrag();
        if (FindAttribute(e.Target, "data-drop") is { } drop && drop.StartsWith("mod:", StringComparison.Ordinal) &&
            int.TryParse(drop.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            DropSource(source, index);
    }

    private void EndSourceDrag()
    {
        _draggingSource = -1;
        if (!IsLoaded)
            return;
        foreach (var dial in Document.AsElement().QuerySelectorAll(".drop-target"))
            dial.SetClass("drop-target", false);
    }

    /// <summary>
    /// A source dropped on knob <paramref name="index"/>: a new route (+25 %, unipolar), or the existing one from that source
    /// selected for Alt-drags.
    /// </summary>
    internal void DropSource(int source, int index)
    {
        if (Tab is not { } tab || Track is not { } track || (uint)source >= (uint)SynthParams.Sources.Length || !SynthParams.IsModTarget(SynthParams.All[index]))
            return;
        var id = SynthParams.All[index].Id;
        var name = SynthParams.Sources[source];
        var existing = Routes.FindIndex(r => r.Source == name && r.Target == id);
        if (existing >= 0)
        {
            _selectedRoute[index] = existing;
            return;
        }

        if (Routes.Count >= SynthPatch.MaxRoutes)
        {
            Log.Info($"[Music] The synth plays at most {SynthPatch.MaxRoutes} routes.");
            return;
        }

        Edit(tab, "Could not add the route", () => tab.Document.AddModRoute(track, new SynthModRoute { Source = name, Target = id, Amount = 0.25f }));
        _selectedRoute[index] = Routes.Count - 1;
    }

    private void OnDragStart(RmlEvent e)
    {
        if (Instrument is null)
            return;
        EndEdits();
        _dragCounter++;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var y = (float)(e.GetParameter("mouse_y", 0.0) / scale);
        if (_page == "fx")
        {
            _dragStartY = y;
            if (_fxPanel.DragStart(Document, e))
                return;
            if (IntAttribute(e.Target, "data-fx-drag") is >= 0 and var row)
            {
                _fxDrag = row;
                if (FindWithAttribute(e.Target, "data-fx-row") is { IsNull: false } dragged)
                    dragged.SetClass("dragging", true);
                return;
            }
        }

        if (FindWithAttribute(e.Target, "data-p") is { IsNull: false } dial && dial.IsClassSet("knob-dial") &&
            int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            _dragStartY = y;
            dial.SetClass("dragging", true);
            if (e.GetParameter("alt_key", 0) == 0 || !BeginRouteDrag(index))
                BeginKnob(index);

            return;
        }

        if (IdUp(e.Target, "sw-mod-canvas") is not null)
        {
            BeginModHandle(Local(_mod, e), e.GetParameter("alt_key", 0) != 0);
            _dragStartY = y;
            return;
        }

        if (IdUp(e.Target, "sw-title") is not null && IdUp(e.Target, "sw-prev", "sw-next", "sw-presets", "sw-reset", "sw-close") is null)
        {
            _moving = true;
            _moveOffset = ((float)(e.GetParameter("mouse_x", 0.0) / scale) - _position.X, y - _position.Y);
        }
    }

    private int SelectedRoute(int index)
    {
        var id = SynthParams.All[index].Id;
        if (_selectedRoute.TryGetValue(index, out var r) && r < Routes.Count && Routes[r].Target == id)
            return r;
        return Routes.FindIndex(x => x.Target == id);
    }

    /// <summary>Starts an Alt-drag on knob <paramref name="index"/>: its selected route's amount (false: the knob has no route).</summary>
    internal bool BeginRouteDrag(int index)
    {
        if (SelectedRoute(index) is not (>= 0 and var route))
            return false;
        EndEdits();
        _dragCounter++;
        _dragParam = -1;
        _dragRoute = route;
        _dragStart = Routes[route].Amount;
        return true;
    }

    /// <summary>Starts a knob drag on parameter <paramref name="index"/>.</summary>
    internal void BeginKnob(int index)
    {
        EndEdits();
        _dragCounter++;
        _dragParam = index;
        _dragRoute = -1;
        _dragStart = SynthParams.All[index].ToNormalized(SynthParams.Value(Values, index));
    }

    /// <summary>The dragged knob <paramref name="upDp"/> above where it started (Shift: a tenth as far).</summary>
    internal void DragKnob(float upDp, bool fine)
    {
        if (Tab is not { } tab || Track is not { } track)
            return;
        var delta = upDp / KnobDragDp * (fine ? 0.1f : 1f);
        if (_dragRoute >= 0 && _dragRoute < Routes.Count)
        {
            var old = Routes[_dragRoute];
            var amount = Math.Clamp(_dragStart + delta, -1, 1);
            Edit(tab, "Could not change the route", () => tab.Document.SetModRoute(track, _dragRoute,
                new SynthModRoute { Source = old.Source, Target = old.Target, Amount = amount, Bipolar = old.Bipolar, Aux = old.Aux, Extra = old.Extra },
                MergeKey("route")));
            return;
        }

        if (_dragParam < 0)
            return;
        var p = SynthParams.All[_dragParam];
        var value = p.FromNormalized(_dragStart + delta);
        Edit(tab, "Could not change the synth", () => tab.Document.SetSynthParam(track, p.Id, value, MergeKey(p.Id)));
    }

    internal void EndDrag()
    {
        _dragParam = -1;
        _dragRoute = -1;
        _modHandle = -1;
        _bending = false;
        EndEdits();
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
        else if (_fxDrag >= 0)
        {
            ShowFxDrop(FxDropIndex(y));
        }
        else if (_fxPanel.Dragging)
        {
            _fxPanel.Drag(Document, e, _dragStartY - y);
        }
        else if (_dragParam >= 0 || _dragRoute >= 0)
        {
            DragKnob(_dragStartY - y, e.GetParameter("shift_key", 0) != 0);
        }
        else if (_modHandle >= 0)
        {
            DragModHandle(Local(_mod, e), _dragStartY - y);
        }
    }

    private void OnDragEnd(RmlEvent e)
    {
        if (_moving)
            Workspace.SaveLayout();
        _moving = false;
        if (_fxDrag >= 0)
        {
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            var slot = FxDropIndex((float)(e.GetParameter("mouse_y", 0.0) / scale));
            var from = _fxDrag;
            _fxDrag = -1;
            _fxSignature = null; // the rows are built again (no drag marks)
            MoveFx(from, slot > from ? slot - 1 : slot);
            return;
        }

        if (_fxPanel.Dragging)
            _fxPanel.DragEnd(Document);
        foreach (var dial in Document.AsElement().QuerySelectorAll(".knob-dial.dragging"))
            dial.SetClass("dragging", false);
        EndDrag();
    }

    private void OnDoubleClick(RmlEvent e)
    {
        if (Tab is not { } tab || Track is not { } track || tab.Document.IsReadOnly)
            return;
        if (_page == "fx" && _fxPanel.DoubleClick(Document, e))
            return;
        if (FindWithAttribute(e.Target, "data-p") is { IsNull: false } dial && dial.IsClassSet("knob-dial") &&
            int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            var p = SynthParams.All[index];
            Edit(tab, "Could not reset the parameter", () => tab.Document.SetSynthParam(track, p.Id, p.Default));
            return;
        }

        if (IdUp(e.Target, "sw-mod-canvas") is not null && _modSource is >= 4 and < 8)
            RemoveDrawnPoint(tab, track, Local(_mod, e));
    }

    private void OnWheel(RmlEvent e)
    {
        if (Tab is not { } tab || Track is not { } track || tab.Document.IsReadOnly)
            return;
        if (_page == "fx" && _fxPanel.Wheel(e))
            return;
        var delta = (float)e.GetParameter("wheel_delta_y", 0.0);
        if (delta == 0 || FindWithAttribute(e.Target, "data-p") is not { IsNull: false } dial || !dial.IsClassSet("knob-dial") ||
            !int.TryParse(dial.GetAttribute("data-p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return;
        e.StopPropagation();
        var p = SynthParams.All[index];
        var step = e.GetParameter("shift_key", 0) != 0 ? 0.005f : 0.02f;
        var value = p.FromNormalized(p.ToNormalized(SynthParams.Value(Values, index)) - MathF.Sign(delta) * step);
        Edit(tab, "Could not change the synth", () => tab.Document.SetSynthParam(track, p.Id, value, "sw-wheel-" + p.Id));
    }

    private void OnChange(RmlEvent e)
    {
        if (Tab is not { } tab || Track is not { } track || tab.Document.IsReadOnly)
            return;
        var route = IntAttribute(e.Target, "data-route-amount");
        if (route < 0 || route >= Routes.Count || !float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return;
        var old = Routes[route];
        var amount = Math.Clamp(percent / 100, -1, 1);
        if (MathF.Abs(amount - old.Amount) < 1e-4f)
            return;
        Edit(tab, "Could not change the route", () => tab.Document.SetModRoute(track, route,
            new SynthModRoute { Source = old.Source, Target = old.Target, Amount = amount, Bipolar = old.Bipolar, Aux = old.Aux, Extra = old.Extra },
            "sw-route-" + route.ToString(CultureInfo.InvariantCulture)));
    }

    private string MergeKey(string id) => "sw-" + _dragCounter.ToString(CultureInfo.InvariantCulture) + "-" + id;

    private void EndEdits() => Tab?.Document.History.EndMerge();

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

    // ── Modulation editor gestures ───────────────────────────────────────────────────────────────────────────────

    internal SynthModCanvas ModCanvas => (SynthModCanvas)_mod.Canvas;

    /// <summary>Starts dragging the envelope handle or drawn LFO point at <paramref name="point"/> (the editor's dp); in a drawn LFO an empty spot adds a point.</summary>
    internal void BeginModHandle(Vector2 point, bool bend)
    {
        EndEdits();
        _dragCounter++;
        _modHandle = -1;
        _bending = bend;
        if (Tab is not { } tab || Track is not { } track || tab.Document.IsReadOnly)
            return;
        var canvas = ModCanvas;
        if (canvas.IsEnvelope)
        {
            var (attack, decay, release, _, _, _) = canvas.EnvelopeHandles();
            var best = 14f;
            foreach (var (p, i) in (ReadOnlySpan<(Vector2, int)>)[(attack, 0), (decay, 1), (release, 2)])
            {
                var d = Vector2.Distance(p, point);
                if (d < best)
                    (best, _modHandle) = (d, i);
            }
        }
        else if (DrawnShape() is { } shape)
        {
            var best = 12f;
            for (var i = 0; i < shape.Points.Count; i++)
            {
                var d = Vector2.Distance(canvas.PointAt(i), point);
                if (d < best)
                    (best, _modHandle) = (d, i);
            }

            if (_modHandle < 0 && !bend)
            {
                // A new point where the click is.
                var next = shape.Clone();
                next.Points.Add([Math.Clamp(point.X / canvas.ViewSize.X, 0, 1), Math.Clamp(1 - (point.Y - 6) / (canvas.ViewSize.Y - 12), 0, 1), 0]);
                next.Points.Sort(static (a, b) => a[0].CompareTo(b[0]));
                Edit(tab, "Could not change the LFO", () => tab.Document.SetLfoShape(track, _modSource - 4, next));
                _modHandle = next.Points.FindIndex(p => Math.Abs(p[0] - point.X / canvas.ViewSize.X) < 1e-4f);
            }
        }

        canvas.SelectedPoint = _modHandle;
        _mod.Dirty = true;
    }

    /// <summary>Moves the dragged handle to <paramref name="point"/> (Alt: bends a drawn segment by <paramref name="upDp"/>).</summary>
    internal void DragModHandle(Vector2 point, float upDp)
    {
        if (_modHandle < 0 || Tab is not { } tab || Track is not { } track)
            return;
        var canvas = ModCanvas;
        if (canvas.IsEnvelope)
        {
            var e = SynthParams.Env[_modSource];
            var (_, _, _, attackStart, decayStart, releaseStart) = canvas.EnvelopeHandles();
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            switch (_modHandle)
            {
                case 0:
                    values[SynthParams.All[e.Attack].Id] = canvas.SegmentSeconds(point.X - attackStart);
                    break;
                case 1:
                    values[SynthParams.All[e.Decay].Id] = canvas.SegmentSeconds(point.X - decayStart);
                    values[SynthParams.All[e.Sustain].Id] = Math.Clamp(1 - (point.Y - 8) / (canvas.ViewSize.Y - 16), 0, 1);
                    break;
                default:
                    values[SynthParams.All[e.Release].Id] = canvas.SegmentSeconds(point.X - releaseStart);
                    break;
            }

            Edit(tab, "Could not change the envelope", () => tab.Document.SetSynthParams(track, values, "Envelope " + (_modSource + 1), MergeKey("env")));
            return;
        }

        if (DrawnShape() is not { } shape || _modHandle >= shape.Points.Count)
            return;
        var next = shape.Clone();
        var p = next.Points[_modHandle];
        if (_bending)
        {
            next.Points[_modHandle] = [p[0], p[1], Math.Clamp(upDp / 60, -1, 1)];
        }
        else
        {
            var x = Math.Clamp(point.X / canvas.ViewSize.X, 0, 1);
            if (_modHandle == 0)
                x = 0; // the cycle starts at its first point
            next.Points[_modHandle] = [x, Math.Clamp(1 - (point.Y - 6) / (canvas.ViewSize.Y - 12), 0, 1), p.Length > 2 ? p[2] : 0];
        }

        Edit(tab, "Could not change the LFO", () => tab.Document.SetLfoShape(track, _modSource - 4, next, MergeKey("lfo")));
    }

    private SynthLfoShape? DrawnShape()
    {
        if (_modSource is < 4 or >= 8 || SynthParams.Value(Values, SynthParams.Lfo[_modSource - 4].Shape) != (float)SynthLfoWave.Drawn)
            return null;
        return Instrument?.Patch?.LfoShapes is { } shapes && _modSource - 4 < shapes.Count ? shapes[_modSource - 4] : null;
    }

    private void RemoveDrawnPoint(SongTab tab, SongTrack track, Vector2 point)
    {
        if (DrawnShape() is not { Points.Count: > 2 } shape)
            return;
        var canvas = ModCanvas;
        var hit = -1;
        for (var i = 1; i < shape.Points.Count; i++)
        {
            if (Vector2.Distance(canvas.PointAt(i), point) < 12)
                hit = i;
        }

        if (hit < 0)
            return;
        var next = shape.Clone();
        next.Points.RemoveAt(hit);
        Edit(tab, "Could not change the LFO", () => tab.Document.SetLfoShape(track, _modSource - 4, next));
    }

    // Draw: the LFO takes a drawn shape, starting from its current built-in shape's outline.
    internal void StartDrawing(SongTab tab, SongTrack track)
    {
        if (_modSource is < 4 or >= 8 || tab.Document.IsReadOnly)
            return;
        var n = _modSource - 4;
        var shape = SynthParams.Lfo[n].Shape;
        var current = (int)SynthParams.Value(Values, shape);
        if (current == (int)SynthLfoWave.Drawn)
            return;
        var points = new List<float[]>();
        for (var i = 0; i <= 8; i++)
            points.Add([i / 8f, SynthModCanvas.ShapeValue(current, Math.Min(i / 8f, 0.999f)), 0]);
        Edit(tab, "Could not draw the LFO", () =>
        {
            tab.Document.SetLfoShape(track, n, new SynthLfoShape { Points = points });
            tab.Document.SetSynthParam(track, SynthParams.All[shape].Id, (float)SynthLfoWave.Drawn);
        });
    }

    // ── Menus ────────────────────────────────────────────────────────────────────────────────────────────────────

    private (float X, float Y) MenuPoint(RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        return ((float)(e.GetParameter("mouse_x", 0.0) / scale), (float)(e.GetParameter("mouse_y", 0.0) / scale));
    }

    private void ChoiceMenu(SongTab tab, SongTrack track, BuiltinParam p, int index, RmlEvent e)
    {
        if (p.Choices is not { } choices || tab.Document.IsReadOnly)
            return;
        var current = (int)SynthParams.Value(Values, index);
        var items = choices.Select((c, i) => new MenuItem(c, i.ToString(CultureInfo.InvariantCulture), Css: i == current ? "current" : null)).ToList();
        var (x, y) = MenuPoint(e);
        var shown = Instrument;
        Workspace.Popup.Show(items, x, y, command =>
        {
            if (int.TryParse(command, NumberStyles.Integer, CultureInfo.InvariantCulture, out var choice) && ReferenceEquals(Instrument, shown))
                Edit(tab, "Could not change the synth", () => tab.Document.SetSynthParam(track, p.Id, choice));
        });
    }

    // A modulated knob's right-click: its routes (invert, bipolar, select for Alt-drag, remove).
    private void RoutesMenu(SongTab tab, SongTrack track, int index, RmlEvent e)
    {
        var id = SynthParams.All[index].Id;
        var items = new List<MenuItem>();
        for (var r = 0; r < Routes.Count; r++)
        {
            var route = Routes[r];
            if (route.Target != id)
                continue;
            var source = SynthParams.SourceIndex(route.Source);
            var n = r.ToString(CultureInfo.InvariantCulture);
            items.Add(MenuItem.Header($"{(source >= 0 ? SourceNames[source] : route.Source)} → {SynthParams.All[index].Name} {route.Amount * 100:+0;-0} %"));
            items.Add(new MenuItem("Adjust with Alt-drag", "select:" + n, Css: SelectedRoute(index) == r ? "current" : null, Icon: "pointer"));
            items.Add(new MenuItem("Invert", "invert:" + n, Enabled: !tab.Document.IsReadOnly, Icon: "arrows-up-down"));
            items.Add(new MenuItem(route.Bipolar ? "Unipolar" : "Bipolar", "bipolar:" + n, Enabled: !tab.Document.IsReadOnly, Icon: "arrows-right-left"));
            items.Add(new MenuItem("Remove", "remove:" + n, Enabled: !tab.Document.IsReadOnly, Icon: "trash"));
        }

        if (items.Count == 0)
            items.Add(new MenuItem("No modulation — drag a source from the strip onto this knob", null, Enabled: false));
        var (x, y) = MenuPoint(e);
        Workspace.Popup.Show(items, x, y, command =>
        {
            if (command is null)
                return;
            var colon = command.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && int.TryParse(command.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                RouteAction(tab, track, r, command[..colon], default, index);
        });
    }

    internal void RouteAction(SongTab tab, SongTrack track, int r, string action, RmlElement anchor = default, int knob = -1)
    {
        if ((uint)r >= (uint)Routes.Count)
            return;
        var old = Routes[r];
        SynthModRoute Copy(float amount, bool bipolar) => new() { Source = old.Source, Target = old.Target, Amount = amount, Bipolar = bipolar, Aux = old.Aux, Extra = old.Extra };
        switch (action)
        {
            case "select" when knob >= 0:
                _selectedRoute[knob] = r;
                break;
            case "invert":
                Edit(tab, "Could not change the route", () => tab.Document.SetModRoute(track, r, Copy(-old.Amount, old.Bipolar)));
                break;
            case "bipolar":
                Edit(tab, "Could not change the route", () => tab.Document.SetModRoute(track, r, Copy(old.Amount, !old.Bipolar)));
                break;
            case "remove":
                Edit(tab, "Could not remove the route", () => tab.Document.RemoveModRoute(track, r));
                break;
            case "source" when !anchor.IsNull:
                RouteEndMenu(tab, track, r, anchor, source: true);
                break;
            case "target" when !anchor.IsNull:
                RouteEndMenu(tab, track, r, anchor, source: false);
                break;
        }
    }

    // The Matrix tab's source or target menu for route r.
    private void RouteEndMenu(SongTab tab, SongTrack track, int r, RmlElement anchor, bool source)
    {
        var old = Routes[r];
        var items = new List<MenuItem>();
        if (source)
        {
            for (var s = 0; s < SynthParams.Sources.Length; s++)
                items.Add(new MenuItem(SourceNames[s], SynthParams.Sources[s], Css: SynthParams.Sources[s] == old.Source ? "current" : null));
        }
        else
        {
            foreach (var group in SynthParams.All.Where(SynthParams.IsModTarget).GroupBy(static p => p.Group ?? ""))
            {
                items.Add(MenuItem.Header(group.Key));
                foreach (var p in group)
                    items.Add(new MenuItem(p.Name, p.Id, Css: p.Id == old.Target ? "current" : null));
            }
        }

        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var b = anchor.Bounds;
        Workspace.Popup.Show(items, b.X / scale, (b.Y + b.Height) / scale + 2, command =>
        {
            if (command is null || (uint)r >= (uint)Routes.Count)
                return;
            var next = new SynthModRoute
            {
                Source = source ? command : old.Source,
                Target = source ? old.Target : command,
                Amount = old.Amount,
                Bipolar = old.Bipolar,
                Aux = old.Aux,
                Extra = old.Extra,
            };
            Edit(tab, "Could not change the route", () => tab.Document.SetModRoute(track, r, next));
        });
    }

    private void ShowMatrix(RmlDocument document)
    {
        var routes = Routes;
        var sb = new StringBuilder();
        sb.Append("<div class=\"sw-table-row head\"><span class=\"c-src\">Source</span><span class=\"c-amount\"><span>Amount</span></span><span class=\"c-bip\">Polarity</span><span class=\"c-target\">Target</span></div>");
        if (routes.Count == 0)
            sb.Append("<div class=\"sw-empty\">No modulation yet: drag a source from the strip below onto a knob, or add a route here.</div>");
        for (var r = 0; r < routes.Count; r++)
        {
            var route = routes[r];
            var n = r.ToString(CultureInfo.InvariantCulture);
            var source = SynthParams.SourceIndex(route.Source);
            var target = SynthParams.Find(route.Target);
            var percent = (route.Amount * 100).ToString("0", CultureInfo.InvariantCulture);
            sb.Append("<div class=\"sw-table-row\"><span class=\"c-src\"><button class=\"text-button\" data-route=\"").Append(n)
                .Append("\" data-route-action=\"source\">").Append(RmlText.Escape(source >= 0 ? SourceNames[source] : route.Source + " (unknown)")).Append("</button></span>")
                .Append("<span class=\"c-amount\"><input type=\"range\" class=\"range\" min=\"-100\" max=\"100\" step=\"1\" value=\"").Append(percent)
                .Append("\" data-route-amount=\"").Append(n).Append("\"/><span class=\"mono\">").Append(percent).Append(" %</span></span>")
                .Append("<span class=\"c-bip\"><div class=\"pw-toggle").Append(route.Bipolar ? " active" : "").Append("\" data-route=\"").Append(n)
                .Append("\" data-route-action=\"bipolar\">Bipolar</div></span>")
                .Append("<span class=\"c-target\"><button class=\"text-button\" data-route=\"").Append(n).Append("\" data-route-action=\"target\">")
                .Append(RmlText.Escape(target is null ? route.Target + " (unknown)" : $"{target.Group} · {target.Name}")).Append("</button></span>")
                .Append("<button class=\"tool-button small\" data-route=\"").Append(n).Append("\" data-route-action=\"remove\" data-tooltip=\"Remove the route\"><span class=\"icon icon-sm icon-trash\"></span></button></div>");
        }

        sb.Append("<div class=\"sw-table-row\"><button id=\"sw-add-route\" class=\"text-button\" data-tooltip=\"A new route (LFO 1 → Osc A position), then choose its source and target\">+ Add Route</button></div>");
        document.GetElementById("sw-matrix").SetInnerRml(sb.ToString());
        document.GetElementById("sw-tab-matrix").SetInnerRml(routes.Count == 0 ? "Matrix" : "Matrix " + routes.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ── FX tab ───────────────────────────────────────────────────────────────────────────────────────────────────

    private List<PluginInsert> Fx => Instrument?.Patch?.Fx ?? NoFx;

    private int FxSignature()
    {
        var hash = new HashCode();
        foreach (var insert in Fx)
        {
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(insert));
            hash.Add(insert.Bypass);
        }

        hash.Add(Fx.Count);
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_fxSelected));
        return hash.ToHashCode();
    }

    // The chain's rows (grip, bypass, name, open, remove) and the selected effect's panel.
    private void ShowFx(RmlDocument document)
    {
        var fx = Fx;
        if (_fxSelected is null || !fx.Contains(_fxSelected))
            _fxSelected = fx.Count > 0 ? fx[0] : null;
        var sb = new StringBuilder();
        for (var i = 0; i < fx.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            var insert = fx[i];
            sb.Append("<div class=\"fx-row").Append(insert.Bypass ? " bypassed" : "").Append(ReferenceEquals(insert, _fxSelected) ? " selected" : "")
                .Append("\" data-fx-row=\"").Append(n).Append("\">")
                .Append("<span class=\"icon icon-sm icon-arrows-up-down grip\" data-fx-drag=\"").Append(n).Append("\" data-tooltip=\"Drag to reorder\"></span>")
                .Append("<button class=\"tool-button small pw-power").Append(insert.Bypass ? "" : " active").Append("\" data-fx=\"").Append(n)
                .Append("\" data-fx-action=\"bypass\" data-tooltip=\"Bypass\"><span class=\"icon icon-sm icon-plug-connected\"></span></button>")
                .Append("<span class=\"name\" data-fx=\"").Append(n).Append("\" data-fx-action=\"select\" data-tooltip=\"Edit it here\">")
                .Append(RmlText.Escape(insert.DisplayName)).Append("</span>")
                .Append("<button class=\"tool-button small\" data-fx=\"").Append(n)
                .Append("\" data-fx-action=\"remove\" data-tooltip=\"Remove\"><span class=\"icon icon-sm icon-trash\"></span></button></div>");
        }

        if (fx.Count == 0)
            sb.Append("<div class=\"sw-empty\">No effects: the synth's own chain runs after its voices, before its volume.</div>");
        sb.Append("<button id=\"sw-add-fx\" class=\"text-button\" data-tooltip=\"Add a built-in effect to the synth's chain\">+ Add Effect</button>");
        document.GetElementById("sw-fx").SetInnerRml(sb.ToString());
        document.GetElementById("sw-tab-fx").SetInnerRml(fx.Count == 0 ? "FX" : "FX " + fx.Count.ToString(CultureInfo.InvariantCulture));
        ShowFxPanel(document);
    }

    private void ShowFxPanel(RmlDocument document)
    {
        var insert = _fxSelected;
        document.GetElementById("sw-fx-panel").SetClass("hidden", insert is null);
        document.GetElementById("sx-none").SetClass("hidden", insert is not null);
        if (insert is null || Tab is not { } tab || Track is not { } track || BuiltinEffects.Find(insert.Builtin ?? "") is not { } info)
        {
            if (_fxPanel.Insert is not null)
                _fxPanel.Clear(document);
            return;
        }

        document.GetElementById("sw-fx-panel").SetClass("bypassed", insert.Bypass);
        document.GetElementById("sx-power").SetClass("active", !insert.Bypass);
        if (ReferenceEquals(_fxPanel.Insert, insert))
            return;
        document.GetElementById("sx-name").SetInnerRml(RmlText.Escape(info.Name));
        document.GetElementById("sx-icon").SetClassNames("icon icon-sm icon-" + info.Icon);
        document.GetElementById("sx-presets").SetClass("hidden", info.Presets is not { Length: > 0 });
        _fxPanel.Show(document, tab, track, insert, info);
    }

    /// <summary>The effect of the synth's chain shown in the FX tab's panel (null: none).</summary>
    public PluginInsert? SelectedFx => _fxSelected;

    /// <summary>The FX tab's effect panel (tests drive its knobs and EQ handles).</summary>
    internal EffectPanel FxPanel => _fxPanel;

    /// <summary>Moves the chain's effect <paramref name="from"/> to <paramref name="to"/> (one undo step).</summary>
    internal void MoveFx(int from, int to)
    {
        if (Tab is not { } tab || Track is not { } track || (uint)from >= (uint)Fx.Count || from == to)
            return;
        var insert = Fx[from];
        Edit(tab, "Could not move the effect", () => tab.Document.MoveSynthFx(track, insert, to));
    }

    // While a row's grip is dragged: where it would land (before the row under the mouse, or after the last).
    private int FxDropIndex(float mouseY)
    {
        var rows = Document.AsElement().QuerySelectorAll(".fx-row");
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var index = 0;
        foreach (var row in rows)
        {
            var b = row.Bounds;
            if (mouseY * scale > b.Y + b.Height * 0.5f)
                index++;
        }

        return index;
    }

    private void ShowFxDrop(int slot)
    {
        var rows = Document.AsElement().QuerySelectorAll(".fx-row");
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].SetClass("drop-above", i == slot && slot != _fxDrag && slot != _fxDrag + 1);
            rows[i].SetClass("drop-below", i == rows.Length - 1 && slot == rows.Length && _fxDrag != rows.Length - 1);
        }
    }

    internal void FxAction(SongTab tab, SongTrack track, int i, string action)
    {
        if ((uint)i >= (uint)Fx.Count)
            return;
        var insert = Fx[i];
        switch (action)
        {
            case "bypass":
                Edit(tab, "Could not bypass the effect", () => tab.Document.SetInsertBypass(insert, !insert.Bypass));
                break;
            case "select":
                _fxSelected = insert;
                _fxSignature = null; // the rows' selection and the panel show it next frame
                break;
            case "open":
                Workspace.PluginWindow.Open(tab, track, insert);
                break;
            case "up":
                Edit(tab, "Could not move the effect", () => tab.Document.MoveSynthFx(track, insert, i - 1));
                break;
            case "down":
                Edit(tab, "Could not move the effect", () => tab.Document.MoveSynthFx(track, insert, i + 1));
                break;
            case "remove":
                Edit(tab, "Could not remove the effect", () => tab.Document.RemoveSynthFx(track, insert));
                break;
        }
    }

    private void AddFxMenu(SongTab tab, SongTrack track, RmlElement anchor)
    {
        var items = new List<MenuItem>();
        foreach (var category in Enum.GetValues<BuiltinCategory>())
        {
            items.Add(MenuItem.Header(BuiltinEffects.CategoryName(category)));
            foreach (var info in BuiltinEffects.InCategory(category))
                items.Add(new MenuItem(info.Name, info.Id, Enabled: !tab.Document.IsReadOnly, Icon: info.Icon));
        }

        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var b = anchor.Bounds;
        Workspace.Popup.Show(items, b.X / scale, (b.Y + b.Height) / scale + 2, command =>
        {
            if (command is not null && ReferenceEquals(track.Instrument, Instrument))
                Edit(tab, "Could not add the effect", () => _fxSelected = tab.Document.AddSynthFx(track, command));
        });
    }

    // ── Presets and wavetables ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A preset id's name for the title ("Warm Pad"), or null.</summary>
    internal static string? PresetName(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (id.StartsWith(SynthPresets.FactoryPrefix, StringComparison.Ordinal))
        {
            var slash = id.LastIndexOf('/');
            return slash < 0 ? id : id[(slash + 1)..];
        }

        var path = AssetUid.IsUid(id) ? AssetDatabase.Current.GetPath(id) : id;
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    private void PresetMenu(SongTab tab, SongTrack track, InstrumentDescriptor synth)
    {
        var presets = SynthPresets.All(Workspace.Session.ProjectRoot);
        var editable = !tab.Document.IsReadOnly;
        var items = new List<MenuItem> { new("Init", "init", Enabled: editable, Icon: "refresh") };
        foreach (var group in presets.GroupBy(static p => (p.Path is null ? "" : "Project · ") + p.Category))
        {
            items.Add(MenuItem.Header(group.Key));
            foreach (var p in group)
                items.Add(new MenuItem(p.Name, "preset:" + p.Id, Enabled: editable, Css: p.Id == synth.Preset ? "current" : null, Icon: "wave-square"));
        }

        items.Add(MenuItem.Separator);
        items.Add(new MenuItem("Save Preset…", "save", Enabled: Workspace.Session.ProjectRoot is not null, Icon: "device-floppy"));
        Workspace.Popup.Show(items, _position.X + WindowWidth - 220, _position.Y + 30, command =>
        {
            if (command is null || !ReferenceEquals(Instrument, synth))
                return;
            if (command == "init")
                Edit(tab, "Could not reset the synth", () => tab.Document.SetSynthPatch(track, SynthPatch.Init(), "Init Patch"));
            else if (command == "save")
                SavePreset(tab, track, synth);
            else if (command.StartsWith("preset:", StringComparison.Ordinal) && presets.Find(p => p.Id == command[7..]) is { } entry)
                LoadPreset(tab, track, entry);
        });
    }

    /// <summary>Loads <paramref name="entry"/> on <paramref name="track"/>'s synth: one undo step ("Preset: Warm Pad").</summary>
    internal static void LoadPreset(SongTab tab, SongTrack track, SynthPresetEntry entry)
    {
        if (SynthPresets.Patch(entry) is not { } patch)
        {
            Log.Warning($"[Music] The preset {entry.Name} could not be read.");
            return;
        }

        Edit(tab, "Could not load the preset", () => tab.Document.SetSynthPatch(track, patch, "Preset: " + entry.Name, entry.Id));
    }

    /// <summary>Next / previous preset in the current one's category (wrapping); with none, the first factory preset.</summary>
    internal void StepPreset(SongTab tab, int direction)
    {
        if (Track is not { } track || Instrument is not { } synth || tab.Document.IsReadOnly)
            return;
        var presets = SynthPresets.All(Workspace.Session.ProjectRoot);
        if (presets.Count == 0)
            return;
        var current = presets.FindIndex(p => p.Id == synth.Preset);
        if (current < 0)
        {
            LoadPreset(tab, track, presets[0]);
            return;
        }

        var category = presets.Where(p => p.Category == presets[current].Category && (p.Path is null) == (presets[current].Path is null)).ToList();
        var at = category.FindIndex(p => p.Id == synth.Preset);
        LoadPreset(tab, track, category[(at + direction + category.Count) % category.Count]);
    }

    private void SavePreset(SongTab tab, SongTrack track, InstrumentDescriptor synth)
    {
        if (Workspace.Session.ProjectRoot is not { } root)
            return;
        Workspace.Message.Show(new MessageRequest
        {
            Title = "Save Preset",
            Message = $"A name for the preset (saved in Content/{SynthPresets.Folder}/; \"Category / Name\" files it under a category):",
            Input = PresetName(synth.Preset) ?? track.Name,
            Buttons = ["Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "device-floppy",
            Callback = (button, text) =>
            {
                if (button != 0 || string.IsNullOrWhiteSpace(text) || synth.Patch is null)
                    return;
                var slash = text.IndexOf('/', StringComparison.Ordinal);
                var category = slash > 0 ? text[..slash].Trim() : "Project";
                var name = (slash > 0 ? text[(slash + 1)..] : text).Trim();
                var file = string.Concat(name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var path = Path.Combine(root, AssetDatabase.ContentFolder, SynthPresets.Folder, file + ".mres");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var uid = SynthPresets.Save(synth.Patch, path, name, category);
                    Log.Info($"[Music] Saved the preset {name} to {AssetDatabase.Current.ToProjectPath(path)}.");
                    if (ReferenceEquals(track.Instrument, synth) && !tab.Document.IsReadOnly)
                        tab.Document.SetSynthPatch(track, synth.Patch.Clone(), "Save Preset", uid);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Workspace.Commands.ReportError("Could not save the preset", e);
                }
            },
        });
    }

    private void TableMenu(SongTab tab, SongTrack track, InstrumentDescriptor synth, string oscillator)
    {
        var current = Table(synth.Patch, oscillator);
        var editable = !tab.Document.IsReadOnly;
        var items = new List<MenuItem>();
        foreach (var group in FactoryWavetables.Sources.GroupBy(static s => s[FactoryWavetables.Prefix.Length..].Split('/')[0]))
        {
            items.Add(MenuItem.Header(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(group.Key)));
            foreach (var source in group)
                items.Add(new MenuItem(FactoryWavetables.DisplayName(source), "table:" + source, Enabled: editable, Css: source == current.Source ? "current" : null, Icon: "wave-sine"));
        }

        var project = ProjectTables();
        if (project.Count > 0)
        {
            items.Add(MenuItem.Header("Project"));
            foreach (var (source, name) in project)
                items.Add(new MenuItem(name, "table:" + source, Enabled: editable, Css: source == current.Source ? "current" : null, Icon: "file-music"));
        }

        items.Add(MenuItem.Separator);
        items.Add(new MenuItem("Import Wavetable…", "import", Enabled: editable && Workspace.Session.ProjectRoot is not null, Icon: "file-plus"));
        if (!current.Source.StartsWith(FactoryWavetables.Prefix, StringComparison.Ordinal))
        {
            items.Add(MenuItem.Header("Frame size"));
            foreach (var size in WavetableWav.FrameSizes)
            {
                var label = size switch { 0 => "Automatic", -1 => "Single cycle", _ => size.ToString(CultureInfo.InvariantCulture) };
                items.Add(new MenuItem(label, "frames:" + size.ToString(CultureInfo.InvariantCulture), Enabled: editable, Css: size == current.FrameSize ? "current" : null));
            }
        }

        var anchor = Document.GetElementById(oscillator == "a" ? "sw-table-a" : "sw-table-b").Bounds;
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        Workspace.Popup.Show(items, anchor.X / scale, (anchor.Y + anchor.Height) / scale + 2, command =>
        {
            if (command is null || !ReferenceEquals(Instrument, synth))
                return;
            if (command == "import")
            {
                if (Workspace.Session.ProjectRoot is { } root)
                    Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Open, root, ["*.wav"]), "Import Wavetable", "Import", path => ImportTable(tab, track, oscillator, path));
                return;
            }

            var colon = command.IndexOf(':', StringComparison.Ordinal);
            var argument = command[(colon + 1)..];
            if (command.StartsWith("table:", StringComparison.Ordinal))
                Edit(tab, "Could not set the wavetable", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = argument }));
            else if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                Edit(tab, "Could not set the frame size", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = current.Source, FrameSize = size }));
        });
    }

    private List<(string Source, string Name)> ProjectTables()
    {
        var result = new List<(string, string)>();
        if (Workspace.Session.ProjectRoot is not { } root)
            return result;
        var folder = Path.Combine(root, AssetDatabase.ContentFolder, "Wavetables");
        if (!Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = AssetDatabase.Current.ToProjectPath(file);
            result.Add((AssetDatabase.Current.GetUid(path) ?? path, Path.GetFileNameWithoutExtension(file)));
        }

        return result;
    }

    /// <summary>Imports <paramref name="file"/> (copied into the project when outside it) as <paramref name="oscillator"/>'s table.</summary>
    internal void ImportTable(SongTab tab, SongTrack track, string oscillator, string file)
    {
        try
        {
            var uid = WavetableWav.Import(file, AssetDatabase.Current);
            Edit(tab, "Could not set the wavetable", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = uid }));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Workspace.Message.Show(new MessageRequest { Title = "Import Wavetable", Message = $"{Path.GetFileName(file)} is not a wavetable: {e.Message}", Buttons = ["OK"] });
        }
    }
}
