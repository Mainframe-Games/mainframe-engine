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
    /// One line: <c>2026-10-05T12:00:00.123Z [WARN] [Audio] message</c>, plus <c>(File.cs:12 Member)</c> when
    /// <paramref name="includeCallSite"/>. Used by the file sink and tools.
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
        if (Category.Length > 0)
            builder.Append('[').Append(Category).Append("] ");
        builder.Append(Message);
        if (includeCallSite && CallerFile.Length > 0)
            builder.Append(" (").Append(Path.GetFileName(CallerFile)).Append(':')
                .Append(CallerLine.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(CallerMember).Append(')');
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

/// <summary>Splits the engine's <c>"[Category] message"</c> convention; category strings are interned.</summary>
internal static class LogCategories
{
    private const int MaxCategoryLength = 48;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, string> Interned = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Lookup =
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

        body = rest >= message.Length ? string.Empty : message[rest..].TrimStart('\t');
        return Intern(name);
    }

    public static string Intern(ReadOnlySpan<char> name)
    {
        lock (Gate)
        {
            if (Lookup.TryGetValue(name, out var existing))
                return existing;
            var created = name.ToString();
            Interned[created] = created;
            return created;
        }
    }
}
