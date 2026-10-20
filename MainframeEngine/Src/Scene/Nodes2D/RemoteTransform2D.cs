using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Pushes its own transform to another <see cref="Node2D"/> whenever it changes (Godot's <c>RemoteTransform2D</c>, ADR
/// 0202): a child of a <see cref="RigidBody2D"/> pointed at a <see cref="Bone2D"/> makes the bone follow the body. The push
/// happens in the frame's transform notifications, after the physics poses are written and before the canvas draws, so
/// a skinned <see cref="Polygon2D"/> shows the bodies where they are this frame.
/// </summary>
[EditorIcon("arrows-move", Family = EditorIconFamily.Space2D)]
public class RemoteTransform2D : Node2D
{
    /// <summary>The node that follows this one (relative to this node).</summary>
    [Export(NodeType = typeof(Node2D))]
    public NodePath RemotePath
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Push();
        }
    }

    /// <summary>Copy the global transform (Godot's default); otherwise the local transform.</summary>
    [Export]
    public bool UseGlobalCoordinates
    {
        get;
        set
        {
            field = value;
            Push();
        }
    } = true;

    [Export]
    public bool UpdatePosition
    {
        get;
        set
        {
            field = value;
            Push();
        }
    } = true;

    [Export]
    public bool UpdateRotation
    {
        get;
        set
        {
            field = value;
            Push();
        }
    } = true;

    [Export]
    public bool UpdateScale
    {
        get;
        set
        {
            field = value;
            Push();
        }
    } = true;

    /// <summary>Pushes the transform now (Godot's <c>force_update_cache</c> followed by an update).</summary>
    public void ForceUpdate() => Push();

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        SetNotifyTransform(true); // queues a first push for the end of this frame
    }

    protected override void OnExitTree()
    {
        SetNotifyTransform(false);
        base.OnExitTree();
    }

    protected override void OnTransformChanged() => Push();

    private void Push()
    {
        if (!IsInsideTree || RemotePath.IsEmpty || GetNodeOrNull(RemotePath) is not Node2D target || ReferenceEquals(target, this))
            return;
        var source = UseGlobalCoordinates ? GlobalTransform : Transform;
        Transform2D result;
        if (UpdatePosition && UpdateRotation && UpdateScale)
        {
            result = source;
        }
        else
        {
            var current = UseGlobalCoordinates ? target.GlobalTransform : target.Transform;
            result = Transform2D.FromTrs(
                UpdatePosition ? source.Origin : current.Origin,
                UpdateRotation ? source.Rotation : current.Rotation,
                UpdateScale ? source.Scale : current.Scale);
        }

        if (UseGlobalCoordinates)
            target.GlobalTransform = result;
        else
            target.Transform = result;
    }
}
