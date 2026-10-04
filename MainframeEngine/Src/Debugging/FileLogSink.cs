using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// Appends entries to <c>{Directory}/{BaseName}.log</c> as UTF-8 lines (<see cref="LogEntry.Format"/>, with call sites
/// for warnings and above). The previous run's file is kept as <c>{BaseName}.1.log</c> (up to <see cref="MaxFiles"/>
/// files); a file that reaches <see cref="MaxBytes"/> rotates the same way while running. <see cref="ForUser"/> puts it
/// in the per-user data folder (<see cref="UserDataPaths"/>).
/// </summary>
/// <remarks>
/// <para><see cref="Write"/> only queues: a background thread does the disk I/O, flushing at least every
/// <see cref="FlushInterval"/> and right away after errors. When more than <see cref="QueueCapacity"/> entries wait,
/// new ones are dropped and a line reports how many. <see cref="Flush"/> writes everything queued.</para>
/// <para>A second process logging to the same folder (two instances of a game) does not take over the file: the first
/// holds <c>{BaseName}.log.lock</c>, and later ones write <c>{BaseName}-{pid}.log</c>.</para>
/// <para>I/O failures are reported once on stderr; the sink keeps trying (a failed rotation is retried after
/// <see cref="RotationRetryDelay"/>, appending meanwhile).</para>
/// </remarks>
public sealed class FileLogSink : ILogSink, IDisposable
{
    /// <summary>Longest time a written entry waits in memory before it is flushed to disk.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long after a failed rotation the sink tries again.</summary>
    public static readonly TimeSpan RotationRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _ioGate = new(); // the writer thread and Flush() drain under it
    private readonly StringBuilder _line = new(256);
    private readonly Thread _thread;
    private readonly FileStream? _lockFile;
    private StreamWriter? _writer;
    private long _bytes;
    private int _queued;
    private long _dropped;
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime _nextRotationAttempt;
    private bool _errorReported;
    private volatile bool _disposed;

    public FileLogSink(string directory, string baseName = "game", long maxBytes = 10 * 1024 * 1024, int maxFiles = 5,
        int queueCapacity = 8192)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        Directory = Path.GetFullPath(directory);
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
        QueueCapacity = queueCapacity;
        System.IO.Directory.CreateDirectory(Directory);

        // One process owns {baseName}.log; another instance gets its own file instead of rotating the live one away.
        _lockFile = TryLock(baseName);
        BaseName = _lockFile is not null ? baseName : $"{baseName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
        _lockFile ??= TryLock(BaseName);

