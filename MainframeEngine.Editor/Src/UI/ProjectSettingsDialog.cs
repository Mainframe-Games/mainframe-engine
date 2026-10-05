using System.Globalization;
using System.Text;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// Project › Project Settings (E4): edits the open project's <c>project.mfproj</c> through a
/// <see cref="ProjectSettingsModel"/> — application (name, main scene picker, game assemblies, Steam), window, the input
/// map (actions, deadzones, bindings captured from a key or mouse press, gamepad inputs from a list), physics 2D/3D,
/// audio (bus layout reference), localization, rendering quality (exposure, shadow quality) and autoloads — with undo/
/// redo (its own history, Cmd/Ctrl+Z inside the dialog) and Save. Closing with unsaved edits asks first.
/// </summary>
public sealed class ProjectSettingsDialog : EditorDocument
{
    private static readonly Dictionary<RmlKey, Key> KeysByRml = BuildKeyMap();

    private RmlEventListener? _change;
    private RmlEventListener? _blur;
    private RmlEventListener? _keys;
    private RmlEventListener? _captureMouse;
    private string _section = ProjectSettingsModel.Sections[0].Name;
    private bool _suppress;

    public ProjectSettingsDialog(EditorWorkspace workspace)
        : base(workspace, "project_settings.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The model being edited (null while closed).</summary>
    public ProjectSettingsModel? Model { get; private set; }

    /// <summary>The section on screen.</summary>
    public string Section => _section;

    /// <summary>The action whose binding is being captured (the next key or mouse press), or null.</summary>
    public string? CapturingAction { get; private set; }

    /// <summary>The last rejected value's message (empty when none).</summary>
    public string Error { get; private set; } = "";

    /// <summary>Opens the dialog on the session's project.</summary>
    public bool Open()
    {
        if (Workspace.Session.Project is not { } settings || Workspace.Session.ProjectRoot is not { } root)
        {
            Workspace.Message.Show(new MessageRequest { Title = "Project Settings", Message = "Open a project first.", Buttons = ["OK"] });
            return false;
        }

        Model = new ProjectSettingsModel(settings, settings.FilePath ?? Path.Combine(root, ProjectSettings.FileName));
        Model.Changed += Render;
        Model.History.Changed += Render;
        Error = "";
        CapturingAction = null;
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
        _captureMouse?.Remove();
        _change = root.AddEventListener("change", OnChange);
        _blur = root.AddEventListener("blur", OnBlur, inCapturePhase: true);
        _keys = root.AddEventListener("keydown", OnKeyDown);
        _captureMouse = root.AddEventListener("mousedown", OnMouseDown, inCapturePhase: true);
        Render();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!IsLoaded || Model is not { } model)
            return;
        var suppressed = _suppress;
        _suppress = true;
        try
        {
            var nav = new StringBuilder();
            foreach (var (name, icon) in ProjectSettingsModel.Sections)
                nav.Append("<div class=\"ps-nav").Append(name == _section ? " selected" : "").Append("\" data-section=\"").Append(RmlText.Escape(name))
                    .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span><span>").Append(RmlText.Escape(name)).Append("</span></div>");
            Document.GetElementById("ps-nav").SetInnerRml(nav.ToString());

            var body = new StringBuilder(4096);
            body.Append("<div class=\"ps-section-title\">").Append(RmlText.Escape(_section)).Append("</div>");
            switch (_section)
            {
                case "Input Map":
                    RenderInputMap(body, model.Current.Input);
                    break;
                case "Autoloads":
                    RenderAutoloads(body, model.Current);
                    break;
                default:
                    foreach (var setting in ProjectSettingsModel.Settings)
                        if (setting.Section == _section)
                            RenderSetting(body, setting, setting.Get(model.Current));
                    break;
            }

            Document.GetElementById("ps-body").SetInnerRml(body.ToString());
            SetText("ps-error", Error);
            Document.GetElementById("ps-undo").SetClass("disabled", !model.History.CanUndo);
            Document.GetElementById("ps-redo").SetClass("disabled", !model.History.CanRedo);
            SetText("ps-state", model.IsDirty ? "Unsaved changes" : "Saved");
            Document.GetElementById("ps-capture").SetClass("hidden", CapturingAction is null);
            SetText("ps-capture-text", CapturingAction is null ? "" : $"Press a key or click a mouse button for “{CapturingAction}” (Esc cancels).");
        }
        finally
        {
            _suppress = suppressed;
        }
    }

