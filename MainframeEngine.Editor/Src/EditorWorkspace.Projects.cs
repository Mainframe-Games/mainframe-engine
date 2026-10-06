using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// The workspace's E4/E5 parts: the Project Manager and project dialogs, start-up (project, scene or the manager),
/// per-frame project/play/file-system work, file drags from the FileSystem panel, editor settings (accent, autosave),
/// and releasing editor references to game code before a code reload.
/// </summary>
public sealed partial class EditorWorkspace
{
    private double _autosaveSeconds;

    /// <summary>The layer of the Project Manager (between the panels and the dialogs).</summary>
    public UiLayer ProjectLayer { get; private set; } = null!;

    public ProjectManager ProjectManager { get; private set; } = null!;
    public NewProjectDialog NewProject { get; private set; } = null!;
    public DownloadDemoDialog DownloadDemo { get; private set; } = null!;
    public ProjectSettingsDialog ProjectSettings { get; private set; } = null!;
    public EditorSettingsDialog EditorSettingsDialog { get; private set; } = null!;

    /// <summary>Editor updates (badges, dialog, Update &amp; restart).</summary>
    public UpdateController Updates { get; private set; } = null!;

    public UpdateDialog UpdateDialog { get; private set; } = null!;

    /// <summary>A file dragged out of the FileSystem panel (absolute path) until the mouse is released.</summary>
    public string? FileDrag { get; private set; }

    private void CreateProjectUi()
    {
        ProjectLayer = new UiLayer { Name = "EditorProjects", Layer = 40 };
        ProjectManager = new ProjectManager(this) { Name = "ProjectManager" };
        ProjectLayer.AddChild(ProjectManager);
        NewProject = new NewProjectDialog(this) { Name = "NewProject" };
        DownloadDemo = new DownloadDemoDialog(this) { Name = "DownloadDemo" };
        ProjectSettings = new ProjectSettingsDialog(this) { Name = "ProjectSettings" };
        EditorSettingsDialog = new EditorSettingsDialog(this) { Name = "EditorSettings" };
        // Above the other dialogs' layer order: pickers and message boxes these dialogs open come later in the list.
        DialogLayer.AddChild(NewProject);
        DialogLayer.AddChild(DownloadDemo);
        DialogLayer.AddChild(ProjectSettings);
        DialogLayer.AddChild(EditorSettingsDialog);
        Updates = new UpdateController(this, Options.Updates);
        UpdateDialog = new UpdateDialog(this) { Name = "UpdateDialog" };
        DialogLayer.AddChild(UpdateDialog);
        Updates.Changed += OnUpdatesChanged;
        DialogLayer.MoveChild(FilePicker, DialogLayer.ChildCount - 1);
        DialogLayer.MoveChild(ListPicker, DialogLayer.ChildCount - 1);
        DialogLayer.MoveChild(Message, DialogLayer.ChildCount - 1);
        DialogLayer.MoveChild(Popup, DialogLayer.ChildCount - 1);

        Session.ProjectChanged += OnProjectChanged;
        Project.Changed += OnProjectStateChanged;
        Play.Changed += OnProjectStateChanged;
    }

    private bool ProjectDialogOpen =>
        NewProject.Visible || DownloadDemo.Visible || ProjectSettings.Visible || EditorSettingsDialog.Visible || UpdateDialog.Visible || ProjectManager.Visible;

    // ── Start-up ─────────────────────────────────────────────────────────────────────────────────────────────────

    // A project (argument or the scene's project), else the scene, else the Project Manager (editor executable), else a new scene.
    private void StartUp()
    {
        Updates.Start();
        if (Options.InitialProject is { } project)
        {
            RunWithSplash($"Opening {Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar))}…", () => Commands.OpenProjectNow(project, Options.InitialScene));
            return;
        }

        if (Options.InitialScene is null && Options.ShowProjectManager)
        {
            ProjectManager.Open();
            if (Splash.Visible)
                Splash.Finish();
            return;
        }

        RunWithSplash(Options.InitialScene is { } initial ? $"Loading {Path.GetFileName(initial)}…" : "Creating a new scene…", OpenInitialScene);
    }

