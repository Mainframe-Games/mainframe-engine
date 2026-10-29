namespace MainframeEngine;

/// <summary>How <see cref="CrashReporter.Install"/> sets the reporter up (<see cref="ForProject"/> for games).</summary>
public sealed class CrashReporterOptions
{
    /// <summary>Default <see cref="LogEntries"/>.</summary>
    public const int DefaultLogEntries = 200;

    /// <summary>Default <see cref="MaxReports"/>.</summary>
    public const int DefaultMaxReports = 10;

    /// <summary>The folder bundles and the session sentinel go to (<c>{user data}/crashes</c>; the editor: <c>~/.mainframe/crashes</c>).</summary>
    public required string Directory { get; init; }

    /// <summary>The game's (or the editor's) name in reports.</summary>
    public string AppName { get; init; } = "Game";

    /// <summary>The game's own version (<see cref="ProjectSettings.Version"/>).</summary>
    public string AppVersion { get; init; } = "";

    public bool Demo { get; init; }

    public bool DebugBuild { get; init; }

    public bool Headless { get; init; }

    /// <summary>The reports are the editor's own.</summary>
    public bool Editor { get; init; }

    /// <summary>Log entries kept in memory and written to a report's <c>log.txt</c> (<c>crash.logEntries</c>).</summary>
    public int LogEntries
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000);
            field = value;
        }
    } = DefaultLogEntries;

    /// <summary>Bundles kept (the newest; <c>crash.maxReports</c>).</summary>
    public int MaxReports
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxReports;

    /// <summary>The run's log file: flushed last on the way down, and its tail goes into the next start's unclean-exit report.</summary>
    public FileLogSink? FileLog { get; init; }

    /// <summary>Installs <c>AppDomain.UnhandledException</c> and <c>TaskScheduler.UnobservedTaskException</c> handlers (default true).</summary>
    public bool HandleUnhandledExceptions { get; init; } = true;

    /// <summary>Writes the session sentinel and turns earlier runs' leftover sentinels into unclean-exit reports (default true).</summary>
    public bool TrackSessions { get; init; } = true;

    /// <summary>How long context providers may take on the way down; slower ones are skipped.</summary>
    public TimeSpan ProviderBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The options a game runs with: <c>{user data}/crashes</c> and the project's <c>crash</c> section.</summary>
    public static CrashReporterOptions ForProject(ProjectSettings settings, FileLogSink? fileLog, bool headless, bool debugBuild)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CrashReporterOptions
        {
            Directory = Path.Combine(UserDataPaths.GetDirectory(settings.UserDataFolderName), "crashes"),
            AppName = settings.Name,
            AppVersion = settings.Version,
            Demo = settings.IsDemo,
            DebugBuild = debugBuild,
            Headless = headless,
            LogEntries = settings.Crash.LogEntries,
            MaxReports = settings.Crash.MaxReports,
            FileLog = fileLog,
        };
    }
}
