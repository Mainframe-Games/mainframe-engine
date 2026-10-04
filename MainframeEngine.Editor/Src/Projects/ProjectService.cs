using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MainframeEngine.Editor;

/// <summary>
/// The open game project (E4): opens a <c>project.mfproj</c> folder into the session, finds the game's projects
/// (<see cref="GameProjectLayout"/>), loads the game assembly (<c>MyGame.dll</c>) into a collectible context
/// (<see cref="GameAssemblyLoader"/>) so scenes use its node types, builds it through the <see cref="PlayService"/>'s
/// builder, and reloads code: a debounced watcher on the build output reloads after every build (the editor's Build
/// &amp; Reload, Play, or an IDE), and a watcher on the sources sets <see cref="NeedsRebuild"/>.
/// </summary>
/// <remarks>
/// A reload re-creates only the open scenes that use game code (<see cref="GameCodeScanner"/>): they are serialized
/// (unsaved edits included), freed, the old assembly is unloaded (verified collected), the new one loaded, and the
/// scenes re-instantiated with their tab, file, dirty state, selection and view; types the new build lacks load as
/// <see cref="MissingNode"/>. Other scenes keep their undo history. Main thread only.
/// </remarks>
public sealed class ProjectService : IDisposable
{
    private readonly EditorWorkspace _workspace;
    private GameAssemblyLoader? _loader;
    private DebouncedFileWatcher? _outputWatcher;
    private DebouncedFileWatcher? _sourceWatcher;
    private int _outputChanged;
    private int _sourceChanged;
    private Task<GameBuildResult>? _build;
    private Action<GameBuildResult>? _afterBuild;
    private DateTime _loadedWriteTime;
    private long _loadedLength;

