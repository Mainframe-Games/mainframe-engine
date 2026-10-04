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

/// <summary>
/// One line of the Output panel. <see cref="CallerFile"/>/<see cref="CallerLine"/> make it clickable: the editor's own
/// log calls, build errors and game log calls open in the external code editor (click-to-source).
/// </summary>
public sealed record OutputMessage(OutputLevel Level, string Text, DateTime Time)
{
    private string? _sourceText;
    private bool? _hasSource;

    /// <summary>The log category (<c>Audio</c>, <c>Editor</c> …; a leading <c>[Name]</c> of the text counts), "" for none.</summary>
    public string Category { get; init; } = "";

    /// <summary>The source file that logged the message (caller info), "" when unknown.</summary>
    public string CallerFile { get; init; } = "";

    public int CallerLine { get; init; }

    // Computed once: the Output panel's data bindings read these whenever its list is refreshed.
    public string TimeText { get; } = Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string LevelText { get; } = Level switch
    {
        OutputLevel.Debug => "debug",
        OutputLevel.Info => "info",
        OutputLevel.Warning => "warn",
        _ => "error",
    };

    /// <summary>The level's icon classes (coloured).</summary>
    public string LevelIcon => OutputCategories.LevelIcon(Level);

    /// <summary>The category's icon classes, "" without a category.</summary>
    public string CategoryIcon => OutputCategories.IconOf(Category);

    /// <summary>Whether <see cref="CallerFile"/> exists on this machine (checked once).</summary>
    public bool HasSource => _hasSource ??= CallerFile.Length > 0 && File.Exists(CallerFile);

    /// <summary>The source link's tooltip: <c>Open File.cs:12 — …</c>.</summary>
    public string SourceText => _sourceText ??= $"Open {Path.GetFileName(CallerFile)}:{CallerLine} — the line that logged this, in your code editor";

    /// <summary>The line as copied to the clipboard: <c>12:00:01 WARN [Audio] text</c>.</summary>
    public string CopyText => Category.Length == 0 ? $"{TimeText} {LevelText.ToUpperInvariant()} {Text}" : $"{TimeText} {LevelText.ToUpperInvariant()} [{Category}] {Text}";
}

/// <summary>Icons of the Output panel's levels and log categories (engine subsystems).</summary>
public static class OutputCategories
{
    /// <summary>The icon classes of a level: debug <c>bug</c>, info <c>info-circle</c>, warning <c>alert-triangle</c>, error <c>alert-circle</c>.</summary>
    public static string LevelIcon(OutputLevel level) => level switch
    {
        OutputLevel.Debug => "icon icon-sm icon-bug icon-debug",
        OutputLevel.Info => "icon icon-sm icon-info-circle icon-info",
        OutputLevel.Warning => "icon icon-sm icon-alert-triangle icon-warn",
        _ => "icon icon-sm icon-alert-circle icon-error",
    };

    /// <summary>
    /// The icon classes of a category by subsystem: rendering <c>brush</c>, audio <c>volume</c>, physics <c>atom</c>,
    /// networking <c>network</c>, UI <c>layout</c>, localization <c>language</c>, the game <c>device-gamepad-2</c>, the
    /// editor <c>tool</c>, content and scenes <c>package</c>; any other <c>point</c>; "" for no category.
    /// </summary>
    public static string IconOf(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (category.Length == 0)
            return "";
        var c = category.AsSpan();
        if (Has(c, "render") || Has(c, "vulkan") || Has(c, "shader") || Has(c, "shadow") || Has(c, "gpu") || Has(c, "sky") || Has(c, "texture") || Has(c, "mesh"))
            return "icon icon-sm icon-brush icon-3d";
        if (Has(c, "audio") || Has(c, "sound"))
            return "icon icon-sm icon-volume icon-audio";
        if (Has(c, "physics"))
            return "icon icon-sm icon-atom icon-physics";
        if (Has(c, "net") || Has(c, "replic") || Has(c, "steam") || Has(c, "multiplayer") || Has(c, "link") || Has(c, "transport"))
            return "icon icon-sm icon-network icon-net";
        if (c.Equals("ui", StringComparison.OrdinalIgnoreCase) || Has(c, "rml") || c.StartsWith("ui", StringComparison.OrdinalIgnoreCase))
            return "icon icon-sm icon-layout icon-ui";
        if (Has(c, "l10n") || Has(c, "locali") || Has(c, "translat") || c.Equals("tr", StringComparison.OrdinalIgnoreCase))
            return "icon icon-sm icon-language icon-logic";
        if (Has(c, "game"))
            return "icon icon-sm icon-device-gamepad-2 icon-logic";
        if (Has(c, "editor"))
            return "icon icon-sm icon-tool icon-logic";
        if (Has(c, "asset") || Has(c, "content") || Has(c, "import") || Has(c, "scene") || Has(c, "serial") || Has(c, "resource"))
            return "icon icon-sm icon-package icon-resource";
        return "icon icon-sm icon-point icon-logic";
    }

