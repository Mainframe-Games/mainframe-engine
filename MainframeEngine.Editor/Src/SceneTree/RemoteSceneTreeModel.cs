using MainframeEngine.EditorLink;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>One visible row of the remote scene tree (a running game's node, read-only).</summary>
public sealed class RemoteTreeRow
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }

    /// <summary>The type icon's classes (the editor's icon when the type is registered, the default node icon otherwise).</summary>
    public required string Icon { get; init; }

    public required int Depth { get; init; }
    public required bool HasChildren { get; init; }
    public required bool Expanded { get; init; }

    /// <summary>The node's path from the root (<c>/root/Main/World</c>): the key its expand state is kept under.</summary>
    public required string Path { get; init; }

    /// <summary>Row index in the flattened list.</summary>
    public int Index { get; init; }

    /// <summary>The row's indentation as an RCSS length.</summary>
    public string Indent { get; init; } = "0dp";

    /// <summary>Name, type and path.</summary>
    public string Tooltip { get; init; } = "";
}

/// <summary>
/// The scene tree panel's Remote view model (Godot's remote scene tree): a running game's last snapshot
/// (<see cref="EditorLinkTreeNode"/>s, depth-first) flattened into visible rows. Nodes above
/// <see cref="ExpandedDepth"/> start expanded and deeper ones collapsed; toggles are remembered by node path, so they
/// survive the next snapshot.
/// </summary>
public sealed class RemoteSceneTreeModel
{
    /// <summary>Nodes shallower than this start expanded (the root and its children: the scene and the autoloads).</summary>
    public const int ExpandedDepth = 2;

    private readonly HashSet<string> _toggled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);
    private readonly List<RemoteTreeRow> _rows = [];

    /// <summary>The visible rows of the last <see cref="Rebuild"/>.</summary>
    public IReadOnlyList<RemoteTreeRow> Rows => _rows;

    /// <summary>The snapshot's node count (visible or not).</summary>
    public int NodeCount { get; private set; }

    /// <summary>Flattens <paramref name="nodes"/> (null: no snapshot yet) into <see cref="Rows"/>.</summary>
    public void Rebuild(IReadOnlyList<EditorLinkTreeNode>? nodes)
    {
        _rows.Clear();
        NodeCount = nodes?.Count ?? 0;
        if (nodes is null)
            return;
        var path = new List<string>();
        var hiddenBelow = int.MaxValue; // rows deeper than this are inside a collapsed node
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var depth = Math.Max(0, node.Depth);
            if (path.Count > depth)
                path.RemoveRange(depth, path.Count - depth);
            while (path.Count < depth)
                path.Add("?"); // a malformed snapshot (a depth jump); keep going
            path.Add(node.Name);
            if (depth <= hiddenBelow)
                hiddenBelow = int.MaxValue;
            if (depth > hiddenBelow)
                continue;
            var key = "/" + string.Join('/', path);
            var hasChildren = i + 1 < nodes.Count && nodes[i + 1].Depth > node.Depth;
            var expanded = hasChildren && IsExpanded(key, depth);
            if (hasChildren && !expanded)
                hiddenBelow = depth;
            _rows.Add(new RemoteTreeRow
            {
                Name = node.Name,
                TypeName = node.Type,
                Icon = IconFor(node.Type),
                Depth = depth,
                HasChildren = hasChildren,
                Expanded = expanded,
                Path = key,
                Index = _rows.Count,
                Indent = RmlText.Dp(4 + depth * 14),
                Tooltip = $"{node.Name} ({node.Type}) — {key}",
            });
        }
    }

    /// <summary>Forgets the cached type icons (a new game session: its types may have been reloaded).</summary>
    public void ClearIcons() => _icons.Clear();

    /// <summary>Expands or collapses the node at <paramref name="path"/> (takes effect on the next <see cref="Rebuild"/>).</summary>
    public void Toggle(string path)
    {
        if (!_toggled.Remove(path))
            _toggled.Add(path);
    }

    private bool IsExpanded(string path, int depth) => depth < ExpandedDepth != _toggled.Contains(path);

    private string IconFor(string typeName)
    {
        if (_icons.TryGetValue(typeName, out var classes))
            return classes;
        classes = TypeRegistry.Get(typeName) is { } info ? EditorIcons.Classes(info.Type) : "icon icon-" + EditorIcons.Default + " icon-logic";
        _icons[typeName] = classes;
        return classes;
    }
}
