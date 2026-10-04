using System.Collections.Concurrent;

namespace MainframeEngine.Editor;

/// <summary>Output panel level filter buckets (Fatal shows as Error).</summary>
public enum OutputLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>One line of the Output panel.</summary>
public sealed record OutputMessage(OutputLevel Level, string Text, DateTime Time)
{
    // Computed once: the Output panel's data bindings read these whenever its list is refreshed.
    public string TimeText { get; } = Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string LevelText { get; } = Level switch
    {
        OutputLevel.Debug => "debug",
        OutputLevel.Info => "info",
        OutputLevel.Warning => "warn",
        _ => "error",
    };
}

/// <summary>
/// Collects engine <see cref="Log"/> messages for the Output panel: an <see cref="ILogSink"/> registered by
/// <see cref="Attach"/>. Messages may come from any thread; they are queued and moved into a bounded history on the
/// main thread by <see cref="Drain"/>, which costs nothing when no message arrived.
/// </summary>
public sealed class OutputLog : ILogSink, IDisposable
{
    private readonly ConcurrentQueue<OutputMessage> _incoming = new();
    private readonly List<OutputMessage> _messages = [];
    private bool _attached;

    public OutputLog(int capacity = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Most messages kept; the oldest are dropped.</summary>
    public int Capacity { get; }

    /// <summary>The history, oldest first (main thread).</summary>
    public IReadOnlyList<OutputMessage> Messages => _messages;

    /// <summary>Messages ever added to the history (drained), including dropped ones.</summary>
    public long TotalAdded { get; private set; }

    /// <summary>Bumped by <see cref="Clear"/>.</summary>
    public int ClearCount { get; private set; }

    /// <summary>Messages dropped because the history was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Bumped by <see cref="Drain"/> and <see cref="Clear"/> when the history changed.</summary>
    public int Version { get; private set; }

    /// <summary>Starts listening to <see cref="Log"/>.</summary>
    public void Attach()
    {
        if (_attached)
            return;
        Log.AddSink(this);
        _attached = true;
    }

    /// <summary>Adds a message (thread-safe; also used for the editor's own notices).</summary>
    public void Add(OutputLevel level, string text) => _incoming.Enqueue(new OutputMessage(level, text ?? "", DateTime.Now));

    /// <summary>
    /// <see cref="ILogSink"/>: queues the entry as <c>[Category] message</c> (any thread). Only registered sinks are
    /// called, so this runs between <see cref="Attach"/> and <see cref="Dispose"/>.
    /// </summary>
    public void Write(in LogEntry entry)
    {
        var text = entry.Category.Length == 0 ? entry.Message : $"[{entry.Category}] {entry.Message}";
        _incoming.Enqueue(new OutputMessage(ToOutputLevel(entry.Level), text, entry.Timestamp.ToLocalTime()));
    }

    public static OutputLevel ToOutputLevel(Log.Level level) => level switch
    {
        Log.Level.Debug => OutputLevel.Debug,
        Log.Level.Info => OutputLevel.Info,
        Log.Level.Warning => OutputLevel.Warning,
        _ => OutputLevel.Error,
    };

    /// <summary>Moves queued messages into the history (main thread). True when anything arrived.</summary>
    public bool Drain()
    {
        if (_incoming.IsEmpty)
            return false;
        while (_incoming.TryDequeue(out var message))
        {
            _messages.Add(message);
            TotalAdded++;
        }

        // Trim once per drain, not per message (a burst would shift the list once for every line).
        if (_messages.Count > Capacity)
        {
            var excess = _messages.Count - Capacity;
            _messages.RemoveRange(0, excess);
            Dropped += excess;
        }

        Version++;
        return true;
    }

    public void Clear()
    {
        _messages.Clear();
        ClearCount++;
        Version++;
    }

    /// <summary>Counts per level (for the filter buttons' badges).</summary>
    public int Count(OutputLevel level)
    {
        var count = 0;
        foreach (var message in _messages)
            if (message.Level == level)
                count++;
        return count;
    }

    public void Dispose()
    {
        if (!_attached)
            return;
        Log.RemoveSink(this);
        _attached = false;
    }
}
