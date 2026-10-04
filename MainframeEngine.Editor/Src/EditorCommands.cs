using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Every user-facing editor action by id (menus, toolbar buttons with <c>data-command</c>, shortcuts, QA scripts).
/// <see cref="Execute"/> never lets an exception escape: failures are logged to the Output panel and shown in a
/// message box, and the editor keeps running with the scene unchanged.
/// </summary>
public sealed class EditorCommands
{
    private readonly EditorWorkspace _workspace;

    public EditorCommands(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    private EditorSession Session => _workspace.Session;
    private EditedScene? Active => Session.Active;

    /// <summary>Raised after a command ran (QA scripts and tests wait on it).</summary>
    public event Action<string>? Executed;

    /// <summary>Runs command <paramref name="id"/>; false when it is unknown or failed.</summary>
    public bool Execute(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        try
        {
            var known = Run(id);
            if (!known)
                Log.Warning($"[Editor] Unknown command '{id}'.");
            Executed?.Invoke(id);
            return known;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"'{id}' failed", e);
            return false;
        }
    }

    /// <summary>Exceptions the editor survives (everything but process-fatal ones).</summary>
    public static bool IsRecoverable(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>Logs an error and shows it in a message box.</summary>
    public void ReportError(string what, Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Log.Error($"[Editor] {what}: {e.Message}\n{e.StackTrace}");
        _workspace.Message.Show(new MessageRequest { Title = "Error", Message = $"{what}:\n{e.Message}", Buttons = ["OK"] });
    }

    private bool Run(string id)
    {
        switch (id)
        {
            case "file.new": Session.NewScene(); return true;
            case "file.open": OpenScene(); return true;
            case "file.save": Save(Active, saveAs: false, then: null); return true;
            case "file.save_as": Save(Active, saveAs: true, then: null); return true;
            case "file.close": CloseScene(Active); return true;
            case "file.quit": _workspace.RequestQuit(); return true;
            case "edit.undo": Active?.History.Undo(); return true;
            case "edit.redo": Active?.History.Redo(); return true;
            case "edit.history": ShowHistory(); return true;
            case "edit.delete": Active?.Delete(Active.Selection.Nodes.ToArray()); return true;
            case "edit.duplicate": Active?.Duplicate(Active.Selection.Nodes.ToArray()); return true;
            case "edit.rename": Rename(); return true;
            case "node.add": AddNode(); return true;
            case "scene.instance": InstanceScene(); return true;
            case "node.move_up": MoveSelected(-1); return true;
            case "node.move_down": MoveSelected(1); return true;
            case "gizmo.select": SetMode(GizmoMode.Select); return true;
            case "gizmo.translate": SetMode(GizmoMode.Translate); return true;
            case "gizmo.rotate": SetMode(GizmoMode.Rotate); return true;
            case "gizmo.scale": SetMode(GizmoMode.Scale); return true;
            case "gizmo.local":
                _workspace.Gizmo.Local = !_workspace.Gizmo.Local;
                _workspace.Toolbar.Refresh();
                return true;
            case "gizmo.snap":
                _workspace.Gizmo.Snap = _workspace.Gizmo.Snap with { Enabled = !_workspace.Gizmo.Snap.Enabled };
                _workspace.Toolbar.Refresh();
                return true;
            case "view.frame": _workspace.Viewport.FrameSelection(); return true;
            case "view.grid":
                _workspace.Viewport.GridVisible = !_workspace.Viewport.GridVisible;
                _workspace.Toolbar.Refresh();
                return true;
            case "view.front": Active?.Camera.SetView(EditorView.Front); return true;
            case "view.right": Active?.Camera.SetView(EditorView.Right); return true;
            case "view.top": Active?.Camera.SetView(EditorView.Top); return true;
            case "view.reset": Active?.Camera.Reset(); return true;
            case "help.shortcuts": ShowShortcuts(); return true;
            case "help.about": ShowAbout(); return true;
            default: return false;
        }
    }

    private void SetMode(GizmoMode mode)
    {
        _workspace.Gizmo.Mode = mode;
        _workspace.Toolbar.Refresh();
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────────────────

    private string StartDirectory(EditedScene? scene)
    {
        if (scene?.FilePath is { } path && Path.GetDirectoryName(path) is { } directory)
            return directory;
        if (Session.ProjectRoot is { } root)
            return Directory.Exists(Path.Combine(root, "Content", "Scenes")) ? Path.Combine(root, "Content", "Scenes") : root;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Shows the file picker for a scene to open.</summary>
    public void OpenScene()
    {
        var model = new FilePickerModel(FilePickerMode.Open, StartDirectory(Active), ["*.mscene"]);
        _workspace.FilePicker.Show(model, "Open Scene", "Open", path => _workspace.RunWithSplash($"Loading {Path.GetFileName(path)}…", () =>
        {
            try
            {
                Session.Open(path);
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                ReportError($"Could not open {Path.GetFileName(path)}", e);
            }
        }));
    }

    /// <summary>
    /// Saves <paramref name="scene"/> (asking for a file when it has none or <paramref name="saveAs"/>), then runs
    /// <paramref name="then"/> with whether it was saved.
    /// </summary>
    public void Save(EditedScene? scene, bool saveAs, Action<bool>? then)
    {
        if (scene is null)
        {
            then?.Invoke(false);
            return;
        }

        if (!saveAs && scene.FilePath is not null)
        {
            var saved = TrySave(scene, null); // not inside then?.Invoke(...): a null callback would skip the save
            then?.Invoke(saved);
            return;
        }

        var name = scene.FilePath is null ? "NewScene.mscene" : Path.GetFileName(scene.FilePath);
        var model = new FilePickerModel(FilePickerMode.Save, StartDirectory(scene), ["*.mscene"], name);
        _workspace.FilePicker.Show(model, saveAs ? "Save Scene As" : "Save Scene", "Save",
            path =>
            {
                var saved = TrySave(scene, path);
                then?.Invoke(saved);
            },
            onCancel: () => then?.Invoke(false));
    }

    /// <summary>Saves every dirty scene that has a file (untitled ones are skipped with a warning); false if any failed.</summary>
    public bool SaveAll()
    {
        var ok = true;
        foreach (var scene in Session.Scenes)
        {
            if (!scene.IsDirty)
                continue;
            if (scene.FilePath is null)
            {
                Log.Warning($"[Editor] {scene.DisplayName} has never been saved; use Save As to keep it.");
                ok = false;
                continue;
            }

            ok &= TrySave(scene, null);
        }

        return ok;
    }

    private bool TrySave(EditedScene scene, string? path)
    {
        try
        {
            Session.Save(scene, path);
            Log.Info($"[Editor] Saved {scene.FilePath}");
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {scene.DisplayName}", e);
            return false;
        }
    }

    /// <summary>Closes <paramref name="scene"/>, asking to save it first when it has unsaved changes.</summary>
    public void CloseScene(EditedScene? scene)
    {
        if (scene is null)
            return;
        if (!scene.IsDirty)
        {
            Session.Close(scene);
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = "Unsaved changes",
            Message = $"Save the changes to {scene.DisplayName} before closing it?",
            Buttons = ["Save", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 1)
                    Session.Close(scene);
                else if (button == 0)
                    Save(scene, saveAs: false, then: saved =>
                    {
                        if (saved)
                            Session.Close(scene);
                    });
            },
        });
    }

    // ── Nodes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Add Node dialog: every registered, instantiable node type.</summary>
    public void AddNode()
    {
        if (Active is not { } scene)
            return;
        var items = NodeTypes().Select(t => new ListPickerItem(t.Name, BaseName(t), SceneTreeModel.IconOf(IconProbe(t)), t)).ToArray();
        _workspace.ListPicker.Show("Add Node", items, "Add", payload =>
        {
            var info = (NodeTypeInfo)payload;
            var node = (Node)info.CreateInstance();
            node.Name = info.Name;
            scene.AddNode(node);
        });
    }

    /// <summary>Registered node types the user can create (not abstract, not the tree root or placeholders).</summary>
    public static IReadOnlyList<NodeTypeInfo> NodeTypes() =>
        [.. TypeRegistry.All
            .Where(t => t.IsNode && !t.IsAbstract && t.Type != typeof(MissingNode) && t.Type != typeof(SceneViewport))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    private static string BaseName(NodeTypeInfo info) => info.Base?.Name ?? "";

    // The icon category needs an instance; the type's pristine default instance is never added to a tree.
    private static Node IconProbe(NodeTypeInfo info) => info.DefaultInstance as Node ?? new Node();

    /// <summary>Picks a scene file and instances it under the selection.</summary>
    public void InstanceScene()
    {
        if (Active is not { } scene)
            return;
        var model = new FilePickerModel(FilePickerMode.Open, StartDirectory(scene), ["*.mscene"]);
        _workspace.FilePicker.Show(model, "Instance Scene", "Instance", path =>
        {
            try
            {
                scene.InstanceScene(path);
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                ReportError($"Could not instance {Path.GetFileName(path)}", e);
            }
        });
    }

    /// <summary>Asks for a new name for the selected node.</summary>
    public void Rename()
    {
        if (Active is not { } scene || scene.Selection.Primary is not { } node || !scene.IsEditable(node))
            return;
        _workspace.Message.Show(new MessageRequest
        {
            Title = "Rename Node",
            Message = $"New name for {node.Name}:",
            Input = node.Name,
            Buttons = ["Rename", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, text) =>
            {
                if (button == 0 && Node.IsInstanceValid(node))
                    scene.Rename(node, text);
            },
        });
    }

    private void MoveSelected(int delta)
    {
        if (Active is { Selection.Primary: { } node } scene)
            scene.Move(node, delta);
    }

    // ── Menus and help ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The undo history as a menu: clicking an entry undoes or redoes up to it.</summary>
    public void ShowHistory(float x = 120, float y = 28)
    {
        if (Active is not { } scene)
            return;
        var history = scene.History;
        var items = new List<MenuItem> { MenuItem.Header("Undo history") };
        items.Add(new MenuItem("(scene opened)", "history:0", Css: history.Position == 0 ? "current" : null));
        for (var i = 0; i < history.Actions.Count; i++)
        {
            var css = i + 1 == history.Position ? "current" : i + 1 > history.Position ? "undone" : null;
            items.Add(new MenuItem(history.Actions[i].Name, $"history:{i + 1}", Css: css));
        }

        _workspace.Popup.Show(items, x, y, command =>
        {
            if (command.StartsWith("history:", StringComparison.Ordinal) &&
                int.TryParse(command.AsSpan(8), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var position))
                scene.History.GoTo(Math.Clamp(position, 0, scene.History.Actions.Count));
        });
    }

    /// <summary>The items of the menu bar's menu <paramref name="menu"/>.</summary>
    public IReadOnlyList<MenuItem> MenuItems(string menu)
    {
        var scene = Active;
        var history = scene?.History;
        var hasSelection = scene?.Selection.Count > 0;
        return menu switch
        {
            "file" =>
            [
                new MenuItem("New Scene", "file.new", "Ctrl+N"),
                new MenuItem("Open Scene…", "file.open", "Ctrl+O"),
                MenuItem.Separator,
                new MenuItem("Save", "file.save", "Ctrl+S", scene is not null),
                new MenuItem("Save As…", "file.save_as", "Ctrl+Shift+S", scene is not null),
                MenuItem.Separator,
                new MenuItem("Close Scene", "file.close", "Ctrl+W", scene is not null),
                new MenuItem("Quit", "file.quit", "Ctrl+Q"),
            ],
            "edit" =>
            [
                new MenuItem(history?.UndoAction is { } undo ? $"Undo {undo.Name}" : "Undo", "edit.undo", "Ctrl+Z", history?.CanUndo == true),
                new MenuItem(history?.RedoAction is { } redo ? $"Redo {redo.Name}" : "Redo", "edit.redo", "Ctrl+Shift+Z", history?.CanRedo == true),
                new MenuItem("Undo History…", "edit.history", null, history?.Actions.Count > 0),
                MenuItem.Separator,
                new MenuItem("Add Node…", "node.add", "Ctrl+A", scene is not null),
                new MenuItem("Instance Scene…", "scene.instance", "Ctrl+Shift+A", scene is not null),
                new MenuItem("Rename", "edit.rename", "F2", hasSelection),
                new MenuItem("Duplicate", "edit.duplicate", "Ctrl+D", hasSelection),
                new MenuItem("Delete", "edit.delete", "Del", hasSelection),
            ],
            "view" =>
            [
                new MenuItem("Frame Selection", "view.frame", "F", hasSelection),
                new MenuItem("Front View", "view.front", "1", scene is not null),
                new MenuItem("Right View", "view.right", "3", scene is not null),
                new MenuItem("Top View", "view.top", "7", scene is not null),
                new MenuItem("Reset Camera", "view.reset", null, scene is not null),
                MenuItem.Separator,
                new MenuItem(_workspace.Viewport.GridVisible ? "Hide Grid" : "Show Grid", "view.grid", "G"),
            ],
            "help" =>
            [
                new MenuItem("Keyboard Shortcuts", "help.shortcuts"),
                new MenuItem("About Mainframe Editor", "help.about"),
            ],
            _ => [],
        };
    }

    private void ShowShortcuts() => _workspace.Message.Show(new MessageRequest
    {
        Title = "Keyboard Shortcuts",
        Message = "Ctrl/Cmd+N new · Ctrl+O open · Ctrl+S save · Ctrl+Shift+S save as · Ctrl+W close\n" +
                  "Ctrl+Z undo · Ctrl+Shift+Z / Ctrl+Y redo · Ctrl+D duplicate · Del delete · F2 rename\n" +
                  "Ctrl+A add node · Ctrl+Shift+A instance scene · Ctrl+Up/Down move in tree\n" +
                  "Q select · W move · E rotate · R scale · T local/global · Y snap · F frame · G grid\n" +
                  "Viewport: click select · RMB+WASD/QE fly (Shift faster) · Alt+LMB or MMB orbit · Shift+MMB pan · wheel zoom\n" +
                  "1 / 3 / 7 front, right, top view · F12 developer overlay · F8 UI debugger",
        Buttons = ["Close"],
    });

    private void ShowAbout() => _workspace.Message.Show(new MessageRequest
    {
        Title = "About",
        Message = $"Mainframe Editor {typeof(EditorCommands).Assembly.GetName().Version}\nMainframe Engine — Vulkan, RmlUi, .NET 10.",
        Buttons = ["OK"],
    });
}
