using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor;

/// <summary>
/// The open tabs — scenes and UI previews (<see cref="UiPreview"/>) — and the active one. Each scene lives in its own
/// <see cref="SubViewport"/> under the host node (one edited world per tab) inside the editor's scene tree, which runs in
/// <see cref="SceneTree.EditMode"/> so only <c>[Tool]</c> nodes process; a preview's <see cref="UiLayer"/> lives there
/// too. Opening, saving and closing go through here; edits go through <see cref="EditedScene"/>.
/// </summary>
/// <remarks>
/// Until the project UI arrives (E4 editor side), the project is found from the opened scene: the nearest ancestor
/// folder holding a <c>project.mfproj</c> (<see cref="ProjectSettings"/>, loaded into <see cref="Project"/>), else —
/// for folders without one — the folder that contains the scene's <c>Content/</c> folder.
/// <see cref="AssetDatabase.Current"/> and <see cref="ContentPaths.ProjectDirectory"/> are pointed at it so the scene's
/// resources resolve from the project sources.
/// </remarks>
public sealed class EditorSession : IDisposable
{
    private readonly Node _host;
    private readonly List<IEditorTab> _tabs = [];
    private readonly List<EditedScene> _scenes = []; // the scene tabs, in tab order
    private int _untitledCounter;
    private int _viewportCounter;
    private int _previewCounter;
    private int _songCounter;
    private EditedScene? _suspending;