    private static void RenderSetting(StringBuilder rml, ProjectSetting setting, string value)
    {
        rml.Append("<div class=\"ps-row\"><div class=\"ps-label\"").Append(setting.Tooltip.Length > 0 ? $" data-tooltip=\"{RmlText.Escape(setting.Tooltip)}\"" : "")
            .Append('>').Append(RmlText.Escape(setting.Label)).Append("</div><div class=\"ps-editor\">");
        var key = RmlText.Escape(setting.Key);
        switch (setting.Kind)
        {
            case SettingKind.Bool:
                rml.Append("<input type=\"checkbox\" class=\"checkbox\" data-key=\"").Append(key).Append('"').Append(value == "true" ? " checked=\"\"" : "").Append("/>");
                break;
            case SettingKind.Choice:
                rml.Append("<select data-key=\"").Append(key).Append("\">");
                foreach (var choice in setting.Choices ?? [])
                    rml.Append("<option value=\"").Append(RmlText.Escape(choice)).Append('"').Append(choice == value ? " selected=\"\"" : "").Append('>')
                        .Append(RmlText.Escape(choice)).Append("</option>");
                rml.Append("</select>");
                break;
            case SettingKind.ReadOnly:
                rml.Append("<span class=\"readonly\">").Append(RmlText.Escape(value)).Append("</span>");
                break;
            default:
                rml.Append("<input type=\"text\" class=\"text\" data-key=\"").Append(key).Append("\" value=\"").Append(RmlText.Escape(value)).Append("\"/>");
                if (setting.Kind is SettingKind.Scene or SettingKind.File)
                    rml.Append("<button class=\"tool-button small\" data-browse=\"").Append(key).Append("\" data-tooltip=\"Browse — pick ")
                        .Append(setting.Kind == SettingKind.Scene ? "a scene of the project" : "a file of the project").Append("\"><span class=\"icon icon-sm icon-folder-open\"></span></button>");
                if (setting.Key == "audio.busLayout")
                    rml.Append("<button class=\"tool-button small\" data-edit-file=\"").Append(key)
                        .Append("\" data-tooltip=\"Edit — open the bus layout in the inspector\"><span class=\"icon icon-sm icon-pencil\"></span></button>");
                break;
        }

        rml.Append("</div></div>");
    }

    private static void RenderInputMap(StringBuilder rml, InputMap map)
    {
        rml.Append("<div class=\"ps-add\"><input type=\"text\" class=\"text\" id=\"ps-new-action\" placeholder=\"New action name (e.g. jump)\"/>")
            .Append("<button class=\"tool-button small\" data-op=\"add-action\" data-tooltip=\"Add Action — add the named action\"><span class=\"icon icon-sm icon-plus\"></span></button></div>");
        if (map.Actions.Count == 0)
            rml.Append("<div class=\"empty\">No actions yet. Actions name inputs (\"jump\" → Space, pad A) so code asks Input.IsActionPressed(\"jump\").</div>");
        foreach (var action in map.Actions)
        {
            var name = RmlText.Escape(action.Name);
            rml.Append("<div class=\"ps-action\"><div class=\"ps-action-head\"><span class=\"icon icon-sm icon-keyboard\"></span>")
                .Append("<input type=\"text\" class=\"text ps-action-name\" data-action-name=\"").Append(name).Append("\" value=\"").Append(name).Append("\"/>")
                .Append("<span class=\"ps-small\">deadzone</span><input type=\"text\" class=\"text ps-deadzone\" data-deadzone=\"").Append(name).Append("\" value=\"")
                .Append(ValueText.Number(action.Deadzone)).Append("\"/>")
                .Append("<button class=\"tool-button small\" data-op=\"capture\" data-action=\"").Append(name)
                .Append("\" data-tooltip=\"Add Key or Mouse Button — press it to bind it\"><span class=\"icon icon-sm icon-keyboard-show\"></span></button>")
                .Append("<button class=\"tool-button small\" data-op=\"pad\" data-action=\"").Append(name)
                .Append("\" data-tooltip=\"Add Gamepad Input — a button or stick/trigger direction\"><span class=\"icon icon-sm icon-device-gamepad-2\"></span></button>")
                .Append("<button class=\"tool-button small\" data-op=\"remove-action\" data-action=\"").Append(name)
                .Append("\" data-tooltip=\"Remove Action\"><span class=\"icon icon-sm icon-trash\"></span></button></div><div class=\"ps-bindings\">");
            foreach (var binding in action.Bindings)
            {
                var text = binding.ToString();
                var icon = binding.Kind switch
                {
                    InputBindingKind.MouseButton => "mouse",
                    InputBindingKind.GamepadButton or InputBindingKind.GamepadAxis => "device-gamepad-2",
                    _ => "keyboard",
                };
                rml.Append("<span class=\"chip\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span><span>").Append(RmlText.Escape(text)).Append("</span>")
                    .Append("<button class=\"tool-button small\" data-op=\"unbind\" data-action=\"").Append(name).Append("\" data-binding=\"").Append(RmlText.Escape(text))
                    .Append("\" data-tooltip=\"Remove this binding\"><span class=\"icon icon-sm icon-x\"></span></button></span>");
            }

            if (action.Bindings.Count == 0)
                rml.Append("<span class=\"ps-small\">no inputs bound</span>");
            rml.Append("</div></div>");
        }
    }

