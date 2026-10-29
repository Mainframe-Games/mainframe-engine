using System.Globalization;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// File › Export… (G5, docs/design/game-export.md#editor-file--export): the project's export presets on the left (platform
/// glyph, target, a validation badge), the selected preset's options on the right (general, build, Steam, icon, macOS,
/// content globs as chips), its validation with fix buttons, and the running export's progress, log and results. Edits
/// have their own undo/redo and are saved with <c>project.mfproj</c> (Export saves first). The dialog may be closed while
/// an export runs; the toolbar and the Output panel keep reporting it.
/// </summary>
public sealed class ExportDialog : EditorDocument
{
    private RmlEventListener? _change;
    private RmlEventListener? _blur;
    private RmlEventListener? _keys;
    private bool _suppress;
    private int _shownLines = -1;
    private string _progressKey = "";
    private readonly Dictionary<int, IReadOnlyList<ExportIssue>> _issues = [];

    public ExportDialog(EditorWorkspace workspace)
        : base(workspace, "export.rml")
    {
        Visible = false;
        Modal = true;
        workspace.Export.Changed += RenderProgress;
    }

    /// <summary>The presets being edited (null while closed).</summary>
    public ExportPresetsModel? Model { get; private set; }

    /// <summary>The selected preset's index (-1: none).</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>The last refused edit's message (empty when none).</summary>
    public string Error { get; private set; } = "";

    /// <summary>The validation of the preset at <paramref name="index"/> as last rendered.</summary>
    public IReadOnlyList<ExportIssue> IssuesOf(int index) => _issues.TryGetValue(index, out var issues) ? issues : [];

    /// <summary>Opens the dialog on the session's project.</summary>
    public bool Open()
    {
        if (Workspace.Session.Project is not { } settings || Workspace.Session.ProjectRoot is not { } root)
        {
            Workspace.Message.Show(new MessageRequest { Title = "Export", Message = "Open a project first.", Buttons = ["OK"] });
            return false;
        }

        Model = new ExportPresetsModel(settings, settings.FilePath ?? Path.Combine(root, ProjectSettings.FileName));
        Model.Settings.Changed += Render;
        Model.History.Changed += Render;
        Selected = Model.Presets.Count > 0 ? 0 : -1;
        Error = "";
        Visible = true;
        Render();
        return true;
    }

