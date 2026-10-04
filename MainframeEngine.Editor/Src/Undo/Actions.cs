using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>Sets an exported property of a node or resource (inspector edits, gizmo drags).</summary>
public sealed class SetPropertyAction : IEditorAction
{
    public SetPropertyAction(object target, ExportPropertyInfo property, object? oldValue, object? newValue)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Property = property ?? throw new ArgumentNullException(nameof(property));
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>Records the change of <paramref name="property"/> on <paramref name="target"/> to <paramref name="newValue"/>.</summary>
    public static SetPropertyAction Create(object target, ExportPropertyInfo property, object? newValue)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new SetPropertyAction(target, property, property.GetValue(target), newValue);
    }

    public object Target { get; }
    public ExportPropertyInfo Property { get; }
    public object? OldValue { get; }
    public object? NewValue { get; private set; }

    public string Name => $"Set {Property.Name}";

    public void Do() => Property.SetValue(Target, NewValue);

    public void Undo() => Property.SetValue(Target, OldValue);

    public bool TryMerge(IEditorAction next)
    {
        if (next is not SetPropertyAction other || !ReferenceEquals(other.Target, Target) || !ReferenceEquals(other.Property, Property))
            return false;
        NewValue = other.NewValue;
        return true;
    }
}

/// <summary>Several actions as one history entry (delete a selection, a gizmo drag that moved and rotated).</summary>
public sealed class CompositeAction : IDiscardableAction
{
    private readonly IEditorAction[] _actions;

    public CompositeAction(string name, params IEditorAction[] actions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(actions);
        Name = name;
        _actions = actions;
    }

    public string Name { get; }

    public IReadOnlyList<IEditorAction> Actions => _actions;

    public void Do()
    {
        foreach (var action in _actions)
            action.Do();
    }

    public void Undo()
    {
        for (var i = _actions.Length - 1; i >= 0; i--)
            _actions[i].Undo();
    }

    public bool TryMerge(IEditorAction next)
    {
        // A drag commits the same set of properties each frame: merge element-wise.
        if (next is not CompositeAction other || other._actions.Length != _actions.Length)
            return false;
        for (var i = 0; i < _actions.Length; i++)
            if (_actions[i] is not SetPropertyAction a || other._actions[i] is not SetPropertyAction b ||
                !ReferenceEquals(a.Target, b.Target) || !ReferenceEquals(a.Property, b.Property))
                return false;
        for (var i = 0; i < _actions.Length; i++)
            _actions[i].TryMerge(other._actions[i]);
        return true;
    }

    public void Discard(bool applied)
    {
        foreach (var action in _actions)
            (action as IDiscardableAction)?.Discard(applied);
    }
}

/// <summary>The owners of a subtree, so a detach (which clears owners that stop being ancestors) can be undone exactly.</summary>
internal readonly struct OwnerSnapshot
{
    private readonly (Node Node, Node? Owner)[] _entries;

    private OwnerSnapshot((Node, Node?)[] entries) => _entries = entries;

    public static OwnerSnapshot Capture(Node root)
    {
        var entries = new List<(Node, Node?)>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            entries.Add((node, node.Owner));
            for (var i = node.ChildCount - 1; i >= 0; i--)
                stack.Push(node.GetChild(i));
        }

        return new OwnerSnapshot([.. entries]);
    }

    /// <summary>Restores owners top-down (an owner must be an ancestor; skipped when it no longer is).</summary>
    public void Restore()
    {
        if (_entries is null)
            return;
        foreach (var (node, owner) in _entries)
            if (owner is null || owner.IsAncestorOf(node))
                node.Owner = owner;
    }
}

