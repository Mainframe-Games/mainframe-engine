using System.Globalization;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>How the FileSystem panel shows the project.</summary>
public enum FileSystemView
{
    /// <summary>The whole project as a tree.</summary>
    Tree,

    /// <summary>The current folder's files as rows.</summary>
    List,

    /// <summary>The current folder as tiles with thumbnails.</summary>
    Grid,
}

/// <summary>
/// The FileSystem dock (E4, Godot's FileSystem): the open project (<see cref="ProjectFileSystem"/>) as a tree, or the
/// current folder as a list or a thumbnail grid. The Simplified view (the default; <see cref="Simplified"/>) shows two
/// roots instead of the project folder: Assets (<c>Assets/</c>) and Scripts (the game library's <c>Src/</c>, C# files
/// only). Icons per file kind (scenes tinted by their root's family, resources by
/// type, image thumbnails generated in the background into <c>.mainframe/cache</c>), status badges (unsaved, missing
/// dependency, import error), <c>.meta</c> hidden by default. Create folders/scenes/resources, rename and move with
/// reference fix-ups by UID, delete to the OS trash (a confirmation before any permanent delete). Double click opens
/// scenes (and C# files in the code editor); drag a file onto a folder (move), the scene tree (instance a scene or
/// assign a resource), the viewport (instance under the root) or an inspector resource slot. A watcher refreshes it.
/// </summary>
public sealed class FileSystemPanel : EditorDocument
{
    private const float DragThreshold = 4f;

    private sealed class Row
    {
        public required ProjectFileEntry Entry { get; init; }
        public required string Name { get; init; }
        public required bool HasChildren { get; init; }
        public required int Index { get; init; }
        public required string Icon { get; init; }
        public required string Indent { get; init; }
        public required string Thumb { get; init; }
        public required bool Expanded { get; init; }
        public required string Badge { get; init; }
        public bool Selected { get; set; }
        public bool DropTarget { get; set; }
    }

    private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
        .Member("name", static r => r.Name)
        .Member("icon", static r => r.Icon)
        .Member("indent", static r => r.Indent)
        .Member("index", static r => r.Index)
        .Member("dir", static r => r.Entry.IsDirectory)
        .Member("has_children", static r => r.HasChildren)
        .Member("expanded", static r => r.Expanded)
        .Member("selected", static r => r.Selected)
        .Member("drop", static r => r.DropTarget)
        .Member("thumb", static r => r.Thumb)
        .Member("badge", static r => r.Badge)
        .Member("tip", static r => r.Entry.BadgeText ?? r.Entry.ProjectPath);

    private readonly List<Row> _rows = [];
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private RmlDataModel? _model;
    private ProjectFileSystem? _fs;
    private FileOperations? _operations;
    private ThumbnailCache? _thumbnails;
    private int _shownVersion = -1;
    private string? _selected;
    private string? _folder;
    private string _folderText = "";
    private int _dragRow = -1;
    private float _dragStartY;
    private float _dragStartX;
    private bool _thumbsDirty;
    private string? _assetsRoot;
    private string? _scriptsRoot;
    private readonly HashSet<string> _scriptFolders = new(StringComparer.Ordinal);
    private int _scriptFoldersVersion = -1;

    /// <summary>The Simplified view's root names.</summary>
    public const string AssetsRootName = "Assets", ScriptsRootName = "Scripts";

    public FileSystemPanel(EditorWorkspace workspace)
        : base(workspace, "filesystem.rml")
    {
        Simplified = !workspace.Layout.Settings.FileSystemVerbose;
    }

    /// <summary>The project's file tree (null without a project).</summary>
    public ProjectFileSystem? Files => _fs;

    /// <summary>File operations of the open project (null without one).</summary>
    public FileOperations? Operations => _operations;

    /// <summary>The trash deletes go to (tests substitute a fake).</summary>
    public ITrash Trash
    {
        get;
        set
        {
            field = value ?? throw new ArgumentNullException(nameof(value));
            if (_fs is not null)
                _operations = new FileOperations(_fs.ProjectRoot, AssetDatabase.Current, field);
        }
    } = SystemTrash.Default;

    public FileSystemView View { get; private set; } = FileSystemView.Tree;

    /// <summary>
    /// The Simplified view: the roots Assets and Scripts instead of the project folder (saved in the layout settings).
    /// In list and grid views the current folder starts at the two roots (<see cref="CurrentFolder"/> null). A folder
    /// with neither (no <c>Assets/</c>, no game library) shows the whole folder.
    /// </summary>
    public bool Simplified { get; private set; }

    /// <summary>The Simplified view's Assets root (absolute), or null when the project has no <c>Assets/</c>.</summary>
    public string? AssetsRoot => _assetsRoot;

    /// <summary>The Simplified view's Scripts root (absolute): the game library's <c>Src/</c>, else its folder; null without one.</summary>
    public string? ScriptsRoot => _scriptsRoot;

