namespace MainframeEngine.Editor;

/// <summary>
/// The selected nodes of one edited scene, in selection order (the last one is <see cref="Primary"/>: the inspector and
/// the gizmo act on it). Selection is not part of the undo history.
/// </summary>
public sealed class Selection
{
    private readonly List<Node> _nodes = [];

    public IReadOnlyList<Node> Nodes => _nodes;

    public int Count => _nodes.Count;

    /// <summary>The most recently selected node, or null.</summary>
    public Node? Primary => _nodes.Count > 0 ? _nodes[^1] : null;

    public event Action? Changed;

    /// <summary>Bumped on every change (the viewport caches its gizmo targets by it).</summary>
    public int Version { get; private set; }

    public bool Contains(Node node) => _nodes.Contains(node);

    private void Raise()
    {
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Selects only <paramref name="node"/> (null clears).</summary>
    public void Set(Node? node)
    {
        if (node is null)
        {
            Clear();
            return;
        }

        if (_nodes.Count == 1 && ReferenceEquals(_nodes[0], node))
            return;
        _nodes.Clear();
        _nodes.Add(node);
        Raise();
    }

    /// <summary>Selects exactly <paramref name="nodes"/>, in that order (the last is primary); one <see cref="Changed"/>.</summary>
    public void Set(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == _nodes.Count)
        {
            var same = true;
            for (var i = 0; i < nodes.Count && same; i++)
                same = ReferenceEquals(nodes[i], _nodes[i]);
            if (same)
                return;
        }

        _nodes.Clear();
        foreach (var node in nodes)
            if (!_nodes.Contains(node))
                _nodes.Add(node);
        Raise();
    }

    /// <summary>Adds <paramref name="nodes"/> in order (already selected ones move to the end); one <see cref="Changed"/>.</summary>
    public void AddRange(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
            return;
        foreach (var node in nodes)
        {
            _nodes.Remove(node);
            _nodes.Add(node);
        }

        Raise();
    }

    /// <summary>Toggles each of <paramref name="nodes"/> (Ctrl/Cmd + box); one <see cref="Changed"/>.</summary>
    public void ToggleRange(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
            return;
        foreach (var node in nodes)
            if (!_nodes.Remove(node))
                _nodes.Add(node);
        Raise();
    }

    /// <summary>Adds <paramref name="node"/> (or makes it primary when already selected).</summary>
    public void Add(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.Count > 0 && ReferenceEquals(_nodes[^1], node))
            return;
        _nodes.Remove(node);
        _nodes.Add(node);
        Raise();
    }

    /// <summary>Toggles <paramref name="node"/> (Ctrl/Cmd+click).</summary>
    public void Toggle(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.Remove(node))
            _nodes.Add(node);
        Raise();
    }

    public void Clear()
    {
        if (_nodes.Count == 0)
            return;
        _nodes.Clear();
        Raise();
    }

    /// <summary>Drops nodes that are no longer inside <paramref name="sceneRoot"/>'s subtree (deleted, undone additions).</summary>
    public void Prune(Node sceneRoot)
    {
        var removed = _nodes.RemoveAll(n => n.IsFreed || (!ReferenceEquals(n, sceneRoot) && !sceneRoot.IsAncestorOf(n)));
        if (removed > 0)
            Raise();
    }
}