    private static void RenderAutoloads(StringBuilder rml, ProjectSettings settings)
    {
        rml.Append("<div class=\"ps-small ps-help\">Nodes added under the root before the main scene, in order (singletons like music or game state).</div>");
        for (var i = 0; i < settings.Autoloads.Count; i++)
        {
            var autoload = settings.Autoloads[i];
            rml.Append("<div class=\"ps-autoload\"><input type=\"checkbox\" class=\"checkbox\" data-autoload-enabled=\"").Append(i).Append('"')
                .Append(autoload.Enabled ? " checked=\"\"" : "").Append(" data-tooltip=\"Enabled\"/><span class=\"icon icon-sm icon-")
                .Append(autoload.Scene is not null ? "movie" : "circle").Append("\"></span><span class=\"ps-autoload-name\">").Append(RmlText.Escape(autoload.Name))
                .Append("</span><span class=\"ps-autoload-what mono\">").Append(RmlText.Escape(autoload.Scene ?? autoload.Type ?? "")).Append("</span>")
                .Append("<button class=\"tool-button small\" data-op=\"autoload-up\" data-index=\"").Append(i).Append("\" data-tooltip=\"Move Up\"><span class=\"icon icon-sm icon-arrow-up\"></span></button>")
                .Append("<button class=\"tool-button small\" data-op=\"autoload-down\" data-index=\"").Append(i).Append("\" data-tooltip=\"Move Down\"><span class=\"icon icon-sm icon-arrow-down\"></span></button>")
                .Append("<button class=\"tool-button small\" data-op=\"autoload-remove\" data-index=\"").Append(i).Append("\" data-tooltip=\"Remove\"><span class=\"icon icon-sm icon-trash\"></span></button></div>");
        }

        rml.Append("<div class=\"ps-add\"><input type=\"text\" class=\"text\" id=\"ps-autoload-name\" placeholder=\"Name\"/>")
            .Append("<input type=\"text\" class=\"text\" id=\"ps-autoload-what\" placeholder=\"Scene (Content/…/X.mscene) or node type\"/>")
            .Append("<button class=\"tool-button small\" data-op=\"autoload-browse\" data-tooltip=\"Browse — pick a scene\"><span class=\"icon icon-sm icon-folder-open\"></span></button>")
            .Append("<button class=\"tool-button small\" data-op=\"autoload-add\" data-tooltip=\"Add Autoload\"><span class=\"icon icon-sm icon-plus\"></span></button></div>");
    }

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnChange(RmlEvent e)
    {
        if (_suppress || Model is null)
            return;
        var target = e.Target;
        if (target.GetAttribute("data-key") is { } key)
        {
            if (target.TagName == "select")
                Apply(Model.Set(key, e.Value));
            else if (target.GetAttribute("type") == "checkbox")
                Apply(Model.Set(key, target.HasAttribute("checked") ? "true" : "false"));
            else if (e.GetParameter("linebreak", false))
                Apply(Model.Set(key, e.Value));
            return;
        }

        if (target.GetAttribute("data-autoload-enabled") is { } index && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            Apply(Model.SetAutoloadEnabled(i, target.HasAttribute("checked")));
            return;
        }

        if (!e.GetParameter("linebreak", false))
            return;
        CommitText(target, target.Value ?? "");
    }