/// <summary>The exported local transform of a 2D/3D node (position, rotation in degrees, scale), restored exactly.</summary>
internal readonly struct LocalTransformState
{
    private readonly Node? _node;
    private readonly Vector3 _position;
    private readonly Vector3 _rotationDegrees;
    private readonly Vector3 _scale;

    private LocalTransformState(Node node, Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        _node = node;
        _position = position;
        _rotationDegrees = rotationDegrees;
        _scale = scale;
    }

    public static LocalTransformState Capture(Node node) => node switch
    {
        Node3D n => new LocalTransformState(n, n.Position, n.RotationDegrees, n.Scale),
        Node2D n => new LocalTransformState(n, new Vector3(n.Position, 0), new Vector3(0, 0, n.RotationDegrees), new Vector3(n.Scale, 1)),
        _ => default,
    };

    public void Restore()
    {
        switch (_node)
        {
            case Node3D n:
                n.Position = _position;
                n.RotationDegrees = _rotationDegrees;
                n.Scale = _scale;
                break;
            case Node2D n:
                n.Position = new Vector2(_position.X, _position.Y);
                n.RotationDegrees = _rotationDegrees.Z;
                n.Scale = new Vector2(_scale.X, _scale.Y);
                break;
        }
    }
}

/// <summary>
/// Adds a node (new, duplicated or an instanced scene) under a parent. The node's subtree gets the scene root as owner
/// where it has none (nodes inside an instanced scene keep the instance root). Undo detaches it; the detached node is
/// freed when the undone action leaves the history.
/// </summary>
public sealed class AddNodeAction : IDiscardableAction
{
    private readonly Node _sceneRoot;
    private OwnerSnapshot _owners;
    private bool _ownersAssigned;

    public AddNodeAction(Node parent, Node node, Node sceneRoot, int index = -1, string? name = null, Node? after = null)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        _sceneRoot = sceneRoot ?? throw new ArgumentNullException(nameof(sceneRoot));
        Index = index;
        After = after;
        Name = name ?? $"Add {node.Name}";
    }

    /// <summary>
    /// When set, the node goes right after this sibling, resolved when the action runs (several duplicates of siblings
    /// in one entry each land next to their original whatever was inserted before them).
    /// </summary>
    public Node? After { get; }

    public Node Parent { get; }
    public Node Node { get; }
    public int Index { get; }
    public string Name { get; }

    public void Do()
    {
        Parent.AddChild(Node);
        var index = After is { } after && ReferenceEquals(after.Parent, Parent) ? after.GetIndex() + 1 : Index;
        if (index >= 0 && index < Parent.ChildCount)
            Parent.MoveChild(Node, index);
        if (!_ownersAssigned)
        {
            AssignOwners(Node, _sceneRoot);
            _ownersAssigned = true;
        }
        else
        {
            _owners.Restore();
        }
    }

    public void Undo()
    {
        _owners = OwnerSnapshot.Capture(Node);
        Parent.RemoveChild(Node);
    }

    public void Discard(bool applied)
    {
        if (!applied && !Node.IsFreed)
            Node.Free();
    }

    // New nodes belong to the edited scene; an instanced sub-scene's inner nodes stay owned by the instance root.
    internal static void AssignOwners(Node node, Node sceneRoot)
    {
        if (ReferenceEquals(node, sceneRoot))
            return;
        if (node.Owner is null || !node.Owner.IsAncestorOf(node))
            node.Owner = sceneRoot;
        foreach (var child in node.Children)
            AssignOwners(child, sceneRoot);
    }
}

/// <summary>Detaches a node (and its subtree) from the scene; undo re-attaches it at the same index with its owners and connections.</summary>
public sealed class RemoveNodeAction : IDiscardableAction
{
    private Node? _parent;
    private int _index;
    private OwnerSnapshot _owners;

    public RemoveNodeAction(Node node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        if (node.Parent is null)
            throw new ArgumentException($"'{node.Name}' has no parent; the scene root cannot be removed.", nameof(node));
    }

    public Node Node { get; }
    public string Name => $"Delete {Node.Name}";

    public void Do()
    {
        _parent = Node.Parent ?? throw new InvalidOperationException($"'{Node.Name}' is not attached.");
        _index = Node.GetIndex();
        _owners = OwnerSnapshot.Capture(Node);
        _parent.RemoveChild(Node);
    }

