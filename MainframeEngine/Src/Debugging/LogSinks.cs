using System.Globalization;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// Writes entries to <see cref="Console.Out"/> in colour: <c>[12:00:00.123] [INFO]	[Audio] message</c>, plus the call
/// site while <see cref="Log.Level.Verbose"/> is set. Registered by default (<see cref="Log.ConsoleSink"/>).
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private static readonly bool Plain = Console.IsOutputRedirected;
    private static readonly string Normal = Plain ? string.Empty : "\x1b[39m";
    private static readonly string Red = Plain ? string.Empty : "\x1b[91m";
    private static readonly string Yellow = Plain ? string.Empty : "\x1b[93m";
    private static readonly string Blue = Plain ? string.Empty : "\x1b[94m";
    private static readonly string Grey = Plain ? string.Empty : "\x1b[97m";

    private readonly Lock _gate = new();

    /// <summary>Levels this sink prints (on top of <see cref="Log.LogLevel"/>). Default: all.</summary>
    public Log.Level Levels { get; set; } = (Log.Level)~0;

    public void Write(in LogEntry entry)
    {
        if ((Levels & entry.Level) == 0)
            return;

        var color = entry.Level switch
        {
            Log.Level.Debug => Grey,
            Log.Level.Info => Blue,
            Log.Level.Warning => Yellow,
            _ => Red,
        };
        var local = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var builder = new StringBuilder(entry.Message.Length + 48);
        builder.Append(Normal).Append('[').Append(local).Append(']').Append(color)
            .Append(" [").Append(LogEntry.LevelTag(entry.Level)).Append("]\t");
        if (entry.Category.Length > 0)
            builder.Append('[').Append(entry.Category).Append("] ");
        builder.Append(entry.Message);
        if (Log.LogLevel.HasFlag(Log.Level.Verbose))
            builder.Append(' ').Append(Normal).Append('[').Append(Path.GetFileName(entry.CallerFile)).Append(':')
                .Append(entry.CallerLine.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(entry.CallerMember).Append(']');
        builder.Append(Normal);

        var line = builder.ToString();
        lock (_gate)
            Console.WriteLine(line);
    }
}

/// <summary>
/// Keeps the last <see cref="Capacity"/> entries in memory for tools (an in-game console, the editor's Output panel,
/// crash reports). Thread-safe; writing never allocates once the ring is full.
/// </summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly Lock _gate = new();
    private readonly LogEntry[] _ring;
    private long _written;

    public MemoryLogSink(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new LogEntry[capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>Levels this sink keeps. Default: all.</summary>
    public Log.Level Levels { get; set; } = (Log.Level)~0;

    /// <summary>Entries currently held (≤ <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return (int)Math.Min(_written, _ring.Length);
        }
    }

    /// <summary>Total entries ever written; also a sequence number for <see cref="CopySince"/>.</summary>
    public long TotalWritten
    {
        get
        {
            lock (_gate)
                return _written;
        }
    }

    public void Write(in LogEntry entry)
    {
        if ((Levels & entry.Level) == 0)
            return;
        lock (_gate)
        {
            _ring[_written % _ring.Length] = entry;
            _written++;
        }
    }

    /// <summary>The held entries, oldest first.</summary>
    public LogEntry[] Snapshot()
    {
        lock (_gate)
        {
            var count = (int)Math.Min(_written, _ring.Length);
            var result = new LogEntry[count];
            CopyLocked(_written - count, result);
            return result;
        }
    }

    /// <summary>
    /// Copies entries with sequence numbers ≥ <paramref name="sequence"/> (oldest first; entries already overwritten
    /// are skipped) into <paramref name="destination"/>; returns how many were copied. Pass the returned
    /// <paramref name="next"/> next time to read only new entries.
    /// </summary>
    public int CopySince(long sequence, Span<LogEntry> destination, out long next)
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _written - _ring.Length);
            var from = Math.Clamp(sequence, oldest, _written);
            var count = (int)Math.Min(_written - from, destination.Length);
            CopyLocked(from, destination[..count]);
            next = from + count;
            return count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _written = 0;
        }
    }

    private void CopyLocked(long from, Span<LogEntry> destination)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = _ring[(from + i) % _ring.Length];
    }
}

