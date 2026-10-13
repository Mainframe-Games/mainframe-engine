using System.Globalization;
using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Analysis;

namespace MainframeEngine.Editor;

/// <summary>
/// Editor › Editor Settings (E5): theme accent (presets or any <c>#rrggbb</c>, applied live), autosave interval, the
/// external code editor command (VS Code, Rider… opens <c>.cs</c> files and click-to-source lines), automatic code
/// reload, the 3D view's resolution, VST3 folders and MIDI inputs (a toggle per device with its connected state; the refresh button lists them),
/// and the Video section (ffmpeg, speech model, filler words, the silence and gameplay numbers, the undo limit; Edit › Video
/// Settings… and the notice line's link open the dialog scrolled to it). The dialog keeps its size and its body scrolls.
/// Apply saves to <c>~/.mainframe/editor_settings.json</c>.
/// </summary>
public sealed class EditorSettingsDialog : EditorDocument
{
    /// <summary>Accent presets (name, colour).</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> AccentPresets =
    [
        ("Blue", EditorSettings.DefaultAccent),
        ("Violet", "#8b5cf6"),
        ("Teal", "#14b8a6"),
        ("Green", "#22c55e"),
        ("Orange", "#f97316"),
        ("Rose", "#f43f5e"),
    ];

    private sealed class MidiRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required bool Enabled { get; init; }
        public required bool Online { get; init; }
        public required string State { get; init; }
        public required string Tooltip { get; init; }
    }

    private static readonly RmlStructType<MidiRow> MidiRowType = new RmlStructType<MidiRow>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name)
        .Member("enabled", static r => r.Enabled)
        .Member("online", static r => r.Online)
        .Member("state", static r => r.State)
        .Member("tooltip", static r => r.Tooltip);

    /// <summary>The id of the Video section, which <see cref="Open"/> can scroll to.</summary>
    public const string VideoSectionId = "video-section";

    private readonly List<MidiRow> _midiRows = [];
    private const int ScrollAfterFrames = 2;
    private string? _scrollTarget;
    private int _scrollFrames;
    private int _midiVersion = -1;
    private RmlDataModel? _model;
    private EditorSettings _working = new();
    private string _error = "";

    public EditorSettingsDialog(EditorWorkspace workspace)
        : base(workspace, "editor_settings.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The values being edited (applied by <see cref="Apply"/>).</summary>
    public EditorSettings Working => _working;

    private System.Threading.Tasks.Task<Music.PluginCatalog>? _scan;

    private string PluginStatus()
    {
        if (_scan is { IsCompleted: false })
            return "Scanning VST3 plugins…";
        var catalog = Music.PluginCatalog.Current;
        var failed = catalog.Failures.Count > 0 ? $"; {catalog.Failures.Count} bundle(s) failed (see the Output panel)" : "";
        return $"{catalog.Instruments.Count()} instrument(s), {catalog.Effects.Count()} effect(s){failed}. Default folders: " +
               string.Join(", ", Music.PluginScanner.DefaultFolders());
    }

    // Rescans every bundle (ignores the cache) with the folders being edited.
    private void RescanPlugins()
    {
        _scan = Music.PluginScanner.RescanInBackground(_working.PluginFolders, force: true);
        if (_scan is null)
            Log.Warning("[Music] Plugin scan not started: the plugin host is not available, or a scan is running.");
        _model?.Dirty("plugin_status");
    }

    // MIDI rows: every device the helper reports plus enabled ones it does not (offline), each with a toggle.
    private void RebuildMidi()
    {
        var midi = Workspace.Midi;
        _midiVersion = midi.Version;
        _midiRows.Clear();
        var names = midi.Devices.Select(d => (d.Name, d.Online)).ToList();
        foreach (var name in _working.MidiInputs)
            if (!names.Exists(n => n.Name == name))
                names.Add((name, false));
        for (var i = 0; i < names.Count; i++)
        {
            var (name, online) = names[i];
            _midiRows.Add(new MidiRow
            {
                Index = i,
                Name = name,
                Enabled = _working.MidiInputs.Contains(name),
                Online = online,
                State = online ? "connected" : "offline",
                Tooltip = $"{name} — {(online ? "connected" : "not connected; opened again when it comes back")}; tick to play and record with it in songs",
            });
        }

        _model?.Dirty("midi_devices");
        _model?.Dirty("midi_status");
    }

    private string MidiStatus() => Workspace.Midi.Error ?? (_midiRows.Count == 0
        ? "No MIDI inputs found. Plug in a keyboard and refresh."
        : "Ticked inputs play the armed or selected song track; unplugged ones reopen when they return.");

    /// <summary>Ticks or unticks a MIDI input (applied with Apply).</summary>
    public void ToggleMidi(string name)
    {
        if (!_working.MidiInputs.Remove(name))
            _working.MidiInputs.Add(name);
        RebuildMidi();
    }

    /// <summary>
    /// Called every frame by the workspace (the dialog is not a <c>[Tool]</c> node, so <c>OnProcess</c> never runs in the
    /// editor): redraws the MIDI list when the devices changed while the dialog is open, the plugin status when a scan ends,
    /// and scrolls the body to what <see cref="Open"/> asked for once the shown document has laid out (scrolling before that
    /// is lost, so it waits a couple of frames).
    /// </summary>
    public void Tick()
    {
        ScrollIfPending();
        if (Visible && Workspace.Midi.Version != _midiVersion)
            RebuildMidi();
        if (_scan is { IsCompleted: true })
        {
            _scan = null;
            _model?.Dirty("plugin_status");
        }
    }

    private void ScrollIfPending()
    {
        if (_scrollTarget is not { } target || !Visible || !IsLoaded || ++_scrollFrames < ScrollAfterFrames)
            return;
        var element = Document.GetElementById(target);
        if (element.IsNull || element.Bounds.Height <= 0)
            return;
        _scrollTarget = null;
        element.ScrollIntoView(alignWithTop: true);
    }

    protected override void OnReady()
    {
        _model = CreateDataModel("editor_settings")
            .Bind("accent", this, static d => d._working.Accent, static (d, v) => d._working.Accent = v ?? EditorSettings.DefaultAccent)
            .Bind("autosave", this, static d => d._working.AutosaveMinutes.ToString(CultureInfo.InvariantCulture),
                static (d, v) => d._working.AutosaveMinutes = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0)
            .Bind("view_resolution", this, static d => d._working.ViewResolution.ToString(),
                static (d, v) => d._working.ViewResolution = Enum.TryParse<ViewResolution>(v, out var r) && Enum.IsDefined(r) ? r : ViewResolution.Auto)
            .Bind("command", this, static d => d._working.CodeEditorCommand, static (d, v) => d._working.CodeEditorCommand = v ?? "")
            .Bind("cutback", this, static d => d._working.CutbackPath, static (d, v) => d._working.CutbackPath = v ?? "")
            .Bind("auto_reload", this, static d => d._working.AutoReloadCode, static (d, v) => d._working.AutoReloadCode = v)
            .Bind("check_updates", this, static d => d._working.CheckForUpdates, static (d, v) => d._working.CheckForUpdates = v)
            .Bind("plugin_folders", this, static d => string.Join(";", d._working.PluginFolders),
                static (d, v) => d._working.PluginFolders = [.. (v ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)])
            .Bind("plugin_status", this, static d => d.PluginStatus())
            .Event("rescan_plugins", _ => RescanPlugins())
            .BindList("midi_devices", _midiRows, MidiRowType)
            .Bind("midi_status", this, static d => d.MidiStatus())
            .Event("midi_toggle", e =>
            {
                var index = e.GetArgument(0).GetInt32();
                if ((uint)index < (uint)_midiRows.Count)
                    ToggleMidi(_midiRows[index].Name);
            })
            .Event("midi_refresh", _ => Workspace.Midi.Refresh())
            .Bind("video_ffmpeg", this, static d => d._working.VideoFfmpegPath ?? "", static (d, v) => d._working.VideoFfmpegPath = v)
            .Bind("video_model", this, static d => d._working.VideoWhisperModel, static (d, v) => d._working.VideoWhisperModel = v ?? "")
            .Bind("video_fillers", this, static d => string.Join(", ", d._working.VideoFillerWords), static (d, v) => d.SetFillerWords(v))
            .Bind("video_threshold", this, static d => d._working.VideoSilenceThresholdDb.ToString(CultureInfo.InvariantCulture),
                static (d, v) => d.SetThreshold(v))
            .Bind("error", this, static d => d._error)
            .Event("apply", _ => Apply())
            .Event("cancel", _ => Cancel());
        BindVideoNumber("video_min_silence", static w => w.VideoMinSilenceMs, static (w, n) => w.VideoMinSilenceMs = n);
        BindVideoNumber("video_padding", static w => w.VideoPaddingMs, static (w, n) => w.VideoPaddingMs = n);
        BindVideoNumber("video_min_keep", static w => w.VideoMinKeepMs, static (w, n) => w.VideoMinKeepMs = n);
        BindVideoNumber("video_idle_min", static w => w.VideoGameplayMinIdleMs, static (w, n) => w.VideoGameplayMinIdleMs = n);
        BindVideoNumber("video_idle_padding", static w => w.VideoGameplayPaddingMs, static (w, n) => w.VideoGameplayPaddingMs = n);
        BindVideoNumber("video_idle_keep", static w => w.VideoGameplayMinKeepMs, static (w, n) => w.VideoGameplayMinKeepMs = n);
        BindVideoNumber("video_marker", static w => w.VideoGameplayMarkerWindowMs, static (w, n) => w.VideoGameplayMarkerWindowMs = n);
        BindVideoNumber("video_undo", static w => w.VideoUndoHistoryLimit, static (w, n) => w.VideoUndoHistoryLimit = n);
    }

    // A number field of the Video section: text in, the setting's own clamp applied, and the field shows the value that
    // stuck (typing 99999 into a 0…500 field reads 500 afterwards). Text that is not a number changes nothing.
    private void BindVideoNumber(string name, Func<EditorSettings, int> get, Action<EditorSettings, int> set) =>
        _model!.Bind(name, () => get(_working).ToString(CultureInfo.InvariantCulture), text =>
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                set(_working, number);
            _model?.Dirty(name);
        });

    private void SetThreshold(string? text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            _working.VideoSilenceThresholdDb = db;
        _model?.Dirty("video_threshold");
    }

    private void SetFillerWords(string? text)
    {
        _working.VideoFillerWords = [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        _model?.Dirty("video_fillers");
    }

    /// <summary>The Whisper model choices as the select shows them: the key and the download size, e.g. <c>base.en (148 MB)</c>.</summary>
    public static string ModelLabel(WhisperModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return string.Create(CultureInfo.InvariantCulture, $"{model.Key} ({Math.Round(model.ApproximateBytes / 1_000_000.0)} MB)");
    }

    protected override void OnAttach(RmlDocument document)
    {
        // Choices are generated once from the presets (they never change at run time).
        var swatches = new System.Text.StringBuilder();
        foreach (var (name, color) in AccentPresets)
            swatches.Append("<div class=\"swatch-choice\" data-accent=\"").Append(color).Append("\" style=\"background-color: ")
                .Append(color).Append(";\" data-tooltip=\"").Append(name).Append(' ').Append(color).Append("\"></div>");
        document.GetElementById("es-swatches").SetInnerRml(swatches.ToString());
        var presets = new System.Text.StringBuilder();
        for (var i = 0; i < EditorSettings.CodeEditorPresets.Count; i++)
            presets.Append("<button class=\"small\" data-preset=\"").Append(i).Append("\">").Append(RmlText.Escape(EditorSettings.CodeEditorPresets[i].Name)).Append("</button>");
        document.GetElementById("es-presets").SetInnerRml(presets.ToString());
        var models = new System.Text.StringBuilder();
        foreach (var model in WhisperModelInfo.All)
            models.Append("<option value=\"").Append(model.Key).Append("\">").Append(RmlText.Escape(ModelLabel(model))).Append("</option>");
        document.GetElementById("es-video-model").SetInnerRml(models.ToString());
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-accent") is { } accent)
            SetAccent(accent);
        else if (FindAttribute(e.Target, "data-preset") is { } preset && int.TryParse(preset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            SetCodeEditorPreset(index);
    }

    /// <summary>Closes without applying (the Cancel button).</summary>
    public void Cancel() => HideAndReleaseFocus();

    /// <summary>Opens the dialog on a copy of the current settings, scrolled to the Video section when asked.</summary>
    /// <param name="scrollToVideo">Edit › Video Settings… and the "ffmpeg not found" link want the Video section in view.</param>
    public void Open(bool scrollToVideo = false)
    {
        _scrollTarget = scrollToVideo ? VideoSectionId : "es-top"; // the body keeps its scroll position between visits
        _scrollFrames = 0;
        _working = Workspace.Settings.Clone();
        _error = "";
        Visible = true;
        if (_working.MidiInputs.Count > 0 || Workspace.Midi.Devices.Count > 0)
            Workspace.Midi.Refresh(); // lists the devices (does not start the helper for people without MIDI)
        RebuildMidi();
        _model?.DirtyAll();
    }

    public void SetAccent(string color)
    {
        _working.Accent = color;
        _model?.Dirty("accent");
    }

    public void SetCodeEditorPreset(int index)
    {
        if ((uint)index >= (uint)EditorSettings.CodeEditorPresets.Count)
            return;
        _working.CodeEditorCommand = EditorSettings.CodeEditorPresets[index].Command;
        _model?.Dirty("command");
    }

    /// <summary>Applies the edited settings to the editor and saves them.</summary>
    public void Apply()
    {
        Workspace.ApplySettings(_working.Clone());
        HideAndReleaseFocus();
    }
}
