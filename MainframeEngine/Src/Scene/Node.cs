using System.Diagnostics.CodeAnalysis;

namespace MainframeEngine;

/// <summary>
/// Base of everything in a scene (Godot's <c>Node</c>): a name, a parent, ordered children, an owner, groups,
/// a <see cref="ProcessMode"/> and lifecycle callbacks driven by the <see cref="SceneTree"/> it is inside.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="OnEnterTree"/> runs top-down when a subtree enters a tree, <see cref="OnReady"/> runs
/// bottom-up once per node after its children are ready, <see cref="OnProcess"/> and
/// <see cref="OnPhysicsProcess"/> run every frame / fixed step while inside the tree, and
/// <see cref="OnExitTree"/> runs bottom-up on removal.
/// </para>
/// <para>
/// Constructors must stay cheap and side-effect free: the type registry instantiates a pristine copy of
/// every serialized type to learn its default property values, and scenes are instantiated outside the tree.
/// Acquire GPU, physics or audio objects in <see cref="OnEnterTree"/> (or through a server), not in the
/// constructor. Nodes are single-threaded: only touch a node that is inside a tree from the main thread.
/// </para>
/// </remarks>
[EditorIcon("circle-dot", Family = EditorIconFamily.Logic)]
public partial class Node : IDisposable
{
    /// <summary>Children are found by linear scan up to this count; above it a name index is kept.</summary>
    private const int NameIndexThreshold = 8;

    private string _name = string.Empty;
    private Node? _parent;
    private List<Node>? _children;
    private Dictionary<string, Node>? _childrenByName;
    private Node? _owner;
    private SceneTree? _tree;
    private SceneViewport? _viewport;
    private int _indexInParent = -1;
    private int _depth = -1;
    private bool _readyDone;
    private bool _freed;
    private bool _queuedForDeletion;
    private bool _exiting;

    public Node()
    {
        Id = NodeId.GetNext();
        InitializeProcessFlags();
    }

    /// <summary>Runtime-unique identity (never reused within a process); see <see cref="SceneTree.Find"/>.</summary>
    public NodeId Id { get; }

    /// <summary>
    /// The node's name, unique among its siblings. Characters that would break a <see cref="NodePath"/>
    /// (<c>/ : @ % "</c>) are replaced with <c>_</c>. When the name collides with a sibling a number is
    /// appended, as in Godot ("Box" → "Box2"). An empty name becomes the type name when the node is added.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            var sanitized = SanitizeName(value);
            if (string.Equals(sanitized, _name, StringComparison.Ordinal))
                return;
            if (_parent is not null)
            {
                if (sanitized.Length == 0)
                    sanitized = GetType().Name;
                sanitized = _parent.MakeUniqueChildName(this, sanitized);
            }

