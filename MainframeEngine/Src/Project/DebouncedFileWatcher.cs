namespace MainframeEngine;

/// <summary>
/// Collects change notifications and releases them as one batch once nothing has changed for <see cref="Delay"/> — a
/// build writes many files in bursts, a reload should run once. Time is passed in, so it is deterministic to test.
/// Thread-safe.
/// </summary>
public sealed class Debouncer
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private DateTime _lastChange;

    public Debouncer(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Delay = delay;
    }

    /// <summary>Quiet time required before a batch is released.</summary>
    public TimeSpan Delay { get; }

    /// <summary>True while changes are waiting.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _pending.Count > 0;
        }
    }

    /// <summary>Records a change to <paramref name="path"/> at <paramref name="now"/> (restarts the quiet period).</summary>
    public void Notify(string path, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            _pending.Add(path);
            _lastChange = now;
        }
    }

    /// <summary>
    /// The changed paths (sorted) when <see cref="Delay"/> has passed since the last change, clearing them; otherwise
    /// false.
    /// </summary>
    public bool TryFlush(DateTime now, out IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            if (_pending.Count == 0 || now - _lastChange < Delay)
            {
                paths = [];
                return false;
            }

            var sorted = _pending.ToList();
            sorted.Sort(StringComparer.Ordinal);
            _pending.Clear();
            paths = sorted;
            return true;
        }
    }
}

/// <summary>
/// A <see cref="FileSystemWatcher"/> whose events are debounced (<see cref="Debouncer"/>): <see cref="Changed"/> fires
/// once per burst of changes (on a timer thread) with every path touched. The editor watches a game's build output
/// (<c>*.dll</c>) to offer a code reload, and its sources (<c>*.cs</c>) to show "rebuild needed".
/// </summary>
public sealed class DebouncedFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Debouncer _debouncer;
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _poll;
    private int _disposed;

    /// <param name="directory">The folder to watch (recursively when <paramref name="recursive"/>).</param>
    /// <param name="filter">A file pattern (<c>*.dll</c>); <c>*</c> for everything.</param>
    /// <param name="delay">Quiet time before <see cref="Changed"/> fires (default 300 ms).</param>
    public DebouncedFileWatcher(string directory, string filter = "*", bool recursive = true, TimeSpan? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        System.IO.Directory.CreateDirectory(directory);
        _debouncer = new Debouncer(delay ?? TimeSpan.FromMilliseconds(300));
        _poll = TimeSpan.FromMilliseconds(Math.Clamp(_debouncer.Delay.TotalMilliseconds / 4, 10, 100));
        _timer = new System.Threading.Timer(static state => ((DebouncedFileWatcher)state!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(Path.GetFullPath(directory), filter)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>The watched folder.</summary>
    public string Directory => _watcher.Path;

    /// <summary>Raised on a timer thread with the paths changed in one burst. Marshal to your main thread.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Notify(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Notify(e.OldFullPath);
        Notify(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // The OS buffer overflowed: something changed, but we do not know what — report the folder itself.
        Notify(_watcher.Path);
    }

    private void Notify(string path)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _debouncer.Notify(path, DateTime.UtcNow);
        try
        {
            _timer.Change(_poll, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        // Timer callbacks can overlap (a slow handler, a re-armed timer): one tick at a time, batches in order.
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            Rearm();
            return;
        }

        try
        {
            if (_debouncer.TryFlush(DateTime.UtcNow, out var paths))
            {
                try
                {
                    Changed?.Invoke(paths);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // An exception on a timer thread would end the process: report it and keep watching.
                    Log.Error($"[FileWatcher] A Changed handler for '{Directory}' failed: {e}");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }

        if (_debouncer.HasPending)
            Rearm();
    }

    private int _ticking;

    private void Rearm()
    {
        try
        {
            _timer.Change(_poll, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
