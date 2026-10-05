using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A node with a 2D transform (Godot's <c>Node2D</c>): position, rotation (radians), scale and a cached
/// global transform relative to the nearest <see cref="Node2D"/> parent. Same dirty-flag and notification
/// rules as <see cref="Node3D"/>.
/// </summary>
[EditorIcon("axis-y", Family = EditorIconFamily.Space2D)]
public class Node2D : CanvasItem, ITransformNotifiable
{
    private Vector2 _position;
    private float _rotation;
    private Vector2 _scale = Vector2.One;
    private Transform2D _local = Transform2D.Identity;
    private Transform2D _global = Transform2D.Identity;
    private bool _localDirty = true;
    private bool _globalDirty = true;
    private bool _notifyTransform;
    private bool _notificationQueued;
    private Track _track; // engine-internal change hooks (physics)

    [Flags]
    private enum Track : byte
    {
        None = 0,
        Global = 1,
        Local = 2,
    }

    [Export]
    public Vector2 Position
    {
        get => _position;
        set
        {
            _position = value;
            MarkLocalDirty();
        }
    }

    /// <summary>Rotation in radians (clockwise on screen: 2D is Y-down, as in Godot).</summary>
    public float Rotation
    {
        get => _rotation;
        set
        {
            _rotation = value;
            MarkLocalDirty();
        }
    }

    /// <summary>Rotation in degrees (the serialized form).</summary>
    [Export]
    public float RotationDegrees
    {
        get => float.RadiansToDegrees(_rotation);
        set => Rotation = float.DegreesToRadians(value);
    }

    [Export]
    public Vector2 Scale
    {
        get => _scale;
        set
        {
            _scale = value;
            MarkLocalDirty();
        }
    }

    public Transform2D Transform
    {
        get
        {
            UpdateLocal();
            return _local;
        }
        set
        {
            _position = value.Origin;
            _rotation = value.Rotation;
            _scale = value.Scale;
            MarkLocalDirty();
        }
    }

    public Transform2D GlobalTransform
    {
        get
        {
            UpdateGlobal();
            return _global;
        }
        set
        {
            var parent = ParentNode2D;
            Transform = parent is null ? value : parent.GlobalTransform.AffineInverse() * value;
        }
    }

    public Vector2 GlobalPosition
    {
        get => GlobalTransform.Origin;
        set
        {
            var parent = ParentNode2D;
            Position = parent is null ? value : parent.GlobalTransform.AffineInverse().TransformPoint(value);
        }
    }

    public float GlobalRotation => GlobalTransform.Rotation;

    /// <summary>The parent the transform is relative to: the parent <see cref="Node2D"/>, or null for roots and top-level nodes.</summary>
    public Node2D? ParentNode2D => TopLevel ? null : Parent as Node2D;

    public override Transform2D GetTransform() => Transform;

    public override Transform2D GetGlobalTransform() => GlobalTransform;

    // As in Godot, toggling top level does not move the node: its local transform is reinterpreted (as global or back).
    private protected override void OnTopLevelChanged()
    {
        _globalDirty = false;
        InvalidateGlobal();
    }

    /// <summary>Requests <see cref="OnTransformChanged"/> after process when the global transform changed.</summary>
    public void SetNotifyTransform(bool enable)
    {
        _notifyTransform = enable;
        if (enable)
            QueueTransformNotification();
    }

    protected virtual void OnTransformChanged()
    {
    }

    /// <summary>Enables <see cref="OnGlobalTransformInvalidated"/> (see <see cref="Node3D"/>'s counterpart).</summary>
    private protected void TrackGlobalTransformChanges(bool enable) => _track = enable ? _track | Track.Global : _track & ~Track.Global;

    /// <summary>The global transform was just invalidated; record it only (runs during dirty propagation).</summary>
    private protected virtual void OnGlobalTransformInvalidated()
    {
    }

    /// <summary>Enables <see cref="OnLocalTransformChanged"/>: called whenever a local transform property is set.</summary>
    private protected void TrackLocalTransformChanges(bool enable) => _track = enable ? _track | Track.Local : _track & ~Track.Local;

    /// <summary>A local transform property was set; record it only.</summary>
    private protected virtual void OnLocalTransformChanged()
    {
    }

    private void MarkLocalDirty()
    {
        _localDirty = true;
        if ((_track & Track.Local) != 0)
            OnLocalTransformChanged();
        InvalidateGlobal();
    }

    private void InvalidateGlobal()
    {
        if (_globalDirty)
            return;
        _globalDirty = true;
        if (_notifyTransform)
            QueueTransformNotification();
        if ((_track & Track.Global) != 0)
            OnGlobalTransformInvalidated();
        var children = ChildList;
        if (children is null)
            return;
        for (var i = 0; i < children.Count; i++)
            if (children[i] is Node2D child)
                child.InvalidateGlobal();
    }

    private void UpdateLocal()
    {
        if (!_localDirty)
            return;
        _local = Transform2D.FromTrs(_position, _rotation, _scale);
        _localDirty = false;
    }

    private void UpdateGlobal()
    {
        if (!_globalDirty)
            return;
        UpdateLocal();
        var parent = ParentNode2D;
        _global = parent is null ? _local : parent.GlobalTransform * _local;
        _globalDirty = false;
    }

    private void QueueTransformNotification()
    {
        if (_notificationQueued || Tree is null)
            return;
        _notificationQueued = true;
        Tree.QueueTransformNotification(this);
    }

    void ITransformNotifiable.OnEnteredTree()
    {
        _notificationQueued = false;
        if (_notifyTransform)
            QueueTransformNotification();
    }

    void ITransformNotifiable.FlushTransformNotification()
    {
        if (!_notificationQueued)
            return;
        _notificationQueued = false;
        if (Tree is null)
            return;
        UpdateGlobal();
        OnTransformChanged();
    }

    private protected override void OnParentChanged()
    {
        base.OnParentChanged();
        _globalDirty = false;
        InvalidateGlobal();
    }

    private protected override object? CaptureGlobalTransform() => GlobalTransform;

    private protected override void RestoreGlobalTransform(object state) => GlobalTransform = (Transform2D)state;
}
