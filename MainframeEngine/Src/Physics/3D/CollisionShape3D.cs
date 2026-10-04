namespace MainframeEngine;

/// <summary>
/// Gives its parent <see cref="CollisionObject3D"/> a shape (Godot's <c>CollisionShape3D</c>). Only direct children
/// of a collision object count. The node's transform (relative to the body, scale included) places the shape;
/// several shape children make a compound body.
/// </summary>
public class CollisionShape3D : Node3D
{
    private Shape3D? _shape;
    private bool _disabled;
    private bool _subscribed;
    private readonly Action _onShapeChanged;

    public CollisionShape3D()
    {
        _onShapeChanged = OnShapeResourceChanged;
    }

    /// <summary>The shape resource (shareable). Null adds nothing to the body.</summary>
    [Export]
    public Shape3D? Shape
    {
        get => _shape;
        set
        {
            if (ReferenceEquals(_shape, value))
                return;
            Unsubscribe();
            _shape = value;
            if (IsInsideTree)
                Subscribe();
            NotifyBody();
        }
    }

    /// <summary>A disabled shape is removed from the body until enabled again.</summary>
    [Export]
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (_disabled == value)
                return;
            _disabled = value;
            NotifyBody();
        }
    }

    /// <summary>The collision object this shape belongs to (the parent), if any.</summary>
    public CollisionObject3D? Body => Parent as CollisionObject3D;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Subscribe();
        TrackLocalTransformChanges(true);
        NotifyBody();
    }

    protected override void OnExitTree()
    {
        TrackLocalTransformChanges(false);
        Unsubscribe();
        NotifyBody();
        base.OnExitTree();
    }

    // The shape moved relative to its body: the body rebuilds its shapes at the next step.
    private protected override void OnLocalTransformChanged() => NotifyBody();

    private void OnShapeResourceChanged() => NotifyBody();

    private void NotifyBody() => Body?.OnShapesChanged();

    private void Subscribe()
    {
        if (_subscribed || _shape is null)
            return;
        _shape.Changed += _onShapeChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _shape is null)
            return;
        _shape.Changed -= _onShapeChanged;
        _subscribed = false;
    }
}