    // ── Per frame ────────────────────────────────────────────────────────────────────────────────────────────────

    private void ProcessProjects(double deltaTime)
    {
        Project.Update();
        Play.Update();
        Updates.Tick();
        FileSystem.Tick();
        if (ProjectManager.Visible)
            ProjectManager.Tick();
        if (NewProject.Visible)
            NewProject.Tick();
        if (DownloadDemo.Visible)
            DownloadDemo.Tick();
        if (FileDrag is not null && !Host.PrimaryMouseDown)
            EndFileDrag();
        Autosave(deltaTime);
    }

    private void Autosave(double deltaTime)
    {
        var minutes = Settings.AutosaveMinutes;
        if (minutes <= 0)
        {
            _autosaveSeconds = 0;
            return;
        }

        _autosaveSeconds += deltaTime;
        if (_autosaveSeconds < minutes * 60.0)
            return;
        _autosaveSeconds = 0;
        if (Viewport.IsInteracting || IsDialogOpen)
            return; // never in the middle of a drag or a dialog; the next interval saves
        var saved = 0;
        foreach (var scene in Session.Scenes)
        {
            if (!scene.IsDirty || scene.FilePath is null)
                continue;
            try
            {
                Session.Save(scene);
                saved++;
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Log.Warning($"[Editor] Autosave of {scene.DisplayName} failed: {e.Message}");
            }
        }

        if (saved > 0)
            Log.Info($"[Editor] Autosaved {saved} scene{(saved == 1 ? "" : "s")}.");
    }

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnProjectChanged()
    {
        FileSystem.OnProjectChanged();
        Toolbar.RefreshPlay();
        UpdateTitleNow();
    }

    private void OnProjectStateChanged() => Toolbar.RefreshPlay();

