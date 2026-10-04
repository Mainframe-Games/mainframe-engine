namespace MainframeEngine;

/// <summary>
/// Collects validation-layer warnings and errors so tests and tools can assert a clean run. Messages
/// are also written to <see cref="Log"/>. Thread-safe: the layers may report from any thread.
/// </summary>
public sealed class VulkanValidationLog
{
    /// <summary>Only the first messages are kept; the counters keep counting.</summary>
    public const int MaxStoredMessages = 64;

    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    private int _warningCount;
    private int _errorCount;

    /// <summary>True when the validation layers were requested and are installed.</summary>
    public bool IsEnabled { get; internal set; }

    public int WarningCount => Volatile.Read(ref _warningCount);
    public int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>A snapshot of the stored messages, prefixed with their severity.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_lock)
                return _messages.ToArray();
        }
    }

    /// <summary>Clears counters and messages, e.g. after expected startup noise.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _messages.Clear();
            _warningCount = 0;
            _errorCount = 0;
        }
    }

    internal void Record(bool isError, string message)
    {
        lock (_lock)
        {
            if (isError) _errorCount++;
            else _warningCount++;

            if (_messages.Count < MaxStoredMessages)
                _messages.Add(isError ? $"ERROR: {message}" : $"WARNING: {message}");
        }

        if (isError) Log.Error($"[Vulkan] {message}");
        else Log.Warning($"[Vulkan] {message}");
    }
}
