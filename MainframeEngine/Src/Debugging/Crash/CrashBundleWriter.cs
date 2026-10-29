using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Builds reports and writes bundles (<c>{crashes}/2026-10-11T10-42-07-a1b2/</c>: <c>report.json</c>, <c>log.txt</c>,
/// <c>settings.json</c>, and for unclean exits <c>game.log.tail</c> and a dump). Written to <c>&lt;folder&gt;.tmp</c> and
/// renamed, so a reader never sees half a bundle; bundles over <see cref="MaxBundleBytes"/> (dumps aside) are refused;
/// only the newest <see cref="CrashReporterOptions.MaxReports"/> are kept.
/// </summary>
internal sealed class CrashBundleWriter
{
    /// <summary>Largest bundle written, dumps aside.</summary>
    public const long MaxBundleBytes = 64L * 1024 * 1024;

    /// <summary>Exceptions of one chain kept (outermost first).</summary>
    public const int MaxExceptions = 16;

    /// <summary>Frames kept per exception (innermost first).</summary>
    public const int MaxFrames = 128;

    private const int MaxMessageChars = 8 * 1024;
    private const string TempSuffix = ".tmp";

    private readonly ArrayBufferWriter<byte> _buffer = new(256 * 1024);

    public CrashBundleWriter(CrashReporterOptions options, CrashScrubber scrubber)
    {
        Options = options;
        Scrubber = scrubber;
    }

    public CrashReporterOptions Options { get; }

    public CrashScrubber Scrubber { get; }

    /// <summary>The run started (for <c>uptimeSeconds</c>).</summary>
    public long StartTimestamp { get; } = Stopwatch.GetTimestamp();

    // ── Facts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public CrashAppInfo AppInfo() => new()
    {
        Name = Options.AppName,
        Version = Options.AppVersion,
        Engine = EngineInfo.Version,
        EngineCommit = EngineInfo.Commit,
        Demo = Options.Demo,
        DebugBuild = Options.DebugBuild,
        Runtime = RuntimeInformation.FrameworkDescription,
        Rid = RuntimeInformation.RuntimeIdentifier,
        Headless = Options.Headless,
        Editor = Options.Editor,
    };

    public static CrashOsInfo OsInfo()
    {
        long memoryMb = 0;
        try
        {
            memoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
        }

        return new CrashOsInfo
        {
            Description = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString(),
            Cpus = Environment.ProcessorCount,
            MemoryMB = memoryMb,
            Locale = CultureInfo.CurrentUICulture.Name,
        };
    }

    public List<string> Args()
    {
        var args = new List<string>();
        try
        {
            var all = Environment.GetCommandLineArgs();
            for (var i = 1; i < all.Length && args.Count < 64; i++)
                args.Add(Scrubber.Scrub(all[i]));
        }
        catch (NotSupportedException)
        {
        }

        return args;
    }

    /// <summary>A report of this run: the facts, the exception chain and the node it left (no providers, no handlers).</summary>
    public CrashReport Build(Exception? exception, CrashKind kind, string? note)
    {
        var report = new CrashReport
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Timestamp = DateTime.UtcNow,
            App = AppInfo(),
            Os = OsInfo(),
            Gpu = CrashContext.Gpu is { } gpu ? CrashGpuInfo.From(gpu) : null,
            Note = note is null ? null : Scrubber.Scrub(note),
        };
        report.Session.UptimeSeconds = Math.Round(Stopwatch.GetElapsedTime(StartTimestamp).TotalSeconds, 2);
        report.Session.Args = Args();
        report.Session.ProcessId = Environment.ProcessId;
        if (exception is null)
            return report;

        Flatten(exception, report.Exception);
        if (CrashContext.NodeOf(exception) is { } node)
        {
            report.Session.Node = node.Path;
            report.Session.NodeType = node.Type;
            report.Session.Callback = CrashContext.CallbackName(node.Callback);
        }

        if (FindDeviceLost(exception) is { } lost)
        {
            report.Kind = CrashKind.DeviceLost;
            report.GpuLoss = lost.Loss;
        }

        return report;
    }

    private static VulkanDeviceLostException? FindDeviceLost(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is VulkanDeviceLostException lost)
                return lost;
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (inner is VulkanDeviceLostException innerLost)
                        return innerLost;
        }

        return null;
    }

    /// <summary>Fills the session's scene and frame counts from the providers (each guarded).</summary>
    public static void ReadSessionProviders(CrashSessionInfo session)
    {
        try
        {
            session.Scene = CrashContext.Scene?.Invoke();
        }
        catch (Exception)
        {
        }

        try
        {
            session.RenderedFrames = CrashContext.RenderedFrames?.Invoke() ?? 0;
        }
        catch (Exception)
        {
        }

        try
        {
            session.ProcessFrames = CrashContext.ProcessFrames?.Invoke() ?? 0;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The exception chain, outermost first: inner exceptions and every aggregate member.</summary>
    public void Flatten(Exception exception, List<CrashException> into)
    {
        var queue = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(exception);
        while (queue.Count > 0 && into.Count < MaxExceptions)
        {
            var e = queue.Dequeue();
            if (!seen.Add(e))
                continue;
            into.Add(Describe(e));
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    queue.Enqueue(inner);
            else if (e.InnerException is { } innerException)
                queue.Enqueue(innerException);
        }
    }

    private CrashException Describe(Exception e)
    {
        var message = e.Message ?? "";
        if (message.Length > MaxMessageChars)
            message = message[..MaxMessageChars] + "…";
        var result = new CrashException { Type = e.GetType().FullName ?? e.GetType().Name, Message = Scrubber.Scrub(message) };
        StackFrame[] frames;
        try
        {
            frames = new StackTrace(e, fNeedFileInfo: true).GetFrames();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var frame in frames)
        {
            if (result.Frames.Count >= MaxFrames)
                break;
            result.Frames.Add(DescribeFrame(frame));
        }

        return result;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Method metadata may be missing in trimmed builds: the frame then keeps DiagnosticMethodInfo's names.")]
    private CrashFrame DescribeFrame(StackFrame frame)
    {
        var result = new CrashFrame();
        try
        {
            var il = frame.GetILOffset();
            if (il != StackFrame.OFFSET_UNKNOWN)
                result.IlOffset = il;
            if (frame.GetFileName() is { Length: > 0 } file)
            {
                result.File = Scrubber.Scrub(file);
                var line = frame.GetFileLineNumber();
                if (line > 0)
                    result.Line = line;
            }

            if (frame.GetMethod() is { } method)
            {
                result.Method = MethodName(method);
                var module = method.Module;
                result.Module = module.Name;
                result.Mvid = module.ModuleVersionId.ToString("D");
                result.Token = method.MetadataToken;
            }
            else if (DiagnosticMethodInfo.Create(frame) is { } info)
            {
                result.Method = info.DeclaringTypeName is { Length: > 0 } type ? $"{type}.{info.Name}" : info.Name;
                result.Module = info.DeclaringAssemblyName;
            }
        }
        catch (Exception)
        {
            // A frame that cannot be described keeps what it has.
        }

        return result;
    }

    private static string MethodName(MethodBase method)
    {
        var builder = new StringBuilder(96);
        if (method.DeclaringType is { } type)
            builder.Append(TypeName(type)).Append('.');
        builder.Append(method.Name);
        if (method.IsGenericMethod)
        {
            builder.Append('[');
            var arguments = method.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
                builder.Append(i > 0 ? "," : "").Append(arguments[i].Name);
            builder.Append(']');
        }

        builder.Append('(');
        try
        {
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
                builder.Append(i > 0 ? ", " : "").Append(parameters[i].ParameterType.Name);
        }
        catch (Exception)
        {
        }

        return builder.Append(')').ToString();
    }

    private static string TypeName(Type type)
    {
        // A constructed generic's FullName lists assembly-qualified arguments ("List`1[[System.Int32, …]]"): keep "List`1".
        var name = type.FullName ?? type.Name;
        var bracket = type.IsGenericType ? name.IndexOf('[', StringComparison.Ordinal) : -1;
        return bracket > 0 ? name[..bracket] : name;
    }

    // ── Writing ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An extra file of a bundle: bytes, or a file moved in (a dump).</summary>
    public readonly record struct BundleFile(string Name, byte[]? Bytes, string? MoveFrom = null);

    /// <summary>
    /// Writes <paramref name="report"/>'s bundle with its log (<paramref name="log"/>, already formatted) and
    /// <paramref name="files"/>; returns the folder, or null when it could not be written (logged to stderr).
    /// </summary>
    public string? WriteBundle(CrashReport report, string? log, byte[]? settings, IReadOnlyList<BundleFile>? files)
    {
        var directory = Options.Directory;
        var name = report.Timestamp.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture) + "-" + report.Id[..Math.Min(4, report.Id.Length)];
        var final = Path.Combine(directory, name);
        var temp = final + TempSuffix;
        try
        {
            Directory.CreateDirectory(directory);
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            long bytes = 0;
            if (log is { Length: > 0 })
            {
                var logBytes = Encoding.UTF8.GetBytes(Scrubber.Scrub(log));
                File.WriteAllBytes(Path.Combine(temp, CrashReport.LogFileName), logBytes);
                bytes += logBytes.Length;
                report.Log ??= CrashReport.LogFileName;
            }

            if (settings is { Length: > 0 })
            {
                var scrubbed = Encoding.UTF8.GetBytes(Scrubber.Scrub(Encoding.UTF8.GetString(settings)));
                File.WriteAllBytes(Path.Combine(temp, CrashReport.SettingsFileName), scrubbed);
                bytes += scrubbed.Length;
            }

            if (files is not null)
            {
                foreach (var file in files)
                {
                    var target = Path.Combine(temp, file.Name);
                    if (file.Bytes is { } content)
                    {
                        File.WriteAllBytes(target, content);
                        bytes += content.Length;
                    }
                    else if (file.MoveFrom is { } source && File.Exists(source))
                    {
                        File.Move(source, target);
                    }
                }
            }

            _buffer.Clear();
            using (var writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = true }))
                report.WriteTo(writer);
            bytes += _buffer.WrittenCount;
            if (bytes > MaxBundleBytes)
            {
                Directory.Delete(temp, recursive: true);
                Stderr($"[Crash] The report bundle would be {bytes / (1024 * 1024)} MB (over {MaxBundleBytes / (1024 * 1024)} MB): not written.");
                return null;
            }

            using (var stream = new FileStream(Path.Combine(temp, CrashReport.FileName), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(_buffer.WrittenSpan);
                stream.Flush(flushToDisk: true);
            }

            if (Directory.Exists(final))
                Directory.Delete(final, recursive: true);
            Directory.Move(temp, final);
            report.Folder = final;
            Prune();
            return final;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Stderr($"[Crash] Could not write the report to '{final}': {e.Message}");
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="CrashReporterOptions.MaxReports"/> bundles; removes leftover <c>.tmp</c> folders.</summary>
    public void Prune()
    {
        try
        {
            var bundles = new List<string>();
            foreach (var folder in Directory.GetDirectories(Options.Directory))
            {
                if (folder.EndsWith(TempSuffix, StringComparison.Ordinal))
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddMinutes(-5))
                        Directory.Delete(folder, recursive: true); // a crash while writing; never one being written now
                    continue;
                }

                if (File.Exists(Path.Combine(folder, CrashReport.FileName)))
                    bundles.Add(folder);
            }

            // Names start with the UTC timestamp: ordinal order is age order.
            bundles.Sort(StringComparer.Ordinal);
            for (var i = 0; i < bundles.Count - Options.MaxReports; i++)
                Directory.Delete(bundles[i], recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The log ring as the console formats it (one entry per line, call sites for warnings and above).</summary>
    public static string FormatLog(ReadOnlySpan<LogEntry> entries)
    {
        var builder = new StringBuilder(entries.Length * 96);
        foreach (var entry in entries)
        {
            entry.AppendTo(builder, includeCallSite: entry.Level >= Log.Level.Warning);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Writes to stderr, never throwing (the crash path's last resort).</summary>
    public static void Stderr(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (Exception)
        {
        }
    }

    // ── Unclean exits ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the <see cref="CrashKind.UncleanExit"/> (or <see cref="CrashKind.NativeDump"/>) report of a run whose
    /// sentinel was left behind: its facts, the tail of its log, and the dump the runtime wrote for it
    /// (<c>dump-&lt;pid&gt;.dmp</c> in the crash folder), when there is one. <paramref name="exitCode"/> is known when the
    /// process was watched (the supervisor). Returns the folder, or null.
    /// </summary>
    public string? WriteUncleanExit(CrashSessionFile session, int? exitCode, string? currentLogFile)
    {
        var report = new CrashReport
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = CrashKind.UncleanExit,
            Timestamp = DateTime.UtcNow,
            App = session.App,
            Os = session.Os,
            Gpu = session.Gpu,
            ExitCode = exitCode,
            Note = string.Create(CultureInfo.InvariantCulture,
                $"The run started at {session.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC ended without a clean shutdown and without a report: a native crash, a stack overflow, a fail fast, a kill or a power loss."),
        };
        report.Session.ProcessId = session.ProcessId;
        report.Session.Args = session.Args;
        report.Session.UptimeSeconds = 0;

        var files = new List<BundleFile>();
        if (PreviousLogOf(session, currentLogFile) is { } logPath && CrashSession.ReadTail(logPath) is { Length: > 0 } tail)
        {
            files.Add(new BundleFile(CrashReport.LogTailFileName, Encoding.UTF8.GetBytes(Scrubber.Scrub(Encoding.UTF8.GetString(tail)))));
            report.Log = CrashReport.LogTailFileName;
        }

        var dump = DumpPathFor(Options.Directory, session.ProcessId);
        if (File.Exists(dump))
        {
            report.Kind = CrashKind.NativeDump;
            files.Add(new BundleFile(CrashReport.DumpFileName, null, dump));
            // The runtime's own crash report (DOTNET_EnableCrashReport, Linux and macOS): native and managed stacks.
            var runtimeReport = dump + ".crashreport.json";
            if (File.Exists(runtimeReport))
                files.Add(new BundleFile("crashreport.json", null, runtimeReport));
        }

        return WriteBundle(report, null, null, files);
    }

    /// <summary>Where the runtime writes a dump of process <paramref name="pid"/> (<c>DOTNET_DbgMiniDumpName</c> = <c>dump-%p.dmp</c>).</summary>
    public static string DumpPathFor(string directory, int pid) =>
        Path.Combine(directory, "dump-" + pid.ToString(CultureInfo.InvariantCulture) + ".dmp");

    /// <summary>
    /// The previous log of a dead session: its file, or — when this run's <see cref="FileLogSink"/> took the same name and
    /// rotated it away — the <c>.1</c> file it was rotated to.
    /// </summary>
    internal static string? PreviousLogOf(CrashSessionFile session, string? currentLogFile)
    {
        if (session.LogFile is not { Length: > 0 } logFile)
            return null;
        if (currentLogFile is not null && string.Equals(Path.GetFullPath(logFile), Path.GetFullPath(currentLogFile), StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(logFile) ?? "";
            var rotated = Path.Combine(directory, Path.GetFileNameWithoutExtension(logFile) + ".1.log");
            return File.Exists(rotated) ? rotated : null;
        }

        return File.Exists(logFile) ? logFile : null;
    }
}