    private void OnUpdatesChanged()
    {
        Toolbar.RefreshUpdate();
        ProjectManager.RefreshUpdate();
        UpdateDialog.Refresh();
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies new editor settings (accent live, autosave, code reload) and saves them.</summary>
    public void ApplySettings(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var accentChanged = !string.Equals(settings.Accent, Settings.Accent, StringComparison.Ordinal);
        Settings = settings;
        Project.AutoReload = settings.AutoReloadCode;
        _autosaveSeconds = 0;
        if (Options.EditorSettingsPath is { } path)
        {
            try
            {
                settings.Save(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Editor] Could not save the editor settings: {e.Message}");
            }
        }

        if (accentChanged)
            ApplyAccent();
    }

    /// <summary>Writes the accent overlay and reloads the style sheets (no-op without an overlay folder).</summary>
    public void ApplyAccent()
    {
        if (Options.ThemeOverlayDirectory is not { } overlay)
            return;
        try
        {
            EditorTheme.Apply(overlay, Settings.Accent, EditorTheme.EditorContentDirectory());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not apply the accent colour: {e.Message}");
            return;
        }

        Tree?.Servers.Get<UiServer>()?.Reload(UiReloadKind.StyleSheets);
    }

    // ── File drags (FileSystem panel → scene tree, viewport, inspector, folders) ─────────────────────────────────

    public void BeginFileDrag(string path) => FileDrag = Path.GetFullPath(path);

    public void EndFileDrag() => FileDrag = null;

    /// <summary>
    /// Drops <paramref name="file"/> on <paramref name="target"/> (the scene tree): a scene or model is instanced under it;
    /// a resource is assigned to the node's matching slot (a menu when several fit). False when nothing applies.
    /// </summary>
    public bool DropFileOnNode(string file, Node target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EndFileDrag();
        if (Session.Active is not { } scene || !scene.IsEditable(target))
            return false;
        if (IsInstanceable(file))
            return Guard($"Could not instance {Path.GetFileName(file)}", () => scene.InstanceScene(file, InstanceParent(scene, target)));
        return AssignResource(scene, file, target);
    }

    /// <summary>Drops <paramref name="file"/> on the viewport: scenes and models are instanced under the root, resources go to the selection.</summary>
    public bool DropFileOnViewport(string file)
    {
        EndFileDrag();
        if (Session.Active is not { } scene)
            return false;
        if (IsInstanceable(file))
            return Guard($"Could not instance {Path.GetFileName(file)}", () => scene.InstanceScene(file, scene.Root));
        return scene.Selection.Primary is { } node && AssignResource(scene, file, node);
    }

    private static bool IsInstanceable(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        return extension is ".mscene" or ".gltf" or ".glb" or ".fbx" or ".obj";
    }

    // Instances go under the target, except into another instance (its insides belong to its own file).
    private static Node InstanceParent(EditedScene scene, Node target) =>
        !ReferenceEquals(target, scene.Root) && target.SceneFilePath is not null ? target.Parent ?? scene.Root : target;

    private bool AssignResource(EditedScene scene, string file, Node target)
    {
        Resource resource;
        try
        {
            resource = ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(file));
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Log.Warning($"[Editor] {Path.GetFileName(file)} is not a resource ({e.Message}).");
            return false;
        }

        var slots = Serialization.TypeRegistry.GetNearest(target.GetType())?.Properties
            .Where(p => typeof(Resource).IsAssignableFrom(p.ValueType) && p.ValueType.IsInstanceOfType(resource))
            .ToList() ?? [];
        if (slots.Count == 0)
        {
            resource.Release();
            Log.Warning($"[Editor] {target.Name} has no slot for a {resource.GetType().Name}.");
            return false;
        }

        if (slots.Count == 1)
        {
            scene.SetProperty(target, slots[0], resource);
            return true;
        }

        Popup.Show([MenuItem.Header($"Assign {Path.GetFileName(file)} to"), .. slots.Select(s => new MenuItem(s.Name, s.Name))],
            Layout.Viewport.X + 40, Layout.Viewport.Y + 40, command =>
            {
                if (slots.FirstOrDefault(s => s.Name == command) is { } slot)
                    scene.SetProperty(target, slot, resource);
            });
        return true;
    }

    private bool Guard(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Commands.ReportError(what, e);
            return false;
        }
    }

    // ── Code reload ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops every reference the editor UI holds to scene objects (and through them to game code) before a code reload
    /// unloads the game assembly: open pickers and dialogs, the inspector's model, cached rows and the viewport's lists.
    /// </summary>
    public void ReleaseEditorReferences()
    {
        Popup.Close();
        if (ListPicker.Visible)
            ListPicker.Cancel();
        if (SignalDialog.Visible)
            SignalDialog.Cancel();
        Inspector.ReleaseReferences();
        SceneTree.Refresh();
        Viewport.ReleaseSceneReferences();
        Tree?.Servers.Get<CanvasServer>()?.ReleaseSceneReferences(); // the last canvas frame holds the 2D tabs' nodes
        _titleScene = null;
        _titleFile = null;
    }

    private void UpdateTitleNow()
    {
        _title = "";
        UpdateTitle();
    }

    // ── Shortcuts ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>F5–F8 (play, play scene, pause, stop), Ctrl/Cmd+F5 (play instances) and Ctrl/Cmd+Shift+B (build &amp; reload); null when not one of them.</summary>
    public static string? ProjectShortcutFor(Key key, EditorModifiers modifiers)
    {
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        if (command && shift && key == Key.B)
            return "project.build_reload";
        if (command && !shift && key == Key.F5)
            return "play.instances";
        if (command || (modifiers & EditorModifiers.Alt) != 0)
            return null;
        return key switch
        {
            Key.F5 when shift => "play.another",
            Key.F5 => "play.main",
            Key.F6 => "play.scene",
            Key.F7 => "play.pause",
            Key.F8 => "play.stop",
            _ => null,
        };
    }
}
