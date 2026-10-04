using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>What a <see cref="TreePickerDialog"/> shows and does with the choice.</summary>
public sealed record TreePickerRequest
{
    /// <summary>Key of the persisted favourites and recents (<c>node</c>, <c>resource</c>, <c>scene</c>).</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string OkLabel { get; init; }

    public required IReadOnlyList<PickerEntry> Entries { get; init; }

    /// <summary>Runs with the chosen entry after the dialog closed.</summary>
    public required Action<PickerEntry> OnAccept { get; init; }

    /// <summary>When set, a Browse… button closes the dialog and runs it (e.g. a file picker for scenes outside the project).</summary>
    public Action? OnBrowse { get; init; }
}

/// <summary>
/// The Godot-style create dialog (Add Node, New Resource, Instance Scene): the entries as an inheritance (or folder)
/// tree with icons and family tints, a fuzzy search that keeps the matches' ancestors visible, Favourites and Recent
/// lists (persisted per <see cref="TreePickerRequest.Kind"/> in the editor settings) and a description pane (icon, name,
/// ancestor chain with icons, doc summary). Double click or Enter accepts; Up/Down move; Escape cancels.
/// </summary>
public sealed class TreePickerDialog : EditorDocument
{
    private sealed class SideRow
    {
        public required PickerEntry Entry { get; init; }
        public required int Index { get; init; }
        public bool Selected { get; set; }
    }

    private sealed class ChainItem
    {
        public required PickerEntry Entry { get; init; }
        public required int Index { get; init; }
    }

    private static readonly RmlStructType<PickerRow> RowType = new RmlStructType<PickerRow>()
        .Member("label", static r => r.Entry.Label)
        .Member("icon", static r => r.Entry.Icon)
        .Member("index", static r => r.Index)
        .Member("indent", static r => r.Indent)
        .Member("has_children", static r => r.HasChildren)
        .Member("expanded", static r => r.Expanded)
        .Member("match", static r => r.Match)
        .Member("selectable", static r => r.Entry.Selectable)
        .Member("selected", static r => r.Selected)
        .Member("favorite", static r => r.Favorite)
        .Member("star_icon", static r => r.Favorite ? "icon icon-sm icon-star-filled" : "icon icon-sm icon-star");

    private static readonly RmlStructType<SideRow> SideType = new RmlStructType<SideRow>()
        .Member("label", static r => r.Entry.Label)
        .Member("icon", static r => r.Entry.Icon)
        .Member("index", static r => r.Index)
        .Member("selected", static r => r.Selected);

    private static readonly RmlStructType<ChainItem> ChainType = new RmlStructType<ChainItem>()
        .Member("label", static c => c.Entry.Label)
        .Member("icon", static c => c.Entry.Icon)
        .Member("index", static c => c.Index);

    private readonly List<PickerRow> _rows = [];
    private readonly List<SideRow> _favorites = [];
    private readonly List<SideRow> _recent = [];
    private readonly List<ChainItem> _chain = [];
    private RmlDataModel? _model;
    private TreePickerRequest? _request;
    private PickerTree _tree = new([]);
    private string _query = "";
    private string _error = "";
    private int _scrollCountdown;

