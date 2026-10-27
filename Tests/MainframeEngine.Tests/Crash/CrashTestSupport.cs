namespace MainframeEngine.Tests.Crash;

/// <summary>The crash reporter is process-global: its tests run one at a time.</summary>
[CollectionDefinition(nameof(CrashReporterState), DisableParallelization = true)]
public sealed class CrashReporterState;

/// <summary>A scratch crash folder; <see cref="Dispose"/> shuts the reporter down and deletes it.</summary>
internal sealed class CrashScratch : IDisposable
{
    public CrashScratch()
    {
        Root = Path.Combine(Path.GetTempPath(), "mf-crash-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Crashes => Path.Combine(Root, "crashes");

    public CrashReporterOptions Options(int maxReports = 10, TimeSpan? budget = null, bool sessions = true, FileLogSink? log = null) => new()
    {
        Directory = Crashes,
        AppName = "CrashTest",
        AppVersion = "1.2.3",
        MaxReports = maxReports,
        LogEntries = 20,
        HandleUnhandledExceptions = false, // never touch the test host's own handlers
        TrackSessions = sessions,
        ProviderBudget = budget ?? TimeSpan.FromSeconds(2),
        FileLog = log,
    };

    /// <summary>The bundles written so far (oldest first).</summary>
    public string[] Bundles()
    {
        if (!Directory.Exists(Crashes))
            return [];
        var bundles = Directory.GetDirectories(Crashes).Where(d => File.Exists(Path.Combine(d, CrashReport.FileName))).ToArray();
        Array.Sort(bundles, StringComparer.Ordinal);
        return bundles;
    }

    public static Exception Thrown(Func<Exception> make)
    {
        try
        {
            throw make();
        }
        catch (Exception e)
        {
            return e;
        }
    }

    public void Dispose()
    {
        CrashReporter.Shutdown();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
