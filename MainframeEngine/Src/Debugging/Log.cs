using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// The engine log. Every message becomes a structured <see cref="LogEntry"/> (level, UTC time, category, message,
/// call site) and is handed to each registered <see cref="ILogSink"/>: the console (<see cref="ConsoleSink"/>, on by
/// default), a rotating file (<see cref="FileLogSink"/>), an in-memory ring for tools (<see cref="MemoryLogSink"/>), or
/// the editor link. <see cref="LogLevel"/> filters before anything is formatted: a filtered-out call — including an
/// interpolated <c>$"..."</c> message — allocates nothing.
/// </summary>
/// <remarks>
/// A message that starts with <c>[Name] </c> (the engine's convention, e.g. <c>"[Audio] ..."</c>) is logged under
/// category <c>Name</c>; <see cref="Write(Level, string, string, string, string, int)"/> takes the category explicitly.
/// Sinks may be called from any thread that logs; the sink list is copy-on-write, so logging takes no lock.
/// </remarks>
public static partial class Log
{
    [Flags]
    public enum Level
    {
        None = 0,
        Debug = 1 << 0,
        Info = 1 << 1,
        Warning = 1 << 2,
        Error = 1 << 3,
        Fatal = 1 << 4,

        /// <summary>
        /// Not a severity: the console sink appends the call site (member, file and line) when set.
        /// </summary>
        Verbose = 1 << 5,
    }

    /// <summary>The levels enabled by default: everything except <see cref="Level.Verbose"/>.</summary>
    public const Level DefaultLevel = (Level)~0 & ~Level.Verbose;

    private static volatile int s_level = (int)DefaultLevel;
    private static readonly Lock SinkGate = new();
    private static volatile ILogSink[] s_sinks;

    [ThreadStatic]
    private static bool t_inSink; // a sink that logs must not recurse into the sinks

    static Log()
    {
        ConsoleSink = new ConsoleLogSink();
        s_sinks = [ConsoleSink];
    }

    /// <summary>Enabled levels (flags). Messages at other levels are dropped before formatting.</summary>
    public static Level LogLevel
    {
        get => (Level)s_level;
        set => s_level = (int)value;
    }

    /// <summary>The console sink registered by default. Remove it with <see cref="RemoveSink"/> to silence stdout.</summary>
    public static ConsoleLogSink ConsoleSink { get; }

    /// <summary>The registered sinks (a snapshot).</summary>
    public static IReadOnlyList<ILogSink> Sinks => s_sinks;

    /// <summary>True when messages at <paramref name="level"/> pass <see cref="LogLevel"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(Level level) => (s_level & (int)level & ~(int)Level.Verbose) != 0;

    /// <summary>Adds <paramref name="sink"/> (no-op when it is already registered).</summary>
    public static void AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (SinkGate)
        {
            var current = s_sinks;
            if (Array.IndexOf(current, sink) >= 0)
                return;
            s_sinks = [.. current, sink];
        }
    }

    /// <summary>Removes <paramref name="sink"/>; returns false when it was not registered.</summary>
    public static bool RemoveSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (SinkGate)
        {
            var current = s_sinks;
            var index = Array.IndexOf(current, sink);
            if (index < 0)
                return false;
            var next = new ILogSink[current.Length - 1];
            current.AsSpan(0, index).CopyTo(next);
            current.AsSpan(index + 1).CopyTo(next.AsSpan(index));
            s_sinks = next;
            return true;
        }
    }

    /// <summary>Logs <paramref name="message"/> under an explicit <paramref name="category"/>.</summary>
    public static void Write(
        Level level,
        string category,
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(level))
            Dispatch(new LogEntry(SingleLevel(level), DateTime.UtcNow, category ?? string.Empty, message ?? string.Empty,
                sourceMemberName, sourceFilePath, sourceLineNumber));
    }

    public static void Debug(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(Level.Debug))
            Emit(Level.Debug, message, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    /// <summary>Interpolated form: nothing is formatted (or allocated) while <see cref="Level.Debug"/> is filtered out.</summary>
    public static void Debug(
        ref DebugInterpolatedStringHandler message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (message.Enabled)
            Emit(Level.Debug, message.ToStringAndClear(), sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Info(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(Level.Info))
            Emit(Level.Info, message, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    /// <summary>Interpolated form: nothing is formatted (or allocated) while <see cref="Level.Info"/> is filtered out.</summary>
    public static void Info(
        ref InfoInterpolatedStringHandler message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (message.Enabled)
            Emit(Level.Info, message.ToStringAndClear(), sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Warning(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(Level.Warning))
            Emit(Level.Warning, message, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    /// <summary>Interpolated form: nothing is formatted (or allocated) while <see cref="Level.Warning"/> is filtered out.</summary>
    public static void Warning(
        ref WarningInterpolatedStringHandler message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (message.Enabled)
            Emit(Level.Warning, message.ToStringAndClear(), sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Error(
        string message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(Level.Error))
            Emit(Level.Error, message, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    /// <summary>Interpolated form: nothing is formatted (or allocated) while <see cref="Level.Error"/> is filtered out.</summary>
    public static void Error(
        ref ErrorInterpolatedStringHandler message,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (message.Enabled)
            Emit(Level.Error, message.ToStringAndClear(), sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    public static void Fatal(
        Exception exception,
        [CallerMemberName] string sourceMemberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (IsEnabled(Level.Fatal))
            Emit(Level.Fatal, exception?.ToString() ?? string.Empty, sourceMemberName, sourceFilePath, sourceLineNumber);
    }

    private static void Emit(Level level, string message, string member, string file, int line)
    {
        message ??= string.Empty;
        var category = LogCategories.Split(message, out var body);
        Dispatch(new LogEntry(level, DateTime.UtcNow, category, body, member, file, line));
    }

    /// <summary>Hands an already-built entry to every sink (used by the editor link to replay a game's log).</summary>
    public static void Dispatch(in LogEntry entry)
    {
        if (t_inSink)
            return;
        t_inSink = true;
        try
        {
            foreach (var sink in s_sinks)
            {
                try
                {
                    sink.Write(entry);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A broken sink must never take the game down or stop the other sinks.
                    try
                    {
                        Console.Error.WriteLine($"[Log] Sink {sink.GetType().Name} failed: {e.Message}");
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        finally
        {
            t_inSink = false;
        }
    }

    // A combined value logs at its highest severity that is enabled (Write(Error | Debug) with only Debug enabled logs
    // a Debug entry; with both enabled, an Error). Verbose is never a severity.
    internal static Level SingleLevel(Level level)
    {
        var enabled = (int)level & s_level & ~(int)Level.Verbose;
        var severity = enabled != 0 ? enabled : (int)level & ~(int)Level.Verbose;
        return severity == 0 ? Level.None : (Level)(1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)severity)));
    }
}
