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

/// <summary>
/// Appends entries to <c>{Directory}/{BaseName}.log</c> as UTF-8 lines (<see cref="LogEntry.Format"/>, with call sites
/// for warnings and above). The previous run's file is kept as <c>{BaseName}.1.log</c> (up to
/// <see cref="MaxFiles"/> files); a file that reaches <see cref="MaxBytes"/> rotates the same way while running.
/// <see cref="ForUser"/> puts it in the per-user data folder (<see cref="UserDataPaths"/>).
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _line = new(256);
    private StreamWriter? _writer;
    private long _bytes;
    private bool _disposed;

    public FileLogSink(string directory, string baseName = "game", long maxBytes = 10 * 1024 * 1024, int maxFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        Directory = Path.GetFullPath(directory);
        BaseName = baseName;
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
        System.IO.Directory.CreateDirectory(Directory);
        Rotate(); // keep the previous run as .1
        Open();
    }

    /// <summary>A sink writing to <c>{user data}/{gameName}/logs/{gameName}.log</c>.</summary>
    public static FileLogSink ForUser(string gameName, long maxBytes = 10 * 1024 * 1024, int maxFiles = 5) =>
        new(UserDataPaths.LogDirectory(gameName), UserDataPaths.SafeName(gameName), maxBytes, maxFiles);

    public string Directory { get; }

    public string BaseName { get; }

    /// <summary>Size at which the current file rotates.</summary>
    public long MaxBytes { get; }

    /// <summary>Files kept, including the current one.</summary>
    public int MaxFiles { get; }

    /// <summary>Levels this sink writes. Default: all.</summary>
    public Log.Level Levels { get; set; } = (Log.Level)~0;

    /// <summary>The file being written.</summary>
    public string FilePath => PathOf(0);

    public void Write(in LogEntry entry)
    {
        if ((Levels & entry.Level) == 0)
            return;
        lock (_gate)
        {
            if (_disposed || _writer is null)
                return;
            _line.Clear();
            entry.AppendTo(_line, includeCallSite: entry.Level >= Log.Level.Warning);
            _line.Append('\n');
            try
            {
                _writer.Write(_line);
                _writer.Flush(); // a crash must not lose the last lines
                foreach (var chunk in _line.GetChunks())
                    _bytes += Encoding.UTF8.GetByteCount(chunk.Span);
                if (_bytes >= MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    Rotate();
                    Open();
                }
            }
            catch (IOException)
            {
                // Disk full or the file vanished: logging must never crash the game.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string PathOf(int index) =>
        Path.Combine(Directory, index == 0 ? BaseName + ".log" : $"{BaseName}.{index.ToString(CultureInfo.InvariantCulture)}.log");

    private void Open()
    {
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _bytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
    }

    // game.log → game.1.log → … → game.{MaxFiles-1}.log; the oldest is deleted.
    private void Rotate()
    {
        try
        {
            if (!File.Exists(PathOf(0)))
                return;
            var last = PathOf(MaxFiles - 1);
            if (MaxFiles == 1)
            {
                File.Delete(last);
                return;
            }

            if (File.Exists(last))
                File.Delete(last);
            for (var i = MaxFiles - 2; i >= 0; i--)
            {
                var from = PathOf(i);
                if (File.Exists(from))
                    File.Move(from, PathOf(i + 1), overwrite: true);
            }
        }
        catch (IOException)
        {
            // Another process holds an old log open (Windows): keep appending to the current file.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