    public ProjectService(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    private EditorSession Session => _workspace.Session;

    /// <summary>The project folder, or null when no project is open.</summary>
    public string? Root { get; private set; }

    public ProjectSettings? Settings => Session.Project;

    /// <summary>The game library project (<c>MyGame/MyGame.csproj</c>), or null for a project without code.</summary>
    public string? GameLibraryProject { get; private set; }

    /// <summary>The launcher project (<c>MyGame.Launcher/MyGame.Launcher.csproj</c>) Play runs.</summary>
    public string? LauncherProject { get; private set; }

    /// <summary>The solution (<c>MyGame.slnx</c>) Play builds; the library project when there is none.</summary>
    public string? BuildPath { get; private set; }

    /// <summary>The loaded game assembly's file, or null.</summary>
    public string? GameAssemblyPath => _loader?.IsLoaded == true ? _loader.AssemblyPath : null;

    /// <summary>The loader (null until the game assembly is first loaded).</summary>
    public GameAssemblyLoader? Loader => _loader;

    public bool IsGameLoaded => _loader?.IsLoaded == true;

    public bool IsBuilding => _build is not null;

    /// <summary>Game sources changed since the loaded build (the Build &amp; Reload button shows it).</summary>
    public bool NeedsRebuild { get; private set; }

    /// <summary>Reload automatically when the build output changes (editor setting).</summary>
    public bool AutoReload { get; set; } = true;

    /// <summary>Completed reloads (loads after the first).</summary>
    public int ReloadCount { get; private set; }

    /// <summary>Whether the last unload was verified collected (null before any).</summary>
    public bool? LastUnloadCollected { get; private set; }

    public TimeSpan? LastBuildDuration { get; private set; }

    public TimeSpan? LastReloadDuration { get; private set; }

    /// <summary>Raised when the project, the loaded code, the build state or <see cref="NeedsRebuild"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Raised after the game assembly was (re)loaded and scenes re-created (code reload done).</summary>
    public event Action? CodeReloaded;

    // ── Open / close ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the project in <paramref name="directory"/>: settings, asset database, recent list, watchers, then the
    /// game code — loaded at once when built, else built first (<paramref name="onReady"/> runs when the code is loaded
    /// or there is none). The caller closes the previous project's scenes first. Throws for an invalid project.
    /// </summary>
    public void Open(string directory, Action? onReady = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = GameProjectLayout.RealPath(Path.GetFullPath(directory));
        CloseCode();
        Session.OpenProject(root);
        Root = root;
        var settings = Session.Project!;
        GameLibraryProject = GameProjectLayout.GameLibraryProjectOf(root, settings);
        LauncherProject = GameProjectLayout.LauncherProjectOf(root);
        BuildPath = GameProjectLayout.SolutionOf(root) ?? GameLibraryProject;
        _workspace.RecentProjects.Touch(root, settings.Name);
        StartWatching();
        Changed?.Invoke();

        if (GameLibraryProject is null)
        {
            Log.Info($"[Project] {settings.Name} has no game library project; scenes use engine types only.");
            onReady?.Invoke();
            return;
        }

        var output = GameAssemblyLoader.FindBuildOutput(GameLibraryProject);
        if (output is null)
        {
            Log.Info($"[Project] {Path.GetFileName(GameLibraryProject)} has not been built yet; building it.");
            Build(GameLibraryProject, result =>
            {
                if (result.Succeeded)
                    LoadGameAssembly();
                onReady?.Invoke();
            });
            return;
        }

        LoadGameAssembly();
        NeedsRebuild = SourcesNewerThan(output);
        if (NeedsRebuild)
            Log.Info("[Project] The game's sources changed since its last build: Build & Reload to use them.");
        onReady?.Invoke();
    }

    /// <summary>The main scene's file (<c>mainScene</c>: a UID or a <c>Content/</c> path), or null.</summary>
    public string? MainScenePath()
    {
        if (Root is null || Settings?.MainScene is not { Length: > 0 } main)
            return null;
        var relative = AssetUid.IsUid(main) ? AssetDatabase.Current.GetPath(main) : main;
        if (relative is null)
            return null;
        var full = AssetDatabase.Current.ToAbsolutePath(relative);
        return File.Exists(full) ? full : null;
    }

    /// <summary>Unloads the game code and stops watching (the session's scenes must be closed first).</summary>
    public void Close()
    {
        CloseCode();
        Root = null;
        GameLibraryProject = null;
        LauncherProject = null;
        BuildPath = null;
        NeedsRebuild = false;
        Changed?.Invoke();
    }

    private void CloseCode()
    {
        StopWatching();
        if (_loader is null)
            return;
        CustomInspectors.Reset();
        UnloadLoader(_loader);
        _loader = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnloadLoader(GameAssemblyLoader loader)
    {
        if (loader.IsLoaded && !loader.Unload(TimeSpan.FromSeconds(2)))
            Log.Warning("[Project] The previous game code is still referenced and stays in memory.");
        loader.Dispose();
    }

    // ── Build ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the game library and reloads its code when the build succeeded (the toolbar's Build &amp; Reload).</summary>
    public bool BuildAndReload()
    {
        if (GameLibraryProject is null)
        {
            Log.Warning("[Project] No game library project to build.");
            return false;
        }

        if (IsBuilding)
            return false;
        Build(GameLibraryProject, result =>
        {
            if (result.Succeeded)
                ReloadIfChanged(force: !IsGameLoaded);
        });
        return true;
    }

    private void Build(string project, Action<GameBuildResult> then)
    {
        var play = _workspace.Play;
        _afterBuild = then;
        _build = play.Service.BuildAsync(project);
        Changed?.Invoke();
    }

    // ── Load / reload ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the newest build of the game library (first load, or after the loader was closed).</summary>
    public bool LoadGameAssembly()
    {
        if (GameLibraryProject is null || GameAssemblyLoader.FindBuildOutput(GameLibraryProject) is not { } dll)
            return false;
        if (_loader is not null && _loader.IsLoaded)
            return ReloadGameAssembly();
        var stopwatch = Stopwatch.StartNew();
        // Scenes opened before the code was loaded hold missing nodes: re-create them with the real types.
        var snapshots = SuspendScenes(null);
        try
        {
            _loader = new GameAssemblyLoader(dll);
            _loader.Load();
            RememberLoaded(dll);
            CustomInspectors.Reset();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Log.Error($"[Project] Could not load {Path.GetFileName(dll)}: {e.Message}");
            _loader = null;
            ResumeScenes(snapshots);
            return false;
        }

        ResumeScenes(snapshots);
        LastReloadDuration = stopwatch.Elapsed;
        NeedsRebuild = false;
        Log.Info($"[Project] Game code loaded ({Path.GetFileName(dll)}, {stopwatch.ElapsedMilliseconds} ms).");
        Changed?.Invoke();
        CodeReloaded?.Invoke();
        return true;
    }

    /// <summary>
    /// Reloads the game code from the newest build: re-creates the scenes that use it around an unload/load of the
    /// assembly. Returns false when nothing is loaded or loading the new build failed (the scenes are restored with
    /// missing types then).
    /// </summary>
    public bool ReloadGameAssembly()
    {
        if (GameLibraryProject is null || _loader is not { IsLoaded: true } loader)
            return LoadGameAssembly();
        var dll = GameAssemblyLoader.FindBuildOutput(GameLibraryProject) ?? loader.AssemblyPath;
        var stopwatch = Stopwatch.StartNew();
        _workspace.ReleaseEditorReferences();
        var snapshots = SuspendGameScenes(loader);
        CustomInspectors.Reset();
        _workspace.ReleaseEditorReferences();
        LastUnloadCollected = UnloadForReload(loader);
        var loaded = LoadAfterUnload(dll);
        ResumeScenes(snapshots);
        LastReloadDuration = stopwatch.Elapsed;
        ReloadCount++;
        NeedsRebuild = !loaded || SourcesNewerThan(dll);
        if (loaded)
            Log.Info($"[Project] Code reloaded in {stopwatch.ElapsedMilliseconds} ms ({snapshots.Count} scene{(snapshots.Count == 1 ? "" : "s")} re-created" +
                     $"{(LastUnloadCollected == false ? "; the previous code is still referenced" : "")}).");
        Changed?.Invoke();
        CodeReloaded?.Invoke();
        return loaded;
    }

    // The scenes using game code leave the session (no game objects referenced after this returns).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<SceneSnapshot> SuspendGameScenes(GameAssemblyLoader loader) => SuspendScenes(loader.Assembly);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<SceneSnapshot> SuspendScenes(System.Reflection.Assembly? assembly)
    {
        var snapshots = new List<SceneSnapshot>();
        var active = Session.Active;
        var scenes = Session.Scenes.ToArray();
        // Last tab first, so every recorded index is the tab's original position; resumed in ascending order, each
        // goes back where it was.
        for (var i = scenes.Length - 1; i >= 0; i--)
        {
            var scene = scenes[i];
            if (!GameCodeScanner.Uses(scene.Root, assembly))
                continue;
            if (ReferenceEquals(scene, active))
                _activeIndex = i;
            snapshots.Add(Session.Suspend(scene));
        }

        snapshots.Reverse();
        return snapshots;
    }

    private int _activeIndex = -1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UnloadForReload(GameAssemblyLoader loader) => loader.Unload(TimeSpan.FromSeconds(5));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool LoadAfterUnload(string dll)
    {
        try
        {
            _loader = new GameAssemblyLoader(dll);
            _loader.Load();
            RememberLoaded(dll);
            CustomInspectors.Reset();
            return true;
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Log.Error($"[Project] Could not load the rebuilt {Path.GetFileName(dll)}: {e.Message}. Scenes keep their data as missing types.");
            _loader = null;
            return false;
        }
    }

    private void ResumeScenes(List<SceneSnapshot> snapshots)
    {
        EditedScene? active = null;
        foreach (var snapshot in snapshots)
        {
            try
            {
                var scene = Session.Resume(snapshot);
                if (snapshot.Index == _activeIndex)
                    active = scene;
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Log.Error($"[Project] Could not re-open {snapshot.FilePath ?? "an untitled scene"} after the code reload: {e.Message}");
            }
        }

        _activeIndex = -1;
        if (active is not null)
            Session.Activate(active);
    }

    private void RememberLoaded(string dll)
    {
        var info = new FileInfo(dll);
        _loadedWriteTime = info.LastWriteTimeUtc;
        _loadedLength = info.Length;
    }

    // Reloads when the build output differs from what is loaded (an IDE build, the editor's own builds).
    private void ReloadIfChanged(bool force = false)
    {
        if (GameLibraryProject is null || GameAssemblyLoader.FindBuildOutput(GameLibraryProject) is not { } dll)
            return;
        var info = new FileInfo(dll);
        if (!force && IsGameLoaded && info.LastWriteTimeUtc == _loadedWriteTime && info.Length == _loadedLength)
            return;
        if (IsGameLoaded)
            ReloadGameAssembly();
        else
            LoadGameAssembly();
    }

    private bool SourcesNewerThan(string dll)
    {
        if (GameLibraryProject is null || Path.GetDirectoryName(GameLibraryProject) is not { } folder)
            return false;
        var built = File.GetLastWriteTimeUtc(dll);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildFolder(folder, file))
                continue;
            if (File.GetLastWriteTimeUtc(file) > built)
                return true;
        }

        return false;
    }

