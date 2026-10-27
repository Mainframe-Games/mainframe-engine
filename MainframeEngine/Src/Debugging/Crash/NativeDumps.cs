namespace MainframeEngine;

/// <summary>
/// The .NET runtime's native dumps (<c>createdump</c>, ADR 0221): the variables a process must start with to have the
/// runtime write a dump of a native crash, a stack overflow or a fail fast into a crash folder, and where that dump lands.
/// Environment-only: <c>runtimeconfig.json</c> cannot carry them (the G15.6 spike). The editor sets them on the games it
/// launches; shipped games opt in with <c>crash.nativeDumps</c> (the supervisor relaunch). A dump found for a process
/// whose run left a sentinel goes into its unclean-exit bundle (<see cref="CrashKind.NativeDump"/>).
/// </summary>
public static class NativeDumps
{
    /// <summary>The variables that make the runtime write <c>dump-&lt;pid&gt;.dmp</c> into <paramref name="crashDirectory"/>.</summary>
    public static Dictionary<string, string> Environment(string crashDirectory, NativeDumpMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crashDirectory);
        if (mode == NativeDumpMode.Off)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Off writes no dumps.");
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
            environment["DOTNET_EnableCrashReport"] = "1"; // <dump>.crashreport.json: native and managed stacks (Linux, macOS)
        return environment;
    }

    /// <summary>Where the runtime writes process <paramref name="pid"/>'s dump in <paramref name="crashDirectory"/>.</summary>
    public static string PathFor(string crashDirectory, int pid) => CrashBundleWriter.DumpPathFor(crashDirectory, pid);
}
