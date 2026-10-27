using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// Native dumps for shipped desktop games (<c>crash.nativeDumps</c>, ADR 0221). The .NET runtime writes a dump of a
/// native crash only when <c>DOTNET_DbgEnableMiniDump</c> and its siblings are in the environment before it starts
/// (the spike found that <c>runtimeconfig.json</c> cannot carry them), so <see cref="GameHost.Run"/> relaunches the game
/// as its own child with them set and waits on it (Crashpad's model). When the child dies without a clean shutdown, the
/// supervisor turns its sentinel into the report at once, with the exit signal and the dump, and returns its exit code.
/// Desktop heads only: never under a debugger, when the editor launched the game (the editor sets the variables
/// itself), headless, or on mobile and consoles (which start no processes).
/// </summary>
internal static class CrashSupervisor
{
    /// <summary>Set on the child, so it runs the game instead of supervising again.</summary>
    public const string ChildVariable = "MAINFRAME_CRASH_CHILD";

    /// <summary>The variables that make the runtime write a dump of <c>%p</c> into <paramref name="crashDirectory"/>.</summary>
    public static Dictionary<string, string> DumpEnvironment(string crashDirectory, NativeDumpMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crashDirectory);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_DbgEnableMiniDump"] = "1",
            // createdump's types: 1 mini, 2 heap (private read/write memory), 3 triage (stacks, no memory), 4 full.
            ["DOTNET_DbgMiniDumpType"] = mode switch
            {
                NativeDumpMode.Triage => "3",
                NativeDumpMode.Heap => "2",
                _ => "1",
            },
            ["DOTNET_DbgMiniDumpName"] = Path.Combine(crashDirectory, "dump-%p.dmp"),
        };
        if (!OperatingSystem.IsWindows())
            environment["DOTNET_EnableCrashReport"] = "1"; // <dump>.crashreport.json: native and managed stacks
        return environment;
    }

    /// <summary>Whether this start should supervise a child instead of running the game itself.</summary>
    public static bool ShouldSupervise(ProjectSettings settings, GameHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return false;
        if (!settings.Crash.Enabled || settings.Crash.NativeDumps == NativeDumpMode.Off)
            return false;
        if (options.EditorPort is not null || options.Headless || Debugger.IsAttached)
            return false;
        if (Environment.GetEnvironmentVariable(ChildVariable) is { Length: > 0 })
            return false;
        // Already set by the user or a launcher: the runtime will dump this process itself.
        if (Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump") is "1")
            return false;
        return Environment.ProcessPath is { Length: > 0 };
    }

    /// <summary>Runs the game as a child with dumps on, waits for it and reports an abnormal end; returns its exit code (null: it could not start).</summary>
    public static int? Run(string[] args, ProjectSettings settings)
    {
        var crashDirectory = Path.Combine(UserDataPaths.GetDirectory(settings.UserDataFolderName), "crashes");
        Directory.CreateDirectory(crashDirectory);
        var processPath = Environment.ProcessPath!;
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };
        // Run as "dotnet Game.dll": the child needs the assembly too.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetCommandLineArgs() is [var entry, ..])
            start.ArgumentList.Add(entry);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment[ChildVariable] = "1";
        foreach (var (name, value) in DumpEnvironment(crashDirectory, settings.Crash.NativeDumps))
            start.Environment[name] = value;

        Process child;
        try
        {
            child = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Cannot relaunch (sandboxed, quarantined): run without dumps rather than not at all.
            Log.Warning($"[Crash] Could not start the game with native dumps ({e.Message}); running without them.");
            return null;
        }

        // Ctrl+C reaches the child too (same console): let it close; the supervisor waits.
        ConsoleCancelEventHandler ignore = static (_, e) => e.Cancel = true;
        Console.CancelKeyPress += ignore;
        try
        {
            using (child)
            {
                child.WaitForExit();
                var code = child.ExitCode;
                if (code != 0)
                {
                    var options = CrashReporterOptions.ForProject(settings, null, headless: false, debugBuild: false);
                    if (CrashReporter.WriteExitedProcessReport(options, child.Id, code) is { } folder)
                        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"[Crash] The game ended with {ExitCodes.Describe(code)}: report written to {folder}"));
                }

                return code;
            }
        }
        finally
        {
            Console.CancelKeyPress -= ignore;
        }
    }
}
