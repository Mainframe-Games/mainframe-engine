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
    public string TimeText => Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string LevelText => Level switch
    {
        OutputLevel.Debug => "debug",
        OutputLevel.Info => "info",
        OutputLevel.Warning => "warn",
        _ => "error",
    };
}

/// <summary>
/// Collects engine <see cref="Log"/> messages for the Output panel through the interim <see cref="Log.MessageLogged"/>
/// hook (replaced by the log-sink API later). Messages may come from any thread; they are queued and moved into a
/// bounded history on the main thread by <see cref="Drain"/>, which costs nothing when no message arrived.
/// </summary>
public sealed class OutputLog : IDisposable
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

    /// <summary>Messages dropped because the history was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Bumped by <see cref="Drain"/> and <see cref="Clear"/> when the history changed.</summary>
    public int Version { get; private set; }

    /// <summary>Starts listening to <see cref="Log"/>.</summary>
    public void Attach()
    {
        if (_attached)
            return;
        Log.MessageLogged += OnMessage;
        _attached = true;
    }

    /// <summary>Adds a message (thread-safe; also used for the editor's own notices).</summary>
    public void Add(OutputLevel level, string text) => _incoming.Enqueue(new OutputMessage(level, text ?? "", DateTime.Now));

    private void OnMessage(Log.Level level, string text) => Add(ToOutputLevel(level), text);

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
            if (_messages.Count > Capacity)
            {
                var excess = _messages.Count - Capacity;
                _messages.RemoveRange(0, excess);
                Dropped += excess;
            }
        }

        Version++;
        return true;
    }

    public void Clear()
    {
        _messages.Clear();
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
        Log.MessageLogged -= OnMessage;
        _attached = false;
    }
}
