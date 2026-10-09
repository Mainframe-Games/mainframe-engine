using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Editor › Editor Settings (E5): theme accent (presets or any <c>#rrggbb</c>, applied live), autosave interval, the
/// external code editor command (VS Code, Rider… opens <c>.cs</c> files and click-to-source lines), automatic code
/// reload, the 3D view's resolution, VST3 folders and MIDI inputs (a toggle per device with its connected state; the refresh button lists them). Apply saves to <c>~/.mainframe/editor_settings.json</c>.
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

    private readonly List<MidiRow> _midiRows = [];
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

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        if (Visible && Workspace.Midi.Version != _midiVersion)
            RebuildMidi();
        if (_scan is { IsCompleted: true })
        {
            _scan = null;
            _model?.Dirty("plugin_status");
        }
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
            .Bind("error", this, static d => d._error)
            .Event("apply", _ => Apply())
            .Event("cancel", _ => Cancel());
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
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-accent") is { } accent)
            SetAccent(accent);
        else if (FindAttribute(e.Target, "data-preset") is { } preset && int.TryParse(preset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            SetCodeEditorPreset(index);
    }

    /// <summary>Opens the dialog on a copy of the current settings.</summary>
    /// <summary>Closes without applying (the Cancel button).</summary>
    public void Cancel() => HideAndReleaseFocus();

    public void Open()
    {
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