    protected override void OnAttach(RmlDocument document)
    {
        var root = document.AsElement();
        _change?.Remove();
        _blur?.Remove();
        _keys?.Remove();
        _change = root.AddEventListener("change", OnChange);
        _blur = root.AddEventListener("blur", OnBlur, inCapturePhase: true);
        _keys = root.AddEventListener("keydown", OnKeyDown);
        _shownLines = -1;
        _progressKey = "";
        Render();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The platform glyph's icon name.</summary>
    public static string PlatformIcon(ExportPlatform platform) => platform switch
    {
        ExportPlatform.Windows => "brand-windows",
        ExportPlatform.MacOS => "brand-apple",
        _ => "brand-ubuntu",
    };

    private void Render()
    {
        if (!IsLoaded || Model is not { } model || Workspace.Session.ProjectRoot is not { } root)
            return;
        var suppressed = _suppress;
        _suppress = true;
        try
        {
            if (Selected >= model.Presets.Count)
                Selected = model.Presets.Count - 1;
            _issues.Clear();
            for (var i = 0; i < model.Presets.Count; i++)
                _issues[i] = Workspace.Export.Validate(root, model.Settings.Current, model.Presets[i], AssetDatabase.Current);

            var list = new StringBuilder();
            for (var i = 0; i < model.Presets.Count; i++)
            {
                var preset = model.Presets[i];
                var issues = _issues[i];
                var (badge, css, tip) = issues.Any(x => x.IsError) ? ("circle-x", "badge-error", "Errors: this preset cannot export")
                    : issues.Count > 0 ? ("alert-triangle", "badge-warn", "Warnings: it exports, but check them")
                    : ("circle-check", "badge-ok", "Ready to export");
                list.Append("<div class=\"ex-preset").Append(i == Selected ? " selected" : "").Append("\" data-preset=\"").Append(i)
                    .Append("\"><span class=\"icon icon-sm icon-").Append(PlatformIcon(preset.Platform)).Append("\"></span><span class=\"ex-name\">")
                    .Append(RmlText.Escape(preset.Name)).Append("</span><span class=\"ex-rid\">").Append(RmlText.Escape(ExportTargets.EffectiveRid(preset)))
                    .Append("</span><span class=\"icon icon-sm icon-").Append(badge).Append(' ').Append(css).Append("\" data-tooltip=\"").Append(tip).Append("\"></span></div>");
            }

            if (model.Presets.Count == 0)
                list.Append("<div class=\"empty\">No presets yet. Add one for this machine with the + button.</div>");
            Document.GetElementById("ex-presets").SetInnerRml(list.ToString());

            var body = new StringBuilder(4096);
            if (Selected >= 0)
                RenderPreset(body, model.Presets[Selected], model.Settings.Current, root);
            else
                body.Append("<div class=\"empty\">Export presets describe player builds: platform, target, output, demo, Steam and content. ")
                    .Append("Add one, then Export. mf-export --preset runs the same presets from scripts and CI.</div>");
            Document.GetElementById("ex-body").SetInnerRml(body.ToString());
            Document.GetElementById("ex-issues").SetInnerRml(IssuesRml(Selected >= 0 ? _issues[Selected] : []));

            SetText("ex-error", Error);
            Document.GetElementById("ex-undo").SetClass("disabled", !model.History.CanUndo);
            Document.GetElementById("ex-redo").SetClass("disabled", !model.History.CanRedo);
            Document.GetElementById("ex-duplicate").SetClass("disabled", Selected < 0);
            Document.GetElementById("ex-delete").SetClass("disabled", Selected < 0);
            SetText("ex-state", model.IsDirty ? "Unsaved changes" : "Saved");
            _progressKey = "";
            RenderProgress();
        }
        finally
        {
            _suppress = suppressed;
        }
    }

    private static void RenderPreset(StringBuilder rml, ExportPreset preset, ProjectSettings project, string root)
    {
        string? group = null;
        foreach (var option in ExportPresetsModel.Options)
        {
            if (!option.AppliesTo(preset.Platform))
                continue;
            if (option.Group != group)
            {
                group = option.Group;
                rml.Append("<div class=\"ex-group\"><span class=\"icon icon-sm icon-").Append(GroupIcon(group)).Append("\"></span><span>")
                    .Append(group).Append("</span></div>");
                if (group == "General")
                    rml.Append("<div class=\"ex-row\"><div class=\"ex-label\">Platform</div><div class=\"ex-editor\"><span class=\"icon icon-sm icon-")
                        .Append(PlatformIcon(preset.Platform)).Append("\"></span><span class=\"readonly\">&nbsp;").Append(ExportPreset.PlatformKey(preset.Platform))
                        .Append("</span></div></div>");
            }

            var value = ExportPresetsModel.Get(preset, option.Key);
            var key = RmlText.Escape(option.Key);
            rml.Append("<div class=\"ex-row\"><div class=\"ex-label\" data-tooltip=\"").Append(RmlText.Escape(option.Tooltip)).Append("\">")
                .Append(RmlText.Escape(option.Label)).Append("</div><div class=\"ex-editor\">");
            switch (option.Kind)
            {
                case ExportOptionKind.Bool:
                    rml.Append("<input type=\"checkbox\" class=\"checkbox\" data-key=\"").Append(key).Append('"').Append(value == "true" ? " checked=\"\"" : "").Append("/>");
                    break;
                case ExportOptionKind.Choice:
                    rml.Append("<select data-key=\"").Append(key).Append("\">");
                    foreach (var choice in ExportPresetsModel.Choices(option.Key, preset))
                        rml.Append("<option value=\"").Append(RmlText.Escape(choice)).Append('"').Append(choice == value ? " selected=\"\"" : "").Append('>')
                            .Append(RmlText.Escape(choice)).Append("</option>");
                    rml.Append("</select>");
                    break;
                default:
                    rml.Append("<input type=\"text\" class=\"text\" data-key=\"").Append(key).Append("\" value=\"").Append(RmlText.Escape(value))
                        .Append("\" placeholder=\"").Append(RmlText.Escape(Placeholder(option.Key, project))).Append("\"/>");
                    if (option.Kind == ExportOptionKind.File)
                    {
                        rml.Append("<button class=\"tool-button small\" data-op=\"browse-icon\" data-tooltip=\"Browse — pick a PNG of the project\"><span class=\"icon icon-sm icon-folder-open\"></span></button>");
                        var icon = value.Length > 0 ? value : project.Window.Icon;
                        if (icon is { Length: > 0 } && icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.GetFullPath(icon, root)))
                            rml.Append("<img class=\"ex-preview\" src=\"").Append(RmlText.Escape(Path.GetFullPath(icon, root))).Append("\"/>");
                    }

                    break;
            }

            if (option.Key == "steam")
            {
                var appId = preset.Demo ? project.Steam.DemoAppId : project.Steam.AppId;
                rml.Append("<span class=\"readonly\">&nbsp;").Append(preset.Demo ? "demo app id " : "app id ")
                    .Append(appId == 0 ? "not set" : appId.ToString(CultureInfo.InvariantCulture)).Append("</span>")
                    .Append("<button class=\"tool-button small\" data-op=\"steamworks\" data-tooltip=\"Steamworks Settings — the app ids live in Project Settings › Steamworks\">")
                    .Append("<span class=\"icon icon-sm icon-brand-steam\"></span></button>");
            }

            rml.Append("</div></div>");
        }

        rml.Append("<div class=\"ex-group\"><span class=\"icon icon-sm icon-filter\"></span><span>Content</span></div>");
        RenderGlobs(rml, "Include", preset.Include, include: true);
        RenderGlobs(rml, "Exclude", preset.Exclude, include: false);
    }