    /// <summary>The selected file or folder (absolute path), or null.</summary>
    public string? SelectedPath => _selected;

    /// <summary>The folder the list and grid views show (absolute path).</summary>
    public string? CurrentFolder => _folder;

    /// <summary>The rows on screen (entries), for tests and QA.</summary>
    public IReadOnlyList<ProjectFileEntry> Rows => _rows.Select(r => r.Entry).ToArray();

    /// <summary>The rows' shown names (the Simplified roots say Assets and Scripts), for tests and QA.</summary>
    public IReadOnlyList<string> RowNames => _rows.Select(r => r.Name).ToArray();

    /// <summary>A file being dragged out of the panel (absolute path) once the mouse moved past the threshold.</summary>
    public string? DraggedFile { get; private set; }

    protected override void OnReady()
    {
        _model = CreateDataModel("filesystem")
            .Bind("has_project", this, static p => p._fs is not null)
            .Bind("view", this, static p => (int)p.View)
            .Bind("simplified", this, static p => p.Simplified)
            .Bind("folder", this, static p => p._folderText)
            .Bind("show_meta", this, static p => p._fs?.ShowMeta == true)
            .BindList("rows", _rows, RowType)
            .Event("press", e => Press(e.GetArgument(0).GetInt32(), e.Event.GetParameter("button", 0),
                (float)e.Event.GetParameter("mouse_x", 0.0), (float)e.Event.GetParameter("mouse_y", 0.0)))
            .Event("hover", e => Hover((float)e.Event.GetParameter("mouse_x", 0.0), (float)e.Event.GetParameter("mouse_y", 0.0)))
            .Event("release", e => Release(e.GetArgument(0).GetInt32()))
            .Event("activate", e => Activate(e.GetArgument(0).GetInt32()))
            .Event("toggle", e =>
            {
                Toggle(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("up", _ => GoUp())
            .Event("set_view", e => SetView((FileSystemView)e.GetArgument(0).GetInt32()))
            .Event("toggle_meta", _ => ToggleMeta())
            .Event("toggle_simplified", _ => SetSimplified(!Simplified));
        Refresh();
    }

    protected override void OnAttach(RmlDocument document)
    {
        _shownVersion = -1;
        document.AsElement().AddEventListener("mouseup", _ => EndDrag());
        Refresh();
    }

    // ── Project ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Points the panel at the session's project (after a project opened or closed).</summary>
    public void OnProjectChanged()
    {
        var root = Workspace.Session.ProjectRoot;
        if (_fs is not null && root is not null && EditorSession.PathsEqual(_fs.ProjectRoot, root))
            return;
        _fs?.Dispose();
        _thumbnails?.Dispose();
        _fs = null;
        _operations = null;
        _thumbnails = null;
        _expanded.Clear();
        _selected = null;
        _folder = null;
        _assetsRoot = null;
        _scriptsRoot = null;
        _scriptFolders.Clear();
        _scriptFoldersVersion = -1;
        if (root is not null)
        {
            _fs = new ProjectFileSystem(root, AssetDatabase.Current);
            _operations = new FileOperations(root, AssetDatabase.Current, Trash);
            _thumbnails = new ThumbnailCache(Path.Combine(root, ".mainframe", "cache", "thumbnails"));
            _thumbnails.Ready += (_, _) => _thumbsDirty = true;
            _thumbnails.Failed += (source, message) => _fs?.ReportImportError(source, message);
            try
            {
                _fs.StartWatching();
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                Log.Warning($"[Editor] The FileSystem panel cannot watch {root} ({e.Message}); it refreshes after editor operations only.");
            }

            _expanded.Add(_fs.Root.FullPath);
            var content = Path.Combine(root, ContentPaths.FolderName);
            if (Directory.Exists(content))
            {
                _expanded.Add(content);
                _folder = content;
                _assetsRoot = content;
            }
            else
            {
                _folder = Simplified ? null : root;
            }

            _scriptsRoot = ScriptsFolderOf(root);
            if (_scriptsRoot is not null)
                _expanded.Add(_scriptsRoot);
        }

        _shownVersion = -1;
        UpdateUnsaved();
        Refresh();
    }

    // The game library's Src/ (else its folder), spelled under root: the layout returns real paths, the tree may not.
    private string? ScriptsFolderOf(string root)
    {
        if (Workspace.Session.Project is not { } settings || GameProjectLayout.GameLibraryProjectOf(root, settings) is not { } csproj ||
            Path.GetDirectoryName(csproj) is not { } library)
            return null;
        var relative = Path.GetRelativePath(GameProjectLayout.RealPath(root), library);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;
        var folder = Path.GetFullPath(Path.Combine(root, relative));
        var src = Path.Combine(folder, "Src");
        return Directory.Exists(src) ? src : folder;
    }

    /// <summary>Main thread, every frame: applies watcher changes and finished thumbnails.</summary>
    public void Tick()
    {
        if (_fs is null)
            return;
        if (_fs.PumpChanges())
        {
            // New files get their .meta sidecar (UID) like on project open.
            AssetDatabase.Current.Scan(createMissingMeta: true);
        }

        _thumbnails?.Pump();
        if (_thumbsDirty)
        {
            _thumbsDirty = false;
            _shownVersion = -1;
        }

        Refresh();
    }

    /// <summary>The open scenes, songs and video projects with unsaved changes get the unsaved badge.</summary>
    public void UpdateUnsaved()
    {
        if (_fs is null)
            return;
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in Workspace.Session.Tabs)
            if (tab is EditedScene or Music.SongTab or Video.VideoTab && tab.IsDirty && tab.FilePath is { } path)
                dirty.Add(path);
        _fs.SetUnsaved(dirty);
    }

    /// <summary>Rescans the project now (after an operation).</summary>
    public void Rescan()
    {
        _fs?.Refresh();
        Refresh();
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the rows when the tree, the view, the folder, the expansion or the selection changed.</summary>
    public void Refresh()
    {
        var version = _fs?.Version ?? -2;
        if (version == _shownVersion && _model is not null)
            return;
        _shownVersion = version;
        _rows.Clear();
        if (_fs is not null && Simplified && SimplifiedRoots().Any())
        {
            UpdateScriptFolders();
            if (View == FileSystemView.Tree)
            {
                foreach (var (root, name) in SimplifiedRoots())
                    AddTree(root, name, 0);
            }
            else if (_folder is not null && RootOf(_folder) is not null && _fs.Find(_folder) is { IsDirectory: true } folder)
            {
                foreach (var entry in VisibleChildren(folder))
                    AddRow(entry, 0);
            }
            else
            {
                foreach (var (root, name) in SimplifiedRoots())
                    AddRow(root, 0, name);
            }

            _folderText = _folder is not null && RootOf(_folder) is { } top ? SimplifiedPath(_folder, top) : "";
        }
        else if (_fs is not null)
        {
            if (View == FileSystemView.Tree)
            {
                foreach (var entry in _fs.Flatten(e => _expanded.Contains(e.FullPath)))
                    AddRow(entry, entry.Depth);
            }
            else if (_fs.Find(_folder ?? _fs.ProjectRoot) is { IsDirectory: true } folder)
            {
                foreach (var entry in _fs.ListFolder(folder))
                    AddRow(entry, 0);
            }

            _folderText = _folder is null ? "" : _fs.Find(_folder) is { } current && current.ProjectPath.Length > 0 ? current.ProjectPath : Path.GetFileName(_fs.ProjectRoot);
        }

        _model?.DirtyAll();
    }

    // ── Simplified view ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Switches between the Simplified view (Assets and Scripts) and the whole project folder; saved.</summary>
    public void SetSimplified(bool simplified)
    {
        if (Simplified == simplified)
            return;
        Simplified = simplified;
        Workspace.Layout.SetFileSystemVerbose(!simplified);
        if (simplified && _folder is not null && RootOf(_folder) is null)
            _folder = null; // list and grid start at the two roots
        else if (!simplified && _folder is null)
            _folder = _assetsRoot ?? _fs?.ProjectRoot;
        _shownVersion = -1;
        Refresh();
    }

    private IEnumerable<(ProjectFileEntry Entry, string Name)> SimplifiedRoots()
    {
        if (_assetsRoot is not null && _fs?.Find(_assetsRoot) is { IsDirectory: true } assets)
            yield return (assets, AssetsRootName);
        if (_scriptsRoot is not null && _fs?.Find(_scriptsRoot) is { IsDirectory: true } scripts)
            yield return (scripts, ScriptsRootName);
    }

    private void AddTree(ProjectFileEntry entry, string? name, int depth)
    {
        AddRow(entry, depth, name);
        if (!entry.IsDirectory || !_expanded.Contains(entry.FullPath))
            return;
        foreach (var child in VisibleChildren(entry))
            AddTree(child, null, depth + 1);
    }

    // Under Scripts only C# files and the folders holding one; elsewhere every (visible) child.
    private IEnumerable<ProjectFileEntry> VisibleChildren(ProjectFileEntry folder) =>
        Simplified && IsUnder(folder.FullPath, _scriptsRoot)
            ? folder.Children.Where(c => c.IsDirectory ? _scriptFolders.Contains(c.FullPath) : c.Kind == FileKind.Script)
            : folder.Children;

    private void UpdateScriptFolders()
    {
        if (_fs is null || _scriptFoldersVersion == _fs.Version)
            return;
        _scriptFoldersVersion = _fs.Version;
        _scriptFolders.Clear();
        if (_scriptsRoot is not null && _fs.Find(_scriptsRoot) is { IsDirectory: true } root)
            CollectScriptFolders(root);
    }

    private bool CollectScriptFolders(ProjectFileEntry folder)
    {
        var any = false;
        foreach (var child in folder.Children)
            any |= child.IsDirectory ? CollectScriptFolders(child) : child.Kind == FileKind.Script;
        if (any)
            _scriptFolders.Add(folder.FullPath);
        return any;
    }

    // The Simplified root holding path (absolute), or null.
    private string? RootOf(string path) =>
        IsUnder(path, _assetsRoot) ? _assetsRoot : IsUnder(path, _scriptsRoot) ? _scriptsRoot : null;

    private string SimplifiedPath(string path, string root) =>
        (EditorSession.PathsEqual(root, _assetsRoot ?? "") ? AssetsRootName : ScriptsRootName) +
        Path.GetFullPath(path)[Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length..].Replace('\\', '/');

    private static bool IsUnder(string path, string? root)
    {
        if (root is null)
            return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return EditorSession.PathsEqual(full, top) || full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>The project folder, and in the Simplified view also its two roots: never renamed, moved, trashed or dragged.</summary>
    public bool IsRoot(ProjectFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Depth == 0 || (Simplified && SimplifiedRoots().Any(r => ReferenceEquals(r.Entry, entry)));
    }

    /// <summary>
    /// Whether a drag of <paramref name="path"/> may drop into <paramref name="folder"/>: in the Simplified view C# files
    /// stay out of Assets, and only C# files and folders go into Scripts.
    /// </summary>
    public bool CanDrop(string path, string folder)
    {
        if (!Simplified)
            return true;
        var script = !Directory.Exists(path) && string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);
        if (IsUnder(folder, _scriptsRoot))
            return script || Directory.Exists(path);
        return !(IsUnder(folder, _assetsRoot) && script);
    }

    private void AddRow(ProjectFileEntry entry, int depth, string? name = null)
    {
        var thumb = "";
        if (View == FileSystemView.Grid && entry.Kind == FileKind.Texture && _thumbnails is not null &&
            _thumbnails.TryGet(entry.FullPath, out var path) && path is not null)
            thumb = path;
        var family = entry.Family is { } f ? " " + f : "";
        var icon = name == ScriptsRootName ? "folder-code"
            : entry.IsDirectory && _expanded.Contains(entry.FullPath) && View == FileSystemView.Tree ? "folder-open" : entry.Icon;
        _rows.Add(new Row
        {
            Entry = entry,
            Name = name ?? (entry.Name.Length == 0 ? "Project" : entry.Name),
            HasChildren = entry.IsDirectory && (Simplified ? VisibleChildren(entry).Any() : entry.Children.Count > 0),
            Index = _rows.Count,
            Icon = $"icon icon-{icon}{family}{(View == FileSystemView.Grid ? " icon-lg" : "")}",
            Indent = RmlText.Dp(4 + depth * 14),
            Thumb = thumb,
            Expanded = _expanded.Contains(entry.FullPath),
            Badge = Workspace.AudioPreview.IsPlayingFile(entry.FullPath) ? PlayingBadge : BadgeClass(entry.Badges),
            Selected = string.Equals(entry.FullPath, _selected, StringComparison.Ordinal),
        });
    }

    private const string PlayingBadge = "icon icon-sm icon-player-stop badge-playing";

    /// <summary>The preview started or stopped: the playing file's badge follows (rows are rebuilt).</summary>
    public void RefreshPreviewBadges()
    {
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>True for audio files and <c>.mres</c> files whose root type is an <see cref="AudioStream"/> (Play / Stop).</summary>
    public static bool IsPlayable(ProjectFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == FileKind.Audio ||
               (entry.Kind == FileKind.Resource && entry.RootTypeName is { } type &&
                Serialization.TypeRegistry.Get(type)?.Type is { } resourceType && typeof(AudioStream).IsAssignableFrom(resourceType));
    }

    private static string BadgeClass(FileBadges badges) =>
        (badges & FileBadges.ImportError) != 0 ? "icon icon-sm icon-circle-x badge-error"
        : (badges & FileBadges.MissingDependency) != 0 ? "icon icon-sm icon-alert-triangle badge-warn"
        : (badges & FileBadges.Unsaved) != 0 ? "icon icon-sm icon-point-filled badge-unsaved"
        : (badges & (FileBadges.RenderOutOfDate | FileBadges.BakeOutOfDate)) != 0 ? "icon icon-sm icon-refresh badge-warn"
        : (badges & FileBadges.Generated) != 0 ? "icon icon-sm icon-wand badge-generated"
        : "";

    private ProjectFileEntry? EntryAt(int index) => (uint)index < (uint)_rows.Count ? _rows[index].Entry : null;

    // ── Interaction ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Selects <paramref name="path"/> (absolute), revealing it in the tree.</summary>
    public void Select(string? path)
    {
        _selected = path is null ? null : Path.GetFullPath(path);
        if (_selected is not null && View == FileSystemView.Tree)
            for (var dir = Path.GetDirectoryName(_selected); dir is not null && _fs is not null && dir.Length >= _fs.ProjectRoot.Length; dir = Path.GetDirectoryName(dir))
                _expanded.Add(dir);
        _shownVersion = -1;
        Refresh();
    }

    private void Press(int index, int button, float x, float y)
    {
        if (EntryAt(index) is not { } entry)
            return;
        Select(entry.FullPath);
        if (button == 1)
        {
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            ShowContextMenu(entry, x / scale, y / scale);
            return;
        }

        if (button != 0 || IsRoot(entry))
            return;
        _dragRow = index;
        _dragStartX = x;
        _dragStartY = y;
    }

    private void Hover(float x, float y)
    {
        if (_dragRow < 0 || DraggedFile is not null)
            return;
        var threshold = DragThreshold * MathF.Max(1f, Workspace.Host.PixelScale);
        if (MathF.Abs(x - _dragStartX) < threshold && MathF.Abs(y - _dragStartY) < threshold)
            return;
        if (EntryAt(_dragRow) is { } entry)
            BeginDrag(entry.FullPath);
    }

    /// <summary>Starts dragging <paramref name="path"/> (tests and QA call it directly).</summary>
    public void BeginDrag(string path)
    {
        DraggedFile = Path.GetFullPath(path);
        Workspace.BeginFileDrag(DraggedFile);
    }

    private void Release(int index)
    {
        var dragged = DraggedFile;
        EndDrag();
        if (dragged is null || EntryAt(index) is not { IsDirectory: true } folder || string.Equals(folder.FullPath, dragged, StringComparison.Ordinal))
            return;
        if (CanDrop(dragged, folder.FullPath))
            Move(dragged, folder.FullPath);
        else
            Log.Warning($"[Editor] {Path.GetFileName(dragged)} cannot go into {_rows[index].Name}: C# files belong in Scripts, other files in Assets.");
    }

    private void EndDrag()
    {
        _dragRow = -1;
        DraggedFile = null;
    }

    /// <summary>
    /// Double click: folders open (tree: expand), scenes open in a tab, UI documents (<c>.rml</c>) in a live preview tab,
    /// C# files and style sheets in the code editor.
    /// </summary>
    public void Activate(int index)
    {
        if (EntryAt(index) is not { } entry)
            return;
        Open(entry);
    }

    /// <summary>Opens <paramref name="entry"/> the way a double click does.</summary>
    public void Open(ProjectFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            if (View == FileSystemView.Tree)
            {
                if (!_expanded.Remove(entry.FullPath))
                    _expanded.Add(entry.FullPath);
            }
            else
            {
                _folder = entry.FullPath;
            }

            _shownVersion = -1;
            Refresh();
            return;
        }

        switch (entry.Kind)
        {
            case FileKind.Scene:
                Workspace.RunWithSplash($"Loading {entry.Name}…", () =>
                {
                    try
                    {
                        Workspace.Session.Open(entry.FullPath);
                    }
                    catch (Exception e) when (EditorCommands.IsRecoverable(e))
                    {
                        Workspace.Commands.ReportError($"Could not open {entry.Name}", e);
                    }
                });
                break;
            case FileKind.Song:
                Workspace.Commands.OpenSong(entry.FullPath);
                break;
            case FileKind.Video or FileKind.VideoProject:
                Workspace.Commands.OpenVideo(entry.FullPath);
                break;
            case FileKind.Rml:
                try
                {
                    Workspace.Session.OpenUiPreview(entry.FullPath);
                }
                catch (Exception e) when (EditorCommands.IsRecoverable(e))
                {
                    Workspace.Commands.ReportError($"Could not preview {entry.Name}", e);
                }

                break;
            case FileKind.Script or FileKind.Shader or FileKind.Rcss or FileKind.Translation or FileKind.Data or FileKind.Project:
                Workspace.CodeEditor.Open(entry.FullPath);
                break;
            case FileKind.Resource:
                Workspace.Inspector.InspectResourceFile(entry.FullPath);
                break;
            case FileKind.Audio:
                Workspace.AudioPreview.ToggleFile(entry.FullPath);
                break;
            default:
                Log.Info($"[Editor] {entry.ProjectPath}: {entry.Kind} ({entry.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes{(entry.Uid is { } uid ? ", " + uid : "")}).");
                break;
        }
    }

    /// <summary>Expands or collapses tree row <paramref name="index"/>.</summary>
    public void Toggle(int index)
    {
        if (EntryAt(index) is not { IsDirectory: true } entry)
            return;
        if (!_expanded.Remove(entry.FullPath))
            _expanded.Add(entry.FullPath);
        _shownVersion = -1;
        Refresh();
    }

    public void SetView(FileSystemView view)
    {
        if (View == view)
            return;
        View = view;
        if (view != FileSystemView.Tree && _selected is not null && _fs?.Find(_selected) is { } selected)
        {
            _folder = selected.IsDirectory ? selected.FullPath : Path.GetDirectoryName(selected.FullPath);
            if (Simplified && _folder is not null && RootOf(_folder) is null)
                _folder = null;
        }

        _shownVersion = -1;
        Refresh();
    }

    /// <summary>List and grid views: the parent folder (the Simplified view's roots above Assets and Scripts).</summary>
    public void GoUp()
    {
        if (_fs is null || _folder is null || EditorSession.PathsEqual(_folder, _fs.ProjectRoot))
            return;
        _folder = Simplified && RootOf(_folder) is { } root && EditorSession.PathsEqual(_folder, root) ? null : Path.GetDirectoryName(_folder);
        _shownVersion = -1;
        Refresh();
    }

    private void ToggleMeta()
    {
        if (_fs is null)
            return;
        _fs.ShowMeta = !_fs.ShowMeta;
        _shownVersion = -1;
        Refresh();
    }

    /// <summary>The folder new files go into: the selected folder, the selected file's folder, or the current folder.</summary>
    public string? TargetFolder()
    {
        if (_fs is null)
            return null;
        if (_selected is not null && _fs.Find(_selected) is { } entry)
            return entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath);
        return _folder ?? (Simplified ? _assetsRoot : null) ?? _fs.ProjectRoot;
    }

    /// <summary>The folder new scenes, resources and songs go into: <see cref="TargetFolder"/>, but Assets instead of Scripts.</summary>
    public string? AssetTargetFolder()
    {
        var folder = TargetFolder();
        return Simplified && folder is not null && IsUnder(folder, _scriptsRoot) ? _assetsRoot ?? folder : folder;
    }

    // ── Commands (context menu, header buttons) ──────────────────────────────────────────────────────────────────

    private void ShowContextMenu(ProjectFileEntry entry, float x, float y)
    {
        var isRoot = IsRoot(entry);
        Workspace.Popup.Show(
        [
            new MenuItem(entry.Kind == FileKind.Rml ? "Preview UI" : "Open", "fs.open", "Double-click", !entry.IsDirectory,
                Icon: entry.Kind == FileKind.Rml ? "layout" : "external-link"),
            .. entry.Kind == FileKind.Rml ? [new MenuItem("Open in Code Editor", "fs.open_code", Icon: "code")] : Array.Empty<MenuItem>(),
            .. entry.Kind == FileKind.Song
                ? [new MenuItem(Workspace.SongView.RenderOf(entry.FullPath) is null ? "Render" : "Cancel Render", "fs.render", Icon: "download")]
                : Array.Empty<MenuItem>(),
            .. IsPlayable(entry)
                ? [new MenuItem("Play", "fs.play", Icon: "player-play"),
                   new MenuItem("Stop", "fs.stop", null, Workspace.AudioPreview.IsPlayingFile(entry.FullPath), Icon: "player-stop")]
                : Array.Empty<MenuItem>(),
            MenuItem.Separator,
            new MenuItem("New Folder…", "fs.new_folder", Icon: "folder-plus"),
            new MenuItem("New Scene…", "fs.new_scene", Icon: "movie"),
            new MenuItem("New 2D Scene…", "fs.new_scene_2d", Icon: "square"),
            new MenuItem("New Resource…", "fs.new_resource", Icon: "file-plus"),
            new MenuItem("New Song…", "fs.new_song", Icon: "music"),
            .. entry is { IsDirectory: false, Kind: FileKind.Texture }
                ? [new MenuItem("New SpriteFrames from Sheet…", "fs.new_sprite_frames", Icon: "library-photo")]
                : Array.Empty<MenuItem>(),
            MenuItem.Separator,
            new MenuItem("Rename…", "fs.rename", "F2", !isRoot, Icon: "pencil"),
            new MenuItem("Move To…", "fs.move", null, !isRoot, Icon: "folder-open"),
            new MenuItem("Move to Trash", "fs.delete", "Del", !isRoot, Icon: "trash"),
            MenuItem.Separator,
            new MenuItem("Copy Path", "fs.copy_path", Icon: "copy"),
            new MenuItem("Copy UID", "fs.copy_uid", null, entry.Uid is not null, Icon: "copy"),
            new MenuItem(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in File Manager", "fs.reveal", Icon: "folder"),
        ], x, y, command => Workspace.Commands.Execute(command));
    }

    /// <summary>Runs a FileSystem command (<c>fs.*</c>) on the selection; false when it is not one.</summary>
    public bool RunCommand(string command)
    {
        var entry = _selected is not null ? _fs?.Find(_selected) : null;
        switch (command)
        {
            case "fs.open":
                if (entry is not null)
                    Open(entry);
                return true;
            case "fs.play":
                if (entry is { IsDirectory: false } && IsPlayable(entry))
                    Workspace.AudioPreview.PlayFile(entry.FullPath);
                return true;
            case "fs.stop":
                Workspace.AudioPreview.Stop();
                return true;
            case "fs.open_code":
                if (entry is { IsDirectory: false })
                    Workspace.CodeEditor.Open(entry.FullPath);
                return true;
            case "fs.new_folder":
                PromptName("New Folder", "Folder name:", "NewFolder", name => Report(_operations?.CreateFolder(TargetFolder()!, name)));
                return true;
            case "fs.new_scene":
            case "fs.new_scene_2d":
                var rootType = command == "fs.new_scene_2d" ? "Node2D" : "Node3D";
                PromptName("New Scene", "Scene name:", "NewScene", name =>
                {
                    var result = _operations?.CreateScene(AssetTargetFolder()!, name, rootType);
                    if (Report(result) && result?.Path is { } path)
                        Workspace.Session.Open(path);
                });
                return true;
            case "fs.new_resource":
                NewResource();
                return true;
            case "fs.new_sprite_frames":
                if (entry is { IsDirectory: false, Kind: FileKind.Texture })
                    NewSpriteFramesFromSheet(entry);
                return true;
            case "fs.new_song":
                PromptName("New Song", "Song name:", "NewSong", name => Workspace.Commands.CreateSong(Path.Combine(AssetTargetFolder()!, name)));
                return true;
            case "fs.render":
                if (entry is { Kind: FileKind.Song })
                    Workspace.SongView.Render(entry.FullPath);
                return true;
            case "fs.rename":
                if (entry is not null && !IsRoot(entry))
                    PromptName("Rename", $"New name for {entry.Name}:", entry.Name, name => Rename(entry.FullPath, name));
                return true;
            case "fs.move":
                if (entry is not null && !IsRoot(entry) && _fs is not null)
                {
                    var model = new FilePickerModel(FilePickerMode.Folder, _fs.ProjectRoot, []);
                    Workspace.FilePicker.Show(model, $"Move {entry.Name} To", "Move", folder => Move(entry.FullPath, folder));
                }

                return true;
            case "fs.delete":
                if (entry is not null && !IsRoot(entry))
                    Delete(entry.FullPath);
                return true;
            case "fs.copy_path":
                if (entry is not null)
                    Clipboard(entry.ProjectPath);
                return true;
            case "fs.copy_uid":
                if (entry?.Uid is { } uid)
                    Clipboard(uid);
                return true;
            case "fs.reveal":
                if (entry is not null)
                    OsShell.Reveal(entry.FullPath);
                return true;
            case "fs.view_tree":
                SetView(FileSystemView.Tree);
                return true;
            case "fs.view_list":
                SetView(FileSystemView.List);
                return true;
            case "fs.view_grid":
                SetView(FileSystemView.Grid);
                return true;
            case "fs.toggle_meta":
                ToggleMeta();
                return true;
            case "fs.toggle_simplified":
                SetSimplified(!Simplified);
                return true;
            case "fs.refresh":
                Rescan();
                return true;
            default:
                return false;
        }
    }

    private void PromptName(string title, string message, string initial, Action<string> then) => Workspace.Message.Show(new MessageRequest
    {
        Title = title,
        Message = message,
        Input = initial,
        Buttons = ["OK", "Cancel"],
        DefaultButton = 0,
        CancelButton = 1,
        Callback = (button, text) =>
        {
            if (button == 0 && !string.IsNullOrWhiteSpace(text))
                then(text.Trim());
        },
    });

    private void NewResource()
    {
        var items = TypeRegistry.All
            .Where(t => t.IsResource && !t.IsAbstract && t.Type != typeof(PackedScene) && t.Type != typeof(MissingResource))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ListPickerItem(t.Name, t.Base?.Name ?? "", "icon-" + EditorIcons.For(t.Type), t))
            .ToArray();
        Workspace.ListPicker.Show("New Resource", items, "Create", payload =>
        {
            var info = (NodeTypeInfo)payload;
            PromptName("New Resource", $"File name for the new {info.Name}:", "New" + info.Name, name =>
            {
                var result = _operations?.CreateResource(AssetTargetFolder()!, name, info.Type);
                if (Report(result) && result?.Path is { } path)
                    Select(path);
            });
        });
    }

    /// <summary>
    /// "New SpriteFrames from Sheet…" (ADR 0224): slices the image in the Select Frames dialog and writes a
    /// <c>.mres</c> beside it with one animation per picked row, then opens it in the inspector.
    /// </summary>
    public void NewSpriteFramesFromSheet(ProjectFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Texture2D sheet;
        try
        {
            sheet = ResourceLoader.Load<Texture2D>(entry.ProjectPath);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError($"Could not load {entry.Name}", e);
            return;
        }

        Workspace.SheetSlicer.Show(entry.ProjectPath, new Vector2I(sheet.Width, sheet.Height), "New SpriteFrames — " + entry.Name, (grid, cells) =>
        {
            try
            {
                var path = CreateSpriteFrames(entry.FullPath, sheet, grid, cells);
                Rescan();
                Select(path);
                Workspace.Inspector.InspectResourceFile(path);
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Workspace.Commands.ReportError($"Could not create SpriteFrames for {entry.Name}", e);
            }
        });
    }

    /// <summary>Writes the SpriteFrames of <paramref name="cells"/> next to <paramref name="imagePath"/> (a free name); returns its path.</summary>
    internal static string CreateSpriteFrames(string imagePath, Texture2D sheet, SpriteSheetGrid grid, IReadOnlyList<Vector2I> cells)
    {
        var folder = Path.GetDirectoryName(imagePath)!;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var path = Path.Combine(folder, stem + ".mres");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} {i.ToString(CultureInfo.InvariantCulture)}.mres");
        ResourceSaver.Save(SpriteFramesEditing.FromSheet(sheet, grid, cells), AssetDatabase.Current.ToProjectPath(path));
        return path;
    }

    /// <summary>Renames <paramref name="path"/> to <paramref name="newName"/> (references fixed by UID); open scenes follow.</summary>
    public bool Rename(string path, string newName)
    {
        var result = _operations?.Rename(path, newName);
        if (!Report(result))
            return false;
        Workspace.Session.FilesMoved(path, result!.Path!);
        Select(result.Path);
        return true;
    }

    /// <summary>Moves <paramref name="path"/> into <paramref name="folder"/> (references fixed by UID); open scenes follow.</summary>
    public bool Move(string path, string folder)
    {
        var result = _operations?.Move(path, folder);
        if (!Report(result))
            return false;
        Workspace.Session.FilesMoved(path, result!.Path!);
        Select(result.Path);
        return true;
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the OS trash; when there is no trash for it, asks before deleting permanently.
    /// An open scene is closed first (its unsaved changes would recreate the file).
    /// </summary>
    public void Delete(string path)
    {
        if (_operations is null)
            return;
        var name = Path.GetFileName(path);
        Workspace.Message.Show(new MessageRequest
        {
            Title = "Move to Trash",
            Message = $"Move {name} to the trash?" + (Directory.Exists(path) ? " Everything inside it goes too." : ""),
            Buttons = ["Move to Trash", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, _) =>
            {
                if (button != 0)
                    return;
                CloseScenesUnder(path);
                var result = _operations.Delete(path);
                if (result.Succeeded)
                {
                    Log.Info($"[Editor] Moved {name} to the trash.");
                    _selected = null;
                    Rescan();
                    return;
                }

                if (!result.TrashUnavailable)
                {
                    Report(result);
                    return;
                }

                Workspace.Message.Show(new MessageRequest
                {
                    Title = "Delete Permanently?",
                    Message = $"{name} cannot be moved to the trash ({result.Error}).\nDelete it permanently? This cannot be undone.",
                    Buttons = ["Delete Permanently", "Cancel"],
                    DefaultButton = 1,
                    CancelButton = 1,
                    Callback = (answer, _) =>
                    {
                        if (answer == 0 && Report(_operations.DeletePermanently(path)))
                        {
                            Log.Info($"[Editor] Deleted {name} permanently.");
                            _selected = null;
                            Rescan();
                        }
                    },
                });
            },
        });
    }

    private void CloseScenesUnder(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var tab in Workspace.Session.Tabs.ToArray())
            if (tab.FilePath is { } file && (EditorSession.PathsEqual(file, full) || file.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                Workspace.Session.Close(tab);
    }

    private bool Report(FileOperationResult? result)
    {
        if (result is null)
            return false;
        if (!result.Succeeded)
        {
            Workspace.Message.Show(new MessageRequest { Title = "FileSystem", Message = result.Error ?? "The operation failed.", Buttons = ["OK"] });
            return false;
        }

        if (result.UpdatedFiles.Count > 0)
            Log.Info($"[Editor] Updated references in {result.UpdatedFiles.Count} file{(result.UpdatedFiles.Count == 1 ? "" : "s")}: " +
                     string.Join(", ", result.UpdatedFiles.Select(Path.GetFileName)));
        Rescan();
        return true;
    }

    private static void Clipboard(string text)
    {
        try
        {
            Silk.NET.SDL.SdlProvider.SDL.Value.SetClipboardText(text);
            Log.Info($"[Editor] Copied {text}");
        }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException)
        {
            Log.Info($"[Editor] {text}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fs?.Dispose();
            _thumbnails?.Dispose();
        }

        base.Dispose(disposing);
    }
}
