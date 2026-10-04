namespace MainframeEngine.Editor;

/// <summary>
/// The open scenes (tabs) and the active one. Each scene lives in its own <see cref="SubViewport"/> under the host node
/// (one edited world per tab) inside the editor's scene tree, which runs in <see cref="SceneTree.EditMode"/> so only
/// <c>[Tool]</c> nodes process. Opening, saving and closing go through here; edits go through <see cref="EditedScene"/>.
/// </summary>
/// <remarks>
/// Until projects arrive (E4), the project is the folder that contains the opened scene's <c>Content/</c> folder:
/// <see cref="AssetDatabase.Current"/> and <see cref="ContentPaths.ProjectDirectory"/> are pointed at it so the scene's
/// resources resolve from the project sources.
/// </remarks>
public sealed class EditorSession : IDisposable
{
    private readonly Node _host;
    private readonly List<EditedScene> _scenes = [];
    private int _untitledCounter;
    private int _viewportCounter;

    public EditorSession(Node host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public IReadOnlyList<EditedScene> Scenes => _scenes;

    public EditedScene? Active { get; private set; }

    /// <summary>The project folder (contains <c>Content/</c>) resources resolve against; null until a scene is opened or saved.</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>Tabs opened, closed, renamed (saved as), or their dirty state changed.</summary>
    public event Action? ScenesChanged;

    /// <summary>The active tab changed.</summary>
    public event Action? ActiveChanged;

    /// <summary>The active scene changed (edit, undo, redo, save) — panels refresh.</summary>
    public event Action<EditedScene>? SceneEdited;

    /// <summary>The active scene's selection changed.</summary>
    public event Action<EditedScene>? SelectionChanged;

    /// <summary>Opens a new, empty scene with a <paramref name="rootType"/> root and makes it active.</summary>
    public EditedScene NewScene(string rootType = "Node3D")
    {
        var root = Serialization.TypeRegistry.CreateNode(rootType);
        root.Name = rootType;
        var scene = Attach(root, filePath: null, source: null, untitled: ++_untitledCounter);
        Activate(scene);
        return scene;
    }

    /// <summary>
    /// Opens the <c>.mscene</c> at <paramref name="path"/> (or activates its tab when already open). Throws when the
    /// file cannot be read or parsed, or belongs to another project while scenes of the current one are open.
    /// </summary>
    public EditedScene Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        foreach (var open in _scenes)
            if (open.FilePath is not null && PathsEqual(open.FilePath, full))
            {
                Activate(open);
                return open;
            }

        if (!File.Exists(full))
            throw new FileNotFoundException($"Scene not found: {full}", full);
        UseProjectOf(full);

        var assets = AssetDatabase.Current;
        var scene = ResourceLoader.Load<PackedScene>(assets.ToProjectPath(full));
        Node root;
        try
        {
            root = scene.Instantiate();
        }
        catch
        {
            scene.Release();
            throw;
        }

        var edited = Attach(root, full, scene, untitled: 0);
        Activate(edited);
        return edited;
    }

    /// <summary>
    /// Saves <paramref name="scene"/> to <paramref name="path"/> (default: its file). The write is atomic (temp file +
    /// rename): a failure leaves the previous file intact, the scene stays dirty and the exception propagates.
    /// </summary>
    public void Save(EditedScene scene, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var target = path is not null ? Path.GetFullPath(path) : scene.FilePath
                     ?? throw new InvalidOperationException("The scene has no file yet; choose one (Save As).");
        // Saving into another project's folder switches the project only when no other open scene belongs to the current
        // one (their resource paths are relative to it); otherwise refuse, like Open.
        var root = FindProjectRoot(target);
        if (ProjectRoot is null || !PathsEqual(ProjectRoot, root))
        {
            if (ProjectRoot is not null && _scenes.Any(s => !ReferenceEquals(s, scene) && s.FilePath is not null))
                throw new InvalidOperationException(
                    $"'{target}' is in the project at {root}, but other scenes of {ProjectRoot} are open. " +
                    "Save it inside the current project, or close the other scenes first.");
            UseProjectOf(target, allowSwitch: true);
        }

        SceneSaver.Save(scene.Root, target);
        scene.FilePath = target;
        scene.History.MarkSaved();
        ScenesChanged?.Invoke();
    }