    public TreePickerDialog(EditorWorkspace workspace)
        : base(workspace, "tree_picker.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The tree on screen (tests and QA scripts).</summary>
    public PickerTree Model => _tree;

    /// <summary>The labels of the visible rows (tests and QA scripts).</summary>
    public IEnumerable<string> VisibleLabels => _rows.Select(r => r.Entry.Label);

    /// <summary>The labels in the Favourites and Recent lists.</summary>
    public IEnumerable<string> FavoriteLabels => _favorites.Select(r => r.Entry.Label);

    public IEnumerable<string> RecentLabels => _recent.Select(r => r.Entry.Label);

    /// <summary>The persisted-list key of the open request.</summary>
    public string? Kind => _request?.Kind;

    protected override void OnReady()
    {
        _model = CreateDataModel("tree_picker")
            .Bind("title", this, static d => d._request?.Title ?? "")
            .Bind("ok_label", this, static d => d._request?.OkLabel ?? "OK")
            .Bind("error", this, static d => d._error)
            .Bind("has_browse", this, static d => d._request?.OnBrowse is not null)
            .Bind("has_selection", this, static d => d._tree.Selected is not null)
            .Bind("desc_icon", this, static d => d._tree.Selected?.Icon ?? "icon icon-circle-dot icon-muted")
            .Bind("desc_title", this, static d => d._tree.Selected?.Label ?? "")
            .Bind("desc_text", this, static d => d.DescriptionText())
            .Bind("query", this, static d => d._query, static (d, v) =>
            {
                d._query = v ?? "";
                // Filtering changes the list's size: done after the UI update that reported the edit, never inside it.
                if (d.IsInsideTree)
                    d.CallDeferred(static state => ((TreePickerDialog)state!).ApplyQuery(), d);
            })
            .BindList("rows", _rows, RowType)
            .BindList("favorites", _favorites, SideType)
            .BindList("recent", _recent, SideType)
            .BindList("chain", _chain, ChainType)
            .Event("select", e => SelectRow(e.GetArgument(0).GetInt32()))
            .Event("activate", e =>
            {
                SelectRow(e.GetArgument(0).GetInt32());
                Accept();
            })
            .Event("toggle", e =>
            {
                Toggle(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("star", e =>
            {
                ToggleFavorite(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("pick_side", e => SelectSide(e.GetArgument(0).GetInt32()))
            .Event("activate_side", e =>
            {
                SelectSide(e.GetArgument(0).GetInt32());
                Accept();
            })
            .Event("browse", _ => Browse())
            .Event("ok", _ => Accept())
            .Event("cancel", _ => Cancel());
    }

    private string DescriptionText()
    {
        if (_tree.Selected is not { } entry)
            return _rows.Count == 0 ? "Nothing matches the search." : "";
        if (entry.Description.Length > 0)
            return entry.Description;
        return entry.Selectable ? "No description." : "Abstract: choose one of the types below it.";
    }

    /// <summary>Opens the dialog for <paramref name="request"/>.</summary>
    public void Show(TreePickerRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _tree = new PickerTree(request.Entries);
        _query = "";
        _error = "";
        _tree.SetQuery("");
        RefreshSideLists();
        Visible = true;
        Sync();
        if (EnsureLoaded() && Document.GetElementById("query") is { IsNull: false } field)
        {
            field.SetValue("");
            field.Focus(focusVisible: true);
        }
    }

    /// <summary>Filters with <paramref name="query"/> (tests and QA scripts; the search field does the same).</summary>
    public void SetQuery(string query)
    {
        _query = query ?? "";
        ApplyQuery();
    }

    private void ApplyQuery()
    {
        _tree.SetQuery(_query);
        _error = "";
        Sync();
    }

    public void SelectRow(int index)
    {
        _tree.SelectRow(index);
        _error = "";
        Sync();
    }

    /// <summary>Selects the entry labelled <paramref name="label"/> (QA scripts, tests).</summary>
    public bool SelectLabel(string label)
    {
        var row = _rows.FindIndex(r => string.Equals(r.Entry.Label, label, StringComparison.Ordinal));
        if (row < 0)
            return false;
        SelectRow(row);
        return true;
    }

    public void Toggle(int index)
    {
        _tree.Toggle(index);
        Sync();
    }

    /// <summary>Stars or un-stars row <paramref name="index"/> (persisted).</summary>
    public void ToggleFavorite(int index)
    {
        if (_request is null || (uint)index >= (uint)_rows.Count || !_rows[index].Entry.Selectable)
            return;
        Workspace.Layout.ToggleFavorite(_request.Kind, _rows[index].Entry.Id);
        Workspace.SaveLayout();
        RefreshSideLists();
        Sync();
    }

    private void SelectSide(int index)
    {
        var row = index < _favorites.Count ? _favorites[index] : index - _favorites.Count < _recent.Count ? _recent[index - _favorites.Count] : null;
        if (row is null)
            return;
        if (_query.Length > 0)
        {
            // Show the whole tree again so the chosen entry is visible.
            _query = "";
            _tree.SetQuery("");
            if (IsLoaded && Document.GetElementById("query") is { IsNull: false } field)
                field.SetValue("");
        }

        _tree.Select(row.Entry);
        _error = "";
        Sync();
        ScrollToSelection();
    }

    private void RefreshSideLists()
    {
        _favorites.Clear();
        _recent.Clear();
        if (_request is null)
            return;
        foreach (var id in Workspace.Layout.PickerFavorites(_request.Kind))
            if (_tree.Find(id) is { Selectable: true } entry)
                _favorites.Add(new SideRow { Entry = entry, Index = _favorites.Count });
        foreach (var id in Workspace.Layout.PickerRecent(_request.Kind))
            if (_tree.Find(id) is { Selectable: true } entry)
                _recent.Add(new SideRow { Entry = entry, Index = _favorites.Count + _recent.Count });
        _tree.SetFavorites(Workspace.Layout.PickerFavorites(_request.Kind));
    }

    // Copies the tree's rows and the selection-dependent lists into the bound collections.
    private void Sync()
    {
        _rows.Clear();
        _rows.AddRange(_tree.Rows);
        foreach (var side in _favorites)
            side.Selected = ReferenceEquals(side.Entry, _tree.Selected);
        foreach (var side in _recent)
            side.Selected = ReferenceEquals(side.Entry, _tree.Selected);
        _chain.Clear();
        if (_tree.Selected is { } selected)
        {
            var chain = _tree.Chain(selected);
            for (var i = 0; i < chain.Count; i++)
                _chain.Add(new ChainItem { Entry = chain[i], Index = i });
        }

        _model?.DirtyAll();
    }

    public void Accept()
    {
        if (_request is null)
            return;
        if (_tree.Selected is not { Selectable: true } entry)
        {
            _error = _tree.Selected is null ? "Nothing selected." : $"{_tree.Selected.Label} can't be created: choose one of the entries below it.";
            _model?.Dirty("error");
            return;
        }

        var request = _request;
        Workspace.Layout.AddRecent(request.Kind, entry.Id);
        Workspace.SaveLayout();
        Close();
        request.OnAccept(entry);
    }

    public void Cancel() => Close();

    private void Browse()
    {
        var browse = _request?.OnBrowse;
        Close();
        browse?.Invoke();
    }

    private void Close()
    {
        HideAndReleaseFocus();
        _request = null;
        _tree = new PickerTree([]);
        _rows.Clear();
        _favorites.Clear();
        _recent.Clear();
        _chain.Clear();
        _model?.DirtyAll();
    }

    /// <summary>Called every frame by the workspace: scrolls a keyboard-moved selection into view once its row exists.</summary>
    public void Tick()
    {
        if (_scrollCountdown == 0 || --_scrollCountdown > 0)
            return;
        var row = _tree.SelectedRow;
        if (row < 0 || !IsLoaded || Document.GetElementById("rows") is not { IsNull: false } list || row >= list.ChildCount)
            return;
        list.GetChild(row).ScrollIntoView(alignWithTop: false);
    }

    private void ScrollToSelection() => _scrollCountdown = 2;

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
                _tree.Move(1);
                Sync();
                ScrollToSelection();
                break;
            case RmlKey.Up:
                _tree.Move(-1);
                Sync();
                ScrollToSelection();
                break;
        }
    }
}
