using System.Collections.Concurrent;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Crash reporting (G15, docs/design/crash-reporting.md): every fatal managed error writes a report bundle to the crash
/// folder first — the exception chain with its frames, the node and callback it left, versions, OS, GPU and driver, the
/// scene, frame counts and the last log entries — and an unclean end (a native fault, a kill, power loss) is noticed on
/// the next start through a session sentinel. The next start lists the bundles (<see cref="PendingReports"/>) for the
/// player's prompt (<see cref="CrashPrompt"/>); nothing is sent anywhere without a project endpoint and the player's
/// consent (<see cref="Uploader"/>, <see cref="UploadConsent"/>).
/// </summary>
/// <remarks>
/// <para><see cref="GameHost.Run"/>, <see cref="HeadlessHost"/> and the editor install it; tools and tests may. Nothing
/// runs per frame: the scene tree's exception filters only run on a throw, the log ring stops allocating once full,
/// and context providers run only at crash time.</para>
/// <para>On the way down: only the first fatal crash writes (a crash while writing goes to stderr); a 1 MB reserve is
/// released first so an out-of-memory report still fits; providers get <see cref="CrashReporterOptions.ProviderBudget"/>;
/// the order is facts, <see cref="SetContext"/> keys, <see cref="Crashing"/> handlers, the write, then the
/// <see cref="ICrashSink"/>s (those with <see cref="ICrashSink.RunsBeforeWrite"/> before the write); the file log is
/// flushed last. The managed layer starts no process and opens no connection (consoles); only
/// <see cref="HttpCrashUploader"/> does, and only when the game configured it.</para>
/// </remarks>
public static class CrashReporter
{
    private const int ReserveBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly Lock WriteGate = new();
    private static readonly ConcurrentDictionary<string, Func<string?>> ContextProviders = new(StringComparer.Ordinal);
    private static readonly HashSet<Type> ReportedTypes = [];
    private static volatile ICrashSink[] s_sinks = [];
    private static CrashBundleWriter? s_writer;
    private static MemoryLogSink? s_ring;
    private static CrashSession? s_session;
    private static byte[]? s_reserve;
    private static Task<List<CrashReportInfo>>? s_pending;
    private static int s_fatal;
    private static bool s_handlersInstalled;

    /// <summary>Whether <see cref="Install"/> ran (and <see cref="Shutdown"/> did not since).</summary>
    public static bool IsInstalled => s_writer is not null;

    /// <summary>The crash folder (<c>{user data}/crashes</c>); empty before <see cref="Install"/>.</summary>
    public static string Directory => s_writer?.Options.Directory ?? "";

    /// <summary>The options it was installed with (null before <see cref="Install"/>).</summary>
    public static CrashReporterOptions? Options => s_writer?.Options;

    /// <summary>
    /// The player agreed to send reports (the prompt's "Always send reports"; persisted in <c>settings.json</c> by
    /// <see cref="UserSettings.CrashUploadConsent"/>). Only matters with an <see cref="Uploader"/>.
    /// </summary>
    public static bool UploadConsent { get; set; }

    /// <summary>Sends reports to the game's endpoint (default: from <c>project.mfproj</c> <c>crash.upload</c>; null sends nothing).</summary>
    public static ICrashUploader? Uploader { get; set; }

    /// <summary>
    /// Raised on the way down with the report about to be written (on the crashing thread): add to its
    /// <see cref="CrashReport.Context"/>, write a recovery copy. Keep it short; a handler that throws is skipped.
    /// </summary>
    public static event Action<CrashReport>? Crashing;

    /// <summary>
    /// Sets up the reporter (idempotent: a second call keeps the first options): the crash folder, the log ring, the
    /// unhandled-exception handlers, the session sentinel; turns sentinels earlier runs left behind into unclean-exit
    /// reports, prunes old bundles, and starts listing <see cref="PendingReports"/> on a worker.
    /// </summary>
    public static void Install(CrashReporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Directory);
        lock (Gate)
        {
            if (s_writer is not null)
                return;
            var writer = new CrashBundleWriter(options, CrashScrubber.ForThisMachine());
            try
            {
                System.IO.Directory.CreateDirectory(options.Directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Crash] The crash folder '{options.Directory}' cannot be created: {e.Message}");
            }

            s_reserve = GC.AllocateUninitializedArray<byte>(ReserveBytes);
            s_ring = new MemoryLogSink(options.LogEntries);
            Log.AddSink(s_ring);
            s_fatal = 0;
            s_writer = writer;
            if (options.HandleUnhandledExceptions && !s_handlersInstalled)
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                s_handlersInstalled = true;
            }