    private static bool IsBuildFolder(string projectFolder, string file)
    {
        var relative = Path.GetRelativePath(projectFolder, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    // ── Watching ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void StartWatching()
    {
        StopWatching();
        if (GameLibraryProject is null || Path.GetDirectoryName(GameLibraryProject) is not { } folder)
            return;
        try
        {
            var bin = Path.Combine(folder, "bin");
            _outputWatcher = new DebouncedFileWatcher(bin, "*.dll", recursive: true, TimeSpan.FromMilliseconds(500));
            _outputWatcher.Changed += _ => Interlocked.Exchange(ref _outputChanged, 1);
            _sourceWatcher = new DebouncedFileWatcher(folder, "*.cs", recursive: true, TimeSpan.FromMilliseconds(300));
            _sourceWatcher.Changed += paths =>
            {
                foreach (var path in paths)
                    if (!IsBuildFolder(folder, path))
                    {
                        Interlocked.Exchange(ref _sourceChanged, 1);
                        return;
                    }
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Warning($"[Project] Cannot watch the game's files ({e.Message}); use Build & Reload after editing code.");
        }
    }

    private void StopWatching()
    {
        _outputWatcher?.Dispose();
        _outputWatcher = null;
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
    }

    /// <summary>Main thread, every frame: finishes builds, reacts to the watchers (reload, rebuild-needed badge).</summary>
    public void Update()
    {
        if (_build is { IsCompleted: true } build)
        {
            _build = null;
            var result = build.IsCompletedSuccessfully ? build.Result : GameBuildResult.Failed(build.Exception?.GetBaseException().Message ?? "Build failed.");
            LastBuildDuration = result.Duration;
            var then = _afterBuild;
            _afterBuild = null;
            // The output watcher fired during the build: the continuation decides about reloading.
            Interlocked.Exchange(ref _outputChanged, 0);
            Changed?.Invoke();
            then?.Invoke(result);
        }

        if (Interlocked.Exchange(ref _sourceChanged, 0) != 0 && !NeedsRebuild)
        {
            NeedsRebuild = true;
            Changed?.Invoke();
        }

        if (_build is null && Interlocked.Exchange(ref _outputChanged, 0) != 0 && AutoReload && IsGameLoaded)
            ReloadIfChanged();
    }

    public void Dispose()
    {
        StopWatching();
        _loader?.Dispose();
        _loader = null;
    }
}
