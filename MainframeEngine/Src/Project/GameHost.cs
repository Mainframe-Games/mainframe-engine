using System.Globalization;
using System.Reflection;

namespace MainframeEngine;

/// <summary>How <see cref="GameHost"/> runs the splash sequence for a launch.</summary>
internal enum GameHostSplashMode
{
    /// <summary>No splash: the start scene starts as without one.</summary>
    None,

    /// <summary>The splash above the asynchronous start-scene load; it ends when both are done.</summary>
    BeforeAsyncLoad,

    /// <summary>The splash first, then the synchronous load (a synchronous load would freeze the cards).</summary>
    BeforeSyncLoad,

    /// <summary><c>--qa-splash</c>: only the splash, then quit.</summary>
    QaOnly,
}

/// <summary>
/// Runs a game project without an <see cref="Engine"/> subclass: reads <c>project.mfproj</c>
/// (<see cref="ProjectSettings"/>), applies its settings (window, physics, audio, localization, exposure, shadow quality,
/// input map), adds
/// the autoloads, shows the splash sequence (ADR 0192: <see cref="SplashSequence"/>, unless <see cref="ShowsSplash"/> says
/// otherwise) and starts the main scene — or <c>--scene</c> — and lets the scene tree run the game. A game's desktop project is one
/// line: <c>return GameHost.Run(args, typeof(MyNode).Assembly);</c>. Command-line flags: <see cref="GameHostOptions"/>.
/// </summary>
/// <remarks>
/// Not sealed: a game that needs the legacy hooks can subclass it (call the base methods). Subclassing
/// <see cref="Engine"/> directly keeps working too (the editor and the render-test host do).
/// </remarks>
public class GameHost : Engine
{
    public GameHost(ProjectSettings settings, GameHostOptions? options = null)
        : this(settings, options, [])
    {
    }

    /// <param name="settings">The project settings.</param>
    /// <param name="options">Command-line options.</param>
    /// <param name="gameAssemblies">The game's assemblies; Debug engine builds hot-reload their source <c>Content/</c> UI (<see cref="CreateUiOptions"/>).</param>
    public GameHost(ProjectSettings settings, GameHostOptions? options, IReadOnlyList<Assembly> gameAssemblies)
        : this(settings, options, gameAssemblies, null)
    {
    }

    /// <param name="settings">The project settings.</param>
    /// <param name="options">Command-line options (they win over the player's settings).</param>
    /// <param name="gameAssemblies">The game's assemblies; Debug engine builds hot-reload their source <c>Content/</c> UI (<see cref="CreateUiOptions"/>).</param>
    /// <param name="userSettings">
    /// The player's settings (<see cref="Run"/> loads them): they patch the engine options before the window opens
    /// (<see cref="MainframeEngine.UserSettings.PatchEngineOptions"/>) and the session applies the rest when it starts. Null: the
    /// project's values only.
    /// </param>
    public GameHost(ProjectSettings settings, GameHostOptions? options, IReadOnlyList<Assembly> gameAssemblies, UserSettings? userSettings)
        : base(WithUi(WithAppIcon(WithSteam((options ?? new GameHostOptions()).Apply(WithUserSettings(
            (settings ?? throw new ArgumentNullException(nameof(settings))).ToEngineOptions(), userSettings)), settings, options)),
            CreateUiOptions(settings, gameAssemblies)))
    {
        Settings = settings;
        Project = settings;
        UserSettings = userSettings;
        HostOptions = options ?? new GameHostOptions();
        Session = new GameSession(Tree, settings, HostOptions) { UserSettings = userSettings };
        Session.QuitRequested += code => Quit(code);
    }

    private int _updates;
    private bool _startPending;
    private SceneLoad? _startLoad;
    private bool _loadingShotRequested;
    private string? _capturePath;
    private bool _syncStartPending;
    private bool _firstFrameLogged;
    private bool _loadedLogged;

    /// <summary>
    /// The UI options a game runs with: the project's UI scale (<see cref="ProjectSettings.Ui"/>), and with <paramref name="hotReload"/> (Debug engine builds) the UI also watches the
    /// game assemblies' <c>MainframeContentSource</c> folders (their source <c>Content/</c>), so editing an <c>.rml</c> or
    /// <c>.rcss</c> in the project reloads the running game.
    /// </summary>
    public static UiServerOptions CreateUiOptions(ProjectSettings settings, IReadOnlyList<Assembly> gameAssemblies,
        bool hotReload = UiServerOptions.DefaultHotReload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(gameAssemblies);
        return new UiServerOptions
        {
            HotReload = hotReload,
            SourceContentDirectories = hotReload ? UiServerOptions.SourceDirectoriesOf([.. gameAssemblies]) : [],
            Scaling = settings.Ui.ToScaling(),
        };
    }