            if (options.TrackSessions)
            {
                CollectUncleanExits(writer);
                s_session = CrashSession.Begin(options.Directory, NewSessionFile(writer));
            }

            writer.Prune();
            var directory = options.Directory;
            s_pending = Task.Run(() => ListReports(directory));
        }
    }

    /// <summary>
    /// A clean end of the run: removes the session sentinel (no unclean-exit report next time), the handlers and the log
    /// ring. Context providers, sinks and <see cref="Crashing"/> handlers are cleared too.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (s_writer is null)
                return;
            s_session?.Dispose();
            s_session = null;
            if (s_ring is not null)
                Log.RemoveSink(s_ring);
            s_ring = null;
            if (s_handlersInstalled)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                s_handlersInstalled = false;
            }

            s_writer = null;
            s_reserve = null;
            s_pending = null;
            s_sinks = [];
            Crashing = null;
            Uploader = null;
            UploadConsent = false;
            ContextProviders.Clear();
            lock (ReportedTypes)
                ReportedTypes.Clear();
            CrashContext.Reset();
        }
    }

    // ── Earlier runs ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports of earlier runs nobody handled yet (<see cref="MarkHandled"/>), newest first. Read from the bundles'
    /// headers on a worker started by <see cref="Install"/>; the first access waits for it. Empty before
    /// <see cref="Install"/>. (M13 relies on this name.)
    /// </summary>
    public static IReadOnlyList<CrashReportInfo> PendingReports
    {
        get
        {
            var pending = s_pending;
            if (pending is null)
                return [];
            try
            {
                lock (pending.Result)
                    return [.. pending.Result];
            }
            catch (AggregateException)
            {
                return [];
            }
        }
    }

    /// <summary>The whole report of <paramref name="info"/>, or null when its bundle is gone or damaged.</summary>
    public static CrashReport? Load(CrashReportInfo info) => LoadFolder(info.Folder);

    /// <summary>The report in bundle <paramref name="folder"/>, or null when it is missing or damaged.</summary>
    public static CrashReport? LoadFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            var report = CrashReport.FromJson(File.ReadAllBytes(Path.Combine(folder, CrashReport.FileName)));
            report.Folder = folder;
            return report;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Records that the player saw the report (it is not offered again); the bundle is kept until pruned.</summary>
    public static void MarkHandled(CrashReportInfo info, CrashDisposition how)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new CrashHandledFile { Disposition = how.ToString(), HandledUtc = DateTime.UtcNow },
                CrashJsonContext.Default.CrashHandledFile);
            File.WriteAllBytes(Path.Combine(info.Folder, HandledFileName), json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Crash] Could not mark the report '{info.Folder}' handled: {e.Message}");
        }

        Forget(info);
    }

    /// <summary>Deletes the report's bundle.</summary>
    public static void Delete(CrashReportInfo info)
    {
        try
        {
            if (System.IO.Directory.Exists(info.Folder))
                System.IO.Directory.Delete(info.Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Crash] Could not delete the report '{info.Folder}': {e.Message}");
        }

        Forget(info);
    }

    /// <summary>The marker <see cref="MarkHandled"/> writes in a bundle.</summary>
    public const string HandledFileName = "handled.json";

    private static void Forget(CrashReportInfo info)
    {
        if (s_pending is not { IsCompletedSuccessfully: true } pending)
            return;
        lock (pending.Result)
            pending.Result.RemoveAll(r => string.Equals(r.Folder, info.Folder, StringComparison.Ordinal));
    }

    /// <summary>The unhandled bundles in <paramref name="directory"/>, newest first (headers only).</summary>
    internal static List<CrashReportInfo> ListReports(string directory)
    {
        var result = new List<CrashReportInfo>();
        string[] folders;
        try
        {
            folders = System.IO.Directory.Exists(directory) ? System.IO.Directory.GetDirectories(directory) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var folder in folders)
        {
            if (folder.EndsWith(".tmp", StringComparison.Ordinal) || File.Exists(Path.Combine(folder, HandledFileName)))
                continue;
            if (LoadFolder(folder) is not { } report)
                continue;
            result.Add(new CrashReportInfo(folder, report.Id, report.Kind, report.Timestamp, report.Summary));
        }

        result.Sort(static (a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return result;
    }

    // ── This run ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fatal path: writes the report of <paramref name="exception"/> and returns its bundle folder (null when not
    /// installed, when a crash was already written this run, or when it could not be written). Never throws.
    /// </summary>
    public static string? WriteReport(Exception exception, CrashKind kind)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (s_writer is null)
            return null;
        if (Interlocked.Exchange(ref s_fatal, 1) == 1)
        {
            CrashBundleWriter.Stderr($"[Crash] Another fatal error while reporting the first: {exception.GetType().FullName}: {exception.Message}");
            return null;
        }

        s_reserve = null; // headroom for an out-of-memory report
        return WriteCore(exception, kind, note: null, fatal: true);
    }

    /// <summary>
    /// A non-fatal error worth a report (the game plays on): at most one report per exception type per run. Returns the
    /// bundle folder, or null when skipped.
    /// </summary>
    public static string? ReportError(Exception exception, string? note = null) => ReportError(exception, CrashKind.Error, note);

    internal static string? ReportError(Exception exception, CrashKind kind, string? note)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (s_writer is null || Volatile.Read(ref s_fatal) == 1)
            return null;
        lock (ReportedTypes)
        {
            if (!ReportedTypes.Add(exception.GetType()))
                return null;
        }

        return WriteCore(exception, kind, note, fatal: false);
    }

    /// <summary>
    /// A game key in every report (<c>"level"</c> → the level's name): <paramref name="provider"/> runs only at crash time
    /// (with a time budget; one that throws is skipped). A null provider removes the key.
    /// </summary>
    public static void SetContext(string key, Func<string?>? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (provider is null)
            ContextProviders.TryRemove(key, out _);
        else
            ContextProviders[key] = provider;
    }

    /// <summary>Adds a sink (the M13 Sentry shim, a console head, the editor link). No-op when already added.</summary>
    public static void AddSink(ICrashSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            if (Array.IndexOf(s_sinks, sink) < 0)
                s_sinks = [.. s_sinks, sink];
        }
    }

    /// <summary>Removes a sink; false when it was not added.</summary>
    public static bool RemoveSink(ICrashSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            var index = Array.IndexOf(s_sinks, sink);
            if (index < 0)
                return false;
            s_sinks = [.. s_sinks.AsSpan(0, index), .. s_sinks.AsSpan(index + 1)];
            return true;
        }
    }

    private static string? WriteCore(Exception exception, CrashKind kind, string? note, bool fatal)
    {
        try
        {
            lock (WriteGate)
            {
                var writer = s_writer;
                if (writer is null)
                    return null;
                var report = writer.Build(exception, kind, note);
                byte[]? settings = null;
                RunProviders(report, writer.Options.ProviderBudget, ref settings);
                RaiseCrashing(report);

                var sinks = s_sinks;
                foreach (var sink in sinks)
                    if (sink.RunsBeforeWrite)
                        CallSink(sink, report);

                var log = s_ring is { } ring ? CrashBundleWriter.FormatLog(ring.Snapshot()) : null;
                var folder = writer.WriteBundle(report, log, settings, null);
                if (folder is not null && fatal)
                    s_session?.SetReport(report.Id);

                foreach (var sink in sinks)
                    if (!sink.RunsBeforeWrite)
                        CallSink(sink, report);

                if (folder is not null)
                    CrashBundleWriter.Stderr($"[Crash] {report.Summary}: report written to {folder}");
                try
                {
                    writer.Options.FileLog?.Flush();
                }
                catch (Exception)
                {
                }

                return folder;
            }
        }
        catch (Exception e)
        {
            CrashBundleWriter.Stderr($"[Crash] Writing the report failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // Scene, frames, settings and the game's keys, on a worker with a time budget: a provider that stalls (a lock the
    // crashing thread holds) costs at most the budget, and the report goes without what it did not deliver.
    private static void RunProviders(CrashReport report, TimeSpan budget, ref byte[]? settings)
    {
        var session = new CrashSessionInfo();
        var context = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        byte[]? snapshot = null;
        var keys = ContextProviders.ToArray();
        void Collect()
        {
            CrashBundleWriter.ReadSessionProviders(session);
            foreach (var (key, provider) in keys)
            {
                try
                {
                    if (provider() is { } value)
                        context[key] = value;
                }
                catch (Exception)
                {
                }
            }

            try
            {
                snapshot = CrashContext.Settings?.Invoke();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            var task = Task.Run(Collect);
            task.Wait(budget);
        }
        catch (Exception)
        {
            // No thread to run them on (out of memory): no providers.
        }

        // A stalled worker may still write these later: the report keeps what was there when the budget ran out.
        report.Session.Scene = session.Scene;
        report.Session.RenderedFrames = session.RenderedFrames;
        report.Session.ProcessFrames = session.ProcessFrames;

        foreach (var (key, value) in context)
            report.Context[key] = s_writer?.Scrubber.Scrub(value) ?? value;
        settings = snapshot;
    }

    private static void RaiseCrashing(CrashReport report)
    {
        if (Crashing is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<CrashReport>)handler)(report);
            }
            catch (Exception e)
            {
                CrashBundleWriter.Stderr($"[Crash] A Crashing handler failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static void CallSink(ICrashSink sink, CrashReport report)
    {
        try
        {
            sink.OnCrash(report);
        }
        catch (Exception e)
        {
            CrashBundleWriter.Stderr($"[Crash] Sink {sink.GetType().Name} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteReport(exception, CrashKind.ThreadException);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        ReportError(e.Exception, CrashKind.UnobservedTask, "A task faulted and nobody observed its exception.");

    // ── Sessions ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static CrashSessionFile NewSessionFile(CrashBundleWriter writer) => new()
    {
        ProcessId = Environment.ProcessId,
        StartedUtc = DateTime.UtcNow,
        App = writer.AppInfo(),
        Os = CrashBundleWriter.OsInfo(),
        Gpu = CrashContext.Gpu is { } gpu ? CrashGpuInfo.From(gpu) : null,
        Args = writer.Args(),
        LogFile = writer.Options.FileLog?.FilePath,
    };

    private static void CollectUncleanExits(CrashBundleWriter writer)
    {
        foreach (var dead in CrashSession.CollectDead(writer.Options.Directory, Environment.ProcessId))
        {
            if (dead.File.ReportId is not null)
                continue; // its end has a report already
            if (writer.WriteUncleanExit(dead.File, exitCode: null, writer.Options.FileLog?.FilePath) is { } folder)
                Log.Warning($"[Crash] The previous run (pid {dead.File.ProcessId}) ended without a clean shutdown: {folder}");
        }
    }

    internal static void OnGpuKnown(GpuInfo gpu) => s_session?.SetGpu(CrashGpuInfo.From(gpu));

    /// <summary>
    /// For a process that watched another one (the supervisor): turns <paramref name="pid"/>'s leftover sentinel in
    /// <paramref name="options"/>' folder into an unclean-exit report with its exit code (and its dump). Null when it
    /// left none (it exited cleanly or wrote a report itself).
    /// </summary>
    internal static string? WriteExitedProcessReport(CrashReporterOptions options, int pid, int exitCode)
    {
        var writer = new CrashBundleWriter(options, CrashScrubber.ForThisMachine());
        foreach (var dead in CrashSession.CollectDead(options.Directory, Environment.ProcessId))
        {
            if (dead.File.ProcessId != pid)
            {
                if (dead.File.ReportId is null)
                    writer.WriteUncleanExit(dead.File, null, null);
                continue;
            }

            if (dead.File.ReportId is not null)
                return null;
            return writer.WriteUncleanExit(dead.File, exitCode, null);
        }

        return null;
    }

    /// <summary>For tests: the log ring the reporter keeps.</summary>
    internal static MemoryLogSink? Ring => s_ring;

    /// <summary>For tests: whether the reserve is still held (released by the first fatal report).</summary>
    internal static bool HoldsReserve => s_reserve is not null;
}
