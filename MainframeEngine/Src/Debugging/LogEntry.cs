using System.Globalization;
using System.Text;

namespace MainframeEngine;

/// <summary>One log message, as handed to every <see cref="ILogSink"/>.</summary>
/// <param name="Level">A single severity flag (<see cref="Log.Level.Debug"/> … <see cref="Log.Level.Fatal"/>).</param>
/// <param name="Timestamp">When it was logged (UTC).</param>
/// <param name="Category">The subsystem (<c>"Audio"</c> for <c>"[Audio] ..."</c>); empty when none was given.</param>
/// <param name="Message">The text, without the <c>[Category] </c> prefix.</param>
/// <param name="CallerMember">The calling member.</param>
/// <param name="CallerFile">The calling source file (full path at compile time).</param>
/// <param name="CallerLine">The calling line.</param>
public readonly record struct LogEntry(
    Log.Level Level,
    DateTime Timestamp,
    string Category,
    string Message,
    string CallerMember,
    string CallerFile,
    int CallerLine)
{
    /// <summary>Who logged it, from <see cref="CallerFile"/> (<see cref="Log.SourceOf"/>): the engine or the game.</summary>
    public LogSource Source => Log.SourceOf(CallerFile);

    /// <summary>The fixed-width tag the console and file sinks print (<c>INFO</c>, <c>WARN</c>, …).</summary>
    public static string LevelTag(Log.Level level) => level switch
    {
        Log.Level.Debug => "Debug",
        Log.Level.Info => "INFO",
        Log.Level.Warning => "WARN",
        Log.Level.Error => "ERROR",
        Log.Level.Fatal => "FATAL",
        _ => "LOG",
    };

    /// <summary>
    /// One line: <c>2026-10-05T12:00:00.123Z [WARN] engine Audio: message</c> (<c>game Net: …</c> for the game's own
    /// entries; no <c>Name: </c> without a category), plus <c>(File.cs:12 Member)</c> when <paramref name="includeCallSite"/>.
    /// Used by the file sink and tools.
    /// </summary>
    public string Format(bool includeCallSite)
    {
        var builder = new StringBuilder(Message.Length + 64);
        AppendTo(builder, includeCallSite);
        return builder.ToString();
    }

    /// <summary>Appends <see cref="Format"/>'s line to <paramref name="builder"/>.</summary>
    public void AppendTo(StringBuilder builder, bool includeCallSite)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelTag(Level)).Append("] ");
        this.AppendSourceAndCategory(builder);
        builder.Append(Message);
        if (includeCallSite && CallerFile.Length > 0)
            builder.Append(" (").Append(Path.GetFileName(CallerFile)).Append(':')
                .Append(CallerLine.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(CallerMember).Append(')');
    }
}

/// <summary>Shared by the sinks: the <c>engine Audio: </c> / <c>game Net: </c> / <c>game </c> part of a line.</summary>
public static class LogEntryFormat
{
    public static void AppendSourceAndCategory(this in LogEntry entry, StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(Log.SourceTag(entry.Source)).Append(' ');
        if (entry.Category.Length > 0)
            builder.Append(entry.Category).Append(": ");
    }
}

/// <summary>
/// Receives every enabled log message (see <see cref="Log.AddSink"/>). Called on the thread that logged, possibly
/// several at once: implementations must be thread-safe and fast (queue slow work; never block on I/O peers).
/// </summary>
public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>
/// Splits the engine's <c>"[Category] message"</c> convention. Category strings are interned in a lock-free map
/// looked up by span, so a known category costs no allocation; the message body is one substring.
/// </summary>
internal static class LogCategories
{
    private const int MaxCategoryLength = 48;
    private const int MaxInterned = 1024; // beyond this (a runaway generator of categories) names are not cached
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Interned = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Lookup =
        Interned.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The category of <paramref name="message"/> (empty when it has none) and the rest of the text.</summary>
    public static string Split(string message, out string body)
    {
        body = message;
        if (message.Length < 3 || message[0] != '[')
            return string.Empty;
        var end = message.AsSpan(1, Math.Min(message.Length - 1, MaxCategoryLength + 1)).IndexOf(']');
        if (end <= 0)
            return string.Empty;
        var name = message.AsSpan(1, end);
        if (name.ContainsAny(' ', '\t', '['))
            return string.Empty;
        var rest = end + 2; // past "]"
        if (rest < message.Length && message[rest] == ' ')
            rest++;
        else if (rest < message.Length && message[rest] != '\t')
            return string.Empty; // "[x]y" is not a category prefix

        while (rest < message.Length && message[rest] == '\t')
            rest++;
        body = rest >= message.Length ? string.Empty : message[rest..];
        return Intern(name);
    }

    public static string Intern(ReadOnlySpan<char> name)
    {
        if (Lookup.TryGetValue(name, out var existing))
            return existing;
        var created = name.ToString();
        return Interned.Count < MaxInterned ? Interned.GetOrAdd(created, created) : created;
    }
}