    public EditorSession(Node host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Every tab, in tab-strip order.</summary>
    public IReadOnlyList<IEditorTab> Tabs => _tabs;

    /// <summary>The position of <paramref name="tab"/> in <see cref="Tabs"/>, or -1.</summary>
    public int IndexOf(IEditorTab tab) => _tabs.IndexOf(tab);

    /// <summary>The scene tabs, in tab-strip order.</summary>
    public IReadOnlyList<EditedScene> Scenes => _scenes;

    /// <summary>The active tab (a scene or a UI preview), or null when none is open.</summary>
    public IEditorTab? ActiveTab { get; private set; }

    /// <summary>The active scene: null when no tab is open or the active tab is not a scene (a UI preview).</summary>
    public EditedScene? Active => ActiveTab as EditedScene;

    /// <summary>The active tab when it is a UI preview.</summary>
    public UiPreview? ActivePreview => ActiveTab as UiPreview;

    /// <summary>The active tab when it is a song (<see cref="SongTab"/>).</summary>
    public SongTab? ActiveSong => ActiveTab as SongTab;

    /// <summary>The scene tab that was active most recently (a UI preview's default backdrop), or null.</summary>
    public EditedScene? LastActiveScene { get; private set; }

    /// <summary>The project folder (contains <c>Content/</c>) resources resolve against; null until a scene is opened or saved.</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>
    /// The project's settings when <see cref="ProjectRoot"/> holds a readable <c>project.mfproj</c>; null for a folder
    /// found by the <c>Content/</c> fallback (or an unreadable file, which is reported in the Output panel).
    /// </summary>
    public ProjectSettings? Project { get; private set; }

    /// <summary>Tabs (scenes or previews) opened, closed, renamed (saved as), or their dirty state changed.</summary>
    public event Action? ScenesChanged;

    /// <summary>The active tab changed.</summary>
    public event Action? ActiveChanged;

    /// <summary>The active scene changed (edit, undo, redo, save) — panels refresh.</summary>
    public event Action<EditedScene>? SceneEdited;

    /// <summary>The active scene's selection changed.</summary>
    public event Action<EditedScene>? SelectionChanged;

    /// <summary>The project changed: opened, switched, or its settings were replaced (Project Settings dialog).</summary>
    public event Action? ProjectChanged;

    /// <summary>
    /// Opens the project in <paramref name="directory"/> (the folder holding <c>project.mfproj</c>): loads its settings
    /// and points the asset database (scanned, creating missing <c>.meta</c> sidecars) and content resolution at it.
    /// Scenes stay open only when they belong to it; the caller closes the others first. Throws when the folder has no
    /// project file or the file cannot be read.
    /// </summary>
    /// <param name="directory">The project folder (a real path when it comes from <see cref="ProjectService"/>).</param>
    /// <param name="displayPath">How to show the folder to the user — the path as they opened it, before symbolic links
    /// such as macOS's <c>/tmp</c> → <c>/private/tmp</c> were resolved; defaults to <paramref name="directory"/>.</param>
    public void OpenProject(string directory, string? displayPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = Path.Combine(root, ProjectSettings.FileName);
        if (!File.Exists(file))
            throw new FileNotFoundException($"'{root}' is not a Mainframe project: it has no {ProjectSettings.FileName}.", file);
        if (ProjectRoot is not null && !PathsEqual(ProjectRoot, root) && _tabs.Count > 0)
            throw new InvalidOperationException($"Close the scenes of {ProjectRoot} before opening another project.");
        var settings = ProjectSettings.Load(file);
        ProjectRoot = root;
        Project = settings;
        var database = new AssetDatabase(root);
        database.Refresh(createMissingMeta: true);
        AssetDatabase.Current = database;
        ContentPaths.ProjectDirectory = root;
        Log.Info($"[Editor] Project '{settings.Name}': {displayPath ?? root}");
        ProjectChanged?.Invoke();
    }

    /// <summary>Replaces the project's settings (after the Project Settings dialog saved them).</summary>
    public void SetProjectSettings(ProjectSettings settings)
    {
        Project = settings ?? throw new ArgumentNullException(nameof(settings));
        ProjectChanged?.Invoke();
    }

    /// <summary>
    /// A file or folder moved from <paramref name="oldPath"/> to <paramref name="newPath"/> (FileSystem rename/move): open
    /// scenes inside it follow, so saving them writes the new file; previews follow their document.
    /// </summary>
    public void FilesMoved(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        var from = Path.GetFullPath(oldPath).TrimEnd(Path.DirectorySeparatorChar);
        var to = Path.GetFullPath(newPath).TrimEnd(Path.DirectorySeparatorChar);
        var changed = false;
        foreach (var scene in _scenes)
        {
            if (scene.FilePath is not { } file)
                continue;
            if (PathsEqual(file, from))
                scene.FilePath = to;
            else if (file.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                scene.FilePath = to + file[from.Length..];
            else
                continue;
            changed = true;
        }

        foreach (var tab in _tabs)
        {
            if (tab is SongTab song)
            {
                var songFile = song.Document.FilePath;
                if (PathsEqual(songFile, from))
                    song.Document.Rename(to);
                else if (songFile.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    song.Document.Rename(to + songFile[from.Length..]);
                else
                    continue;
                changed = true;
                continue;
            }

            if (tab is not UiPreview preview)
                continue;
            var file = preview.FilePath;
            if (PathsEqual(file, from))
                preview.MoveTo(to);
            else if (file.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                preview.MoveTo(to + file[from.Length..]);
            else
                continue;
            changed = true;
        }

        if (changed)
            ScenesChanged?.Invoke();
    }

    /// <summary>Closes every tab without asking (the caller dealt with unsaved changes).</summary>
    public void CloseAll()
    {
        foreach (var tab in _tabs.ToArray())
            Close(tab);
    }

    /// <summary>
    /// Opens a UI preview of the <c>.rml</c> document at <paramref name="path"/> (or activates its tab when already open)
    /// and makes it active. Throws when the file does not exist or belongs to another project while tabs of the current
    /// one are open.
    /// </summary>
    public UiPreview OpenUiPreview(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        foreach (var tab in _tabs)
            if (tab is UiPreview open && PathsEqual(open.FilePath, full))
            {
                Activate(open);
                return open;
            }

        if (!File.Exists(full))
            throw new FileNotFoundException($"UI document not found: {full}", full);
        UseProjectOf(full);
        var preview = new UiPreview(full, ++_previewCounter);
        _host.AddChild(preview.Layer);
        preview.Attached();
        if (LastActiveScene is { } backdrop)
            preview.SetBackdrop(backdrop, show: false);
        _tabs.Add(preview);
        ScenesChanged?.Invoke();
        Activate(preview);
        return preview;
    }

    /// <summary>
    /// Shows <paramref name="scene"/> behind <paramref name="preview"/>'s UI (<paramref name="show"/>), or the neutral
    /// backdrop; a null scene turns it off.
    /// </summary>
    public void SetBackdrop(UiPreview preview, EditedScene? scene, bool show)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (scene is not null && !_scenes.Contains(scene))
            throw new ArgumentException("The scene is not open in this session.", nameof(scene));
        preview.SetBackdrop(scene, show);
    }

    /// <summary>
    /// Opens the song (<c>.msong</c>) at <paramref name="path"/> in a song tab (or activates its tab when already open) and
    /// makes it active. A song from a newer editor opens read-only. Throws when the file cannot be read or belongs to
    /// another project while tabs of the current one are open.
    /// </summary>
    public SongTab OpenSong(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        foreach (var tab in _tabs)
            if (tab is SongTab open && PathsEqual(open.Document.FilePath, full))
            {
                Activate(open);
                return open;
            }

        if (!File.Exists(full))
            throw new FileNotFoundException($"Song not found: {full}", full);
        UseProjectOf(full);
        var document = SongDocument.Load(full);
        SongPlayer player;
        try
        {
            player = new SongPlayer(_host.Tree?.Servers.Get<AudioServer>(),
                plugins: static rate => new PluginRack(static () => PluginHostClient.TryCreate(), rate));
        }
        catch
        {
            document.Dispose();
            throw;
        }

        var song = new SongTab(document, player, ++_songCounter);
        _host.AddChild(song.ArrangeViewport);
        _host.AddChild(song.RollViewport);
        song.DirtyChanged += OnSongDirtyChanged;
        _tabs.Add(song);
        ScenesChanged?.Invoke();
        Activate(song);
        return song;
    }

    /// <summary>Saves <paramref name="song"/> (to <paramref name="path"/> for Save As: the tab follows the new file).</summary>
    public void SaveSong(SongTab song, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (path is not null)
        {
            var full = Path.GetFullPath(path);
            if (!full.EndsWith(".msong", StringComparison.OrdinalIgnoreCase))
                full += ".msong";
            if (!PathsEqual(full, song.Document.FilePath))
            {
                song.Document.Song.Uid = AssetUid.Generate(AssetUid.SongPrefix); // a copy is a new asset
                song.Document.Rename(full);
                song.Document.History.MarkUnsaved();
            }
        }

        song.Player.CapturePluginStates(song.Document); // plugin states go into the file
        song.Document.Save();
        song.Saved();
        ScenesChanged?.Invoke();
    }

    private void OnSongDirtyChanged(SongTab song) => ScenesChanged?.Invoke();

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
        foreach (var file in scene.SaveEditedResourceFiles())
            Log.Info($"[Editor] Saved {file}");
        scene.FilePath = target;
        scene.History.MarkSaved();
        ScenesChanged?.Invoke();
    }

    /// <summary>Closes <paramref name="scene"/> without saving (the caller asks first when it is dirty).</summary>
    public void Close(EditedScene scene) => Close((IEditorTab)scene);

    /// <summary>Closes <paramref name="tab"/> without saving (the caller asks first when it is dirty).</summary>
    public void Close(IEditorTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        if (tab is EditedScene scene)
        {
            _scenes.Remove(scene);
            scene.Changed -= OnSceneChanged;
            scene.Selection.Changed -= OnSelectionChanged;
            if (ReferenceEquals(LastActiveScene, scene))
                LastActiveScene = null;
            ForgetBackdrop(scene);
        }

        if (tab is SongTab song)
            song.DirtyChanged -= OnSongDirtyChanged;
        tab.Dispose();
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = null;
            if (_tabs.Count > 0)
                Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
            else
                ActiveChanged?.Invoke();
        }

        ScenesChanged?.Invoke();
    }

    // Previews showing a closed scene behind their UI fall back to the neutral backdrop — unless it is only suspended
    // for a code reload (Resume puts it back).
    private void ForgetBackdrop(EditedScene scene)
    {
        foreach (var tab in _tabs)
        {
            if (tab is not UiPreview preview || !ReferenceEquals(preview.BackdropScene, scene))
                continue;
            if (ReferenceEquals(scene, _suspending))
            {
                preview.PendingBackdrop = _pendingSnapshot; // ShowBackdrop is kept for Resume
                preview.BackdropScene = null;
            }
            else
            {
                preview.SetBackdrop(null, false);
            }
        }
    }

    /// <summary>Makes <paramref name="scene"/> the active tab (its viewport renders; the others pause).</summary>
    public void Activate(EditedScene scene) => Activate((IEditorTab)scene);

    /// <summary>
    /// Makes <paramref name="tab"/> the active tab: a scene's viewport renders and the others pause; a preview's layer is
    /// shown and the others are hidden.
    /// </summary>
    public void Activate(IEditorTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ReferenceEquals(ActiveTab, tab))
            return;
        if (!_tabs.Contains(tab))
            throw new ArgumentException("The tab is not open in this session.", nameof(tab));
        foreach (var t in _tabs)
        {
            var active = ReferenceEquals(t, tab);
            if (t is EditedScene s)
                s.Viewport.UpdateMode = active ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
            else if (t is UiPreview p)
                p.Layer.Visible = active;
            else if (t is SongTab song && !active && ReferenceEquals(t, ActiveTab))
                song.Deactivate();
        }

        ActiveTab = tab;
        if (tab is EditedScene scene)
            LastActiveScene = scene;
        ActiveChanged?.Invoke();
    }