    private static void RenderGlobs(StringBuilder rml, string label, List<string> globs, bool include)
    {
        var kind = include ? "include" : "exclude";
        rml.Append("<div class=\"ex-row\"><div class=\"ex-label\" data-tooltip=\"")
            .Append(include ? "The game's Assets/ files that ship (globs: * one folder, ** any depth, ?). Engine content always ships."
                : "Applied after Include. .po sources are excluded by default: the build compiles them to .mo.")
            .Append("\">").Append(label).Append("</div><div class=\"ex-editor\"><div class=\"chips\">");
        for (var i = 0; i < globs.Count; i++)
            rml.Append("<div class=\"chip\"><span>").Append(RmlText.Escape(globs[i])).Append("</span><button class=\"tool-button small\" data-op=\"remove-glob\" data-kind=\"")
                .Append(kind).Append("\" data-index=\"").Append(i).Append("\" data-tooltip=\"Remove\"><span class=\"icon icon-sm icon-x\"></span></button></div>");
        rml.Append("<div class=\"ex-add\"><input type=\"text\" class=\"text\" id=\"ex-new-").Append(kind).Append("\" placeholder=\"Assets/…\"/>")
            .Append("<button class=\"tool-button small\" data-op=\"add-glob\" data-kind=\"").Append(kind).Append("\" data-tooltip=\"Add ").Append(label)
            .Append(" Glob\"><span class=\"icon icon-sm icon-plus\"></span></button></div></div></div></div>");
    }

    private static string GroupIcon(string group) => group switch
    {
        "General" => "settings",
        "Build" => "hammer",
        "Steam" => "brand-steam",
        "Icon" => "photo",
        "macOS" => "brand-apple",
        _ => "point",
    };