    /// <summary>Closes <paramref name="scene"/> without saving (the caller asks first when it is dirty).</summary>
    public void Close(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var index = _scenes.IndexOf(scene);
        if (index < 0)
            return;
        _scenes.RemoveAt(index);
        scene.Changed -= OnSceneChanged;
        scene.Selection.Changed -= OnSelectionChanged;
        scene.Dispose();
        if (ReferenceEquals(Active, scene))
        {
            Active = null;
            if (_scenes.Count > 0)
                Activate(_scenes[Math.Min(index, _scenes.Count - 1)]);
            else
                ActiveChanged?.Invoke();
        }

        ScenesChanged?.Invoke();
    }

    /// <summary>Makes <paramref name="scene"/> the active tab (its viewport renders; the others pause).</summary>
    public void Activate(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (ReferenceEquals(Active, scene))
            return;
        if (!_scenes.Contains(scene))
            throw new ArgumentException("The scene is not open in this session.", nameof(scene));
        foreach (var s in _scenes)
            s.Viewport.UpdateMode = ReferenceEquals(s, scene) ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        Active = scene;
        ActiveChanged?.Invoke();
    }

    /// <summary>True when any open scene has unsaved changes.</summary>
    public bool HasUnsavedChanges => _scenes.Any(s => s.IsDirty);

    /// <summary>
    /// Points the asset database and content resolution at the project containing <paramref name="scenePath"/> (the
    /// nearest ancestor folder with a <c>Content/</c> folder holding the file; else the file's folder). Refuses to
    /// switch projects while scenes of another project are open.
    /// </summary>
    public void UseProjectOf(string scenePath, bool allowSwitch = false)
    {
        var root = FindProjectRoot(scenePath);
        if (ProjectRoot is not null && PathsEqual(ProjectRoot, root))
            return;
        if (ProjectRoot is not null && !allowSwitch && _scenes.Any(s => s.FilePath is not null))
            throw new InvalidOperationException(
                $"'{scenePath}' belongs to the project at {root}, but scenes of {ProjectRoot} are open. Close them first.");
        ProjectRoot = root;
        var database = new AssetDatabase(root);
        database.Refresh();
        AssetDatabase.Current = database;
        ContentPaths.ProjectDirectory = root;
        Log.Info($"[Editor] Project folder: {root}");
    }

    /// <summary>The folder above the nearest <c>Content/</c> ancestor of <paramref name="path"/>, else its folder.</summary>
    public static string FindProjectRoot(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        for (var d = directory; d is not null; d = Path.GetDirectoryName(d))
            if (string.Equals(Path.GetFileName(d), ContentPaths.FolderName, StringComparison.Ordinal))
                return Path.GetDirectoryName(d) ?? d;
        return directory ?? Path.GetFullPath(".");
    }

    private EditedScene Attach(Node root, string? filePath, PackedScene? source, int untitled)
    {
        var viewport = new SubViewport
        {
            Name = $"EditedScene{++_viewportCounter}",
            UpdateMode = SubViewportUpdateMode.Disabled,
            ClearColor = System.Drawing.Color.FromArgb(255, 48, 52, 61),
        };
        _host.AddChild(viewport);
        viewport.AddChild(root);
        var scene = new EditedScene(root, filePath, viewport, source) { UntitledNumber = untitled };
        scene.Changed += OnSceneChanged;
        scene.Selection.Changed += OnSelectionChanged;
        _scenes.Add(scene);
        ScenesChanged?.Invoke();
        return scene;
    }

    private void OnSceneChanged(EditedScene scene)
    {
        ScenesChanged?.Invoke(); // titles (dirty asterisk)
        if (ReferenceEquals(scene, Active))
            SceneEdited?.Invoke(scene);
    }

    private void OnSelectionChanged()
    {
        if (Active is { } active)
            SelectionChanged?.Invoke(active);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var scene in _scenes.ToArray())
        {
            scene.Changed -= OnSceneChanged;
            scene.Selection.Changed -= OnSelectionChanged;
            scene.Dispose();
        }

        _scenes.Clear();
        Active = null;
    }
}