    private static EngineOptions WithUi(EngineOptions options, UiServerOptions ui)
    {
        options.Ui ??= ui;
        return options;
    }

    private static EngineOptions WithUserSettings(EngineOptions options, UserSettings? userSettings) =>
        userSettings?.PatchEngineOptions(options) ?? options;

    private static EngineOptions WithAppIcon(EngineOptions options)
    {
        // macOS has no per-window icons: SDL's window icon replaces the Dock icon, which a bundled game already has from its
        // .icns (Apple's icon grid; window.icon is full-bleed for Windows and Linux). Unbundled runs still get window.icon.
        if (MacAppBundle.HasIcon)
            options.IconPath = null;
        return options;
    }

    private static EngineOptions WithSteam(EngineOptions options, ProjectSettings settings, GameHostOptions? host)
    {
        options.SteamWriteDevAppIdFile = settings.Steam.DevAppIdFile && IsDevelopmentRun(host ?? new GameHostOptions());
        return options;
    }

    /// <summary>
    /// The assembly metadata key the engine's <c>build/MainframeGame.props</c> stamps on game projects: <c>"true"</c> in
    /// demo builds (<c>DEMO</c> defined), <c>"false"</c> otherwise.
    /// </summary>
    public const string DemoMetadataKey = "MainframeDemo";

    /// <summary>
    /// Whether the running game is its demo build (<see cref="ProjectSettings.IsDemo"/> as the game was built — the
    /// runtime counterpart of <c>#if DEMO</c>). False when no project is running.
    /// </summary>
    public static bool IsDemo => Project?.IsDemo == true;

