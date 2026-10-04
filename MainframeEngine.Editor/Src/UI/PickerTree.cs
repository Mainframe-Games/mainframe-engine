namespace MainframeEngine.Editor;

/// <summary>
/// One entry of a <see cref="TreePickerDialog"/> (a node or resource type, a scene file, a folder): shown with its icon
/// under its parent (<see cref="ParentId"/>, null for a root).
/// </summary>
/// <param name="Id">Unique key: favourites and recents are stored by it (type name, project-relative path).</param>
/// <param name="Label">The text in the tree and the search target.</param>
/// <param name="Icon">The icon's classes (<c>"icon icon-cube icon-3d"</c>).</param>
/// <param name="ParentId">The parent entry's id, or null for a root.</param>
/// <param name="Payload">What the dialog hands back when the entry is chosen.</param>
public sealed record PickerEntry(string Id, string Label, string Icon, string? ParentId, object? Payload)
{
    /// <summary>The description pane's text (type doc summary, file path).</summary>
    public string Description { get; init; } = "";

    /// <summary>False for structure-only entries (abstract types, folders): shown, never accepted.</summary>
    public bool Selectable { get; init; } = true;

    /// <summary>A grouping entry (a folder): sorted before its siblings.</summary>
    public bool IsGroup { get; init; }
}

/// <summary>A visible row of a <see cref="PickerTree"/>.</summary>
public sealed class PickerRow
{
    public required PickerEntry Entry { get; init; }
    public required int Index { get; init; }
    public required int Depth { get; init; }
    public required bool HasChildren { get; init; }
    public required bool Expanded { get; init; }

    /// <summary>The entry matches the search (false for ancestors shown only to keep the path visible).</summary>
    public required bool Match { get; init; }

    public bool Selected { get; set; }
    public bool Favorite { get; set; }
    public string Indent { get; init; } = "0dp";
}

/// <summary>
/// The model of the tree picker (Add Node, New Resource, Instance Scene; Godot's create dialog): entries as a tree
/// sorted by label, expand/collapse, a fuzzy search that keeps every match's ancestors visible and selects the best
/// match, keyboard movement over selectable rows and the ancestor chain of the selection (description pane).
/// </summary>
public sealed class PickerTree
{
    private readonly Dictionary<string, PickerEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PickerEntry>> _children = new(StringComparer.Ordinal);
    private readonly List<PickerEntry> _roots = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly List<PickerRow> _rows = [];
    private HashSet<string> _favorites = new(StringComparer.Ordinal);

    public PickerTree(IEnumerable<PickerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            _byId.TryAdd(entry.Id, entry);
        foreach (var entry in _byId.Values)
        {
            // An entry whose parent is not in the set becomes a root.
            if (entry.ParentId is { } parent && _byId.ContainsKey(parent))
            {
                if (!_children.TryGetValue(parent, out var list))
                    _children[parent] = list = [];
                list.Add(entry);
            }
            else
            {
                _roots.Add(entry);
            }
        }

        // Groups (folders) first, then by label.
        Comparison<PickerEntry> byLabel = static (a, b) => a.IsGroup != b.IsGroup
            ? (a.IsGroup ? -1 : 1)
            : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        _roots.Sort(byLabel);
        foreach (var list in _children.Values)
            list.Sort(byLabel);
        Rebuild();
    }

    public IReadOnlyList<PickerRow> Rows => _rows;

    public string Query { get; private set; } = "";

    /// <summary>The selected entry (null when nothing matches).</summary>
    public PickerEntry? Selected { get; private set; }

    /// <summary>Every entry by id.</summary>
    public PickerEntry? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The ids shown with a filled star.</summary>
    public void SetFavorites(IEnumerable<string> ids)
    {
        _favorites = new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        foreach (var row in _rows)
            row.Favorite = _favorites.Contains(row.Entry.Id);
    }

    /// <summary>Filters by <paramref name="query"/> (fuzzy, case-insensitive) and selects the best match.</summary>
    public void SetQuery(string query)
    {
        Query = query?.Trim() ?? "";
        Selected = null;
        Rebuild();
        if (Query.Length == 0)
        {
            SelectFirstSelectable();
            return;
        }

        PickerEntry? best = null;
        var bestScore = int.MinValue;
        foreach (var row in _rows)
        {
            if (!row.Match || !row.Entry.Selectable)
                continue;
            var score = Score(row.Entry.Label, Query);
            if (score > bestScore)
            {
                bestScore = score;
                best = row.Entry;
            }
        }

        Select(best);
    }

    /// <summary>Expands or collapses the entry of row <paramref name="index"/> (no effect while searching).</summary>
    public void Toggle(int index)
    {
        if ((uint)index >= (uint)_rows.Count || Query.Length > 0)
            return;
        var id = _rows[index].Entry.Id;
        if (!_collapsed.Remove(id))
            _collapsed.Add(id);
        Rebuild();
    }

