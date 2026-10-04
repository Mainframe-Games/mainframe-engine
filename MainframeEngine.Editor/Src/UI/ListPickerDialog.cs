using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>An entry of a <see cref="ListPickerDialog"/>.</summary>
public sealed record ListPickerItem(string Label, string Detail, string Icon, object Payload);

/// <summary>
/// A modal searchable list (Add Node: the registered node types; NodePath and resource-type pickers): type to filter,
/// click to select, double click or OK to accept.
/// </summary>
public sealed class ListPickerDialog : EditorDocument
{
    private sealed class Row
    {
        public required ListPickerItem Item { get; init; }
        public required int Index { get; init; }
        public bool Selected { get; set; }
    }

    private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
        .Member("label", static r => r.Item.Label)
        .Member("detail", static r => r.Item.Detail)
        .Member("icon", static r => r.Item.Icon)
        .Member("index", static r => r.Index)
        .Member("css", static r => r.Selected ? "list-item selected" : "list-item");

    private readonly List<Row> _rows = [];
    private IReadOnlyList<ListPickerItem> _items = [];
    private RmlDataModel? _model;
    private Action<object>? _onAccept;
    private string _title = "";
    private string _okLabel = "OK";
    private string _query = "";
    private string _error = "";
    private int _selected = -1;

    public ListPickerDialog(EditorWorkspace workspace)
        : base(workspace, "list_picker.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The filtered rows on screen (labels), for tests and QA scripts.</summary>
    public IEnumerable<string> VisibleLabels => _rows.Select(r => r.Item.Label);

    protected override void OnReady()
    {
        _model = CreateDataModel("list_picker")
            .Bind("title", this, static d => d._title)
            .Bind("ok_label", this, static d => d._okLabel)
            .Bind("error", this, static d => d._error)
            // Filtering changes the list's size: done after the UI update that reported the edit, never inside it.
            .Bind("query", this, static d => d._query, static (d, v) =>
            {
                d._query = v ?? "";
                if (d.IsInsideTree)
                    d.CallDeferred(static state => ((ListPickerDialog)state!).Filter(), d);
            })
            .BindList("items", _rows, RowType)
            .Event("select", e => Select(e.GetArgument(0).GetInt32()))
            .Event("activate", e =>
            {
                Select(e.GetArgument(0).GetInt32());
                Accept();
            })
            .Event("ok", _ => Accept())
            .Event("cancel", _ => Cancel());
    }

    public void Show(string title, IReadOnlyList<ListPickerItem> items, string okLabel, Action<object> onAccept)
    {
        _title = title;
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _okLabel = okLabel;
        _onAccept = onAccept;
        _query = "";
        _error = "";
        Visible = true;
        Filter();
        if (EnsureLoaded() && Document.GetElementById("query") is { IsNull: false } field)
        {
            field.SetValue("");
            field.Focus(focusVisible: true);
        }
    }

    /// <summary>Filters by a case-insensitive substring of the label or detail.</summary>
    public void SetQuery(string query)
    {
        _query = query;
        Filter();
    }

    private void Filter()
    {
        _rows.Clear();
        foreach (var item in _items)
            if (_query.Length == 0 || item.Label.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                item.Detail.Contains(_query, StringComparison.OrdinalIgnoreCase))
                _rows.Add(new Row { Item = item, Index = _rows.Count });
        _selected = _rows.Count > 0 ? 0 : -1;
        if (_selected == 0)
            _rows[0].Selected = true;
        _model?.DirtyAll();
    }

    public void Select(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].Selected = false;
        _selected = index;
        _rows[index].Selected = true;
        _model?.Dirty("items");
    }

    /// <summary>Selects the first row whose label is <paramref name="label"/> (QA scripts, tests).</summary>
    public bool SelectLabel(string label)
    {
        var index = _rows.FindIndex(r => string.Equals(r.Item.Label, label, StringComparison.Ordinal));
        if (index < 0)
            return false;
        Select(index);
        return true;
    }

    public void Accept()
    {
        if ((uint)_selected >= (uint)_rows.Count)
        {
            _error = "Nothing selected.";
            _model?.Dirty("error");
            return;
        }

        var payload = _rows[_selected].Item.Payload;
        var callback = _onAccept;
        Close();
        callback?.Invoke(payload);
    }

    public void Cancel() => Close();

    private void Close()
    {
        HideAndReleaseFocus();
        _onAccept = null;
        _items = [];
        _rows.Clear();
        _model?.DirtyAll();
    }

    protected override void OnAttach(RmlDocument document)
    {
        document.AsElement().AddEventListener("keydown", OnKeyDown);
    }

    private void OnKeyDown(RmlEvent e)
    {
        if (!Visible)
            return;
        switch ((RmlKey)e.GetParameter("key_identifier", 0))
        {
            case RmlKey.Escape:
                Cancel();
                break;
            case RmlKey.Return or RmlKey.NumpadEnter:
                Accept();
                break;
            case RmlKey.Down:
                Select(Math.Min(_rows.Count - 1, _selected + 1));
                break;
            case RmlKey.Up:
                Select(Math.Max(0, _selected - 1));
                break;
        }
    }
}