    private void OnBlur(RmlEvent e)
    {
        if (_suppress || Model is null)
            return;
        var target = e.Target;
        if (target.TagName != "input" || target.GetAttribute("type") is "checkbox")
            return;
        if (target.GetAttribute("data-key") is { } key)
        {
            var text = target.Value ?? "";
            if (!string.Equals(text, Model.Get(key), StringComparison.Ordinal))
                Apply(Model.Set(key, text));
            return;
        }

        CommitText(target, target.Value ?? "");
    }

    private void CommitText(RmlElement target, string text)
    {
        if (Model is null)
            return;
        if (target.GetAttribute("data-action-name") is { } action)
            Apply(Model.RenameAction(action, text));
        else if (target.GetAttribute("data-deadzone") is { } deadzoneAction && Model.Current.Input.GetAction(deadzoneAction) is { } a &&
                 !string.Equals(ValueText.Number(a.Deadzone), text.Trim(), StringComparison.Ordinal))
            Apply(Model.SetDeadzone(deadzoneAction, text));
        else if (target.Id == "ps-new-action" && text.Trim().Length > 0)
            Apply(Model.AddAction(text));
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (Model is null)
            return;
        if (FindAttribute(e.Target, "data-section") is { } section)
        {
            SelectSection(section);
            return;
        }

        if (FindAttribute(e.Target, "data-browse") is { } browse)
        {
            Browse(browse);
            return;
        }

        if (FindAttribute(e.Target, "data-edit-file") is { } editKey)
        {
            if (Model.Get(editKey) is { Length: > 0 } file)
                Workspace.Inspector.InspectResourceFile(AssetDatabase.Current.ToAbsolutePath(file));
            return;
        }

        var element = FindWithAttribute(e.Target, "data-op");
        if (element.IsNull)
        {
            switch (FindAttribute(e.Target, "id"))
            {
                case "ps-undo":
                    Model.History.Undo();
                    break;
                case "ps-redo":
                    Model.History.Redo();
                    break;
                case "ps-save":
                    Save();
                    break;
                case "ps-close":
                    RequestClose();
                    break;
            }

            return;
        }

        var action = element.GetAttribute("data-action") ?? "";
        var index = int.TryParse(element.GetAttribute("data-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
        switch (element.GetAttribute("data-op"))
        {
            case "add-action":
                Apply(Model.AddAction(Document.GetElementById("ps-new-action").Value ?? ""));
                break;
            case "remove-action":
                Apply(Model.RemoveAction(action));
                break;
            case "capture":
                BeginCapture(action);
                break;
            case "pad":
                PickGamepad(action);
                break;
            case "unbind":
                if (InputBinding.TryParse(element.GetAttribute("data-binding"), out var binding))
                    Apply(Model.RemoveBinding(action, binding));
                break;
            case "autoload-up":
                Apply(Model.MoveAutoload(index, -1));
                break;
            case "autoload-down":
                Apply(Model.MoveAutoload(index, 1));
                break;
            case "autoload-remove":
                Apply(Model.RemoveAutoload(index));
                break;
            case "autoload-browse":
                PickScene(path => Document.GetElementById("ps-autoload-what").SetValue(path));
                break;
            case "autoload-add":
                Apply(Model.AddAutoload(Document.GetElementById("ps-autoload-name").Value ?? "", Document.GetElementById("ps-autoload-what").Value ?? ""));
                break;
        }
    }

    /// <summary>Shows <paramref name="section"/> (one of <see cref="ProjectSettingsModel.Sections"/>).</summary>
    public void SelectSection(string section)
    {
        _section = section;
        CapturingAction = null;
        Render();
    }

    private void Apply(string? error)
    {
        Error = error ?? "";
        if (error is not null)
            Log.Warning($"[Editor] Project settings: {error}");
        Render();
    }

    // ── Pickers and capture ──────────────────────────────────────────────────────────────────────────────────────

    private void Browse(string key)
    {
        if (Model is null || ProjectSettingsModel.Find(key) is not { } setting)
            return;
        if (setting.Kind == SettingKind.Scene)
        {
            PickScene(path => Apply(Model.Set(key, path)));
            return;
        }

        var root = Workspace.Session.ProjectRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = Directory.Exists(Path.Combine(root, ContentPaths.FolderName)) ? Path.Combine(root, ContentPaths.FolderName) : root;
        var filters = setting.Filter is { } filter ? new[] { filter } : [];
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Open, start, filters), $"Choose {setting.Label}", "Choose",
            path => Apply(Model.Set(key, AssetDatabase.Current.ToProjectPath(path))));
    }