    public void Undo()
    {
        var parent = _parent ?? throw new InvalidOperationException("Undo before Do.");
        parent.AddChild(Node);
        if (_index < parent.ChildCount)
            parent.MoveChild(Node, _index);
        _owners.Restore();
    }

    public void Discard(bool applied)
    {
        if (applied && !Node.IsFreed)
            Node.Free();
    }
}

/// <summary>Moves a node under another parent, optionally keeping its global transform; undo restores parent, index and local transform exactly.</summary>
public sealed class ReparentAction : IEditorAction
{
    private Node? _oldParent;
    private int _oldIndex;
    private LocalTransformState _oldTransform;
    private OwnerSnapshot _owners;

    public ReparentAction(Node node, Node newParent, int newIndex = -1, bool keepGlobalTransform = true)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        NewParent = newParent ?? throw new ArgumentNullException(nameof(newParent));
        if (ReferenceEquals(node, newParent) || node.IsAncestorOf(newParent))
            throw new ArgumentException($"'{newParent.Name}' is inside '{node.Name}'.", nameof(newParent));
        NewIndex = newIndex;
        KeepGlobalTransform = keepGlobalTransform;
    }

    public Node Node { get; }
    public Node NewParent { get; }
    public int NewIndex { get; }
    public bool KeepGlobalTransform { get; }
    public string Name => $"Reparent {Node.Name}";

    public void Do()
    {
        _oldParent = Node.Parent ?? throw new InvalidOperationException($"'{Node.Name}' is not attached.");
        _oldIndex = Node.GetIndex();
        _oldTransform = LocalTransformState.Capture(Node);
        _owners = OwnerSnapshot.Capture(Node);
        if (!ReferenceEquals(_oldParent, NewParent))
            Node.Reparent(NewParent, KeepGlobalTransform);
        var index = NewIndex;
        // Moving down within the same parent: the node's own slot disappears first.
        if (index >= 0 && ReferenceEquals(_oldParent, NewParent) && index > _oldIndex)
            index--;
        if (index >= 0 && index < NewParent.ChildCount)
            NewParent.MoveChild(Node, index);
        _owners.Restore();
    }

    public void Undo()
    {
        var oldParent = _oldParent ?? throw new InvalidOperationException("Undo before Do.");
        if (!ReferenceEquals(Node.Parent, oldParent))
            Node.Reparent(oldParent, keepGlobalTransform: false);
        if (_oldIndex < oldParent.ChildCount)
            oldParent.MoveChild(Node, _oldIndex);
        _oldTransform.Restore();
        _owners.Restore();
    }
}

/// <summary>Renames a node (the name is made unique among its siblings, as always).</summary>
public sealed class RenameAction : IEditorAction
{
    private string _oldName = "";

    public RenameAction(Node node, string newName)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        RequestedName = newName;
    }

    public Node Node { get; }
    public string RequestedName { get; }
    public string Name => $"Rename {_oldName} to {Node.Name}";

    public void Do()
    {
        _oldName = Node.Name;
        Node.Name = RequestedName;
    }

    public void Undo() => Node.Name = _oldName;
}

/// <summary>Moves a node among its siblings.</summary>
public sealed class MoveInTreeAction : IEditorAction
{
    private int _oldIndex;

    public MoveInTreeAction(Node node, int newIndex)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        if (node.Parent is null)
            throw new ArgumentException("The node has no parent.", nameof(node));
        NewIndex = newIndex;
    }

    public Node Node { get; }
    public int NewIndex { get; }
    public string Name => $"Move {Node.Name}";

    public void Do()
    {
        _oldIndex = Node.GetIndex();
        Node.Parent!.MoveChild(Node, Math.Clamp(NewIndex, 0, Node.Parent.ChildCount - 1));
    }

    public void Undo() => Node.Parent!.MoveChild(Node, _oldIndex);
}
