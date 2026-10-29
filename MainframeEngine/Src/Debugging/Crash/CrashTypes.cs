namespace MainframeEngine;

/// <summary>What ended (or troubled) the run a <see cref="CrashReport"/> describes.</summary>
public enum CrashKind
{
    /// <summary>An exception reached <see cref="GameHost.Run"/> (a node callback, a server, the renderer) on the main thread.</summary>
    ManagedException,

    /// <summary>An exception no code caught on another thread (<c>AppDomain.UnhandledException</c>); the process ends.</summary>
    ThreadException,

    /// <summary>A faulted task nobody observed (<c>TaskScheduler.UnobservedTaskException</c>); non-fatal.</summary>
    UnobservedTask,

    /// <summary>The audio thread faulted and the game plays on in silence; non-fatal.</summary>
    AudioThread,

    /// <summary>The GPU stopped responding (<see cref="VulkanDeviceLostException"/>).</summary>
    DeviceLost,

    /// <summary>
    /// The previous run ended without a clean shutdown and without a report: a native fault, a stack overflow,
    /// <c>Environment.FailFast</c>, a kill or a power loss (found through its session sentinel on the next start).
    /// </summary>
    UncleanExit,

    /// <summary>Like <see cref="UncleanExit"/>, with the native dump the .NET runtime wrote (<c>crash.nativeDumps</c>).</summary>
    NativeDump,

    /// <summary>The main thread stopped responding (reserved for hang detection, G15.9).</summary>
    Hang,

    /// <summary>A non-fatal error the game reported (<see cref="CrashReporter.ReportError"/>).</summary>
    Error,
}

/// <summary>How the player (or the game) dealt with an earlier run's report (<see cref="CrashReporter.MarkHandled"/>).</summary>
public enum CrashDisposition
{
    Dismissed,
    Copied,
    Sent,
}

/// <summary>The node callback that was running when an exception left it (<see cref="CrashReport"/>'s <c>session.callback</c>).</summary>
public enum NodeCallback
{
    None,
    EnterTree,
    Ready,
    ExitTree,
    Process,
    PhysicsProcess,
    Input,
    UnhandledInput,
    Deferred,
}

/// <summary>An earlier run's report bundle, as listed by <see cref="CrashReporter.PendingReports"/> (only its header is read).</summary>
/// <param name="Folder">The bundle folder (<c>{crashes}/2026-10-11T10-42-07-a1b2</c>).</param>
/// <param name="Id">The report id (32 hex digits).</param>
/// <param name="Kind">What happened.</param>
/// <param name="TimestampUtc">When it was written.</param>
/// <param name="Summary">One line for people: "NullReferenceException in PlayerController.OnPhysicsProcess".</param>
public readonly record struct CrashReportInfo(string Folder, string Id, CrashKind Kind, DateTime TimestampUtc, string Summary);

/// <summary>
/// Receives every report this run writes (the M13 Sentry shim, console heads, the editor link). Called on the crashing
/// thread, on the way down: keep it short and never throw.
/// </summary>
public interface ICrashSink
{
    /// <summary>Called before the bundle is written (M13's Sentry shim forwards first); default false (after).</summary>
    bool RunsBeforeWrite => false;

    /// <summary>The report (its <see cref="CrashReport.Folder"/> is set when called after the write). Must not throw.</summary>
    void OnCrash(CrashReport report);
}
