using System.Collections.Concurrent;
using System.Diagnostics;

namespace MainframeEngine;

/// <summary>What a batch of file changes requires of loaded documents.</summary>
[Flags]
public enum UiReloadKind
{
    None = 0,

    /// <summary>Only style sheets changed: re-read them, keeping each document's DOM and state.</summary>
    StyleSheets = 1,

    /// <summary>Documents or templates changed: reload the documents (data models survive; they live in C#).</summary>
    Documents = 2,

    /// <summary>Images or fonts changed: release textures so they are re-created on demand.</summary>
    Textures = 4,
}

/// <summary>
/// Watches UI source folders for <c>.rml</c>, <c>.rcss</c> and image changes (development hot reload). Change
/// notifications arrive on thread-pool threads and are only queued; <see cref="TryTake"/> hands a batch to the main
/// thread once no change has arrived for the debounce interval, so an editor's save (often several writes) reloads once.
/// </summary>
public sealed class UiHotReload : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentQueue<string> _changes = new();
    private long _lastChange;

    public UiHotReload(TimeSpan? debounce = null)
    {
        Debounce = debounce ?? TimeSpan.FromMilliseconds(150);
    }

    public TimeSpan Debounce { get; }

    public IReadOnlyList<string> Directories => _watchers.ConvertAll(w => w.Path);

    /// <summary>Watches <paramref name="directory"/> recursively (ignored if it does not exist).</summary>
    public void Watch(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        var full = Path.GetFullPath(directory);
        foreach (var existing in _watchers)
            if (string.Equals(existing.Path, full, StringComparison.Ordinal))
                return;

        var watcher = new FileSystemWatcher(full)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Enqueue(e.FullPath);

    /// <summary>Records a change (thread-safe; also used by tests and tools).</summary>
    public void Enqueue(string path)
    {
        if (Classify(path) == UiReloadKind.None)
            return;
        _changes.Enqueue(path);
        Interlocked.Exchange(ref _lastChange, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Main thread: when changes are pending and the debounce interval has passed since the last one, returns what
    /// they require and clears the queue.
    /// </summary>
    public bool TryTake(out UiReloadKind kind)
    {
        kind = UiReloadKind.None;
        if (_changes.IsEmpty)
            return false;
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChange)) < Debounce)
            return false;
        while (_changes.TryDequeue(out var path))
            kind |= Classify(path);
        return kind != UiReloadKind.None;
    }

    /// <summary>The reload a changed file needs.</summary>
    public static UiReloadKind Classify(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".rcss" => UiReloadKind.StyleSheets,
            ".rml" => UiReloadKind.Documents,
            ".png" or ".tga" or ".jpg" or ".jpeg" or ".bmp" or ".ttf" or ".otf" => UiReloadKind.Textures,
            _ => UiReloadKind.None,
        };
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