    // The project's scenes (from the asset database) in a searchable list; the value is the scene's project path.
    private void PickScene(Action<string> then)
    {
        var items = AssetDatabase.Current.Entries
            .Where(e => e.Value.EndsWith(".mscene", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Value, StringComparer.Ordinal)
            .Select(e => new ListPickerItem(Path.GetFileNameWithoutExtension(e.Value), e.Value, "icon-movie", e.Value))
            .ToArray();
        Workspace.ListPicker.Show("Choose Scene", items, "Choose", payload => then((string)payload));
    }

    private void PickGamepad(string action)
    {
        if (Model is null)
            return;
        var items = ProjectSettingsModel.GamepadBindings()
            .Select(b => new ListPickerItem(b.ToString(), b.Kind == InputBindingKind.GamepadAxis ? "stick / trigger" : "button", "icon-device-gamepad-2", b))
            .ToArray();
        Workspace.ListPicker.Show($"Gamepad Input for {action}", items, "Bind", payload => Apply(Model.AddBinding(action, (InputBinding)payload)));
    }

    /// <summary>Waits for the next key or mouse button press and binds it to <paramref name="action"/>.</summary>
    public void BeginCapture(string action)
    {
        CapturingAction = action;
        Render();
        if (IsLoaded && Document.GetElementById("ps-capture") is { IsNull: false } overlay)
            overlay.Focus(focusVisible: false);
    }

    /// <summary>Completes a capture with <paramref name="binding"/> (null cancels).</summary>
    public void CompleteCapture(InputBinding? binding)
    {
        if (CapturingAction is not { } action || Model is null)
            return;
        CapturingAction = null;
        if (binding is { } b)
            Apply(Model.AddBinding(action, b));
        else
            Render();
    }

    private void OnKeyDown(RmlEvent e)
    {
        if (!Visible || Model is null)
            return;
        var rml = (RmlKey)e.GetParameter("key_identifier", 0);
        if (CapturingAction is not null)
        {
            e.StopPropagation();
            CompleteCapture(rml == RmlKey.Escape || !KeysByRml.TryGetValue(rml, out var key) ? null : ProjectSettingsModel.Capture(key));
            return;
        }

        var command = e.GetParameter("ctrl_key", 0) != 0 || e.GetParameter("meta_key", 0) != 0;
        if (command && rml == RmlKey.Z)
        {
            if (e.GetParameter("shift_key", 0) != 0)
                Model.History.Redo();
            else
                Model.History.Undo();
        }
        else if (command && rml == RmlKey.Y)
        {
            Model.History.Redo();
        }
        else if (command && rml == RmlKey.S)
        {
            Save();
        }
        else if (rml == RmlKey.Escape)
        {
            RequestClose();
        }
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (CapturingAction is null)
            return;
        e.StopPropagation();
        var button = e.GetParameter("button", 0) switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Right,
            2 => MouseButton.Middle,
            3 => MouseButton.Button4,
            4 => MouseButton.Button5,
            _ => MouseButton.Unknown,
        };
        CompleteCapture(ProjectSettingsModel.Capture(button));
    }

    private static Dictionary<RmlKey, Key> BuildKeyMap()
    {
        var map = new Dictionary<RmlKey, Key>();
        foreach (var key in Enum.GetValues<Key>())
        {
            var rml = UiInputMap.ToRmlKey(key);
            if (rml != RmlKey.Unknown)
                map.TryAdd(rml, key);
        }

        return map;
    }

    // ── Save / close ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes <c>project.mfproj</c> and hands the settings to the session.</summary>
    public bool Save()
    {
        if (Model is not { } model || !model.Save())
            return false;
        Workspace.Session.SetProjectSettings(model.Current);
        Log.Info($"[Editor] Saved {Workspace.Session.DisplayPath(model.FilePath)}");
        Render();
        return true;
    }

    /// <summary>Closes the dialog, asking about unsaved edits.</summary>
    public void RequestClose()
    {
        if (Model is not { IsDirty: true })
        {
            Close();
            return;
        }

        Workspace.Message.Show(new MessageRequest
        {
            Title = "Unsaved Project Settings",
            Message = "Save the changes to project.mfproj?",
            Buttons = ["Save", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 0 && Save())
                    Close();
                else if (button == 1)
                    Close();
            },
        });
    }

    private void Close()
    {
        if (Model is not null)
        {
            Model.Changed -= Render;
            Model.History.Changed -= Render;
        }

        Model = null;
        CapturingAction = null;
        HideAndReleaseFocus();
    }
}