        lock (_ioGate)
        {
            Rotate(); // keep the previous run as .1
            Open();
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "FileLogSink " + BaseName };
        _thread.Start();
    }

    /// <summary>A sink writing to <c>{user data}/{gameName}/logs/{gameName}.log</c>.</summary>
    public static FileLogSink ForUser(string gameName, long maxBytes = 10 * 1024 * 1024, int maxFiles = 5) =>
        new(UserDataPaths.LogDirectory(gameName), UserDataPaths.SafeName(gameName), maxBytes, maxFiles);

    public string Directory { get; }

    /// <summary>The file name without extension: the requested name, or <c>{name}-{pid}</c> when another process owns it.</summary>
    public string BaseName { get; }

    /// <summary>Size at which the current file rotates.</summary>
    public long MaxBytes { get; }

    /// <summary>Files kept, including the current one.</summary>
    public int MaxFiles { get; }

    /// <summary>Most entries waiting for the writer thread.</summary>
    public int QueueCapacity { get; }

    /// <summary>Levels this sink writes. Default: all.</summary>
    public Log.Level Levels { get; set; } = (Log.Level)~0;

    /// <summary>The file being written.</summary>
    public string FilePath => PathOf(0);

    /// <summary>Entries dropped because the queue was full (total).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Queues the entry for the writer thread (never touches the disk on the calling thread).</summary>
    public void Write(in LogEntry entry)
    {
        if (_disposed || (Levels & entry.Level) == 0)
            return;
        if (Interlocked.Increment(ref _queued) > QueueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _droppedPending);
            return;
        }

        _queue.Enqueue(entry);
        if (entry.Level >= Log.Level.Error)
            Wake(); // errors reach the disk right away; the rest within FlushInterval
    }

    private long _droppedPending;

    /// <summary>Writes and flushes everything queued so far (on the calling thread).</summary>
    public void Flush()
    {
        lock (_ioGate)
            Drain(forceFlush: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Wake();
        _thread.Join(TimeSpan.FromSeconds(2));
        lock (_ioGate)
        {
            Drain(forceFlush: true);
            _writer?.Dispose();
            _writer = null;
        }

        _lockFile?.Dispose();
        _signal.Dispose();
    }

    private void Run()
    {
        while (!_disposed)
        {
            try
            {
                _signal.WaitOne(FlushInterval / 4);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            lock (_ioGate)
                Drain(forceFlush: false);
        }
    }

    private void Wake()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Writes queued entries; flushes when an error was written, when asked, or FlushInterval after the last flush.
    private void Drain(bool forceFlush)
    {
        var urgent = forceFlush;
        var dropped = Interlocked.Exchange(ref _droppedPending, 0);
        if (dropped > 0)
            Append(new LogEntry(Log.Level.Warning, DateTime.UtcNow, "Log",
                $"{dropped.ToString(CultureInfo.InvariantCulture)} log entries were dropped (the log file could not keep up).", "", "", 0));

        while (_queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _queued);
            Append(entry);
            urgent |= entry.Level >= Log.Level.Error;
        }

        if (!_dirty || _writer is null || (!urgent && DateTime.UtcNow - _lastFlush < FlushInterval))
            return;
        try
        {
            _writer.Flush();
            _dirty = false;
            _lastFlush = DateTime.UtcNow;
        }
        catch (IOException e)
        {
            ReportOnce(e);
        }
    }

    private bool _dirty; // written since the last flush

    private void Append(in LogEntry entry)
    {
        if (_writer is null)
        {
            Open(); // a previous failure: try again
            if (_writer is null)
                return;
        }

        _line.Clear();
        entry.AppendTo(_line, includeCallSite: entry.Level >= Log.Level.Warning);
        _line.Append('\n');
        try
        {
            _writer.Write(_line);
            _dirty = true;
            foreach (var chunk in _line.GetChunks())
                _bytes += Encoding.UTF8.GetByteCount(chunk.Span);
            if (_bytes >= MaxBytes && DateTime.UtcNow >= _nextRotationAttempt)
            {
                _writer.Dispose();
                _writer = null;
                if (!Rotate())
                    _nextRotationAttempt = DateTime.UtcNow + RotationRetryDelay; // keep appending meanwhile
                Open();
            }
        }
        catch (IOException e)
        {
            // Disk full or the file vanished: logging must never crash the game.
            ReportOnce(e);
        }
    }

    private string PathOf(int index) =>
        Path.Combine(Directory, index == 0 ? BaseName + ".log" : $"{BaseName}.{index.ToString(CultureInfo.InvariantCulture)}.log");

    private FileStream? TryLock(string baseName)
    {
        try
        {
            return new FileStream(Path.Combine(Directory, baseName + ".log.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // another process owns this name
        }
    }

    private void Open()
    {
        try
        {
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _bytes = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _writer = null;
            ReportOnce(e);
        }
    }

    // game.log → game.1.log → … → game.{MaxFiles-1}.log; the oldest is deleted. False when it could not rotate.
    private bool Rotate()
    {
        try
        {
            if (!File.Exists(PathOf(0)))
                return true;
            var last = PathOf(MaxFiles - 1);
            if (File.Exists(last))
                File.Delete(last);
            for (var i = MaxFiles - 2; i >= 0; i--)
            {
                var from = PathOf(i);
                if (File.Exists(from))
                    File.Move(from, PathOf(i + 1), overwrite: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ReportOnce(e);
            return false;
        }
    }

    private void ReportOnce(Exception e)
    {
        if (_errorReported)
            return;
        _errorReported = true;
        try
        {
            Console.Error.WriteLine($"[Log] File log '{FilePath}' failed: {e.Message} (further errors are not reported).");
        }
        catch (IOException)
        {
        }
    }
}