    private static bool Has(ReadOnlySpan<char> category, string part) => category.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a leading <c>[Category] </c> off <paramref name="text"/> (the editor's own messages use it).</summary>
    public static (string Category, string Text) SplitPrefix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 3 && text[0] == '[')
        {
            var close = text.IndexOf("] ", StringComparison.Ordinal);
            if (close is > 1 and <= 24 && text.AsSpan(1, close - 1).IndexOfAny(" \n[") < 0)
                return (text[1..close], text[(close + 2)..]);
        }

        return ("", text);
    }
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
    public void Add(OutputLevel level, string text)
    {
        var (category, message) = OutputCategories.SplitPrefix(text ?? "");
        _incoming.Enqueue(new OutputMessage(level, message, DateTime.Now) { Category = category });
    }

    /// <summary>
    /// Adds a message pointing at <paramref name="file"/>:<paramref name="line"/> (a build diagnostic: click-to-source;
    /// thread-safe). A leading <c>[Name] </c> becomes its category.
    /// </summary>
    public void Add(OutputLevel level, string text, string? file, int line)
    {
        var (category, message) = OutputCategories.SplitPrefix(text ?? "");
        _incoming.Enqueue(new OutputMessage(level, message, DateTime.Now) { Category = category, CallerFile = file ?? "", CallerLine = line });
    }

    /// <summary>
    /// Adds a running game's log entry (category <c>game</c>, or the game's own category, with the instance label when
    /// several games run); its call site becomes the click target when the source file exists on this machine.
    /// </summary>
    public void AddGame(in LogEntry entry, string? instanceLabel)
    {
        var text = instanceLabel is null ? entry.Message : $"{instanceLabel}: {entry.Message}";
        _incoming.Enqueue(new OutputMessage(ToOutputLevel(entry.Level), text, entry.Timestamp.ToLocalTime())
        {
            // Marked as game output, keeping the game's own category when it has one.
            Category = entry.Category is "" or PlayService.GameCategory ? "game" : $"game·{entry.Category}",
            CallerFile = entry.CallerFile ?? "",
            CallerLine = entry.CallerLine,
        });
    }

    /// <summary>
    /// <see cref="ILogSink"/>: queues the entry with its category and call site (any thread); a message without a
    /// category but with a leading <c>[Name] </c> gets that as its category. Only registered sinks are called, so this
    /// runs between <see cref="Attach"/> and <see cref="Dispose"/>.
    /// </summary>
    public void Write(in LogEntry entry)
    {
        var (category, text) = entry.Category.Length == 0 ? OutputCategories.SplitPrefix(entry.Message) : (entry.Category, entry.Message);
        _incoming.Enqueue(new OutputMessage(ToOutputLevel(entry.Level), text, entry.Timestamp.ToLocalTime())
        {
            Category = category,
            CallerFile = entry.CallerFile ?? "",
            CallerLine = entry.CallerLine,
        });
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