    private static string Placeholder(string key, ProjectSettings project) => key switch
    {
        "output" => "export/<name>",
        "version" => project.Version.Length > 0 ? project.Version : "0.0.0",
        "exe" => ExportTargets.DefaultExe(project.Name),
        "icon" => project.Window.Icon ?? "Assets/icon.png",
        "bundleId" => "com.mainframegames." + ExportTargets.DefaultExe(project.Name).ToLowerInvariant(),
        "sign" => "ad hoc",
        _ => "",
    };

    private static string IssuesRml(IReadOnlyList<ExportIssue> issues)
    {
        if (issues.Count == 0)
            return "<div class=\"issue none\"><span class=\"icon icon-sm icon-circle-check badge-ok\"></span><span>Ready to export.</span></div>";
        var rml = new StringBuilder();
        foreach (var issue in issues.OrderByDescending(i => i.IsError))
        {
            rml.Append("<div class=\"issue\"><span class=\"icon icon-sm ").Append(issue.IsError ? "icon-alert-circle badge-error" : "icon-alert-triangle badge-warn")
                .Append("\"></span><span class=\"code\">").Append(RmlText.Escape(issue.Code)).Append("</span><span class=\"text\">").Append(RmlText.Escape(issue.Message)).Append("</span>");
            if (issue.Fix is { } fix)
                rml.Append("<span class=\"fix\">").Append(RmlText.Escape(fix)).Append("</span>");
            if (issue.Code.StartsWith("steam-app-id", StringComparison.Ordinal))
                rml.Append("<button class=\"tool-button small\" data-op=\"steamworks\" data-tooltip=\"Open Project Settings › Steamworks\"><span class=\"icon icon-sm icon-brand-steam\"></span></button>");
            if (issue.Code == "icon")
                rml.Append("<button class=\"tool-button small\" data-op=\"browse-icon\" data-tooltip=\"Pick an icon PNG\"><span class=\"icon icon-sm icon-photo\"></span></button>");
            rml.Append("</div>");
        }

        return rml.ToString();
    }

    // The progress strip: status, bar, the last log lines and the last run's results (cheap: only on export changes).
    private void RenderProgress()
    {
        if (!IsLoaded || !Visible)
            return;
        var export = Workspace.Export;
        var running = export.IsRunning;
        var hasResults = export.LastResults.Count > 0;
        Document.GetElementById("ex-progress").SetClass("hidden", !running && !hasResults);
        Document.GetElementById("ex-cancel").SetClass("disabled", !running);
        var canExport = !running && Model is { Presets.Count: > 0 };
        Document.GetElementById("ex-export").SetClass("disabled", !canExport || Selected < 0);
        Document.GetElementById("ex-export-all").SetClass("disabled", !canExport);

        var progress = export.Progress;
        var key = string.Create(CultureInfo.InvariantCulture, $"{running}|{progress}|{export.LastResults.Count}|{export.IsWaiting}");
        if (key != _progressKey)
        {
            _progressKey = key;
            var fill = Document.GetElementById("ex-fill");
            float fraction;
            if (!running)
                fraction = hasResults ? 1f : 0f;
            else if (progress is { } p)
                fraction = (p.PresetIndex + PhaseFraction(p)) / Math.Max(1, p.PresetCount);
            else
                fraction = 0f;
            fill.SetProperty("width", string.Create(CultureInfo.InvariantCulture, $"{fraction * 100f:0.#}%"));
            fill.SetClass("indeterminate", running && progress is { Fraction: null });
            SetText("ex-status", running ? export.Status : hasResults ? ExportResult.Tally(export.LastResults) : "");
        }

        if (export.RecentLines.Count != _shownLines || !running)
        {
            _shownLines = export.RecentLines.Count;
            var log = new StringBuilder();
            if (!running && hasResults)
            {
                foreach (var result in export.LastResults)
                {
                    log.Append("<div class=\"result\"><span class=\"icon icon-sm ")
                        .Append(result.Succeeded ? "icon-circle-check badge-ok" : result.Cancelled ? "icon-player-stop badge-warn" : "icon-circle-x badge-error")
                        .Append("\"></span><span>").Append(RmlText.Escape(result.ArtifactPath is { } done
                            ? $"{result.Preset}: exported {Path.GetFileName(done)}"
                            : $"{result.Preset}: {result.Summary}")).Append("</span>");
                    if (result.ArtifactPath is { } artifact)
                        log.Append("<button class=\"tool-button small\" data-op=\"reveal\" data-path=\"").Append(RmlText.Escape(artifact))
                            .Append("\" data-tooltip=\"").Append(OperatingSystem.IsMacOS() ? "Show in Finder" : "Show in Explorer")
                            .Append(" — the exported build\"><span class=\"icon icon-sm icon-").Append(File.Exists(artifact) ? "file-zip" : "folder")
                            .Append("\"></span></button>");
                    log.Append("</div>");
                }
            }

            foreach (var line in export.RecentLines.TakeLast(running ? 40 : 12))
                log.Append("<div>").Append(RmlText.Escape(line)).Append("</div>");
            Document.GetElementById("ex-log").SetInnerRml(log.ToString());
        }
    }

