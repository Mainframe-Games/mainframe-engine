using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A node with a 3D transform (Godot's <c>Node3D</c>): local position, rotation (a quaternion; Euler degrees as
/// a convenience) and scale, and a cached global transform relative to the nearest <see cref="Node3D"/> parent.
/// </summary>
/// <remarks>
/// <para>
/// Setting any transform property marks the node's local transform dirty and its <see cref="Node3D"/>
/// subtree's global transforms dirty (stopping at subtrees that are already dirty). Global transforms are
/// recomputed lazily on read. A <see cref="Node3D"/> under a plain <see cref="Node"/> starts a new transform
/// root, as in Godot.
/// </para>
/// <para>
/// Nodes that need to push their transform to a server (lights, cameras, physics bodies) call
/// <see cref="SetNotifyTransform"/>; the tree then calls <see cref="OnTransformChanged"/> once per frame
/// after process when their global transform changed.
/// </para>
/// </remarks>
[EditorIcon("axis-x", Family = EditorIconFamily.Space3D)]
public class Node3D : Node, ITransformNotifiable
{
    [Flags]
    private enum Dirty : byte
    {
        None = 0,
        Local = 1,
        Global = 2,
        Euler = 4,

        // Not dirtiness: the engine-internal change hooks, kept in the same byte so Node3D stays as small and the
        // propagation loop reads one field.
        TrackGlobal = 8,
        TrackLocal = 16,
    }

    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _rotationDegrees;
    private Vector3 _scale = Vector3.One;
    private Transform3D _local = Transform3D.Identity;
    private Transform3D _global = Transform3D.Identity;
    private Matrix4x4 _globalMatrix = Matrix4x4.Identity;
    private Dirty _dirty = Dirty.Local | Dirty.Global;
    private bool _visible = true;
    private bool _notifyTransform;
    private bool _notificationQueued;

    /// <summary>Position relative to the parent.</summary>
    [Export]
    public Vector3 Position
    {
        get => _position;
        set
        {
            _position = value;
            MarkLocalDirty();
        }
    }

    /// <summary>Rotation relative to the parent (stored normalized).</summary>
    public Quaternion Rotation
    {
        get => _rotation;
        set
        {
            _rotation = value == default ? Quaternion.Identity : Quaternion.Normalize(value);
            _dirty |= Dirty.Euler;
            MarkLocalDirty();
        }
    }

    /// <summary>
    /// Rotation as Euler degrees, applied about X, then Y, then Z (<see cref="EulerAngles"/>). The value set is
    /// returned unchanged until <see cref="Rotation"/> is set directly, so incrementing it is stable.
    /// </summary>
    [Export]
    public Vector3 RotationDegrees
    {
        get
        {
            if ((_dirty & Dirty.Euler) != 0)
            {
                _rotationDegrees = EulerAngles.FromQuaternion(_rotation);
                _dirty &= ~Dirty.Euler;
            }

            return _rotationDegrees;
        }
        set
        {
            _rotationDegrees = value;
            _rotation = EulerAngles.ToQuaternion(value);
            _dirty &= ~Dirty.Euler;
            MarkLocalDirty();
        }
    }

    /// <summary>Scale along the local axes.</summary>
    [Export]
    public Vector3 Scale
    {
        get => _scale;
        set
        {
            _scale = value;
            MarkLocalDirty();
        }
    }

    /// <summary>Hidden nodes (and their 3D descendants) are not drawn.</summary>
    [Export]
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;
            _visible = value;
            OnVisibilityChanged();
            NotifyVisibilityInTreeChanged();
        }
    }

    /// <summary>Local transform (relative to the parent). Setting it decomposes into position/rotation/scale.</summary>
    public Transform3D Transform
    {
        get
        {
            UpdateLocal();
            return _local;
        }
        set
        {
            value.Decompose(out var t, out var r, out var s);
            _position = t;
            _rotation = r;
            _scale = s;
            _dirty |= Dirty.Euler;
            MarkLocalDirty();
        }
    }

    /// <summary>World transform: <c>parent.GlobalTransform × Transform</c>, cached until something above changes.</summary>
    public Transform3D GlobalTransform
    {
        get
        {
            UpdateGlobal();
            return _global;
        }
        set
        {
            var parent = ParentNode3D;
            Transform = parent is null ? value : parent.GlobalTransform.AffineInverse() * value;
        }
    }

    /// <summary>World position.</summary>
    public Vector3 GlobalPosition
    {
        get
        {
            UpdateGlobal();
            return _global.Origin;
        }
        set
        {
            var parent = ParentNode3D;
            Position = parent is null ? value : parent.GlobalTransform.AffineInverse().TransformPoint(value);
        }
    }

    /// <summary>World rotation.</summary>
    public Quaternion GlobalRotation => GlobalTransform.Basis.GetRotation();

    /// <summary>World-space forward (<c>-Z</c>), normalized.</summary>
    public Vector3 GlobalForward => Vector3.Normalize(-GlobalTransform.Basis.Z);

    /// <summary>World-space up (<c>+Y</c>), normalized.</summary>
    public Vector3 GlobalUp => Vector3.Normalize(GlobalTransform.Basis.Y);

    /// <summary>The world (model) matrix in <c>System.Numerics</c> row-vector form, cached with the global transform.</summary>
    public Matrix4x4 ModelMatrix
    {
        get
        {
            UpdateGlobal();
            return _globalMatrix;
        }
    }

    /// <summary>The parent if it is a <see cref="Node3D"/> (transforms only chain through 3D parents).</summary>
    public Node3D? ParentNode3D => Parent as Node3D;

    /// <summary>True if this node and every 3D ancestor up to the transform root are visible.</summary>
    public bool IsVisibleInTree()
    {
        for (var n = this; n is not null; n = n.ParentNode3D)
            if (!n._visible)
                return false;
        return true;
    }

    /// <summary>
    /// Rotates the node so its <c>-Z</c> axis points at <paramref name="target"/> (world space). Position and
    /// scale are unchanged.
    /// </summary>
    public void LookAt(Vector3 target, Vector3? up = null)
    {
        var globalRotation = Transform3D.BasisLookingAlong(target - GlobalPosition, up ?? Vector3.UnitY).GetRotation();
        // global = local then parent  =>  local = global then inverse(parent)
        Rotation = ParentNode3D is { } parent
            ? Quaternion.Concatenate(globalRotation, Quaternion.Inverse(parent.GlobalRotation))
            : globalRotation;
    }

    /// <summary>Moves to <paramref name="position"/> (world) and looks at <paramref name="target"/>.</summary>
    public void LookAtFromPosition(Vector3 position, Vector3 target, Vector3? up = null)
    {
        GlobalPosition = position;
        LookAt(target, up);
    }

    /// <summary>Moves the node by <paramref name="offset"/> in its local (rotated) frame.</summary>
    public void TranslateObjectLocal(Vector3 offset) => Position += Vector3.Transform(offset, _rotation);

    /// <summary>Rotates the node about a local axis by <paramref name="radians"/>.</summary>
    public void RotateObjectLocal(Vector3 axis, float radians) =>
        Rotation = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians), _rotation);

    /// <summary>
    /// Requests <see cref="OnTransformChanged"/> after process on frames where the global transform changed
    /// (and once when entering the tree).
    /// </summary>
    public void SetNotifyTransform(bool enable)
    {
        _notifyTransform = enable;
        if (enable)
            QueueTransformNotification();
    }

    public bool IsTransformNotificationEnabled => _notifyTransform;

    /// <summary>The global transform changed since the last notification; see <see cref="SetNotifyTransform"/>.</summary>
    protected virtual void OnTransformChanged()
    {
    }

    /// <summary>Called when <see cref="Visible"/> changes on this node.</summary>
    protected virtual void OnVisibilityChanged()
    {
    }

    /// <summary>
    /// Called on this node and every 3D descendant when <see cref="Visible"/> changes on this node or an ancestor:
    /// <see cref="IsVisibleInTree"/> may have changed (lights use it to join or leave their world's lights).
    /// </summary>
    private protected virtual void OnVisibilityInTreeChanged()
    {
    }

    private void NotifyVisibilityInTreeChanged()
    {
        OnVisibilityInTreeChanged();
        foreach (var child in Children)
            if (child is Node3D child3D)
                child3D.NotifyVisibilityInTreeChanged();
    }

    /// <summary>
    /// Enables <see cref="OnGlobalTransformInvalidated"/>: called synchronously whenever this node's global
    /// transform becomes dirty (its own or an ancestor's transform was set, or it was reparented). Physics bodies
    /// use it to push moved nodes to their server without a per-step scan.
    /// </summary>
    private protected void TrackGlobalTransformChanges(bool enable) =>
        _dirty = enable ? _dirty | Dirty.TrackGlobal : _dirty & ~Dirty.TrackGlobal;

    /// <summary>Enables <see cref="OnLocalTransformChanged"/>: called whenever a local transform property is set.</summary>
    private protected void TrackLocalTransformChanges(bool enable) =>
        _dirty = enable ? _dirty | Dirty.TrackLocal : _dirty & ~Dirty.TrackLocal;

    /// <summary>A local transform property was set (see <see cref="TrackLocalTransformChanges"/>); record it only.</summary>
    private protected virtual void OnLocalTransformChanged()
    {
    }

    /// <summary>
    /// The global transform was just invalidated (see <see cref="TrackGlobalTransformChanges"/>). Runs during dirty
    /// propagation: implementations must only record the fact, not read transforms.
    /// </summary>
    private protected virtual void OnGlobalTransformInvalidated()
    {
    }

    private void MarkLocalDirty()
    {
        _dirty |= Dirty.Local;
        if ((_dirty & Dirty.TrackLocal) != 0)
            OnLocalTransformChanged();
        InvalidateGlobal();
    }

    private void InvalidateGlobal()
    {
        // A dirty node's subtree is already dirty (a node is only cleaned after its ancestors), and any
        // notifying node in it was queued when it became dirty.
        if ((_dirty & Dirty.Global) != 0)
            return;
        _dirty |= Dirty.Global;
        if (_notifyTransform)
            QueueTransformNotification();
        if ((_dirty & Dirty.TrackGlobal) != 0)
            OnGlobalTransformInvalidated();

        var children = ChildList;
        if (children is null)
            return;
        for (var i = 0; i < children.Count; i++)
            if (children[i] is Node3D child)
                child.InvalidateGlobal();
    }

    private void UpdateLocal()
    {
        if ((_dirty & Dirty.Local) == 0)
            return;
        _local = Transform3D.FromTrs(_position, _rotation, _scale);
        _dirty &= ~Dirty.Local;
    }

    private void UpdateGlobal()
    {
        if ((_dirty & Dirty.Global) == 0)
            return;
        UpdateLocal();
        var parent = ParentNode3D;
        _global = parent is null ? _local : parent.GlobalTransform * _local;
        _globalMatrix = _global.ToMatrix4x4();
        _dirty &= ~Dirty.Global;
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
        // Force: the parent chain changed even if this node was already dirty.
        _dirty &= ~Dirty.Global;
        InvalidateGlobal();
    }

    private protected override object? CaptureGlobalTransform() => GlobalTransform;

    private protected override void RestoreGlobalTransform(object state) => GlobalTransform = (Transform3D)state;
}
