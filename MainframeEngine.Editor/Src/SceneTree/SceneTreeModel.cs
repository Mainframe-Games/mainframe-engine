using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>One visible row of the scene tree panel.</summary>
public sealed class SceneTreeRow
{
    public required Node Node { get; init; }
    public required int Depth { get; init; }
    public required bool HasChildren { get; init; }
    public required bool Expanded { get; init; }
    public bool Selected { get; set; }
    public required string Name { get; init; }
    public required string TypeName { get; init; }

    /// <summary>The type icon's classes (<c>"icon icon-cube icon-3d"</c>, <see cref="EditorIcons.Classes(Node)"/>).</summary>
    public required string Icon { get; init; }

    /// <summary>The row's tooltip: name and type (and the instanced scene).</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>The root of an instanced sub-scene (its inner nodes are not listed).</summary>
    public required bool IsInstance { get; init; }

    /// <summary>The instance badge's tooltip (the scene file).</summary>
    public string InstanceTooltip { get; init; } = "";

    /// <summary>The node's exported <c>Visible</c> flag (Node3D, Node2D, UI), or null when it has none: the eye toggle.</summary>
    public Serialization.ExportPropertyInfo? VisibleProperty { get; init; }

    /// <summary>The <c>Visible</c> value shown by the eye toggle.</summary>
    public bool Shown { get; init; } = true;

    /// <summary>Script badge icon classes: <c>code</c> (a game type), <c>tool</c> (a [Tool] type that runs in the editor) or "" (engine type).</summary>
    public string Script { get; init; } = "";

    /// <summary>The script badge's tooltip.</summary>
    public string ScriptTooltip { get; init; } = "";

    /// <summary>Configuration warnings (<see cref="NodeWarnings"/>), "" when there are none.</summary>
    public string Warning { get; init; } = "";

    /// <summary>Row index in the flattened list.</summary>
    public int Index { get; init; }

    /// <summary>The row's indentation as an RCSS length (cached: data bindings read it without allocating).</summary>
    public string Indent { get; init; } = "0dp";

    /// <summary>Drag-and-drop feedback: 0 none, 1 drop inside, 2 before, 3 after.</summary>
    public int Drop { get; set; }

    /// <summary>The row being dragged.</summary>
    public bool Dragging { get; set; }
}

/// <summary>
/// The scene tree panel's view model: the edited scene flattened into visible rows (children of collapsed nodes and the
/// inner nodes of instanced sub-scenes are skipped), with expand state per node. Rebuilt only when the scene, the
/// selection or the expand state changed, so idle frames do nothing.
/// </summary>
public sealed class SceneTreeModel
{
    private readonly HashSet<Node> _collapsed = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneTreeRow> _rows = [];

    public IReadOnlyList<SceneTreeRow> Rows => _rows;

    /// <summary>Whether <paramref name="node"/> shows its children (default: expanded).</summary>
    public bool IsExpanded(Node node) => !_collapsed.Contains(node);

    public void SetExpanded(Node node, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (expanded)
            _collapsed.Remove(node);
        else
            _collapsed.Add(node);
    }

    public void Toggle(Node node) => SetExpanded(node, !IsExpanded(node));

    /// <summary>Expands every ancestor of <paramref name="node"/> up to <paramref name="root"/> (reveal a picked node).</summary>
    public void Reveal(Node root, Node node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            _collapsed.Remove(n);
            if (ReferenceEquals(n, root))
                break;
        }
    }

    /// <summary>Rebuilds the rows of <paramref name="scene"/> (null clears).</summary>
    public void Rebuild(EditedScene? scene)
    {
        _rows.Clear();
        if (scene is null)
            return;
        _collapsed.RemoveWhere(n => n.IsFreed);
        Add(scene, scene.Root, 0);
    }

    /// <summary>Index of the row showing <paramref name="node"/>, or -1.</summary>
    public int IndexOf(Node node)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Node, node))
                return i;
        return -1;
    }

    private void Add(EditedScene scene, Node node, int depth)
    {
        var isInstance = depth > 0 && node.SceneFilePath is not null;
        var listedChildren = 0;
        if (!isInstance)
            foreach (var child in node.Children)
                if (scene.IsEditable(child))
                    listedChildren++;
        var expanded = IsExpanded(node);
        var typeName = node is MissingNode missing ? missing.OriginalType : TypeNameOf(node);
        var info = TypeRegistry.GetNearest(node.GetType());
        var visible = info?.FindProperty("Visible") is { } property && property.ValueType == typeof(bool) ? property : null;
        var (script, scriptTooltip) = ScriptOf(node, info);
        _rows.Add(new SceneTreeRow
        {
            Node = node,
            Depth = depth,
            HasChildren = listedChildren > 0,
            Expanded = expanded,
            Selected = scene.Selection.Contains(node),
            Name = node.Name,
            TypeName = typeName,
            Icon = EditorIcons.Classes(node),
            Tooltip = node is MissingNode ? $"{node.Name} — {typeName} (not loaded)" : $"{node.Name} — {typeName}",
            IsInstance = isInstance,
            InstanceTooltip = isInstance ? $"Instance of {node.SceneFilePath} — its nodes belong to that scene" : "",
            VisibleProperty = visible,
            Shown = visible is null || visible.GetValue(node) is true,
            Script = script,
            ScriptTooltip = scriptTooltip,
            Warning = NodeWarnings.For(node) ?? "",
            Index = _rows.Count,
            Indent = RmlText.Dp(4 + depth * 14),
        });

        if (!expanded || isInstance)
            return;
        foreach (var child in node.Children)
            if (scene.IsEditable(child))
                Add(scene, child, depth + 1);
    }

    private static string TypeNameOf(Node node) => TypeRegistry.GetNearest(node.GetType())?.Name ?? node.GetType().Name;

    // A type from outside the engine is the game's script (Godot's script badge); [Tool] types also run in the editor.
    private static (string Badge, string Tooltip) ScriptOf(Node node, NodeTypeInfo? info)
    {
        if (node is MissingNode || info is null)
            return ("", "");
        if (info.IsTool && info.Type.Assembly != typeof(Node).Assembly)
            return ("icon icon-sm icon-tool", $"Tool Script — {info.Type.FullName} runs in the editor too");
        if (info.Type.Assembly != typeof(Node).Assembly)
            return ("icon icon-sm icon-code", $"Script — {info.Type.FullName}");
        return ("", "");
    }
}
