namespace MainframeEngine;

/// <summary>
/// Gives its parent <see cref="CollisionObject2D"/> a shape (Godot's <c>CollisionShape2D</c>). Only direct children of
/// a collision object count; the node's transform (pixels, relative to the body, scale included) places the shape.
/// </summary>
[EditorIcon("shape", Family = EditorIconFamily.Physics)]
public class CollisionShape2D : Node2D
{
    private Shape2D? _shape;
    private bool _disabled;
    private bool _subscribed;
    private readonly Action _onShapeChanged;

    public CollisionShape2D()
    {
        _onShapeChanged = NotifyBody;
    }

    /// <summary>The shape resource (shareable). Null adds nothing to the body.</summary>
    [Export]
    public Shape2D? Shape
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
    public CollisionObject2D? Body => Parent as CollisionObject2D;

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

    private protected override void OnLocalTransformChanged() => NotifyBody();

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
