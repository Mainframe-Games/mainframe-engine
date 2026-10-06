namespace MainframeEngine.Editor;

/// <summary>
/// A UI preview tab: an <c>.rml</c> document rendered live, as the game draws it, with its linked style sheets
/// (<see cref="Links"/>). The document sits in its own <see cref="UiLayer"/> (in the editor's root viewport, so it is
/// drawn and takes the mouse) whose <see cref="UiLayer.Region"/> the workspace keeps on the viewport area; it is
/// visible only while its tab is active. Changes on disk to the document, its style sheets, templates, images or fonts
/// reload it (<see cref="Tick"/>) — the editor's UI server only watches the editor's own files. The data models game
/// code would create are stood in for (<see cref="Data"/>), so bindings render. Optionally a scene tab renders behind
/// it (<see cref="ShowBackdrop"/>), to see the UI in context.
/// </summary>
public sealed class UiPreview : IEditorTab
{
    /// <summary>The preview layers' order: above the editor panels (0), below the project, dialog and tooltip layers.</summary>
    public const int LayerOrder = 1;

    private UiHotReload _watcher;
    private bool _disposed;

    internal UiPreview(string filePath, int number)
    {
        FilePath = Path.GetFullPath(filePath);
        Layer = new UiLayer { Name = $"UiPreview{number}", Layer = LayerOrder, Visible = false };
        Document = new UiDocument { Name = "Document", Source = FilePath, AutoFocus = false };
        Layer.AddChild(Document);
        Links = RmlLinks.Parse(FilePath);
        _watcher = CreateWatcher();
    }

    /// <summary>Absolute path of the <c>.rml</c> document.</summary>
    public string FilePath { get; private set; }

    public string DisplayName => Path.GetFileName(FilePath);

    public string Title => DisplayName;

    /// <summary>Never: a preview only shows the file.</summary>
    public bool IsDirty => false;

    public string IconClasses => "icon icon-layout icon-ui";

    public string Tooltip => DisplayName + " — UI preview, reloads when its files change — " + FilePath;

    /// <summary>The preview's layer (in the editor's tree while the tab is open).</summary>
    public UiLayer Layer { get; }

    /// <summary>The previewed document.</summary>
    public UiDocument Document { get; }

    /// <summary>The style sheets and templates the document links (re-read when the document changes).</summary>
    public RmlLinks Links { get; private set; }

    /// <summary>Stand-ins for the data models the document binds (rebuilt when the document changes).</summary>
    public UiPreviewData Data { get; } = new();

    /// <summary>The scene tab rendered behind the UI while <see cref="ShowBackdrop"/> is on (null: none chosen yet).</summary>
    public EditedScene? BackdropScene { get; internal set; }

    /// <summary>Render <see cref="BackdropScene"/> behind the UI (off by default: the UI shows over a neutral backdrop).</summary>
    public bool ShowBackdrop { get; internal set; }

    /// <summary>The backdrop scene was taken out by a code reload; <see cref="EditorSession.Resume"/> puts it back.</summary>
    internal SceneSnapshot? PendingBackdrop { get; set; }

    /// <summary>Bumped when <see cref="Links"/>, the file or the backdrop changed (the viewport panel refreshes).</summary>
    public int Version { get; private set; }

    /// <summary>Main thread, every frame: applies changes the file watcher reported (debounced).</summary>
    public void Tick()
    {
        if (!_disposed && _watcher.TryTake(out var kind))
            Reload(kind);
    }

    /// <summary>Reloads the document (default) or only what <paramref name="kind"/> says changed.</summary>
    public void Reload(UiReloadKind kind = UiReloadKind.Documents)
    {
        if (_disposed || kind == UiReloadKind.None)
            return;
        var documents = (kind & UiReloadKind.Documents) != 0;
        if (documents)
            BindData(); // before the document reloads, so its views find the new stand-ins
        if (Layer.Server is { } ui)
            ui.Reload(Document, kind);
        if (documents)
        {
            Links = RmlLinks.Parse(FilePath);
            Version++;
        }
    }

    /// <summary>The layer entered the editor's tree: binds the stand-in data before the document first loads.</summary>
    internal void Attached() => BindData();

    private void BindData()
    {
        if (Layer.Context is { IsDisposed: false } context)
            Data.Bind(context, RmlBindings.ParseFile(FilePath));
    }

    /// <summary>The file moved (FileSystem rename or move): the preview follows it.</summary>
    internal void MoveTo(string path)
    {
        FilePath = Path.GetFullPath(path);
        Document.Source = FilePath;
        BindData();
        Links = RmlLinks.Parse(FilePath);
        _watcher.Dispose();
        _watcher = CreateWatcher();
        Version++;
    }

    internal void SetBackdrop(EditedScene? scene, bool show)
    {
        if (ReferenceEquals(scene, BackdropScene) && show == ShowBackdrop)
            return;
        BackdropScene = scene;
        ShowBackdrop = show && scene is not null;
        Version++;
    }

    // The document's folder, the project's Content/ and every folder a linked file lives in.
    private UiHotReload CreateWatcher()
    {
        var watcher = new UiHotReload();
        if (Path.GetDirectoryName(FilePath) is { } folder)
            watcher.Watch(folder);
        if (ContentPaths.ProjectDirectory is { } project)
            watcher.Watch(Path.Combine(project, ContentPaths.FolderName));
        foreach (var link in Links.Links)
            if (link.FullPath is { } file && Path.GetDirectoryName(file) is { } linkFolder && !IsWatched(watcher, linkFolder))
                watcher.Watch(linkFolder);
        return watcher;
    }

    private static bool IsWatched(UiHotReload watcher, string folder)
    {
        foreach (var directory in watcher.Directories)
            if (EditorSession.PathsEqual(directory, folder) ||
                folder.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher.Dispose();
        BackdropScene = null;
        PendingBackdrop = null;
        Data.Dispose();
        if (!Layer.IsFreed)
            Layer.Free(); // the document with it
    }
}