    // Stage and Archive report fractions; the long phases (Publish, Sign, Notarize) count as half-way through a preset.
    private static float PhaseFraction(ExportProgress p) => p.Phase switch
    {
        ExportPhase.Validate => 0.02f,
        ExportPhase.Icon or ExportPhase.Publish => 0.1f,
        ExportPhase.Stage => 0.6f + 0.1f * (p.Fraction ?? 0f),
        ExportPhase.Bundle or ExportPhase.Sign or ExportPhase.Notarize => 0.75f,
        ExportPhase.Archive => 0.8f + 0.2f * (p.Fraction ?? 0f),
        _ => 0f,
    };

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnChange(RmlEvent e)
    {
        if (_suppress || Model is null || Selected < 0)
            return;
        var target = e.Target;
        if (target.GetAttribute("data-key") is not { } key)
        {
            if (e.GetParameter("linebreak", false) && target.Id is "ex-new-include" or "ex-new-exclude")
                Apply(Model.AddGlob(Selected, target.Id == "ex-new-include", target.Value ?? ""));
            return;
        }

        if (target.TagName == "select")
            Apply(Model.Set(Selected, key, e.Value));
        else if (target.GetAttribute("type") == "checkbox")
            Apply(Model.Set(Selected, key, target.HasAttribute("checked") ? "true" : "false"));
        else if (e.GetParameter("linebreak", false))
            Apply(Model.Set(Selected, key, e.Value));
    }

    private void OnBlur(RmlEvent e)
    {
        if (_suppress || Model is null || Selected < 0)
            return;
        var target = e.Target;
        if (target.TagName != "input" || target.GetAttribute("type") is "checkbox" || target.GetAttribute("data-key") is not { } key)
            return;
        var text = target.Value ?? "";
        if (!string.Equals(text, ExportPresetsModel.Get(Model.Presets[Selected], key), StringComparison.Ordinal))
            Apply(Model.Set(Selected, key, text));
    }

