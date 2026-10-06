using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of 3D physics nodes (Godot's <c>CollisionObject3D</c>): a body or area in its viewport's physics space
/// (<see cref="PhysicsServer3D"/>, one Jitter2 world per <see cref="World3D"/>). Shapes come from direct
/// <see cref="CollisionShape3D"/> children. The body is created when the node enters the tree and removed when it
/// leaves, or while its resolved <see cref="Node.ProcessMode"/> is <see cref="ProcessMode.Disabled"/> (Godot's
/// default <c>DisableMode.Remove</c>).
/// </summary>
/// <remarks>
/// <para>
/// Moving the node (or an ancestor) is pushed to the physics server before the next step: static bodies and areas
/// move there, dynamic bodies are teleported, kinematic and character bodies are moved with the velocity that reaches
/// the new pose in one step (so they push what they touch).
/// </para>
/// <para>
/// Bodies the simulation moves (dynamic, kinematic, character) render interpolated between the last two physics
/// steps when <see cref="PhysicsInterpolation"/> is on: outside the fixed steps their transform is the interpolated
/// pose; inside <see cref="Node.OnPhysicsProcess"/> it is the physics pose.
/// </para>
/// </remarks>
[EditorIcon("atom-2", Family = EditorIconFamily.Physics)]
public abstract class CollisionObject3D : Node3D
{
    private bool _applyingPose;

    private uint _layer = CollisionLayers.Default;
    private uint _mask = CollisionLayers.Default;
    private bool _interpolate = true;

    internal BodyRecord3D? Record { get; set; }

    /// <summary>Layers this object is in (bit i = layer i + 1).</summary>
    [Export(Flags = true)]
    public uint CollisionLayer
    {
        get => _layer;
        set
        {
            if (_layer == value)
                return;
            _layer = value;
            Record?.OnFilterChanged();
        }
    }

    /// <summary>Layers this object scans: it collides with (or, for areas, detects) objects in these layers.</summary>
    [Export(Flags = true)]
    public uint CollisionMask
    {
        get => _mask;
        set
        {
            if (_mask == value)
                return;
            _mask = value;
            Record?.OnFilterChanged();
        }
    }

    /// <summary>Render between the last two physics steps (dynamic, kinematic and character bodies).</summary>
    [Export]
    public bool PhysicsInterpolation
    {
        get => _interpolate;
        set
        {
            if (_interpolate == value)
                return;
            _interpolate = value;
            Record?.Space.OnInterpolationChanged(Record);
        }
    }

    /// <summary>The physics space this object is in (null outside a tree).</summary>
    public PhysicsSpace3D? Space => Record?.Space;

    internal abstract PhysicsBodyKind Kind { get; }

    public bool GetCollisionLayerValue(int layerNumber) => (_layer & CollisionLayers.Layer(layerNumber)) != 0;

    public void SetCollisionLayerValue(int layerNumber, bool value) =>
        CollisionLayer = value ? _layer | CollisionLayers.Layer(layerNumber) : _layer & ~CollisionLayers.Layer(layerNumber);

    public bool GetCollisionMaskValue(int layerNumber) => (_mask & CollisionLayers.Layer(layerNumber)) != 0;

    public void SetCollisionMaskValue(int layerNumber, bool value) =>
        CollisionMask = value ? _mask | CollisionLayers.Layer(layerNumber) : _mask & ~CollisionLayers.Layer(layerNumber);

    /// <summary>The <see cref="CollisionShape3D"/> children (enabled, with a shape) that make up the body.</summary>
    public int GetShapes(List<CollisionShape3D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var count = 0;
        for (var i = 0; i < ChildCount; i++)
        {
            if (GetChild(i) is CollisionShape3D { Disabled: false, Shape: not null } shape)
            {
                results.Add(shape);
                count++;
            }
        }

        return count;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        TrackGlobalTransformChanges(true);
        if (ResolvedProcessMode != ProcessMode.Disabled)
            PhysicsServer3D.For(Tree!).AddObject(this);
    }

    protected override void OnExitTree()
    {
        LeaveSpace();
        TrackGlobalTransformChanges(false);
        base.OnExitTree();
    }

    // Godot's default DisableMode.Remove: a disabled object leaves the physics space (it neither moves nor collides
    // nor shows up in queries) and joins it again, at its current transform, when enabled.
    private protected override void OnDisabledChanged(bool disabled)
    {
        if (disabled)
            LeaveSpace();
        else if (Tree is { } tree && Record is null)
            PhysicsServer3D.For(tree).AddObject(this);
    }

    /// <summary>Removes the object from its space (leaving the tree or disabled). Subclasses keep state they need first.</summary>
    private protected virtual void LeaveSpace()
    {
        if (Record is { } record)
            record.Space.RemoveObject(record);
    }

    private protected override void OnGlobalTransformInvalidated()
    {
        // Our own pose writes are not user moves; a parent's (even a physics write) is.
        if (!_applyingPose && Record is { } record)
            record.Space.MarkTransformDirty(record);
    }

    /// <summary>A shape child was added, removed, enabled/disabled, moved or its resource changed.</summary>
    internal void OnShapesChanged()
    {
        if (Record is { } record)
            record.Space.MarkShapesDirty(record);
    }

    /// <summary>Writes a physics pose (world position and rotation; the node keeps its global scale) without it counting as a user move.</summary>
    internal void ApplyPhysicsPose(Vector3 position, Quaternion rotation, Vector3 globalScale)
    {
        _applyingPose = true;
        try
        {
            if (ParentNode3D is { } parent)
                Transform = parent.GlobalTransform.AffineInverse() * Transform3D.FromTrs(position, rotation, globalScale);
            else
            {
                Position = position;
                Rotation = rotation;
            }

            // Recompute now so the global is clean: the next user change must invalidate it (and notify us) again.
            _ = GlobalTransform;
        }
        finally
        {
            _applyingPose = false;
        }
    }

    // Signal plumbing, overridden by the node types that have the signals.
    internal virtual void EmitBodyEntered(Node3D body)
    {
    }

    internal virtual void EmitBodyExited(Node3D body)
    {
    }

    internal virtual void EmitSleepingStateChanged()
    {
    }
}

/// <summary>Base of the 3D bodies that collide (Godot's <c>PhysicsBody3D</c>): static, rigid and character bodies.</summary>
[EditorIcon("atom")]
public abstract class PhysicsBody3D : CollisionObject3D
{
}

/// <summary>An immovable body (Godot's <c>StaticBody3D</c>): floors, walls, level geometry. Moving it by code teleports it.</summary>
[EditorIcon("box")]
public class StaticBody3D : PhysicsBody3D
{
    private readonly ResourceSubscription<PhysicsMaterial> _material;

    public StaticBody3D()
    {
        _material = new ResourceSubscription<PhysicsMaterial>(OnMaterialChanged);
    }

    /// <summary>Friction and bounce; null uses the defaults.</summary>
    [Export]
    public PhysicsMaterial? PhysicsMaterial
    {
        get => _material.Value;
        set
        {
            if (_material.Set(value))
                OnMaterialChanged();
        }
    }

    internal override PhysicsBodyKind Kind => PhysicsBodyKind.Static;

    protected override void OnEnterTree()
    {
        _material.Activate();
        base.OnEnterTree();
    }

    protected override void OnExitTree()
    {
        base.OnExitTree();
        _material.Deactivate();
    }

    private void OnMaterialChanged() => Record?.ApplyMaterial();
}