    /// <summary>Main thread, every frame: the previews apply file changes; the active song's player follows its edits.</summary>
    public void Tick()
    {
        foreach (var tab in _tabs)
            if (tab is UiPreview preview)
                preview.Tick();
        if (ActiveTab is SongTab song)
            song.Player.Update(song.Document);
    }

    /// <summary>True when any open tab (scene or song) has unsaved changes.</summary>
    public bool HasUnsavedChanges => _tabs.Any(t => t.IsDirty);

    /// <summary>The tabs with unsaved changes, in tab order.</summary>
    public IReadOnlyList<IEditorTab> DirtyTabs => [.. _tabs.Where(t => t.IsDirty)];

    /// <summary>
    /// Points the asset database and content resolution at the project containing <paramref name="scenePath"/> (see
    /// <see cref="FindProjectRoot"/>) and loads its <c>project.mfproj</c> when there is one. Refuses to switch projects
    /// while scenes of another project are open.
    /// </summary>
    public void UseProjectOf(string scenePath, bool allowSwitch = false)
    {
        var root = FindProjectRoot(scenePath);
        if (ProjectRoot is not null && PathsEqual(ProjectRoot, root))
            return;
        if (ProjectRoot is not null && !allowSwitch && _tabs.Any(t => t.FilePath is not null))
            throw new InvalidOperationException(
                $"'{scenePath}' belongs to the project at {root}, but files of {ProjectRoot} are open. Close them first.");
        ProjectRoot = root;
        Project = LoadProjectSettings(root);
        var database = new AssetDatabase(root);
        database.Refresh();
        AssetDatabase.Current = database;
        ContentPaths.ProjectDirectory = root;
        Log.Info(Project is { } project
            ? $"[Editor] Project '{project.Name}': {root}"
            : $"[Editor] Project folder (no {ProjectSettings.FileName}): {root}");
        ProjectChanged?.Invoke();
    }

