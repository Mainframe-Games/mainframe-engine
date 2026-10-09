using MainframeEngine.Editor.Music;
using MainframeEngine.Serialization;
using System.Runtime.InteropServices;

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
        // While the mouse drives the viewport (a gizmo drag applies live, committed on release), commands that change
        // the scene or its history would interleave with the drag's own entry: ignore them until it ends.
        if (_workspace.Viewport?.IsInteracting == true && ChangesScene(id))
        {
            Log.Info($"[Editor] '{id}' ignored while dragging in the viewport.");
            return false;
        }

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

    private static bool ChangesScene(string id) =>
        id.StartsWith("edit.", StringComparison.Ordinal) || id.StartsWith("node.", StringComparison.Ordinal) ||
        id.StartsWith("scene.", StringComparison.Ordinal) || id.StartsWith("file.", StringComparison.Ordinal);

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
        if (Session.ActiveTab is SongTab song && RunSong(song, id))
            return true;
        switch (id)
        {
            case "file.new": Session.NewScene(); return true;
            case "file.new_song": NewSong(); return true;
            case "file.open": OpenScene(); return true;
            case "file.save": Save(Active, saveAs: false, then: null); return true;
            case "file.save_as": Save(Active, saveAs: true, then: null); return true;
            case "file.close": CloseTab(Session.ActiveTab); return true;
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
            case "view.post":
                _workspace.Viewport.PostPreview = !_workspace.Viewport.PostPreview;
                _workspace.Toolbar.Refresh();
                return true;
            case "view.front": Active?.Camera.SetView(EditorView.Front); return true;
            case "view.right": Active?.Camera.SetView(EditorView.Right); return true;
            case "view.top": Active?.Camera.SetView(EditorView.Top); return true;
            case "view.reset":
                if (Active?.Camera is { Is2D: true } camera2D)
                    camera2D.Reset2D();
                else
                    Active?.Camera?.Reset();
                return true;
            case "view.2d":
                _workspace.Viewport.Toggle2D();
                _workspace.Toolbar.Refresh();
                return true;
            case "help.shortcuts": ShowShortcuts(); return true;
            case "help.about": ShowAbout(); return true;
            case "help.check_updates": _workspace.Updates.CheckNow(); return true;
            case "help.update":
                if (_workspace.Updates.Available is { IsUpdate: true })
                    _workspace.UpdateDialog.Open();
                return true;
            case "play.main": _workspace.Play.PlayMain(); return true;
            case "play.scene": _workspace.Play.PlayCurrent(); return true;
            case "play.pause": _workspace.Play.TogglePause(); return true;
            case "play.stop": _workspace.Play.Stop(); return true;
            case "play.another": _workspace.Play.PlayAnotherInstance(); return true;
            case "play.instances": _workspace.Play.PlayInstances(); return true;
            case "play.reload_scene": _workspace.Play.ReloadScene(); return true;
            case "project.build_reload": _workspace.Project.BuildAndReload(); return true;
            case "project.reload_code": _workspace.Project.ReloadGameAssembly(); return true;
            case "project.open": ChooseProjectFile(); return true;
            case "project.new": _workspace.NewProject.Open(); return true;
            case "project.manager": ShowProjectManager(); return true;
            case "project.settings": _workspace.ProjectSettings.Open(); return true;
            case "project.close": CloseProject(); return true;
            case "editor.settings": _workspace.EditorSettingsDialog.Open(); return true;
            default:
                return id.StartsWith("fs.", StringComparison.Ordinal) && _workspace.FileSystem.RunCommand(id);
        }
    }

    // Commands that act on the active song tab instead of a scene (false: not a song command, run it as usual).
    private bool RunSong(SongTab song, string id)
    {
        var history = song.Document.History;
        switch (id)
        {
            case "file.save": SaveSong(song, saveAs: false, then: null); return true;
            case "file.save_as": SaveSong(song, saveAs: true, then: null); return true;
            case "edit.undo": history.Undo(); return true;
            case "edit.redo": history.Redo(); return true;
            case "edit.history": return true;
            case "edit.delete": song.Controller.Delete(); return true;
            case "edit.duplicate": song.Controller.Duplicate(); return true;
            case "song.play": song.Player.TogglePlay(); return true;
            case "song.stop": song.Player.Stop(); return true;
            case "song.rewind": song.Controller.Key(Silk.NET.Input.Key.Home, EditorModifiers.None); return true;
            case "song.loop": song.Controller.ToggleLoop(); return true;
            case "song.record": song.Controller.ToggleRecording(); return true;
            case "song.render": _workspace.SongView.Render(song.Document.FilePath); return true;
            case "song.mixer": song.SetPanel(song.Panel == SongBottomPanel.Mixer ? SongBottomPanel.PianoRoll : SongBottomPanel.Mixer); return true;
            default: return false;
        }
    }

    /// <summary>File › New Song: asks where to save it (default <c>Content/Music</c>), creates it and opens its tab.</summary>
    public void NewSong()
    {
        var root = Session.ProjectRoot;
        var folder = root is null ? StartDirectory(null) : Path.Combine(root, "Content", "Music");
        if (root is not null)
            Directory.CreateDirectory(folder);
        var model = new FilePickerModel(FilePickerMode.Save, folder, ["*.msong"], "NewSong.msong");
        _workspace.FilePicker.Show(model, "New Song", "Create", path => CreateSong(path));
    }

    /// <summary>Creates a song at <paramref name="path"/> (one built-in instrument track) and opens it; false on failure.</summary>
    public bool CreateSong(string path)
    {
        try
        {
            var full = SongTab.CreateFile(path);
            Log.Info($"[Editor] Created {Session.DisplayPath(full)}");
            Session.OpenSong(full);
            _workspace.FileSystem.Rescan();
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not create {Path.GetFileName(path)}", e);
            return false;
        }
    }

    /// <summary>Opens a song tab for <paramref name="path"/>, reporting failures.</summary>
    public void OpenSong(string path)
    {
        try
        {
            Session.OpenSong(path);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not open {Path.GetFileName(path)}", e);
        }
    }

    /// <summary>Saves a song tab (Save As asks for a file), then runs <paramref name="then"/> with whether it was saved.</summary>
    public void SaveSong(SongTab song, bool saveAs, Action<bool>? then)
    {
        if (song.Document.IsReadOnly && !saveAs)
        {
            ReportError($"Could not save {song.DisplayName}", new InvalidOperationException(song.Document.ReadOnlyNotice ?? "The song is read-only."));
            then?.Invoke(false);
            return;
        }

        if (!saveAs)
        {
            var saved = TrySaveSong(song, null);
            then?.Invoke(saved);
            return;
        }

        var model = new FilePickerModel(FilePickerMode.Save, Path.GetDirectoryName(song.Document.FilePath)!, ["*.msong"], song.DisplayName);
        _workspace.FilePicker.Show(model, "Save Song As", "Save", path => then?.Invoke(TrySaveSong(song, path)), onCancel: () => then?.Invoke(false));
    }

    private bool TrySaveSong(SongTab song, string? path)
    {
        try
        {
            Session.SaveSong(song, path);
            Log.Info($"[Editor] Saved {Session.DisplayPath(song.FilePath)}");
            _workspace.FileSystem.UpdateUnsaved();
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {song.DisplayName}", e);
            return false;
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
        foreach (var tab in Session.Tabs)
            if (tab is SongTab { IsDirty: true, Document.IsReadOnly: false } song)
                ok &= TrySaveSong(song, null);
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

    /// <summary>
    /// Saves every dirty scene: those with a file at once, then each untitled one through Save As (one dialog after the
    /// other). <paramref name="then"/> gets true only when everything was saved (false on a failure or a cancelled dialog).
    /// </summary>
    public void SaveAll(Action<bool> then)
    {
        ArgumentNullException.ThrowIfNull(then);
        if (!SaveAll() && Session.Tabs.Any(t => t.IsDirty && t is SongTab or EditedScene { FilePath: not null }))
        {
            then(false); // a file-backed save failed (already reported)
            return;
        }

        var untitled = new Queue<EditedScene>(Session.Scenes.Where(s => s.IsDirty && s.FilePath is null));
        void Next(bool previousSaved)
        {
            if (!previousSaved)
            {
                then(false);
                return;
            }

            if (untitled.Count == 0)
            {
                then(true);
                return;
            }

            var scene = untitled.Dequeue();
            Session.Activate(scene);
            Save(scene, saveAs: true, then: Next);
        }

        Next(true);
    }

    private bool TrySave(EditedScene scene, string? path)
    {
        try
        {
            Session.Save(scene, path);
            Log.Info($"[Editor] Saved {Session.DisplayPath(scene.FilePath)}");
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not save {scene.DisplayName}", e);
            return false;
        }
    }

    /// <summary>Closes <paramref name="tab"/>: a scene asks to save first when it has unsaved changes (<see cref="CloseScene"/>).</summary>
    public void CloseTab(IEditorTab? tab)
    {
        if (tab is EditedScene scene)
            CloseScene(scene);
        else if (tab is SongTab { IsDirty: true } song)
            CloseSong(song);
        else if (tab is not null)
            Session.Close(tab);
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

    // ── Projects ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the project in <paramref name="directory"/>: asks to save unsaved scenes, closes them and the previous
    /// project's code, then opens it (building its game code when needed) and its main scene, behind the splash.
    /// </summary>
    public void OpenProject(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        AfterUnsavedScenes("Open Project", () =>
            _workspace.RunWithSplash($"Opening {Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar))}…", () => OpenProjectNow(directory, null)));
    }

    /// <summary>Opens a project at once (no questions): start-up and <see cref="OpenProject"/>; then <paramref name="scene"/> or the main scene.</summary>
    public void OpenProjectNow(string directory, string? scene)
    {
        _workspace.ProjectManager.Close();
        _workspace.Play.Stop();
        Session.CloseAll();
        _workspace.Inspector.CloseResource();
        try
        {
            _workspace.Project.Open(directory, onReady: () => OpenStartScene(scene));
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            ReportError($"Could not open the project at {directory}", e);
            if (Session.Scenes.Count == 0 && Session.ProjectRoot is null)
                _workspace.ProjectManager.Open();
        }
    }

    // The requested scene, else the project's main scene, else an empty scene.
    private void OpenStartScene(string? scene)
    {
        var path = scene ?? _workspace.Project.MainScenePath();
        if (path is not null)
        {
            try
            {
                Session.Open(path);
                return;
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Log.Error($"[Editor] Could not open '{path}': {e.Message}");
            }
        }

        if (Session.Scenes.Count == 0)
            Session.NewScene();
    }

    private void ChooseProjectFile()
    {
        var start = Session.ProjectRoot is { } root && Path.GetDirectoryName(root) is { } parent
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ChooseProjectFile(start);
    }

    /// <summary>
    /// Open Project (File menu, Project Manager): a file picker that lists and accepts only <c>.mfproj</c> files — never a
    /// folder — and opens the project of the chosen <c>project.mfproj</c>.
    /// </summary>
    public void ChooseProjectFile(string startDirectory)
    {
        var model = new FilePickerModel(FilePickerMode.Open, startDirectory, ["*.mfproj"]);
        _workspace.FilePicker.Show(model, "Open Project", "Open", file =>
        {
            if (EditorCommandLine.ProjectFolderOf(file) is not { } folder)
            {
                _workspace.Message.Show(new MessageRequest
                {
                    Title = "Not a Project",
                    Message = $"{file} is not a project file. Choose a project's {ProjectSettings.FileName}.",
                    Buttons = ["OK"],
                });
                return;
            }

            OpenProject(folder);
        });
    }

    private void ShowProjectManager() => AfterUnsavedScenes("Project Manager", () =>
    {
        _workspace.Play.Stop();
        Session.CloseAll();
        _workspace.Inspector.CloseResource();
        _workspace.Project.Close();
        _workspace.ProjectManager.Open();
    });

    /// <summary>Closes a song tab with unsaved changes after asking (Save, Don't Save, Cancel).</summary>
    private void CloseSong(SongTab song) => _workspace.Message.Show(new MessageRequest
    {
        Title = "Unsaved changes",
        Message = $"Save the changes to {song.DisplayName} before closing it?",
        Buttons = ["Save", "Don't Save", "Cancel"],
        DefaultButton = 0,
        CancelButton = 2,
        Callback = (button, _) =>
        {
            if (button == 1)
                Session.Close(song);
            else if (button == 0)
                SaveSong(song, saveAs: false, then: saved =>
                {
                    if (saved)
                        Session.Close(song);
                });
        },
    });

    private void CloseProject() => AfterUnsavedScenes("Close Project", () =>
    {
        _workspace.Play.Stop();
        Session.CloseAll();
        _workspace.Inspector.CloseResource();
        _workspace.Project.Close();
        _workspace.ProjectManager.Open();
    });

    // Runs then once unsaved scenes were saved or discarded (Save All / Don't Save / Cancel).
    private void AfterUnsavedScenes(string title, Action then)
    {
        var dirty = Session.DirtyTabs;
        if (dirty.Count == 0)
        {
            then();
            return;
        }

        _workspace.Message.Show(new MessageRequest
        {
            Title = title,
            Message = $"Save the changes to {string.Join(", ", dirty.Select(s => s.DisplayName))} first?",
            Buttons = ["Save All", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 1)
                    then();
                else if (button == 0)
                    SaveAll(saved =>
                    {
                        if (saved)
                            then();
                    });
            },
        });
    }

    // ── Nodes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Add Node dialog: every registered node type as an inheritance tree (abstract types are structure only).</summary>
    public void AddNode()
    {
        if (Active is not { } scene)
            return;
        _workspace.TreePicker.Show(new TreePickerRequest
        {
            Kind = "node",
            Title = "Create New Node",
            OkLabel = "Create",
            Entries = PickerSources.NodeTypes(),
            OnAccept = entry =>
            {
                var info = (NodeTypeInfo)entry.Payload!;
                var node = (Node)info.CreateInstance();
                node.Name = info.Name;
                scene.AddNode(node);
            },
        });
    }

    /// <summary>Registered node types the user can create (not abstract, not the tree root or placeholders).</summary>
    public static IReadOnlyList<NodeTypeInfo> NodeTypes() =>
        [.. PickerSources.NodeTypes().Where(e => e.Selectable).Select(e => (NodeTypeInfo)e.Payload!).OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Instances a scene under the selection: the project's scenes as a folder tree (Browse… for any file), or the file
    /// picker when there is no project.
    /// </summary>
    public void InstanceScene()
    {
        if (Active is not { } scene)
            return;
        void Instance(string path)
        {
            try
            {
                scene.InstanceScene(path);
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                ReportError($"Could not instance {Path.GetFileName(path)}", e);
            }
        }

        void Browse()
        {
            var model = new FilePickerModel(FilePickerMode.Open, StartDirectory(scene), ["*.mscene"]);
            _workspace.FilePicker.Show(model, "Instance Scene", "Instance", Instance);
        }

        if (Session.ProjectRoot is not { } root)
        {
            Browse();
            return;
        }

        _workspace.TreePicker.Show(new TreePickerRequest
        {
            Kind = "scene",
            Title = "Instance Child Scene",
            OkLabel = "Instance",
            // The scene being edited cannot contain itself.
            Entries = [.. PickerSources.Scenes(root).Where(e => e.Payload is not string file || !string.Equals(
                Path.GetFullPath(file), scene.FilePath is null ? null : Path.GetFullPath(scene.FilePath), StringComparison.Ordinal))],
            OnAccept = entry => Instance((string)entry.Payload!),
            OnBrowse = Browse,
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
        items.Add(new MenuItem("(scene opened)", "history:0", Css: history.Position == 0 ? "current" : null, Icon: "file"));
        for (var i = 0; i < history.Actions.Count; i++)
        {
            var css = i + 1 == history.Position ? "current" : i + 1 > history.Position ? "undone" : null;
            items.Add(new MenuItem(history.Actions[i].Name, $"history:{i + 1}", Css: css, Icon: HistoryIcon(history.Actions[i])));
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
        var song = Session.ActiveSong;
        var history = scene?.History ?? song?.Document.History;
        var hasSelection = scene?.Selection.Count > 0;
        return menu switch
        {
            "file" =>
            [
                new MenuItem("New Scene", "file.new", "Ctrl+N", Icon: "file-plus"),
                new MenuItem("Open Scene…", "file.open", "Ctrl+O", Icon: "folder-open"),
                new MenuItem("New Song…", "file.new_song", null, Session.ProjectRoot is not null, Icon: "music"),
                MenuItem.Separator,
                new MenuItem("New Project…", "project.new", Icon: "square-plus"),
                new MenuItem("Open Project…", "project.open", Icon: "folder-open"),
                new MenuItem("Project Manager…", "project.manager", Icon: "folders"),
                MenuItem.Separator,
                new MenuItem("Save", "file.save", "Ctrl+S", scene is not null || song is not null, Icon: "device-floppy"),
                new MenuItem("Save As…", "file.save_as", "Ctrl+Shift+S", scene is not null || song is not null, Icon: "file-export"),
                MenuItem.Separator,
                new MenuItem("Close Tab", "file.close", "Ctrl+W", Session.ActiveTab is not null, Icon: "x"),
                new MenuItem("Quit", "file.quit", "Ctrl+Q", Icon: "logout"),
            ],
            "edit" =>
            [
                new MenuItem(history?.UndoAction is { } undo ? $"Undo {undo.Name}" : "Undo", "edit.undo", "Ctrl+Z", history?.CanUndo == true, Icon: "arrow-back-up"),
                new MenuItem(history?.RedoAction is { } redo ? $"Redo {redo.Name}" : "Redo", "edit.redo", "Ctrl+Shift+Z", history?.CanRedo == true, Icon: "arrow-forward-up"),
                new MenuItem("Undo History…", "edit.history", null, scene is not null && history?.Actions.Count > 0, Icon: "history"),
                MenuItem.Separator,
                new MenuItem("Add Node…", "node.add", "Ctrl+A", scene is not null, Icon: "circle-plus"),
                new MenuItem("Instance Scene…", "scene.instance", "Ctrl+Shift+A", scene is not null, Icon: "link"),
                new MenuItem("Rename", "edit.rename", "F2", hasSelection, Icon: "pencil"),
                new MenuItem("Duplicate", "edit.duplicate", "Ctrl+D", hasSelection || song?.SelectedClip is not null, Icon: "copy"),
                new MenuItem("Delete", "edit.delete", "Del", hasSelection || song?.SelectedClip is not null, Icon: "trash"),
            ],
            "view" =>
            [
                new MenuItem("Frame Selection", "view.frame", "F", hasSelection, Icon: "focus-centered"),
                new MenuItem("Front View", "view.front", "1", scene is not null, Icon: "square-letter-f"),
                new MenuItem("Right View", "view.right", "3", scene is not null, Icon: "square-letter-r"),
                new MenuItem("Top View", "view.top", "7", scene is not null, Icon: "square-letter-t"),
                new MenuItem("Reset Camera", "view.reset", null, scene is not null, Icon: "refresh"),
                new MenuItem(scene?.Camera.Is2D == true ? "3D View" : "2D View", "view.2d", null, scene is not null,
                    Icon: scene?.Camera.Is2D == true ? "box" : "square"),
                MenuItem.Separator,
                new MenuItem(_workspace.Viewport.GridVisible ? "Hide Grid" : "Show Grid", "view.grid", "G", Icon: "grid-3x3"),
                new MenuItem(_workspace.Viewport.PostPreview ? "Hide Post-Processing" : "Preview Post-Processing", "view.post", null, Icon: "sparkles"),
            ],
            "project" =>
            [
                new MenuItem("Project Settings…", "project.settings", null, Session.Project is not null, Icon: "settings"),
                MenuItem.Separator,
                new MenuItem("Build & Reload Code", "project.build_reload", "Ctrl+Shift+B",
                    _workspace.Project.GameLibraryProject is not null && !_workspace.Project.IsBuilding, Icon: "hammer"),
                new MenuItem("Reload Code", "project.reload_code", null, _workspace.Project.IsGameLoaded, Icon: "reload"),
                MenuItem.Separator,
                new MenuItem("Editor Settings…", "editor.settings", Icon: "adjustments"),
                MenuItem.Separator,
                new MenuItem("Close Project", "project.close", null, Session.ProjectRoot is not null, Icon: "folder-x"),
            ],
            "run" =>
            [
                new MenuItem("Play", "play.main", "F5", _workspace.Project.DesktopProject is not null, Icon: "player-play-filled"),
                new MenuItem("Play Scene", "play.scene", "F6", _workspace.Project.DesktopProject is not null && scene is not null, Icon: "movie"),
                new MenuItem("Run Another Instance", "play.another", "Shift+F5", _workspace.Project.DesktopProject is not null, Icon: "copy"),
                new MenuItem("Play Instances", "play.instances", "Ctrl+F5", _workspace.Project.DesktopProject is not null && _workspace.Session.Project?.PlayInstances.Count > 0, Icon: "copy"),
                MenuItem.Separator,
                new MenuItem(_workspace.Play.IsPaused ? "Resume" : "Pause", "play.pause", "F7", _workspace.Play.IsPlaying, Icon: "player-pause"),
                new MenuItem("Reload Scene in Game", "play.reload_scene", null, _workspace.Play.IsPlaying, Icon: "refresh"),
                new MenuItem("Stop", "play.stop", "F8", _workspace.Play.IsPlaying, Icon: "player-stop"),
            ],
            "help" =>
            [
                new MenuItem("Keyboard Shortcuts", "help.shortcuts", Icon: "keyboard"),
                new MenuItem("Check for Updates…", "help.check_updates", null, _workspace.Updates.IsEnabled, Icon: "refresh"),
                new MenuItem("About Mainframe Editor", "help.about", Icon: "info-circle"),
            ],
            _ => [],
        };
    }

    /// <summary>The icon of an undo history entry: what the action did (property icon, add, delete, move …).</summary>
    public static string HistoryIcon(IEditorAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            SetPropertyAction set => PropertyIcons.SemanticIcon(set.Property.Name) ?? "pencil",
            AddNodeAction add when add.Name.StartsWith("Instance", StringComparison.Ordinal) => "link",
            AddNodeAction add when add.Name.StartsWith("Duplicate", StringComparison.Ordinal) => "copy",
            AddNodeAction => "circle-plus",
            RemoveNodeAction => "trash",
            ReparentAction => "arrows-right-left",
            RenameAction => "pencil",
            MoveInTreeAction => "arrows-up-down",
            _ when action.Name.StartsWith("Delete", StringComparison.Ordinal) => "trash",
            _ when action.Name.StartsWith("Duplicate", StringComparison.Ordinal) => "copy",
            _ when action.Name.StartsWith("Move", StringComparison.Ordinal) => "arrows-move",
            _ when action.Name.StartsWith("Rotate", StringComparison.Ordinal) => "rotate",
            _ when action.Name.StartsWith("Scale", StringComparison.Ordinal) => "arrows-maximize",
            _ => "stack-2",
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
                  "1 / 3 / 7 front, right, top view · F12 developer overlay · F9 UI debugger\n" +
                  "Song tab: Space play/stop · L loop · Home to start · Del · Ctrl+C/V/D · arrows nudge (Shift: octave) · Q quantize · Ctrl+wheel zoom\n" +
                  "F5 play · F6 play scene · Shift+F5 another instance · F7 pause · F8 stop · Ctrl+Shift+B build & reload code",
        Buttons = ["Close"],
    });

    private void ShowAbout() => _workspace.Message.Show(new MessageRequest
    {
        Title = "About",
        Message = $"{EditorBrand.NameWithVersion}\n" +
                  "A modular C# game engine and Godot-style editor. Open source under the MIT licence.\n\n" +
                  "Engine: Vulkan 1.2 renderer (HDR, materials, cascaded shadows, glTF/FBX/OBJ import) · " +
                  "Godot-style node tree with .mscene scenes · RmlUi game UI · Jitter2 3D and Box2D 2D physics · " +
                  "SoundFlow audio · multiplayer replication · gettext localization · Spine animation.\n\n" +
                  "Editor: scene tree, inspector and undo · 3D/2D viewport with gizmos · FileSystem panel · " +
                  "Play in a separate process · C# code reload. Its UI is built with the engine's own RmlUi.\n\n" +
                  $"{RuntimeInformation.FrameworkDescription} · " +
                  $"{RuntimeInformation.OSDescription} " +
                  $"({RuntimeInformation.OSArchitecture})\n" +
                  EditorBrand.RepositoryUrl.AbsoluteUri,
        Icon = "info-circle",
        Buttons = ["OK", "GitHub"],
        CancelButton = 0,
        Callback = (button, _) =>
        {
            if (button == 1)
                OsShell.OpenUrl(EditorBrand.RepositoryUrl);
        },
    });
}
