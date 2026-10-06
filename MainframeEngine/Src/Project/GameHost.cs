using System.Reflection;

namespace MainframeEngine;

/// <summary>
/// Runs a game project without an <see cref="Engine"/> subclass: reads <c>project.mfproj</c>
/// (<see cref="ProjectSettings"/>), applies its settings (window, physics, audio, localization, exposure, shadow quality,
/// input map), adds
/// the autoloads and starts the main scene — or <c>--scene</c> — and lets the scene tree run the game. A game's desktop project is one
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
        : base(WithUi(WithSteam((options ?? new GameHostOptions()).Apply((settings ?? throw new ArgumentNullException(nameof(settings))).ToEngineOptions()),
            settings, options), CreateUiOptions(settings, gameAssemblies)))
    {
        Settings = settings;
        Project = settings;
        HostOptions = options ?? new GameHostOptions();
        Session = new GameSession(Tree, settings, HostOptions);
        Session.QuitRequested += code => Quit(code);
    }

    private int _updates;

    /// <summary>
    /// The UI options a game runs with: with <paramref name="hotReload"/> (Debug engine builds) the UI also watches the
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
        };
    }

    private static EngineOptions WithUi(EngineOptions options, UiServerOptions ui)
    {
        options.Ui ??= ui;
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

    /// <summary>The project session (autoloads, scene, editor link).</summary>
    public GameSession Session { get; }

    /// <summary>The running game's project settings (null before <see cref="Run"/> loads them; tools and tests have none).</summary>
    public static ProjectSettings? Project { get; internal set; }

    /// <summary>The game's own command-line arguments: everything after <c>++</c> (<see cref="GameHostOptions.UserArgs"/>).</summary>
    public static IReadOnlyList<string> UserArgs { get; private set; } = [];

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

        var fileLog = options.LogFile ? TryCreateFileLog(settings.Name) : null;
        if (fileLog is not null)
            Log.AddSink(fileLog);
        if (options.Headless)
            return RunHeadless(settings, options, gameAssemblies, fileLog);

        GameHost? host = null;
        try
        {
            GameSession.LoadGameAssemblies(settings, gameAssemblies);
            host = new GameHost(settings, options, gameAssemblies);
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

    protected override void OnLoad()
    {
        base.OnLoad();
        if (Settings.Window.MaxFps > 0)
            MaxFPS = Settings.Window.MaxFps;
        if (Renderer is IVulkanContext vk)
            vk.Exposure = Settings.Rendering.Exposure;
        if (Servers.Render is { } render)
            render.ShadowQuality = Settings.Rendering.Shadows; // before any visual creates GPU resources (Off)
        if (!Session.Start())
            Quit(ExitCode.Error);
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        Session.Update(gameTime);
        if (HostOptions.ScreenshotPath is not null && ++_updates == HostOptions.ScreenshotFrame)
            CaptureFrame();
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        if (HostOptions.ScreenshotPath is not { } path)
            return;
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