    /// <summary>
    /// The project folder of <paramref name="path"/>: the nearest ancestor holding a <c>project.mfproj</c>; else (a
    /// folder without project settings) the folder above the nearest <c>Content/</c> ancestor; else the file's folder.
    /// </summary>
    public static string FindProjectRoot(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        for (var d = directory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, ProjectSettings.FileName)))
                return d;
        for (var d = directory; d is not null; d = Path.GetDirectoryName(d))
            if (string.Equals(Path.GetFileName(d), ContentPaths.FolderName, StringComparison.Ordinal))
                return Path.GetDirectoryName(d) ?? d;
        return directory ?? Path.GetFullPath(".");
    }

    private static ProjectSettings? LoadProjectSettings(string root)
    {
        var file = Path.Combine(root, ProjectSettings.FileName);
        if (!File.Exists(file))
            return null;
        try
        {
            return ProjectSettings.Load(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"[Editor] {e.Message} The project's settings are ignored.");
            return null;
        }
    }

    private EditedScene Attach(Node root, string? filePath, PackedScene? source, int untitled, int index = -1)
    {
        var viewport = new SubViewport
        {
            Name = $"EditedScene{++_viewportCounter}",
            UpdateMode = SubViewportUpdateMode.Disabled,
            Shadows = true, // the editor's main world is empty, so the active tab's view gets the shadow maps
            ClearColor = System.Drawing.Color.FromArgb(255, 48, 52, 61),
        };
        _host.AddChild(viewport);
        viewport.AddChild(root);
        var scene = new EditedScene(root, filePath, viewport, source) { UntitledNumber = untitled };
        // Node2D scenes open in the 2D view (orthographic, pixels, y up); View › 2D switches.
        scene.Camera.Is2D = root is Node2D;
        scene.Changed += OnSceneChanged;
        scene.Selection.Changed += OnSelectionChanged;
        if (index >= 0 && index <= _tabs.Count)
            _tabs.Insert(index, scene);
        else
            _tabs.Add(scene);
        RebuildScenes();
        ScenesChanged?.Invoke();
        return scene;
    }

    private void RebuildScenes()
    {
        _scenes.Clear();
        foreach (var tab in _tabs)
            if (tab is EditedScene scene)
                _scenes.Add(scene);
    }

    // ── Code reload ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes <paramref name="scene"/> out of the session for a code reload: serializes it (unsaved edits included,
    /// game types and missing types with their data), remembers its tab position (among all tabs), file, dirty state,
    /// selection and view, and frees its nodes so nothing references the game assembly any more. <see cref="Resume"/>
    /// puts it back — also behind the previews that showed it. Its undo history cannot survive (the actions point at
    /// the freed nodes).
    /// </summary>
    public SceneSnapshot Suspend(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var index = _tabs.IndexOf(scene);
        if (index < 0)
            throw new ArgumentException("The scene is not open in this session.", nameof(scene));
        var snapshot = CaptureSnapshot(scene, index);
        _suspending = scene;
        _pendingSnapshot = snapshot;
        try
        {
            Close(scene);
        }
        finally
        {
            _suspending = null;
            _pendingSnapshot = null;
        }

        return snapshot;
    }

    private SceneSnapshot? _pendingSnapshot;

    private static SceneSnapshot CaptureSnapshot(EditedScene scene, int index)
    {
        var uid = scene.FilePath is { } path ? AssetDatabase.Current.GetUid(path) : null;
        var selection = new List<string>(scene.Selection.Count);
        foreach (var node in scene.Selection.Nodes)
            if (!node.IsFreed && (ReferenceEquals(node, scene.Root) || scene.Root.IsAncestorOf(node)))
                selection.Add(scene.Root.GetPathTo(node).Path);
        var camera = new EditorCamera();
        camera.CopyFrom(scene.Camera);
        return new SceneSnapshot(SceneSaver.ToJson(scene.Root, uid), scene.FilePath, scene.IsDirty, scene.UntitledNumber, index,
            selection, camera);
    }

    /// <summary>
    /// Re-creates a scene taken out by <see cref="Suspend"/> with the types loaded now (a type the new build lacks
    /// loads as <see cref="MissingNode"/>, keeping its data): same tab position, file, selection and view; dirty when it
    /// was. Activate it with <see cref="Activate"/> when it was the active tab.
    /// </summary>
    public EditedScene Resume(SceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PackedScene? source = null;
        Node root;
        if (snapshot.FilePath is { } file && !snapshot.Dirty && File.Exists(file))
        {
            // Unchanged on disk: load it like Open, so the loader's references are released with the tab.
            source = ResourceLoader.Load<PackedScene>(AssetDatabase.Current.ToProjectPath(file));
            try
            {
                root = source.Instantiate();
            }
            catch
            {
                source.Release();
                throw;
            }
        }
        else
        {
            root = PackedScene.Parse(snapshot.Json, snapshot.FilePath).Instantiate();
        }

        var scene = Attach(root, snapshot.FilePath, source, snapshot.UntitledNumber, snapshot.Index);
        scene.Camera.CopyFrom(snapshot.Camera);
        foreach (var path in snapshot.Selection)
            if (root.GetNodeOrNull(path) is { } node)
                scene.Selection.Add(node);
        if (snapshot.Dirty)
            scene.History.MarkUnsaved();
        foreach (var tab in _tabs)
            if (tab is UiPreview preview && ReferenceEquals(preview.PendingBackdrop, snapshot))
            {
                preview.PendingBackdrop = null;
                preview.SetBackdrop(scene, preview.ShowBackdrop);
            }

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

    /// <summary>
    /// How to show <paramref name="path"/> to the user: relative to the project (<c>Content/Scenes/Main.mscene</c>, with
    /// <c>/</c>) when it is inside <see cref="ProjectRoot"/>, else the full path.
    /// </summary>
    public string DisplayPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        var full = Path.GetFullPath(path);
        if (ProjectRoot is not { } root)
            return full;
        var relative = Path.GetRelativePath(root, full);
        return relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               Path.IsPathRooted(relative)
            ? full
            : relative.Replace('\\', '/');
    }

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var tab in _tabs.ToArray())
        {
            if (tab is EditedScene scene)
            {
                scene.Changed -= OnSceneChanged;
                scene.Selection.Changed -= OnSelectionChanged;
            }
            else if (tab is SongTab song)
            {
                song.DirtyChanged -= OnSongDirtyChanged;
            }

            tab.Dispose();
        }

        _tabs.Clear();
        _scenes.Clear();
        ActiveTab = null;
        LastActiveScene = null;
    }
}

/// <summary>An open scene taken out of the session for a code reload (<see cref="EditorSession.Suspend"/>).</summary>
public sealed record SceneSnapshot(
    byte[] Json,
    string? FilePath,
    bool Dirty,
    int UntitledNumber,
    int Index,
    IReadOnlyList<string> Selection,
    EditorCamera Camera);