            SetNameInternal(sanitized);
            Renamed?.Invoke();
        }
    }

    /// <summary>
    /// Changes the name keeping the parent's name index and the owner's unique-name registry consistent; a
    /// collision in the owner's registry throws before anything changes.
    /// </summary>
    private void SetNameInternal(string name)
    {
        if (string.Equals(name, _name, StringComparison.Ordinal))
            return;
        var owner = UniqueNameInOwner ? _owner : null;
        owner?.EnsureUniqueNameFree(name, this);

        owner?.UnregisterUniqueName(this);
        _parent?.UnindexChildName(this);
        _name = name;
        _parent?.IndexChildName(this);
        owner?.RegisterUniqueName(this);
    }

    public Node? Parent => _parent;

    /// <summary>The children in order. Do not hold on to it across tree changes.</summary>
    public IReadOnlyList<Node> Children => (IReadOnlyList<Node>?)_children ?? [];

    public int ChildCount => _children?.Count ?? 0;

    public Node GetChild(int index)
    {
        if (_children is null || (uint)index >= (uint)_children.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"'{Name}' has {ChildCount} children.");
        return _children[index];
    }

    /// <summary>Position among the parent's children; -1 without a parent.</summary>
    public int GetIndex() => _indexInParent;

    /// <summary>
    /// The scene root this node was saved with (Godot semantics). Must be an ancestor. The scene saver only
    /// writes nodes owned by the root being saved, so nodes spawned at runtime (no owner) are never saved.
    /// </summary>
    public Node? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(value, _owner))
                return;
            if (value is not null && !value.IsAncestorOf(this))
                throw new InvalidOperationException($"Owner '{value.Name}' must be an ancestor of '{Name}'.");

            if (_owner is not null && UniqueNameInOwner)
                _owner.UnregisterUniqueName(this);
            _owner = value;
            if (_owner is not null && UniqueNameInOwner)
                _owner.RegisterUniqueName(this);
        }
    }

    /// <summary>The tree this node is inside, or null.</summary>
    public SceneTree? Tree => _tree;

    public bool IsInsideTree => _tree is not null;

    /// <summary>True once <see cref="OnReady"/> has run (it runs once per node, see <see cref="RequestReady"/>).</summary>
    public bool IsNodeReady => _readyDone;

    /// <summary>Depth below the tree root (root = 0); -1 outside a tree.</summary>
    public int Depth => _depth;

    /// <summary>Set on the root of an instanced scene: the scene file it came from.</summary>
    public string? SceneFilePath { get; set; }

    /// <summary>Nearest <see cref="SceneViewport"/> ancestor (or self); null outside a tree.</summary>
    public SceneViewport? GetViewport() => _viewport;

    /// <summary>The 3D world this node renders into (its viewport's), or null outside a tree.</summary>
    public World3D? GetWorld3D() => _viewport?.World3D;

    /// <summary>The 2D world this node belongs to (its viewport's), or null outside a tree.</summary>
    public World2D? GetWorld2D() => _viewport?.World2D;

    // ------------------------------------------------------------------------------------------------
    // Children
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="child"/> as the last child. If this node is inside a tree the child's subtree
    /// enters it: <see cref="OnEnterTree"/> top-down, then <see cref="OnReady"/> bottom-up.
    /// </summary>
    public void AddChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ThrowIfFreed();
        child.ThrowIfFreed();
        if (child._parent is not null)
            throw new InvalidOperationException(
                $"'{child.Name}' already has a parent ('{child._parent.Name}'); call RemoveChild or Reparent first.");
        if (ReferenceEquals(child, this) || child.IsAncestorOf(this))
            throw new InvalidOperationException($"Adding '{child.Name}' under '{Name}' would create a cycle.");
        if (child is SceneViewport { IsTreeRoot: true })
            throw new InvalidOperationException("A tree root cannot be added as a child.");

        AttachChild(child, (_children?.Count) ?? 0);

        if (_tree is { } tree)
        {
            child.PropagateEnterTree(tree, _viewport, _depth + 1);
            child.PropagateReady();
        }
    }

    /// <summary>Adds <paramref name="sibling"/> right after this node under the same parent.</summary>
    public void AddSibling(Node sibling)
    {
        if (_parent is null)
            throw new InvalidOperationException($"'{Name}' has no parent.");
        _parent.AddChild(sibling);
        _parent.MoveChild(sibling, _indexInParent + 1);
    }

    /// <summary>Removes <paramref name="child"/> (and its subtree) from this node, and from the tree if inside one.</summary>
    public void RemoveChild(Node child) => RemoveChild(child, validateOwners: true);

    private void RemoveChild(Node child, bool validateOwners)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(child._parent, this))
            throw new InvalidOperationException($"'{child.Name}' is not a child of '{Name}'.");

        var tree = _tree;
        if (tree is not null && child._tree is not null)
            child.PropagateExitTree();

        // The subtree may have been re-parented by an exit callback; only detach if it is still ours.
        if (!ReferenceEquals(child._parent, this))
            return;

        DetachChild(child);

        if (tree is not null)
            child.PropagateAfterExitTree();
        if (validateOwners)
            child.PropagateValidateOwner();
    }

    /// <summary>Moves <paramref name="child"/> to <paramref name="toIndex"/> among the children (-1 = last).</summary>
    public void MoveChild(Node child, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(child._parent, this) || _children is null)
            throw new InvalidOperationException($"'{child.Name}' is not a child of '{Name}'.");
        if (toIndex < 0)
            toIndex = _children.Count - 1;
        if (toIndex >= _children.Count)
            throw new ArgumentOutOfRangeException(nameof(toIndex), toIndex, $"'{Name}' has {_children.Count} children.");

        var from = child._indexInParent;
        if (from == toIndex)
            return;

        _children.RemoveAt(from);
        _children.Insert(toIndex, child);
        var lo = Math.Min(from, toIndex);
        var hi = Math.Max(from, toIndex);
        for (var i = lo; i <= hi; i++)
            _children[i]._indexInParent = i;

        _tree?.MarkStructureChanged();
        OnChildOrderChanged();
    }

    /// <summary>
    /// Moves this node under <paramref name="newParent"/>. With <paramref name="keepGlobalTransform"/>,
    /// 2D/3D nodes keep their world transform (the local transform is recomputed). Owners stay valid when the
    /// new parent is still below them.
    /// </summary>
    public void Reparent(Node newParent, bool keepGlobalTransform = true)
    {
        ArgumentNullException.ThrowIfNull(newParent);
        if (ReferenceEquals(newParent, _parent))
            return;
        if (ReferenceEquals(newParent, this) || IsAncestorOf(newParent))
            throw new InvalidOperationException($"Reparenting '{Name}' under '{newParent.Name}' would create a cycle.");

        var saved = keepGlobalTransform ? CaptureGlobalTransform() : null;
        _parent?.RemoveChild(this, validateOwners: false);
        newParent.AddChild(this);
        if (saved is not null)
            RestoreGlobalTransform(saved);
        PropagateValidateOwner();
    }

    /// <summary>Global transform snapshot used by <see cref="Reparent"/>; 2D/3D nodes override.</summary>
    private protected virtual object? CaptureGlobalTransform() => null;

    private protected virtual void RestoreGlobalTransform(object state)
    {
    }

    /// <summary>True if this node is a (strict) ancestor of <paramref name="node"/>.</summary>
    public bool IsAncestorOf(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (var p = node._parent; p is not null; p = p._parent)
            if (ReferenceEquals(p, this))
                return true;
        return false;
    }

    internal List<Node>? ChildList => _children;

    private void AttachChild(Node child, int index)
    {
        _children ??= [];
        // Renamed before it is linked (not yet in our list or name index), keeping its owner's registry right.
        child.SetNameInternal(MakeUniqueChildName(child, child._name.Length == 0 ? child.GetType().Name : child._name));
        child._parent = this;
        child._indexInParent = index;
        _children.Insert(index, child);
        for (var i = index + 1; i < _children.Count; i++)
            _children[i]._indexInParent = i;
        IndexChildName(child);
        child.OnParentChanged();
        _tree?.MarkStructureChanged();
    }

    private void DetachChild(Node child)
    {
        var children = _children!;
        UnindexChildName(child);
        var index = child._indexInParent;
        children.RemoveAt(index);
        for (var i = index; i < children.Count; i++)
            children[i]._indexInParent = i;
        child._parent = null;
        child._indexInParent = -1;
        child.OnParentChanged();
        _tree?.MarkStructureChanged();
    }

    /// <summary>Called after the parent changed (attach or detach); 2D/3D nodes invalidate their global transform.</summary>
    private protected virtual void OnParentChanged()
    {
    }

    /// <summary>Called after <see cref="MoveChild"/> reorders the children.</summary>
    private protected virtual void OnChildOrderChanged()
    {
    }

    // ------------------------------------------------------------------------------------------------
    // Names
    // ------------------------------------------------------------------------------------------------

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        if (name is "." or "..")
            return "_";
        if (name.AsSpan().IndexOfAny("/:@%\"") < 0)
            return name;

        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is '/' or ':' or '@' or '%' or '"' ? '_' : source[i];
        });
    }

    private string MakeUniqueChildName(Node child, string name)
    {
        if (!IsNameTakenByOther(name, child))
            return name;

        // "Box" -> "Box2", "Box2" -> "Box3": strip trailing digits and count up from the next number.
        var stem = name.AsSpan().TrimEnd("0123456789");
        var start = 2;
        if (stem.Length < name.Length && int.TryParse(name.AsSpan(stem.Length), out var n) && n < int.MaxValue)
            start = n + 1;
        var stemText = stem.ToString();
        for (var i = start; ; i++)
        {
            var candidate = string.Concat(stemText, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!IsNameTakenByOther(candidate, child))
                return candidate;
        }
    }

    /// <summary>True if a child other than <paramref name="child"/> is called <paramref name="name"/>.</summary>
    private bool IsNameTakenByOther(string name, Node child)
    {
        // The child itself is not in the name index while it is being (re)named.
        if (_childrenByName is not null)
            return _childrenByName.TryGetValue(name, out var indexed) && !ReferenceEquals(indexed, child);

        if (_children is null)
            return false;
        foreach (var other in _children)
            if (!ReferenceEquals(other, child) && string.Equals(other._name, name, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void IndexChildName(Node child)
    {
        if (_childrenByName is null)
        {
            if (_children is null || _children.Count <= NameIndexThreshold)
                return;
            _childrenByName = new Dictionary<string, Node>(_children.Count * 2, StringComparer.Ordinal);
            foreach (var c in _children)
                _childrenByName[c._name] = c;
            return;
        }

        _childrenByName[child._name] = child;
    }

    private void UnindexChildName(Node child)
    {
        if (_childrenByName is not null
            && _childrenByName.TryGetValue(child._name, out var indexed)
            && ReferenceEquals(indexed, child))
            _childrenByName.Remove(child._name);
    }

    /// <summary>Finds a direct child by exact name without allocating.</summary>
    internal Node? FindChildByName(ReadOnlySpan<char> name)
    {
        if (_childrenByName is not null)
            return _childrenByName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var found) ? found : null;

        if (_children is null)
            return null;
        foreach (var child in _children)
            if (name.SequenceEqual(child._name))
                return child;
        return null;
    }

    // ------------------------------------------------------------------------------------------------
    // Lookup
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="path"/> relative to this node (or from <c>/root</c> when absolute) and casts it.
    /// Throws when nothing is found or the node is not a <typeparamref name="T"/>.
    /// </summary>
    public T GetNode<T>(NodePath path) where T : Node
    {
        var node = GetNodeOrNull(path)
                   ?? throw new InvalidOperationException($"Node not found: '{path}' (relative to '{GetPathOrName()}').");
        return node as T
               ?? throw new InvalidCastException($"Node '{path}' is a {node.GetType().Name}, not a {typeof(T).Name}.");
    }

    public Node GetNode(NodePath path) => GetNode<Node>(path);

    /// <summary>Like <see cref="GetNode{T}"/> but returns null when not found or of another type.</summary>
    public T? GetNodeOrNull<T>(NodePath path) where T : Node => GetNodeOrNull(path) as T;

    public bool HasNode(NodePath path) => GetNodeOrNull(path) is not null;

    /// <summary>Allocation-free path resolution; see <see cref="NodePath"/> for the syntax.</summary>
    public Node? GetNodeOrNull(NodePath path)
    {
        var text = path.Path.AsSpan();
        if (text.IsEmpty)
            return null;

        Node? current = this;
        if (text[0] == '/')
        {
            if (_tree is null)
                return null;
            text = text[1..];
            var slash = text.IndexOf('/');
            var first = slash < 0 ? text : text[..slash];
            if (!first.SequenceEqual(_tree.Root._name))
                return null;
            current = _tree.Root;
            text = slash < 0 ? [] : text[(slash + 1)..];
        }

        while (!text.IsEmpty && current is not null)
        {
            var slash = text.IndexOf('/');
            var segment = slash < 0 ? text : text[..slash];
            text = slash < 0 ? [] : text[(slash + 1)..];

            if (segment.IsEmpty || segment is ".")
                continue;
            if (segment is "..")
                current = current._parent;
            else if (segment[0] == '%')
                current = current.FindUniqueNode(segment[1..]);
            else
                current = current.FindChildByName(segment);
        }

        return current;
    }

    /// <summary>
    /// Finds the first descendant whose name matches <paramref name="pattern"/> (<c>*</c> and <c>?</c>
    /// wildcards), depth-first. With <paramref name="owned"/>, only nodes that share this node's owner (or
    /// are owned by it) match, as in Godot.
    /// </summary>
    public Node? FindChild(string pattern, bool recursive = true, bool owned = true)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (_children is null)
            return null;
        foreach (var child in _children)
        {
            if (MatchesOwnership(child, owned) && WildcardMatch(child._name, pattern))
                return child;
            if (recursive && child.FindChild(pattern, true, owned) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>All descendants matching <paramref name="pattern"/>, optionally only of type <typeparamref name="T"/>.</summary>
    public List<T> FindChildren<T>(string pattern = "*", bool recursive = true, bool owned = true) where T : Node
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var results = new List<T>();
        CollectChildren(pattern, recursive, owned, results);
        return results;
    }

    private void CollectChildren<T>(string pattern, bool recursive, bool owned, List<T> results) where T : Node
    {
        if (_children is null)
            return;
        foreach (var child in _children)
        {
            if (child is T typed && MatchesOwnership(child, owned) && WildcardMatch(child._name, pattern))
                results.Add(typed);
            if (recursive)
                child.CollectChildren(pattern, true, owned, results);
        }
    }

    private bool MatchesOwnership(Node child, bool owned) =>
        !owned || ReferenceEquals(child._owner, this) || (child._owner is not null && ReferenceEquals(child._owner, _owner));

    internal static bool WildcardMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        // Iterative glob with single-star backtracking.
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>Absolute path (<c>/root/Main/Player</c>); requires the node to be inside a tree.</summary>
    public NodePath GetPath()
    {
        if (_tree is null)
            throw new InvalidOperationException($"'{Name}' is not inside a tree.");
        return new NodePath(BuildPath(null));
    }

    /// <summary>Relative path from this node to <paramref name="node"/> (e.g. <c>../Sibling/Child</c>).</summary>
    public NodePath GetPathTo(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ReferenceEquals(node, this))
            return new NodePath(".");

        // Find the common ancestor.
        Node? common = null;
        for (var a = this; a is not null && common is null; a = a._parent)
            if (ReferenceEquals(a, node) || a.IsAncestorOf(node))
                common = a;
        if (common is null)
            throw new InvalidOperationException($"'{Name}' and '{node.Name}' are not in the same tree.");

        var sb = new System.Text.StringBuilder();
        for (var a = this; !ReferenceEquals(a, common); a = a._parent!)
        {
            if (sb.Length > 0)
                sb.Append('/');
            sb.Append("..");
        }

        if (!ReferenceEquals(node, common))
        {
            if (sb.Length > 0)
                sb.Append('/');
            sb.Append(node.BuildPath(common));
        }

        return new NodePath(sb.ToString());
    }

    /// <summary>Path from <paramref name="ancestor"/> (exclusive) to this node, or absolute when null.</summary>
    private string BuildPath(Node? ancestor)
    {
        var parts = new List<string>();
        for (var n = this; n is not null && !ReferenceEquals(n, ancestor); n = n._parent)
            parts.Add(n._name);
        parts.Reverse();
        var joined = string.Join('/', parts);
        return ancestor is null ? "/" + joined : joined;
    }

    private string GetPathOrName() => _tree is not null ? BuildPath(null) : _name;

    // ------------------------------------------------------------------------------------------------
    // Freeing
    // ------------------------------------------------------------------------------------------------

    /// <summary>True between <see cref="QueueFree"/> and the end-of-frame flush that frees the node.</summary>
    public bool IsQueuedForDeletion => _queuedForDeletion;

    /// <summary>True once the node has been freed; it must not be used afterwards.</summary>
    public bool IsFreed => _freed;

    /// <summary>Godot's <c>is_instance_valid</c>: not null and not freed.</summary>
    public static bool IsInstanceValid([NotNullWhen(true)] Node? node) => node is not null && !node._freed;

    /// <summary>
    /// Frees this node at the end of the current frame (after process, with the other deferred calls), so it
    /// is safe to call mid-traversal. Outside a tree the node is freed immediately.
    /// </summary>
    public void QueueFree()
    {
        if (_freed || _queuedForDeletion)
            return;
        if (_tree is null)
        {
            Free();
            return;
        }

        _queuedForDeletion = true;
        _tree.QueueDelete(this);
    }

    /// <summary>
    /// Frees the node now: removes it from its parent (exiting the tree), frees the children, disconnects
    /// its signal connections and releases its resources (<see cref="Dispose(bool)"/>). Prefer
    /// <see cref="QueueFree"/> while the tree is processing.
    /// </summary>
    public void Free()
    {
        if (_freed)
            return;
        if (_exiting)
        {
            // Freed from its own (or a descendant's) exit callback: finish leaving the tree first.
            QueueFree();
            return;
        }

        _parent?.RemoveChild(this, validateOwners: false);

        while (_children is { Count: > 0 })
            _children[^1].Free();

        DisconnectAllSignals();
        Owner = null;
        _freed = true;
        _queuedForDeletion = false;
        Dispose(true);
    }

    /// <summary>Same as <see cref="Free"/>, so nodes work with <c>using</c>.</summary>
    public void Dispose() => Free();

    /// <summary>Release resources here (GPU objects, native handles). Called once, from <see cref="Free"/>.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    private void ThrowIfFreed()
    {
        if (_freed)
            throw new ObjectDisposedException(GetType().Name, $"Node '{_name}' has been freed.");
    }

    // ------------------------------------------------------------------------------------------------
    // Lifecycle propagation
    // ------------------------------------------------------------------------------------------------

    /// <summary>Called when the node enters a tree, parents before children. Register with servers here.</summary>
    protected virtual void OnEnterTree()
    {
    }

    /// <summary>
    /// Called once, after the node and all its children have entered the tree (children are ready first).
    /// The place for <see cref="GetNode{T}"/> lookups.
    /// </summary>
    protected virtual void OnReady()
    {
    }

    /// <summary>Called when the node leaves the tree, children before parents. Unregister from servers here.</summary>
    protected virtual void OnExitTree()
    {
    }

    /// <summary>Makes <see cref="OnReady"/> run again the next time the node enters a tree.</summary>
    public void RequestReady() => _readyDone = false;

    internal void PropagateEnterTree(SceneTree tree, SceneViewport? viewport, int depth)
    {
        _tree = tree;
        _depth = depth;
        _viewport = this as SceneViewport ?? viewport;
        ResolveProcessMode();

        tree.OnNodeEntered(this);
        InvokeEnterTree();
        TreeEntered?.Invoke();
        _parent?.ChildEnteredTree?.Invoke(this);

        // Over a snapshot: callbacks may add children (they enter immediately and are skipped here) or remove
        // siblings (skipped once they are no longer ours).
        var count = _children?.Count ?? 0;
        if (count == 0)
            return;
        var snapshot = System.Buffers.ArrayPool<Node>.Shared.Rent(count);
        _children!.CopyTo(snapshot);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var child = snapshot[i];
                if (ReferenceEquals(child._parent, this) && child._tree is null && _tree is not null)
                    child.PropagateEnterTree(tree, _viewport, depth + 1);
            }
        }
        finally
        {
            Array.Clear(snapshot, 0, count);
            System.Buffers.ArrayPool<Node>.Shared.Return(snapshot);
        }
    }

    internal void PropagateReady()
    {
        var count = _children?.Count ?? 0;
        if (count > 0)
        {
            var snapshot = System.Buffers.ArrayPool<Node>.Shared.Rent(count);
            _children!.CopyTo(snapshot);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var child = snapshot[i];
                    if (ReferenceEquals(child._parent, this) && child._tree is not null)
                        child.PropagateReady();
                }
            }
            finally
            {
                Array.Clear(snapshot, 0, count);
                System.Buffers.ArrayPool<Node>.Shared.Return(snapshot);
            }
        }

        if (_readyDone || _tree is null || _freed)
            return;
        _readyDone = true;
        InvokeReady();
        Ready?.Invoke();
    }

    private void PropagateExitTree()
    {
        _exiting = true;
        for (var i = (_children?.Count ?? 0) - 1; i >= 0; i--)
        {
            if (_children is null || i >= _children.Count)
                continue;
            var child = _children[i];
            if (child._tree is not null)
                child.PropagateExitTree();
        }

        var tree = _tree!;
        InvokeExitTree();
        TreeExiting?.Invoke();
        _parent?.ChildExitingTree?.Invoke(this);
        tree.OnNodeExited(this);
        _tree = null;
        _viewport = null;
        _depth = -1;
        _exiting = false;
    }

    /// <summary>Takes a tree root out of its tree (roots have no parent to remove them).</summary>
    internal void ExitTreeAsRoot()
    {
        if (_tree is null)
            return;
        PropagateExitTree();
        PropagateAfterExitTree();
    }

    private void PropagateAfterExitTree()
    {
        TreeExited?.Invoke();
        for (var i = 0; _children is not null && i < _children.Count; i++)
            _children[i].PropagateAfterExitTree();
    }

    /// <summary>Clears owners that are no longer ancestors after a removal (Godot semantics).</summary>
    private void PropagateValidateOwner()
    {
        if (_owner is not null && !_owner.IsAncestorOf(this))
            Owner = null;
        for (var i = 0; _children is not null && i < _children.Count; i++)
            _children[i].PropagateValidateOwner();
    }

    public override string ToString() => $"{GetType().Name}:{_name}";
}
