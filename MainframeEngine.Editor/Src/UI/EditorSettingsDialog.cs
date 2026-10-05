using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Editor › Editor Settings (E5): theme accent (presets or any <c>#rrggbb</c>, applied live), autosave interval, the
/// external code editor command (VS Code, Rider… opens <c>.cs</c> files and click-to-source lines) and automatic code
/// reload. Apply saves to <c>~/.mainframe/editor_settings.json</c>.
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

    protected override void OnReady()
    {
        _model = CreateDataModel("editor_settings")
            .Bind("accent", this, static d => d._working.Accent, static (d, v) => d._working.Accent = v ?? EditorSettings.DefaultAccent)
            .Bind("autosave", this, static d => d._working.AutosaveMinutes.ToString(CultureInfo.InvariantCulture),
                static (d, v) => d._working.AutosaveMinutes = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0)
            .Bind("command", this, static d => d._working.CodeEditorCommand, static (d, v) => d._working.CodeEditorCommand = v ?? "")
            .Bind("auto_reload", this, static d => d._working.AutoReloadCode, static (d, v) => d._working.AutoReloadCode = v)
            .Bind("check_updates", this, static d => d._working.CheckForUpdates, static (d, v) => d._working.CheckForUpdates = v)
            .Bind("error", this, static d => d._error)
            .Event("apply", _ => Apply())
            .Event("cancel", _ => HideAndReleaseFocus());
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
    public void Open()
    {
        _working = Workspace.Settings.Clone();
        _error = "";
        Visible = true;
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