    /// <summary>Selects <paramref name="entry"/> (expanding its ancestors); null clears.</summary>
    public void Select(PickerEntry? entry)
    {
        Selected = entry;
        if (entry is not null && Query.Length == 0)
        {
            var changed = false;
            for (var parent = Parent(entry); parent is not null; parent = Parent(parent))
                changed |= _collapsed.Remove(parent.Id);
            if (changed)
                Rebuild();
        }

        foreach (var row in _rows)
            row.Selected = ReferenceEquals(row.Entry, entry);
    }

    /// <summary>Selects row <paramref name="index"/>.</summary>
    public void SelectRow(int index)
    {
        if ((uint)index < (uint)_rows.Count)
            Select(_rows[index].Entry);
    }

    /// <summary>The index of the selected row, or -1.</summary>
    public int SelectedRow
    {
        get
        {
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].Selected)
                    return i;
            return -1;
        }
    }

    /// <summary>Moves the selection <paramref name="delta"/> rows up (−) or down (+), skipping structure-only rows.</summary>
    public void Move(int delta)
    {
        if (_rows.Count == 0)
            return;
        var index = SelectedRow;
        var step = Math.Sign(delta);
        if (step == 0)
            return;
        for (var i = index < 0 ? (step > 0 ? -1 : _rows.Count) : index; ;)
        {
            i += step;
            if (i < 0 || i >= _rows.Count)
                return;
            if (_rows[i].Entry.Selectable && (_rows[i].Match || Query.Length == 0))
            {
                Select(_rows[i].Entry);
                return;
            }
        }
    }

    /// <summary>The ancestors of <paramref name="entry"/> from the root down, ending with the entry itself.</summary>
    public IReadOnlyList<PickerEntry> Chain(PickerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var chain = new List<PickerEntry>();
        for (var e = entry; e is not null; e = Parent(e))
            chain.Insert(0, e);
        return chain;
    }

    private PickerEntry? Parent(PickerEntry entry) => entry.ParentId is { } id ? _byId.GetValueOrDefault(id) : null;

    private void SelectFirstSelectable()
    {
        foreach (var row in _rows)
        {
            if (row.Entry.Selectable)
            {
                Select(row.Entry);
                return;
            }
        }

        Select(null);
    }

    private void Rebuild()
    {
        _rows.Clear();
        HashSet<string>? visible = null;
        HashSet<string>? matches = null;
        if (Query.Length > 0)
        {
            visible = new HashSet<string>(StringComparer.Ordinal);
            matches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _byId.Values)
            {
                if (Score(entry.Label, Query) < 0)
                    continue;
                matches.Add(entry.Id);
                for (var e = entry; e is not null && visible.Add(e.Id); e = Parent(e))
                {
                }
            }
        }

        foreach (var root in _roots)
            Add(root, 0, visible, matches);
        foreach (var row in _rows)
        {
            row.Selected = ReferenceEquals(row.Entry, Selected);
            row.Favorite = _favorites.Contains(row.Entry.Id);
        }
    }

    private void Add(PickerEntry entry, int depth, HashSet<string>? visible, HashSet<string>? matches)
    {
        if (visible is not null && !visible.Contains(entry.Id))
            return;
        var children = _children.GetValueOrDefault(entry.Id);
        var searching = visible is not null;
        var expanded = searching || !_collapsed.Contains(entry.Id);
        _rows.Add(new PickerRow
        {
            Entry = entry,
            Index = _rows.Count,
            Depth = depth,
            HasChildren = children is { Count: > 0 },
            Expanded = expanded,
            Match = matches?.Contains(entry.Id) ?? true,
            Indent = RmlText.Dp(4 + depth * 16),
        });
        if (children is null || !expanded)
            return;
        foreach (var child in children)
            Add(child, depth + 1, visible, matches);
    }

    /// <summary>
    /// How well <paramref name="label"/> matches <paramref name="query"/> (higher is better), or -1: exact, prefix,
    /// word-start and substring matches beat a subsequence match ("mi3" finds MeshInstance3D); shorter labels win ties.
    /// </summary>
    public static int Score(string label, string query)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
            return 0;
        if (label.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 10_000;
        var lengthPenalty = Math.Min(label.Length, 99);
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 8_000 - lengthPenalty;
        var at = label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at > 0)
            return (char.IsUpper(label[at]) || !char.IsLetterOrDigit(label[at - 1]) ? 6_000 : 5_000) - at * 10 - lengthPenalty;

        // Subsequence: every query character in order; runs and word starts score higher, gaps lower.
        var score = 2_000 - lengthPenalty;
        var position = 0;
        var previous = -2;
        foreach (var c in query)
        {
            var found = -1;
            for (var i = position; i < label.Length; i++)
            {
                if (char.ToUpperInvariant(label[i]) == char.ToUpperInvariant(c))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
                return -1;
            if (found == previous + 1)
                score += 15;
            else if (char.IsUpper(label[found]) || found == 0 || char.IsDigit(label[found]))
                score += 10;
            else
                score -= found - position;
            previous = found;
            position = found + 1;
        }

        return score;
    }
}
