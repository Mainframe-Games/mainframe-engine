using System.Reflection;

namespace MainframeEngine;

/// <summary>
/// Runs a game project without an <see cref="Engine"/> subclass: reads <c>project.mfproj</c>
/// (<see cref="ProjectSettings"/>), applies its settings (window, physics, audio, exposure, shadows, input map), adds
/// the autoloads and starts the main scene — or <c>--scene</c> — and lets the scene tree run the game. A launcher is one
/// line: <c>return GameHost.Run(args, typeof(MyNode).Assembly);</c>. Command-line flags: <see cref="GameHostOptions"/>.
/// </summary>
/// <remarks>
/// Not sealed: a game that needs the legacy hooks can subclass it (call the base methods). Subclassing
/// <see cref="Engine"/> directly keeps working too (the Sandbox does).
/// </remarks>
public class GameHost : Engine
{
    public GameHost(ProjectSettings settings, GameHostOptions? options = null)
        : base((options ?? new GameHostOptions()).Apply((settings ?? throw new ArgumentNullException(nameof(settings))).ToEngineOptions()))
    {
        Settings = settings;
        HostOptions = options ?? new GameHostOptions();
        Session = new GameSession(Tree, settings, HostOptions);
        Session.QuitRequested += code => Quit(code);
    }

    public ProjectSettings Settings { get; }

    public GameHostOptions HostOptions { get; }

    /// <summary>The project session (autoloads, scene, editor link).</summary>
    public GameSession Session { get; }

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

        var fileLog = options.LogFile ? TryCreateFileLog(settings.Name) : null;
        if (fileLog is not null)
            Log.AddSink(fileLog);
        GameHost? host = null;
        try
        {
            GameSession.LoadGameAssemblies(settings, gameAssemblies);
            host = new GameHost(settings, options);
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

    protected override void OnLoad()
    {
        base.OnLoad();
        if (Settings.Window.MaxFps > 0)
            MaxFPS = Settings.Window.MaxFps;
        if (Renderer is IVulkanContext vk)
            vk.Exposure = Settings.Rendering.Exposure;
        if (Settings.Rendering.Shadows == ShadowQuality.Off && Servers.Render is { } render)
            render.ShadowsEnabled = false; // before any visual creates GPU resources
        if (!Session.Start())
            Quit(ExitCode.Error);
    }

    protected override void OnUpdate(in GameTime gameTime) => Session.Update(gameTime);

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