    /// <summary>
    /// The demo flag <paramref name="assemblies"/> were built with (<see cref="DemoMetadataKey"/>; the first that has it
    /// wins), or null when none was built with the engine's game build props.
    /// </summary>
    public static bool? BuiltAsDemo(IEnumerable<Assembly?> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            if (assembly is null)
                continue;
            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == DemoMetadataKey && bool.TryParse(metadata.Value, out var demo))
                    return demo;
            }
        }

        return null;
    }

    /// <summary>
    /// A development run — started by the editor (<see cref="GameHostOptions.EditorPort"/>) or a Debug build of the game
    /// (its entry assembly is not optimized) — rather than a shipped build: it may write <c>steam_appid.txt</c> and never
    /// relaunches through Steam.
    /// </summary>
    public static bool IsDevelopmentRun(GameHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.EditorPort is not null
               || Assembly.GetEntryAssembly()?.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled == true;
    }

    public ProjectSettings Settings { get; }

    public GameHostOptions HostOptions { get; }

    /// <summary>The player's settings this host started with (null when constructed without them).</summary>
    public UserSettings? UserSettings { get; }

    /// <summary>The project session (autoloads, scene, editor link).</summary>
    public GameSession Session { get; }

    /// <summary>The running game's project settings (null before <see cref="Run"/> loads them; tools and tests have none).</summary>
    public static ProjectSettings? Project { get; internal set; }

    /// <summary>The game's own command-line arguments: everything after <c>++</c> (<see cref="GameHostOptions.UserArgs"/>).</summary>
    public static IReadOnlyList<string> UserArgs { get; private set; } = [];

    /// <summary>
    /// The game was compiled in Debug (its first assembly passed to <see cref="Run"/> carries a debuggable, unoptimised
    /// JIT setting): for developer keys and checks that must never ship. False before <see cref="Run"/>.
    /// </summary>
    public static bool IsDebugBuild { get; private set; }

    /// <summary>
    /// The running game's writable folder for saves and settings: <see cref="UserDataPaths.GetDirectory"/> of the
    /// project's <see cref="ProjectSettings.UserDataFolderName"/> (the engine's logs live there too), created on first use. <c>MAINFRAME_USER_DATA</c> moves its base
    /// (harness and test runs point it at a scratch folder). Before a project is loaded (tests), the game name is "game".
    /// </summary>
    public static string UserDataDirectory
    {
        get
        {
            var directory = UserDataPaths.GetDirectory(Project?.UserDataFolderName is { Length: > 0 } name ? name : "game");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>A path inside <see cref="UserDataDirectory"/> (<c>"saves/run-1.json"</c>; forward slashes are fine).</summary>
    public static string UserDataPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Path.Combine(UserDataDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Parses <paramref name="args"/>, loads the project (<c>--project</c>, else <c>project.mfproj</c> next to the app),
    /// registers <paramref name="gameAssemblies"/> and <see cref="ProjectSettings.Assemblies"/>, logs to the console and a
    /// rotating file in the user data folder, and runs until the window closes. Returns the process exit code (2 for a
    /// bad command line, 1 for a failed start).
    /// </summary>
    public static int Run(string[] args, params Assembly[] gameAssemblies)
    {
        ArgumentNullException.ThrowIfNull(args);
        GameHostOptions options;
        try
        {
            options = GameHostOptions.Parse(args);
            UserArgs = options.UserArgs;
            IsDebugBuild = gameAssemblies.Length > 0 && gameAssemblies[0].GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled == true;
        }
        catch (ArgumentException e)
        {
            Log.Error($"[GameHost] {e.Message}");
            return 2;
        }

        ProjectSettings settings;
        try
        {
            settings = options.ProjectPath is { } path
                ? ProjectSettings.Load(path)
                : ProjectSettings.LoadFromApplicationDirectory()
                  ?? throw new FileNotFoundException($"No {ProjectSettings.FileName} next to the application ({ContentPaths.BaseDirectory}); pass --project <path>.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error($"[GameHost] {e.Message}");
            return (int)ExitCode.Error;
        }

        // The build decides demo or full game (-p:MainframeDemo may differ from the project file it copied).
        if (BuiltAsDemo([Assembly.GetEntryAssembly(), .. gameAssemblies]) is { } demo)
            settings.IsDemo = demo;
        if (settings.IsDemo)
            Log.Info($"[GameHost] Demo build (Steam app {settings.SteamAppId}).");
        if (settings.Steam.RestartThroughSteam && !IsDevelopmentRun(options) && Steam.RestartAppIfNecessary(settings.SteamAppId))
        {
            Log.Info($"[GameHost] Not started by Steam: relaunching through Steam (app {settings.SteamAppId}).");
            return (int)ExitCode.Ok;
        }

        var fileLog = options.LogFile ? TryCreateFileLog(settings.UserDataFolderName) : null;
        if (fileLog is not null)
            Log.AddSink(fileLog);
        if (options.Headless)
            return RunHeadless(settings, options, gameAssemblies, fileLog);

        // The player's settings.json, before the window exists: VSync, the windowed size and the language patch the engine
        // options; the session applies the rest when it starts (after the file log, so a damaged file is reported there).
        var userSettings = UserSettings.Current = UserSettings.Load(settings);

        GameHost? host = null;
        try
        {
            GameSession.LoadGameAssemblies(settings, gameAssemblies);
            host = new GameHost(settings, options, gameAssemblies, userSettings);
            return (int)host.Run();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Fatal(e);
            return (int)ExitCode.Error;
        }
        finally
        {
            // OnClose shuts the session down on a normal exit; after a crash the editor still gets its goodbye.
            host?.Session.Shutdown(ExitCode.Error);
            host?.Dispose();
            if (fileLog is not null)
            {
                Log.RemoveSink(fileLog);
                fileLog.Dispose();
            }
        }
    }

    /// <summary>Whether the running game is headless (<c>--headless</c>: <see cref="HeadlessHost"/>; Godot's "headless" display server).</summary>
    public static bool IsHeadless { get; private set; }

    private static int RunHeadless(ProjectSettings settings, GameHostOptions options, Assembly[] gameAssemblies, FileLogSink? fileLog)
    {
        IsHeadless = true;
        HeadlessHost? host = null;
        try
        {
            GameSession.LoadGameAssemblies(settings, gameAssemblies);
            host = new HeadlessHost(settings, options);
            return (int)host.Run();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Fatal(e);
            return (int)ExitCode.Error;
        }
        finally
        {
            host?.Dispose();
            IsHeadless = false;
            if (fileLog is not null)
            {
                Log.RemoveSink(fileLog);
                fileLog.Dispose();
            }
        }
    }

    /// <summary>
    /// The save server a project runs with: <see cref="LocalSaveStorage"/> in <c>{user data}/saves</c> (created by the first
    /// save, not here) and the project's <c>saves</c> section. <see cref="GameHost"/> and <see cref="HeadlessHost"/> register it.
    /// </summary>
    public static SaveServer CreateSaveServer(ProjectSettings settings, SceneTree? tree)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folder = Path.Combine(UserDataPaths.GetDirectory(settings.UserDataFolderName), "saves");
        return new SaveServer(new LocalSaveStorage(folder), SaveOptions.FromProject(settings), tree);
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        // Registered last, so it is disposed first on close: queued saves are flushed while everything else still runs.
        Servers.Register(CreateSaveServer(Settings, Tree));
        if (Settings.Window.MaxFps > 0)
            MaxFPS = Settings.Window.MaxFps;
        if (Renderer is IVulkanContext vk)
        {
            vk.Exposure = Settings.Rendering.Exposure;
            vk.AntiAliasing = Settings.Rendering.AntiAliasing;
            vk.TaaSharpness = Settings.Rendering.TaaSharpness;
            vk.Scaling3DMode = Settings.Rendering.Scaling3DMode; // ADR 0174
            vk.Scaling3DScale = Settings.Rendering.Scaling3DScale;
            vk.FsrSharpness = Settings.Rendering.FsrSharpness;
        }
        if (Servers.Render is { } render)
        {
            render.ShadowQuality = Settings.Rendering.Shadows; // before any visual creates GPU resources (Off)
            render.WaterSsr = Settings.Rendering.WaterSsr;
        }
        // The session (autoloads, the main scene) starts on the first update, once the window is up (Godot readies the
        // scene after its window exists): SDL shows the window after OnLoad, which took ~0.3 s that the first scene's
        // first frame would otherwise lose (process deltas drop what the physics steps cannot cover).
        _startPending = true;
        CreateRecorder();
    }

    // Gameplay recording (ADR 0190): the renderer streams frames and the audio server exists from here on. --record
    // starts it on the first update, just before the session, so the start scene's loading is recorded too.
    private void CreateRecorder()
    {
        var recorder = Tree.Recorder = new GameRecorder(
            _ => new FfmpegRecordingBackend(FfmpegResolver.ResolveForThisMachine(HostOptions.FfmpegPath)),
            Renderer as IFrameStreamSource,
            Servers.Get<AudioServer>(),
            HostOptions.RecordOffline ? HostOptions.FixedFps : 0,
            new CaptureInfoSource(Settings.Name, () => Tree.CurrentScene?.SceneFilePath ?? "", EngineInfo.Version));
        Session.AttachRecorder(recorder);
        recorder.Started += () => DevOverlayVisible = false; // F12 still brings it back, into the recording
        if (HostOptions.RecordPath is not null)
        {
            // Stopped also fires from OnClose (the normal end at quit), where Quit only sets the exit code Run returns.
            recorder.Stopped += result =>
            {
                if (ShouldQuitOnRecordingError(HostOptions.RecordOffline, result))
                {
                    Log.Error("[Recorder] The offline --record run did not produce a recording: exiting with an error.");
                    Quit(ExitCode.Error);
                }
            };
        }
        // A key released in another window never sends its key-up here: the recorder must not think it is still held.
        Window.FocusChanged += focused =>
        {
            if (!focused)
                recorder.ReleaseHeldInput();
        };
    }

    /// <summary>
    /// Whether a <c>--record</c> run that ended with <paramref name="result"/> must exit non-zero: only an offline run
    /// (a capture script or CI job whose whole purpose is the file) whose recording ended with an error, or whose
    /// finalising did not finish before shutdown gave up on it (<paramref name="result"/> null). A live run plays on, so
    /// the player keeps their game and only sees the logged error.
    /// </summary>
    internal static bool ShouldQuitOnRecordingError(bool offline, RecordingResult? result) =>
        offline && (result is null || result.Error is not null);

    // --record: a run that cannot record fails loudly instead of playing unrecorded. False when the game must quit.
    private bool StartCommandLineRecording()
    {
        if (HostOptions.RecordPath is not { } path || Tree.Recorder is not { } recorder)
            return true;
        try
        {
            recorder.Start(new RecordingOptions { Path = path, Fps = HostOptions.FixedFps > 0 ? HostOptions.FixedFps : 60 });
            return true;
        }
        catch (Exception e) when (e is FfmpegNotFoundException or IOException or InvalidOperationException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            Log.Error($"[Recorder] {e.Message}");
            Session.ReportRecordingFailed(path, e.Message); // the editor's Play and Record shows "Recording failed" with why
            return false;
        }
    }

    /// <summary>
    /// Whether the start scene loads asynchronously behind a loading screen (ADR 0183): the project's <c>loading.async</c>
    /// unless <c>--sync-load</c>.
    /// </summary>
    public bool LoadsAsync => Settings.Loading.Async && !HostOptions.SyncLoad;

    /// <summary>The start scene's asynchronous load (null when loading synchronously, before the start or without a scene).</summary>
    public SceneLoad? StartLoad => _startLoad;

    /// <summary>The loading screen shown for the start scene (null once it has faded out, or when there is none).</summary>
    public LoadingScreen? LoadingScreen { get; private set; }

    /// <summary>The splash sequence while it runs (null before, after, or when it does not run).</summary>
    public SplashSequence? Splash { get; private set; }

    /// <summary>
    /// Whether a launch with <paramref name="options"/> shows the splash sequence (ADR 0192). Never with <c>--headless</c>
    /// (nothing is displayed: the BRUTAL licence's own exception), <c>--editor-port</c> (the editor's Play), <c>--scene</c>
    /// (QA, screenshots and tests start a specific scene), <c>--max-frames</c> with <c>--hidden</c> (CI smoke runs) or
    /// <c>--no-splash</c>; always with <c>--qa-splash</c>. A shipped game launched normally always shows it.
    /// </summary>
    public static bool ShowsSplash(GameHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.QaSplash)
            return true;
        return !(options.Headless || options.EditorPort is not null || options.Scene is not null || (options.MaxFrames > 0 && options.Hidden)
                 || options.NoSplash);
    }

    /// <summary>The splash mode for a launch: <see cref="ShowsSplash"/>, a UI to draw it and cards to show, and how the start scene loads.</summary>
    internal static GameHostSplashMode SplashModeFor(GameHostOptions options, ProjectSettings settings, bool hasUi)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (options.QaSplash)
            return GameHostSplashMode.QaOnly;
        if (!hasUi || !ShowsSplash(options) || settings.Splash.ResolvedCards.Count == 0)
            return GameHostSplashMode.None;
        return settings.Loading.Async && !options.SyncLoad ? GameHostSplashMode.BeforeAsyncLoad : GameHostSplashMode.BeforeSyncLoad;
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        if (_startPending)
        {
            _startPending = false;
            DiscardFrameDelta();
            if (!StartCommandLineRecording())
            {
                Quit(ExitCode.Error);
                return;
            }

            switch (SplashModeFor(HostOptions, Settings, Ui is not null))
            {
                case GameHostSplashMode.QaOnly:
                    StartSplashQa();
                    break;
                case GameHostSplashMode.BeforeSyncLoad:
                    // A synchronous load would freeze the cards: the session starts on the update after they are done.
                    ShowSplash(load: null).Finished += () => _syncStartPending = true;
                    break;
                case GameHostSplashMode.BeforeAsyncLoad:
                    StartAsync();
                    ShowSplash(_startLoad);
                    break;
                default:
                    if (LoadsAsync)
                    {
                        StartAsync();
                    }
                    else if (!StartSync())
                    {
                        Quit(ExitCode.Error);
                        return;
                    }

                    break;
            }
        }
        else if (_syncStartPending)
        {
            _syncStartPending = false;
            if (!StartSync())
            {
                Quit(ExitCode.Error);
                return;
            }
        }

        LogStartTimes();
        Session.Update(gameTime);
        Tree.Recorder?.OnUpdate(Tree.IsLoading, Tree.Paused); // before the early return: loading frames are recorded too
        if (Tree.IsLoading)
        {
            // --loading-screenshot: the loading screen at half progress (or its last frame, if the load skips past it).
            if (HostOptions.LoadingScreenshotPath is { } loadingShot && !_loadingShotRequested && _startLoad is { } load &&
                (load.Progress >= 0.5f || load.IsCompleted))
            {
                _loadingShotRequested = true;
                _capturePath = loadingShot;
                CaptureFrame();
            }

            return; // frames count once the game is on screen
        }

        if (HostOptions.ScreenshotPath is { } screenshot && ++_updates == HostOptions.ScreenshotFrame)
        {
            _capturePath = screenshot;
            CaptureFrame();
        }
    }

    private void StartAsync()
    {
        var load = _startLoad = Session.StartAsync(new SceneLoadOptions { HoldSeconds = HostOptions.LoadingHoldSeconds });
        if (load is null)
            return;
        load.Completed += l =>
        {
            if (l.Stage == SceneLoadStage.Failed)
                Quit(ExitCode.Error);
        };
        var source = Settings.Loading.Screen ?? MainframeEngine.LoadingScreen.DefaultSource;
        if (Ui is not null && source.Length > 0)
        {
            LoadingScreen = MainframeEngine.LoadingScreen.Show(Tree, load, source, Settings.Name);
            LoadingScreen.TreeExited += () => LoadingScreen = null;
        }
    }

    // The synchronous start (loading.async false or --sync-load). False when the scene failed to load.
    private bool StartSync()
    {
        // The synchronous load blocks this update: open a loading span now, the recorder's update below closes it. Today the
        // first captured frame comes after this load, and the live origin is anchored there, so the load is not in the video
        // and the span is empty (dropped); it is marked anyway so a recording that already has frames when a synchronous
        // start load runs never shows the hitch as play.
        Tree.Recorder?.OnUpdate(loading: true, paused: false);
        return Session.Start();
    }

    // The splash sequence (ADR 0192) above everything, waiting for load (null: none to wait for).
    private SplashSequence ShowSplash(SceneLoad? load, bool holdsLoading = true, bool? skippable = null)
    {
        var splash = Splash = new SplashSequence { Settings = Settings.Splash, Load = load, HoldsLoading = holdsLoading, Skippable = skippable };
        splash.TreeExited += () => Splash = null;
        Tree.Root.AddChild(splash);
        return splash;
    }

    // --qa-splash: only the sequence (the session starts nothing), then quit; --qa-splash-out saves each card halfway through
    // its hold, unskippable. Frames count (HoldsLoading off), so --max-frames still caps the run.
    private void StartSplashQa()
    {
        if (Ui is null || Settings.Splash.ResolvedCards.Count == 0)
        {
            Log.Error("[Splash] --qa-splash: nothing to show (no UI, or an empty splash.cards with nonProfit).");
            Quit(ExitCode.Error);
            return;
        }

        var output = HostOptions.QaSplashOutput;
        var splash = ShowSplash(load: null, holdsLoading: false, skippable: output is null ? null : false);
        if (output is not null)
        {
            splash.CardShown += index =>
            {
                _capturePath = Path.Combine(output, $"splash-{(index + 1).ToString(CultureInfo.InvariantCulture)}.png");
                CaptureFrame();
            };
        }

        splash.Finished += () => Quit(ExitCode.Ok);
    }

    // How long the window took to show something and the game to be playable, from the process start (ADR 0183).
    private void LogStartTimes()
    {
        if (!_firstFrameLogged && RenderedFrameCount > 0)
        {
            _firstFrameLogged = true;
            Log.Info($"[GameHost] First frame presented {SinceProcessStart():0} ms after the process started.");
        }

        if (!_loadedLogged && RenderedFrameCount > 0 && !Tree.IsLoading && (_startLoad is null || _startLoad.IsCompleted))
        {
            _loadedLogged = true;
            Log.Info($"[GameHost] Start scene on screen {SinceProcessStart():0} ms after the process started" +
                     (_startLoad is { } load ? $" (loaded asynchronously in {load.Elapsed.TotalSeconds:0.00} s)." : "."));
        }
    }

    private static double SinceProcessStart()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return double.NaN;
        }
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        if (_capturePath is not { } path)
            return;
        _capturePath = null;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            capture.SavePng(path);
            Log.Info($"[GameHost] Screenshot saved to {Path.GetFullPath(path)} ({capture.Width}x{capture.Height}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[GameHost] Could not save the screenshot '{path}': {e.Message}");
        }
    }

    protected override void OnClose()
    {
        // The renderer (pending frames) and the audio server (the tap) must still be alive while the recording stops.
        if (Tree.Recorder is { } recorder)
        {
            if (recorder.IsRecording || recorder.IsFinalising)
            {
                // A result (success or error) raised Stopped, whose handler already set the exit code.
                if (recorder.StopAndWait(TimeSpan.FromMinutes(2)) is null)
                {
                    Log.Error("[Recorder] Finalising the recording took over two minutes; the partial files were kept.");
                    if (ShouldQuitOnRecordingError(HostOptions.RecordOffline, null))
                    {
                        Log.Error("[Recorder] The offline --record run did not produce a recording: exiting with an error.");
                        Quit(ExitCode.Error); // only sets the code Run returns: the window is already closing
                    }
                }
            }
            else
            {
                recorder.PollCompletion(); // finished since the last update: still raise Stopped and log the result
            }
        }

        Session.Shutdown(CurrentExitCode);
        base.OnClose();
    }

    private static FileLogSink? TryCreateFileLog(string gameName)
    {
        try
        {
            return FileLogSink.ForUser(gameName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[GameHost] No log file: {e.Message}");
            return null;
        }
    }
}
