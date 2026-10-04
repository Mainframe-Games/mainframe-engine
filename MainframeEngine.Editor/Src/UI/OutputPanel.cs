using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>A visible line of the Output panel: a message, folded repeats of it, its position.</summary>
public sealed class OutputRow
{
    public required OutputMessage Message { get; init; }
    public required int Index { get; init; }

    /// <summary>How many identical consecutive messages the row stands for (Collapse Duplicates), 1 otherwise.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>
/// The output panel: engine <see cref="Log"/> messages (through <see cref="OutputLog"/>) as lines with a level icon, the
/// time, a category icon (the subsystem: rendering, audio, physics, networking, UI, localization, game, editor), the
/// text, a ×N badge for folded repeats and a link to the logging source line. The header has a toggle per level with
/// its count, a filter field and icon buttons for Collapse Duplicates, Follow (scroll to new lines), Copy and Clear; the
/// level filter and the toggles persist with the layout. Data-bound as the <c>output</c> model; it only changes when a
/// message arrives or a setting changes.
/// </summary>
public sealed class OutputPanel : EditorDocument
{
    private static readonly RmlStructType<OutputRow> LineType = new RmlStructType<OutputRow>()
        .Member("level", static r => r.Message.LevelText)
        .Member("level_icon", static r => r.Message.LevelIcon)
        .Member("time", static r => r.Message.TimeText)
        .Member("category", static r => r.Message.Category)
        .Member("category_icon", static r => r.Message.CategoryIcon)
        .Member("text", static r => r.Message.Text)
        .Member("count", static r => r.Count)
        .Member("has_source", static r => r.Message.HasSource)
        .Member("source", static r => r.Message.SourceText)
        .Member("index", static r => r.Index);

    private static readonly RmlStructType<OutputRow> LineTypeWithoutTime = new RmlStructType<OutputRow>()
        .Member("level", static r => r.Message.LevelText)
        .Member("level_icon", static r => r.Message.LevelIcon)
        .Member("time", static _ => "")
        .Member("category", static r => r.Message.Category)
        .Member("category_icon", static r => r.Message.CategoryIcon)
        .Member("text", static r => r.Message.Text)
        .Member("count", static r => r.Count)
        .Member("has_source", static r => r.Message.HasSource)
        .Member("source", static r => r.Message.SourceText)
        .Member("index", static r => r.Index);

    private readonly List<OutputRow> _rows = [];
    private readonly List<OutputMessage> _visible = [];
    private readonly int[] _counts = new int[4];
    private RmlDataModel? _model;
    private int _filter;
    private bool _collapse;
    private bool _follow;
    private string _query = "";
    private int _shownVersion = -1;
    private long _shownTotal;      // OutputLog.TotalAdded already reflected in _rows
    private int _shownClears = -1; // OutputLog.ClearCount reflected; -1 forces a full rebuild
    private int _scrollCountdown;

    public OutputPanel(EditorWorkspace workspace)
        : base(workspace, "output.rml")
    {
        var settings = workspace.Layout.Settings;
        _filter = settings.OutputFilter;
        _collapse = settings.OutputCollapse;
        _follow = settings.OutputFollow;
    }

    /// <summary>The messages of the visible lines, oldest first (the first of each folded run).</summary>
    public IReadOnlyList<OutputMessage> VisibleMessages => _visible;

    /// <summary>The visible lines.</summary>
    public IReadOnlyList<OutputRow> Rows => _rows;

    public bool IsShown(OutputLevel level) => (_filter & (1 << (int)level)) != 0;

    /// <summary>Repeated consecutive lines fold into one with a count.</summary>
    public bool CollapseDuplicates => _collapse;

    /// <summary>New lines scroll into view.</summary>
    public bool Follow => _follow;

    /// <summary>The filter text (case-insensitive, matches the text or the category).</summary>
    public string Query => _query;

    /// <summary>Messages per level in the whole history (the toggles' counts).</summary>
    public int CountOf(OutputLevel level) => _counts[(int)level];

    protected override void OnReady()
    {
        _model = CreateDataModel("output")
            .Bind("show_debug", this, static p => p.IsShown(OutputLevel.Debug))
            .Bind("show_info", this, static p => p.IsShown(OutputLevel.Info))
            .Bind("show_warn", this, static p => p.IsShown(OutputLevel.Warning))
            .Bind("show_error", this, static p => p.IsShown(OutputLevel.Error))
            .Bind("count_debug", this, static p => p._counts[0])
            .Bind("count_info", this, static p => p._counts[1])
            .Bind("count_warn", this, static p => p._counts[2])
            .Bind("count_error", this, static p => p._counts[3])
            .Bind("collapse", this, static p => p._collapse)
            .Bind("follow", this, static p => p._follow)
            .Bind("query", this, static p => p._query, static (p, v) =>
            {
                p._query = v ?? "";
                // Filtering changes the list's size: done after the UI update that reported the edit, never inside it.
                if (p.IsInsideTree)
                    p.CallDeferred(static state => ((OutputPanel)state!).Rebuild(), p);
            })
            .BindList("lines", _rows, Workspace.Options.OutputTimestamps ? LineType : LineTypeWithoutTime)
            .Event("toggle", e => Toggle((OutputLevel)e.GetArgument(0).GetInt32()))
            .Event("collapse", _ => SetCollapseDuplicates(!_collapse))
            .Event("follow", _ => SetFollow(!_follow))
            .Event("copy", _ => Copy())
            .Event("clear", _ => Clear())
            .Event("open_source", e => OpenSource(e.GetArgument(0).GetInt32()));
        Refresh();
    }