    private void OnKeyDown(RmlEvent e)
    {
        if (!Visible || Model is null)
            return;
        var rml = (RmlKey)e.GetParameter("key_identifier", 0);
        var command = e.GetParameter("ctrl_key", 0) != 0 || e.GetParameter("meta_key", 0) != 0;
        if (command && rml == RmlKey.Z)
        {
            if (e.GetParameter("shift_key", 0) != 0)
                Model.History.Redo();
            else
                Model.History.Undo();
        }
        else if (command && rml == RmlKey.S)
        {
            Save();
        }
        else if (rml == RmlKey.Escape)
        {
            RequestClose();
        }
        else
        {
            return;
        }

        e.StopPropagation();
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (Model is null)
            return;
        if (FindAttribute(e.Target, "data-preset") is { } presetText && int.TryParse(presetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset))
        {
            Select(preset);
            return;
        }

        var element = FindWithAttribute(e.Target, "data-op");
        if (!element.IsNull)
        {
            var include = element.GetAttribute("data-kind") == "include";
            var index = int.TryParse(element.GetAttribute("data-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
            switch (element.GetAttribute("data-op"))
            {
                case "add-glob":
                    Apply(Model.AddGlob(Selected, include, Document.GetElementById(include ? "ex-new-include" : "ex-new-exclude").Value ?? ""));
                    break;
                case "remove-glob":
                    Apply(Model.RemoveGlob(Selected, include, index));
                    break;
                case "browse-icon":
                    BrowseIcon();
                    break;
                case "steamworks":
                    OpenSteamworks();
                    break;
                case "reveal":
                    if (element.GetAttribute("data-path") is { } path)
                        OsShell.Reveal(path);
                    break;
            }

            return;
        }

        switch (FindAttribute(e.Target, "id"))
        {
            case "ex-undo":
                Model.History.Undo();
                break;
            case "ex-redo":
                Model.History.Redo();
                break;
            case "ex-add":
                ShowAddMenu(FindWithAttribute(e.Target, "id"));
                break;
            case "ex-duplicate":
                if (Selected >= 0 && Apply(Model.Duplicate(Selected)))
                    Select(Selected + 1);
                break;
            case "ex-delete":
                ConfirmDelete();
                break;
            case "ex-save":
                Save();
                break;
            case "ex-close":
                RequestClose();
                break;
            case "ex-export":
                if (Selected >= 0)
                    StartExport(all: false);
                break;
            case "ex-export-all":
                StartExport(all: true);
                break;
            case "ex-cancel":
                Workspace.Export.Cancel();
                break;
        }
    }

    /// <summary>Selects the preset at <paramref name="index"/>.</summary>
    public void Select(int index)
    {
        if (Model is null)
            return;
        Selected = Math.Clamp(index, -1, Model.Presets.Count - 1);
        Render();
    }

    /// <summary>Adds a preset for <paramref name="platform"/> (Add Preset's menu) and selects it.</summary>
    public string? AddPreset(ExportPlatform platform)
    {
        if (Model is null)
            return "The dialog is closed.";
        var error = Model.Add(platform, ExportTargets.CurrentHost, ExportTargets.CurrentRid);
        if (Apply(error))
            Select(Model.Presets.Count - 1);
        return error;
    }

    /// <summary>The Add Preset menu's items: desktop platforms, this machine's first; mobile disabled until M12.7.</summary>
    public static IReadOnlyList<MenuItem> AddMenuItems(ExportHost host)
    {
        var platforms = new List<ExportPlatform> { ExportTargets.PlatformOf(host) };
        foreach (var p in (ExportPlatform[])[ExportPlatform.Windows, ExportPlatform.MacOS, ExportPlatform.Linux])
            if (!platforms.Contains(p))
                platforms.Add(p);
        var items = new List<MenuItem> { MenuItem.Header("Add Preset") };
        foreach (var platform in platforms)
        {
            var label = platform switch { ExportPlatform.Windows => "Windows", ExportPlatform.MacOS => "macOS", _ => "Linux" };
            var needsMac = platform == ExportPlatform.MacOS && host != ExportHost.MacOS;
            items.Add(new MenuItem(label + (platform == ExportTargets.PlatformOf(host) ? " (this machine)" : ""), "add:" + ExportPreset.PlatformKey(platform),
                Icon: PlatformIcon(platform))
            {
                Tooltip = needsMac ? "macOS exports need a Mac (codesign); the preset can be added and exported there." : null,
            });
        }

        items.Add(MenuItem.Separator);
        items.Add(new MenuItem("Android", null, null, Enabled: false, Icon: "device-mobile") { Tooltip = "Mobile exports come with M12.7." });
        items.Add(new MenuItem("iOS", null, null, Enabled: false, Icon: "device-mobile") { Tooltip = "Mobile exports come with M12.7." });
        return items;
    }

    private void ShowAddMenu(RmlElement anchor)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var bounds = anchor.IsNull ? default : anchor.Bounds;
        Workspace.Popup.Show(AddMenuItems(ExportTargets.CurrentHost), bounds.X / scale, (bounds.Y + bounds.Height) / scale, command =>
        {
            if (command.StartsWith("add:", StringComparison.Ordinal) && ExportPreset.TryParsePlatform(command[4..], out var platform))
                AddPreset(platform);
        });
    }

    private void ConfirmDelete()
    {
        if (Model is null || Selected < 0)
            return;
        var name = Model.Presets[Selected].Name;
        Workspace.Message.Show(new MessageRequest
        {
            Title = "Delete Preset",
            Message = $"Delete the export preset “{name}”? (Undo brings it back until the dialog closes.)",
            Buttons = ["Delete", "Cancel"],
            DefaultButton = 1,
            CancelButton = 1,
            Callback = (button, _) =>
            {
                if (button == 0 && Model is not null && Selected >= 0)
                    Apply(Model.Remove(Selected));
            },
        });
    }

    private void BrowseIcon()
    {
        if (Model is null || Selected < 0 || Workspace.Session.ProjectRoot is not { } root)
            return;
        var assets = Path.Combine(root, ContentPaths.FolderName);
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Open, Directory.Exists(assets) ? assets : root, [".png"]), "Choose Icon", "Choose",
            path => Apply(Model.Set(Selected, "icon", AssetDatabase.Current.ToProjectPath(path))));
    }

    private void OpenSteamworks()
    {
        RequestClose(() =>
        {
            if (Workspace.ProjectSettings.Open())
                Workspace.ProjectSettings.SelectSection("Steamworks");
        });
    }

    /// <summary>Saves the presets (if edited) and every scene, then starts exporting the selected preset or all of them.</summary>
    public bool StartExport(bool all)
    {
        if (Model is null || Workspace.Session.ProjectRoot is not { } root || Workspace.Export.IsRunning)
            return false;
        if (Model.IsDirty && !Save())
            return false;
        // The game reads scenes from disk, as Play does; an export does not start when a save fails.
        if (!Workspace.Commands.SaveAll())
        {
            Error = "Some scenes could not be saved; the export did not start.";
            Render();
            return false;
        }

        IReadOnlyList<ExportPreset> presets = all ? [.. Model.Presets] : Selected >= 0 ? [Model.Presets[Selected]] : [];
        if (!Workspace.Export.Start(root, presets))
            return false;
        Log.Info(all ? $"[export] Exporting {presets.Count} preset(s)…" : $"[export] Exporting {presets[0].Name}…");
        return true;
    }

    private bool Apply(string? error)
    {
        Error = error ?? "";
        if (error is not null)
            Log.Warning($"[export] {error}");
        Render();
        return error is null;
    }

    // ── Save / close ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes <c>project.mfproj</c> and hands the settings to the session.</summary>
    public bool Save()
    {
        if (Model is not { } model || !model.Save())
            return false;
        Workspace.Session.SetProjectSettings(model.Settings.Current);
        Render();
        return true;
    }

    /// <summary>Closes the dialog, asking about unsaved edits; then <paramref name="closed"/> (not after Cancel). A running export goes on.</summary>
    public void RequestClose(Action? closed = null)
    {
        if (Model is not { IsDirty: true })
        {
            Close();
            closed?.Invoke();
            return;
        }

        Workspace.Message.Show(new MessageRequest
        {
            Title = "Unsaved Export Presets",
            Message = "Save the preset changes to project.mfproj?",
            Buttons = ["Save", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 0 && Save())
                    Close();
                else if (button == 1)
                    Close();
                else
                    return;
                closed?.Invoke();
            },
        });
    }

    private void Close()
    {
        if (Model is not null)
        {
            Model.Settings.Changed -= Render;
            Model.History.Changed -= Render;
        }

        Model = null;
        Selected = -1;
        HideAndReleaseFocus();
    }
}
