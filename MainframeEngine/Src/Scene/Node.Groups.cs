namespace MainframeEngine;

public partial class Node
{
    private List<GroupMembership>? _groups;
    private Dictionary<string, Node>? _uniqueNodes;
    private bool _uniqueNameInOwner;

    /// <summary>A group the node belongs to; persistent groups are saved with the scene.</summary>
    public readonly record struct GroupMembership(string Name, bool Persistent);

    /// <summary>
    /// Adds the node to <paramref name="group"/>. Persistent groups are written to scene files; others are
    /// runtime-only. Groups are tracked by the tree while the node is inside it
    /// (<see cref="SceneTree.GetNodesInGroup"/>, <see cref="SceneTree.CallGroup"/>).
    /// </summary>
    public void AddToGroup(string group, bool persistent = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        _groups ??= [];
        for (var i = 0; i < _groups.Count; i++)
        {
            if (!string.Equals(_groups[i].Name, group, StringComparison.Ordinal))
                continue;
            if (persistent && !_groups[i].Persistent)
                _groups[i] = new GroupMembership(group, true);
            return;
        }

        _groups.Add(new GroupMembership(group, persistent));
        _tree?.AddToGroup(group, this);
    }

    public void RemoveFromGroup(string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        if (_groups is null)
            return;
        for (var i = 0; i < _groups.Count; i++)
        {
            if (!string.Equals(_groups[i].Name, group, StringComparison.Ordinal))
                continue;
            _groups.RemoveAt(i);
            _tree?.RemoveFromGroup(group, this);
            return;
        }
    }

    public bool IsInGroup(string group)
    {
        if (_groups is null)
            return false;
        foreach (var g in _groups)
            if (string.Equals(g.Name, group, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The node's groups (runtime and persistent).</summary>
    public IReadOnlyList<GroupMembership> Groups => (IReadOnlyList<GroupMembership>?)_groups ?? [];

    // ------------------------------------------------------------------------------------------------
    // Scene-unique names (%Name)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the node reachable as <c>%Name</c> from any node sharing its <see cref="Owner"/> (Godot's
    /// "Access as Unique Name"). Names must be unique per owner.
    /// </summary>
    [Export]
    public bool UniqueNameInOwner
    {
        get => _uniqueNameInOwner;
        set
        {
            if (_uniqueNameInOwner == value)
                return;
            if (_owner is not null && _uniqueNameInOwner)
                _owner.UnregisterUniqueName(this);
            _uniqueNameInOwner = value;
            if (_owner is not null && _uniqueNameInOwner)
                _owner.RegisterUniqueName(this);
        }
    }

    private void RegisterUniqueName(Node node)
    {
        EnsureUniqueNameFree(node.Name, node);
        _uniqueNodes ??= new Dictionary<string, Node>(StringComparer.Ordinal);
        _uniqueNodes[node.Name] = node;
    }

    private void EnsureUniqueNameFree(string name, Node node)
    {
        if (_uniqueNodes is not null && _uniqueNodes.TryGetValue(name, out var existing) && !ReferenceEquals(existing, node))
            throw new InvalidOperationException(
                $"Unique name '%{name}' is already used by another node owned by '{Name}'.");
    }

    private void UnregisterUniqueName(Node node)
    {
        if (_uniqueNodes is not null && _uniqueNodes.TryGetValue(node.Name, out var existing) && ReferenceEquals(existing, node))
            _uniqueNodes.Remove(node.Name);
    }

    /// <summary>Resolves <c>%name</c>: among nodes owned by this node first, then among this node's owner's.</summary>
    private Node? FindUniqueNode(ReadOnlySpan<char> name)
    {
        if (_uniqueNodes is not null
            && _uniqueNodes.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var own))
            return own;
        if (_owner?._uniqueNodes is { } ownerNodes
            && ownerNodes.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var shared))
            return shared;
        return null;
    }
}
