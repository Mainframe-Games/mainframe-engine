namespace MainframeEngine.Editor;

/// <summary>
/// The editor's own crash reporting (G15, docs/design/editor.md#crashes): <see cref="CrashReporter"/> in
/// <c>~/.mainframe/crashes</c> with a rotating <c>~/.mainframe/logs/editor.log</c>, the recovery copies of unsaved scenes
/// listed in the report, and a clean end that removes the session sentinel. The next start's <see cref="CrashDialog"/>
/// offers the report and the copies back.
/// </summary>
public static class EditorCrashReporting
{
    /// <summary>The editor log's file name (without <c>.log</c>).</summary>
    public const string LogName = "editor";

    /// <summary>Installs the reporter in <paramref name="crashDirectory"/> (null: nothing) with a file log in <paramref name="logDirectory"/>.</summary>
    public static FileLogSink? Install(string? crashDirectory, string logDirectory)
    {
        if (crashDirectory is null)
            return null;
        FileLogSink? log = null;
        try
        {
            log = new FileLogSink(logDirectory, LogName);
            Log.AddSink(log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Editor] No log file in '{logDirectory}': {e.Message}");
        }

        CrashReporter.Install(new CrashReporterOptions
        {
            Directory = crashDirectory,
            AppName = "Mainframe Editor",
            AppVersion = EngineInfo.Version,
            Editor = true,
            FileLog = log,
#if DEBUG
            DebugBuild = true,
#endif
        });
        return log;
    }

    /// <summary>Lists <paramref name="copies"/> in the report (<see cref="CrashDialog.RecoveryCopiesKey"/>).</summary>
    public static void ListRecoveryCopies(CrashReport report, IReadOnlyList<string> copies)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count > 0)
            report.Context[CrashDialog.RecoveryCopiesKey] = string.Join('\n', copies);
    }

    /// <summary>
    /// An exception filter for the editor's main loop: writes the report (kind <see cref="CrashKind.ManagedException"/>)
    /// and returns false, so the exception still ends the process as before (the runtime's unhandled-exception path then
    /// finds the report written and adds nothing).
    /// </summary>
    public static bool ReportMainThread(Exception exception)
    {
        CrashReporter.WriteReport(exception, CrashKind.ManagedException);
        return false;
    }

    /// <summary>A clean exit: the sentinel goes, then the log.</summary>
    public static void End(FileLogSink? log)
    {
        CrashReporter.Shutdown();
        if (log is null)
            return;
        Log.RemoveSink(log);
        log.Dispose();
    }
}
