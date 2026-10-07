using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The FileSystem panel's model: the project folder as a tree of <see cref="ProjectFileEntry"/>s with kinds, icons,
/// UIDs and badges (unsaved, missing dependency, import error). <see cref="Refresh"/> rescans; unchanged scenes and
/// resources are not re-read (peeks are cached by path, size and modification time). Main thread only: the optional
/// watcher (<see cref="StartWatching"/>) only flags changes, which <see cref="PumpChanges"/> applies.
/// </summary>
public sealed class ProjectFileSystem : IDisposable
{
    /// <summary>Folders hidden unless <see cref="ShowHidden"/> (build output, tool state, VCS).</summary>
    public static IReadOnlySet<string> HiddenFolderNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".mainframe", ".git", ".vs", ".idea", "artifacts", "TestResults", "node_modules",
    };

    private readonly AssetDatabase _database;
    private readonly Dictionary<string, ProjectFileEntry> _entries = new(StringComparer.Ordinal);
    private readonly FilePeekCache _peeks = new();
    private readonly Dictionary<string, (long Length, DateTime ModifiedUtc, string Message)> _importErrors = new(StringComparer.Ordinal);
    private readonly string[] _watchRoots;
    private HashSet<string> _unsaved = new(StringComparer.Ordinal);
    private DebouncedFileWatcher? _watcher;
    private int _changesPending;
    private volatile bool _showMeta;
    private volatile bool _showHidden;
    private bool _changed;
    private bool _disposed;

    public ProjectFileSystem(string projectRoot, AssetDatabase database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        ProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        // macOS reports watcher events for /var and /tmp under their real /private/... paths.
        _watchRoots = OperatingSystem.IsMacOS() && (ProjectRoot.StartsWith("/var/", StringComparison.Ordinal) || ProjectRoot.StartsWith("/tmp/", StringComparison.Ordinal))
            ? [ProjectRoot, "/private" + ProjectRoot]
            : [ProjectRoot];
        Root = new ProjectFileEntry(ProjectRoot, "", isDirectory: true);
        _entries[ProjectRoot] = Root;
        Refresh();
    }

    /// <summary>The project folder (absolute, no trailing separator).</summary>
    public string ProjectRoot { get; }

    /// <summary>The project folder's entry (depth 0).</summary>
    public ProjectFileEntry Root { get; }

    /// <summary>Incremented whenever the tree, an entry's values or a badge changed; the panel re-renders when it differs.</summary>
    public int Version { get; private set; }

    /// <summary>True when the file watcher runs.</summary>
    public bool IsWatching => _watcher is not null;

    /// <summary>Lists <c>.meta</c> sidecars (hidden by default). Setting it rescans.</summary>
    public bool ShowMeta
    {
        get => _showMeta;
        set
        {
            if (_showMeta == value)
                return;
            _showMeta = value;
            Refresh();
            Version++;
        }
    }

    /// <summary>Lists dot-files and folders, build output and tool folders (<see cref="HiddenFolderNames"/>). Setting it rescans.</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden == value)
                return;
            _showHidden = value;
            Refresh();
            Version++;
        }
    }

    /// <summary>Rescans the project folder. Cheap for unchanged files; bumps <see cref="Version"/> when anything changed.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _changed = false;
        var live = new HashSet<string>(StringComparer.Ordinal) { ProjectRoot };
        Root.ModifiedUtc = Directory.Exists(ProjectRoot) ? Directory.GetLastWriteTimeUtc(ProjectRoot) : default;
        ScanFolder(Root, live);

        foreach (var gone in _entries.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _entries.Remove(gone);
            _importErrors.Remove(gone);
            _changed = true;
        }

        _peeks.Retain(live);
        foreach (var entry in _entries.Values)
            UpdateBadges(entry);
        if (_changed)
            Version++;
    }

    /// <summary>The entry for an absolute or project-relative path, or null when it is not listed (missing or hidden).</summary>
    public ProjectFileEntry? Find(string pathOrProjectPath)
    {
        ArgumentNullException.ThrowIfNull(pathOrProjectPath);
        if (pathOrProjectPath.Length == 0)
            return Root;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathOrProjectPath, ProjectRoot));
        return _entries.GetValueOrDefault(full);
    }

    /// <summary>
    /// The tree's rows, depth first: <see cref="Root"/>, then (when <paramref name="isExpanded"/> says so) each folder's
    /// children.
    /// </summary>
    public IReadOnlyList<ProjectFileEntry> Flatten(Func<ProjectFileEntry, bool> isExpanded)
    {
        ArgumentNullException.ThrowIfNull(isExpanded);
        var rows = new List<ProjectFileEntry>();
        var stack = new Stack<ProjectFileEntry>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            rows.Add(entry);
            if (!entry.IsDirectory || entry.Children.Count == 0 || !isExpanded(entry))
                continue;
            for (var i = entry.Children.Count - 1; i >= 0; i--)
                stack.Push(entry.Children[i]);
        }

        return rows;
    }

    /// <summary>A folder's contents for the list/grid view (folders first); empty for a file.</summary>
    public IReadOnlyList<ProjectFileEntry> ListFolder(ProjectFileEntry folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return folder.IsDirectory ? folder.Children : ProjectFileEntry.NoChildren;
    }

    /// <summary>Marks these files (absolute paths: the open scenes with unsaved changes) <see cref="FileBadges.Unsaved"/>.</summary>
    public void SetUnsaved(IReadOnlySet<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in fullPaths)
            if (!string.IsNullOrWhiteSpace(path))
                normalized.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, ProjectRoot)));
        if (normalized.SetEquals(_unsaved))
            return;

        _unsaved = normalized;
        UpdateAllBadges();
    }

    /// <summary>
    /// Records that <paramref name="fullPath"/> failed to import (a thumbnail that did not decode); null clears it. The
    /// error holds until the file changes.
    /// </summary>
    public void ReportImportError(string fullPath, string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath, ProjectRoot));
        if (message is null)
            _importErrors.Remove(full);
        else if (_entries.TryGetValue(full, out var entry))
            _importErrors[full] = (entry.Size, entry.ModifiedUtc, message);
        else
            return;
        UpdateAllBadges();
    }

    /// <summary>
    /// Watches the project folder (debounced, default 200 ms) for changes outside hidden folders. Changes are applied by
    /// <see cref="PumpChanges"/> on the main thread.
    /// </summary>
    public void StartWatching(TimeSpan? debounce = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
            return;
        _watcher = new DebouncedFileWatcher(ProjectRoot, "*", recursive: true, debounce ?? TimeSpan.FromMilliseconds(200));
        _watcher.Changed += OnWatcherChanged;
    }

    /// <summary>Main thread: when the watcher reported changes since the last call, rescans and returns true.</summary>
    public bool PumpChanges()
    {
        if (_disposed || Interlocked.Exchange(ref _changesPending, 0) == 0)
            return false;
        Refresh();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }

    // ── Scanning ─────────────────────────────────────────────────────────────

    private void ScanFolder(ProjectFileEntry folder, HashSet<string> live)
    {
        FileSystemInfo[] infos;
        try
        {
            infos = new DirectoryInfo(folder.FullPath).GetFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            infos = [];
        }

        var children = new List<ProjectFileEntry>(infos.Length);
        foreach (var info in infos)
        {
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            if (!IsVisible(info.Name, isDirectory))
                continue;

            var full = Path.TrimEndingDirectorySeparator(info.FullName);
            if (!_entries.TryGetValue(full, out var entry) || entry.IsDirectory != isDirectory)
            {
                entry = new ProjectFileEntry(full, Path.GetRelativePath(ProjectRoot, full).Replace('\\', '/'), isDirectory);
                _entries[full] = entry;
                _changed = true;
            }

            live.Add(full);
            entry.Parent = folder;
            entry.Depth = folder.Depth + 1;
            if (isDirectory)
            {
                Set(entry.ModifiedUtc, info.LastWriteTimeUtc, v => entry.ModifiedUtc = v);
                // Linked folders are listed but not entered (no cycles).
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                    ScanFolder(entry, live);
            }
            else
            {
                UpdateFile(entry, (FileInfo)info);
            }

            children.Add(entry);
        }

        children.Sort(CompareEntries);
        if (!children.SequenceEqual(folder.Children))
        {
            folder.Children = children;
            _changed = true;
        }
    }

    private void UpdateFile(ProjectFileEntry entry, FileInfo info)
    {
        long length;
        DateTime modified;
        try
        {
            length = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            length = 0;
            modified = default;
        }

        Set(entry.Size, length, v => entry.Size = v);
        Set(entry.ModifiedUtc, modified, v => entry.ModifiedUtc = v);

        if (FilePeekCache.IsPeeked(entry.Name, entry.Kind))
        {
            var peek = _peeks.Get(entry.FullPath, entry.Kind, length, modified);
            if (!ReferenceEquals(peek, entry.Peek))
            {
                entry.Peek = peek;
                entry.RootTypeName = peek.RootTypeName;
                (entry.Icon, entry.Family) = Describe(entry, peek.RootTypeName);
                _changed = true;
            }

            Set(entry.Uid, peek.Uid ?? _database.GetUid(entry.ProjectPath), v => entry.Uid = v);
        }
        else
        {
            Set(entry.Uid, _database.GetUid(entry.ProjectPath), v => entry.Uid = v);
        }
    }

    private static (string Icon, string? Family) Describe(ProjectFileEntry entry, string? typeName)
    {
        var info = typeName is null or "instance" ? null : TypeRegistry.Get(typeName);
        return entry.Kind switch
        {
            // Scenes by their root's type and family, resources by their type; other files by extension (EditorIcons).
            FileKind.Scene when info is { IsNode: true } => (EditorIcons.For(info.Type), EditorIcons.Family(info.Type)),
            FileKind.Resource when info is { IsResource: true } => (EditorIcons.For(info.Type), "icon-resource"),
            _ => (EditorIcons.ForFile(entry.FullPath, entry.IsDirectory), EditorIcons.FileFamily(entry.FullPath, entry.IsDirectory)),
        };
    }

    private bool IsVisible(string name, bool isDirectory)
    {
        if (!isDirectory && name.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
            return _showMeta;
        if (_showHidden)
            return true;
        if (name.StartsWith('.'))
            return false;
        return isDirectory ? !HiddenFolderNames.Contains(name) : !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareEntries(ProjectFileEntry a, ProjectFileEntry b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }

    private void Set<T>(T current, T value, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        assign(value);
        _changed = true;
    }

    // ── Badges ───────────────────────────────────────────────────────────────

    private void UpdateAllBadges()
    {
        _changed = false;
        foreach (var entry in _entries.Values)
            UpdateBadges(entry);
        if (_changed)
            Version++;
    }

    private void UpdateBadges(ProjectFileEntry entry)
    {
        var badges = FileBadges.None;
        List<string>? lines = null;
        if (_unsaved.Contains(entry.FullPath))
        {
            badges |= FileBadges.Unsaved;
            (lines ??= []).Add("Unsaved changes");
        }

        if (!entry.IsDirectory && entry.Peek is { } peek)
        {
            if (peek.Error is not null)
            {
                badges |= FileBadges.ImportError;
                (lines ??= []).Add(peek.Error);
            }
            else
            {
                foreach (var reference in peek.References)
                {
                    if (Resolves(reference))
                        continue;
                    var line = $"Missing: {reference.Path ?? reference.Uid}";
                    lines ??= [];
                    if (!lines.Contains(line))
                        lines.Add(line);
                    badges |= FileBadges.MissingDependency;
                }
            }
        }

        if (entry.Kind == FileKind.Song && entry.Peek is { Error: null } song && RenderOutputOf(entry, song) is { } output &&
            File.Exists(output.Full) && File.GetLastWriteTimeUtc(output.Full) < entry.ModifiedUtc)
        {
            badges |= FileBadges.RenderOutOfDate;
            (lines ??= []).Add($"Render out of date: {output.Project} is older than the song");
        }

        if (_importErrors.TryGetValue(entry.FullPath, out var error))
        {
            if (error.Length == entry.Size && error.ModifiedUtc == entry.ModifiedUtc)
            {
                badges |= FileBadges.ImportError;
                (lines ??= []).Add(error.Message);
            }
            else
            {
                _importErrors.Remove(entry.FullPath); // the file changed since it failed
            }
        }

        Set(entry.Badges, badges, v => entry.Badges = v);
        Set(entry.BadgeText, lines is null ? null : string.Join('\n', lines), v => entry.BadgeText = v);
    }

    // The song's render output, as SongRenderer names it (.ogg when this editor has the plugin helper, else .wav).
    private static readonly bool s_canEncode = MainframeEngine.Editor.Music.PluginHostClient.IsAvailable;

    private (string Project, string Full)? RenderOutputOf(ProjectFileEntry entry, FilePeek peek)
    {
        var name = Path.GetFileNameWithoutExtension(entry.Name);
        var output = MainframeEngine.Editor.Music.SongRenderer.OutputPathFor(peek.RenderOutput, name, s_canEncode);
        try
        {
            return (output, Path.GetFullPath(output, ProjectRoot));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool Resolves(FileReference reference)
    {
        if (reference.Uid is { } uid && _database.GetPath(uid) is { } registered && Exists(_database.ToAbsolutePath(registered)))
            return true;
        return reference.Path is { } path && Exists(Path.GetFullPath(path, ProjectRoot));
    }

    private static bool Exists(string fullPath) => File.Exists(fullPath) || Directory.Exists(fullPath);

    // ── Watching (timer thread) ──────────────────────────────────────────────

    private void OnWatcherChanged(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (!IsRelevantChange(path))
                continue;
            Interlocked.Exchange(ref _changesPending, 1);
            return;
        }
    }

    private bool IsRelevantChange(string path)
    {
        string? relative = null;
        foreach (var root in _watchRoots)
        {
            var candidate = Path.GetRelativePath(root, path);
            if (!candidate.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(candidate))
            {
                relative = candidate;
                break;
            }
        }

        if (relative is null || relative == ".")
            return true; // unknown location (or an overflow reported for the root): rescan to be safe

        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            // The last segment may be a file or a folder: hidden when hidden as either.
            if (!IsVisible(segments[i], isDirectory: true) || (isLast && !IsVisible(segments[i], isDirectory: false)))
                return false;
        }

        return true;
    }
}