    public void Toggle(OutputLevel level)
    {
        _filter ^= 1 << (int)level;
        Workspace.Layout.SetOutputFilter(_filter);
        Rebuild();
    }

    public void SetCollapseDuplicates(bool collapse)
    {
        _collapse = collapse;
        Workspace.Layout.SetOutputOptions(_collapse, _follow);
        Rebuild();
    }

    public void SetFollow(bool follow)
    {
        _follow = follow;
        Workspace.Layout.SetOutputOptions(_collapse, _follow);
        _model?.Dirty("follow");
        if (follow)
            _scrollCountdown = 2;
    }

    /// <summary>Shows only lines whose text or category contains <paramref name="query"/> (the filter field does the same).</summary>
    public void SetQuery(string query)
    {
        _query = query ?? "";
        Rebuild();
    }

    public void Clear()
    {
        Workspace.Output.Clear();
        Refresh();
    }

    /// <summary>Copies the visible lines (time, level, category, text, ×N) to the clipboard; returns the text.</summary>
    public string Copy()
    {
        var text = new StringBuilder();
        foreach (var row in _rows)
        {
            text.Append(row.Message.CopyText);
            if (row.Count > 1)
                text.Append(" (×").Append(row.Count).Append(')');
            text.Append('\n');
        }

        var result = text.ToString();
        if (Layer?.Server is { } server)
            server.ClipboardText = result;
        return result;
    }

    /// <summary>Opens the source line that logged row <paramref name="index"/> (<see cref="EditorWorkspace.SourceOpener"/>).</summary>
    public bool OpenSource(int index)
    {
        if ((uint)index >= (uint)_rows.Count || _rows[index].Message is not { HasSource: true } message)
            return false;
        return Workspace.SourceOpener(message.CallerFile, message.CallerLine);
    }

    private void Rebuild()
    {
        _shownVersion = -1;
        _shownClears = -1;
        Refresh();
    }

    /// <summary>Rebuilds the visible list when the log changed, and scrolls to the newest line.</summary>
    public void Refresh()
    {
        var log = Workspace.Output;
        if (_shownVersion == log.Version)
            return;
        _shownVersion = log.Version;
        var messages = log.Messages;
        var added = log.TotalAdded - _shownTotal;
        if (_shownClears != log.ClearCount || added > messages.Count || log.Dropped > 0)
        {
            // Full rebuild: first time, filter changed, cleared, or the history dropped old lines.
            _rows.Clear();
            _visible.Clear();
            Array.Clear(_counts);
            foreach (var message in messages)
                Append(message);
        }
        else
        {
            // Only the new lines (a busy log appends a few per frame).
            for (var i = messages.Count - (int)added; i < messages.Count; i++)
                Append(messages[i]);
        }

        _shownTotal = log.TotalAdded;
        _shownClears = log.ClearCount;

        if (_model is null)
            return;
        _model.DirtyAll();
        // Scroll to the newest line once the data views created it (the UI updates after the tree's process step).
        if (_follow && _scrollCountdown == 0)
            _scrollCountdown = 2;
    }

    private void Append(OutputMessage message)
    {
        _counts[(int)message.Level]++;
        if (!IsShown(message.Level) || !Matches(message))
            return;
        if (_collapse && _rows.Count > 0 && SameLine(_rows[^1].Message, message))
        {
            _rows[^1].Count++;
            return;
        }

        _rows.Add(new OutputRow { Message = message, Index = _rows.Count });
        _visible.Add(message);
    }

    private bool Matches(OutputMessage message) =>
        _query.Length == 0 || message.Text.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
        message.Category.Contains(_query, StringComparison.OrdinalIgnoreCase);

    private static bool SameLine(OutputMessage a, OutputMessage b) =>
        a.Level == b.Level && string.Equals(a.Text, b.Text, StringComparison.Ordinal) && string.Equals(a.Category, b.Category, StringComparison.Ordinal);

    /// <summary>Called every frame by the workspace: finishes a pending scroll to the newest line.</summary>
    public void Tick()
    {
        if (_scrollCountdown == 0 || --_scrollCountdown > 0)
            return;
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (!IsLoaded || Document.GetElementById("lines") is not { IsNull: false } lines || lines.ChildCount == 0)
            return;
        // The data-for template element stays (hidden) after the generated lines: scroll to the last real line.
        for (var i = lines.ChildCount - 1; i >= 0; i--)
        {
            var line = lines.GetChild(i);
            if (line.IsClassSet("line"))
            {
                line.ScrollIntoView(alignWithTop: false);
                return;
            }
        }
    }
}
