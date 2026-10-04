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

    /// <summary>Icon category (CSS class): node, node3d, node2d, mesh, light, camera, physics, audio, ui, env, missing.</summary>
    public required string Icon { get; init; }

    /// <summary>The root of an instanced sub-scene (its inner nodes are not listed).</summary>
    public required bool IsInstance { get; init; }

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
        _rows.Add(new SceneTreeRow
        {
            Node = node,
            Depth = depth,
            HasChildren = listedChildren > 0,
            Expanded = expanded,
            Selected = scene.Selection.Contains(node),
            Name = node.Name,
            TypeName = node is MissingNode missing ? missing.OriginalType : TypeNameOf(node),
            Icon = IconOf(node),
            IsInstance = isInstance,
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

    /// <summary>The icon category of a node (most specific first).</summary>
    public static string IconOf(Node node) => node switch
    {
        MissingNode => "missing",
        Light3D => "light",
        Camera3D or Camera2D => "camera",
        GeometryInstance3D or SpineNode => "mesh",
        CollisionObject3D or CollisionShape3D or CollisionObject2D or CollisionShape2D => "physics",
        AudioPlayer or AudioPlayer3D or AudioPlayer2D or AudioListener3D => "audio",
        UiLayer or UiDocument => "ui",
        WorldEnvironment => "env",
        SubViewport => "viewport",
        Node3D => "node3d",
        Node2D => "node2d",
        _ => "node",
    };
}
