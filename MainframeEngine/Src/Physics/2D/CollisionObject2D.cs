using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of 2D physics nodes (Godot's <c>CollisionObject2D</c>): a body or area in its viewport's 2D physics space
/// (<see cref="PhysicsServer2D"/>, one Box2D world per <see cref="World2D"/>). Works in pixels; shapes come from direct
/// <see cref="CollisionShape2D"/> children. Same rules as <see cref="CollisionObject3D"/>: moving the node is pushed to
/// the physics server; dynamic, kinematic and character bodies render interpolated between steps.
/// </summary>
[EditorIcon("atom-2", Family = EditorIconFamily.Physics)]
public abstract class CollisionObject2D : Node2D
{
    private uint _layer = CollisionLayers.Default;
    private uint _mask = CollisionLayers.Default;
    private bool _interpolate = true;
    private bool _applyingPose;

    internal BodyRecord2D? Record { get; set; }

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
    public PhysicsSpace2D? Space => Record?.Space;

    internal abstract PhysicsBodyKind Kind { get; }

    public bool GetCollisionLayerValue(int layerNumber) => (_layer & CollisionLayers.Layer(layerNumber)) != 0;

    public void SetCollisionLayerValue(int layerNumber, bool value) =>
        CollisionLayer = value ? _layer | CollisionLayers.Layer(layerNumber) : _layer & ~CollisionLayers.Layer(layerNumber);

    public bool GetCollisionMaskValue(int layerNumber) => (_mask & CollisionLayers.Layer(layerNumber)) != 0;

    public void SetCollisionMaskValue(int layerNumber, bool value) =>
        CollisionMask = value ? _mask | CollisionLayers.Layer(layerNumber) : _mask & ~CollisionLayers.Layer(layerNumber);

    /// <summary>The <see cref="CollisionShape2D"/> children (enabled, with a shape) that make up the body.</summary>
    public int GetShapes(List<CollisionShape2D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var count = 0;
        for (var i = 0; i < ChildCount; i++)
        {
            if (GetChild(i) is CollisionShape2D { Disabled: false, Shape: not null } shape)
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
        PhysicsServer2D.For(Tree!).AddObject(this);
    }

    protected override void OnExitTree()
    {
        if (Record is { } record)
            record.Space.RemoveObject(record);
        TrackGlobalTransformChanges(false);
        base.OnExitTree();
    }

    private protected override void OnGlobalTransformInvalidated()
    {
        if (!_applyingPose && Record is { } record)
            record.Space.MarkTransformDirty(record);
    }

    internal void OnShapesChanged()
    {
        if (Record is { } record)
            record.Space.MarkShapesDirty(record);
    }

    /// <summary>Writes a physics pose (world position in pixels, rotation in radians; global scale kept) without it counting as a user move.</summary>
    internal void ApplyPhysicsPose(Vector2 position, float rotation, Vector2 globalScale)
    {
        _applyingPose = true;
        try
        {
            if (ParentNode2D is { } parent)
                Transform = parent.GlobalTransform.AffineInverse() * Transform2D.FromTrs(position, rotation, globalScale);
            else
            {
                Position = position;
                Rotation = rotation;
            }

            _ = GlobalTransform;
        }
        finally
        {
            _applyingPose = false;
        }
    }

    internal virtual void EmitBodyEntered(Node2D body)
    {
    }

    internal virtual void EmitBodyExited(Node2D body)
    {
    }

    internal virtual void EmitSleepingStateChanged()
    {
    }
}

/// <summary>Base of the 2D bodies that collide (Godot's <c>PhysicsBody2D</c>).</summary>
[EditorIcon("atom")]
public abstract class PhysicsBody2D : CollisionObject2D
{
}

/// <summary>An immovable body (Godot's <c>StaticBody2D</c>): floors, walls, level geometry. Moving it by code teleports it.</summary>
[EditorIcon("box")]
public class StaticBody2D : PhysicsBody2D
{
    private readonly ResourceSubscription<PhysicsMaterial> _material;

    public StaticBody2D()
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

/// <summary>
/// A simulated 2D body (Godot's <c>RigidBody2D</c>), in pixels: dynamic (gravity, forces, contacts) or kinematic (moved by
/// code). Mass is in kg; forces and impulses are in pixel units (N = kg·px/s²) and converted at the server boundary.
/// </summary>
[EditorIcon("ball-bowling")]
public class RigidBody2D : PhysicsBody2D
{
    private RigidBodyMode _mode;
    private float _mass = 1f;
    private float _gravityScale = 1f;
    private float _linearDamp = 0.1f;
    private float _angularDamp = 0.3f;
    private bool _ccd;
    private bool _canSleep = true;
    private bool _contactMonitor;
    private AxisLock _locks;
    private Vector2 _linearVelocity;
    private float _angularVelocity;
    private readonly ResourceSubscription<PhysicsMaterial> _material;

    public RigidBody2D()
    {
        _material = new ResourceSubscription<PhysicsMaterial>(OnMaterialChanged);
    }

    [Export]
    public RigidBodyMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            Record?.Space.OnKindChanged(Record);
        }
    }

    /// <summary>Mass in kg; rotational inertia follows from the shapes.</summary>
    [Export(Range = "0.001,10000,0.01")]
    public float Mass
    {
        get => _mass;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Mass must be positive.");
            if (_mass == value)
                return;
            _mass = value;
            Record?.Space.MarkShapesDirty(Record);
        }
    }

    [Export(Range = "-8,8,0.01")]
    public float GravityScale
    {
        get => _gravityScale;
        set
        {
            _gravityScale = value;
            Record?.ApplyProperties();
        }
    }

    /// <summary>Linear velocity damping per second.</summary>
    [Export(Range = "0,100,0.01")]
    public float LinearDamp
    {
        get => _linearDamp;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _linearDamp = value;
            Record?.ApplyProperties();
        }
    }

    /// <summary>Angular velocity damping per second.</summary>
    [Export(Range = "0,100,0.01")]
    public float AngularDamp
    {
        get => _angularDamp;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _angularDamp = value;
            Record?.ApplyProperties();
        }
    }

    /// <summary>Continuous collision against other dynamic bodies too (Box2D "bullet").</summary>
    [Export]
    public bool Ccd
    {
        get => _ccd;
        set
        {
            _ccd = value;
            Record?.ApplyProperties();
        }
    }

    [Export]
    public bool CanSleep
    {
        get => _canSleep;
        set
        {
            _canSleep = value;
            Record?.ApplyProperties();
        }
    }

    /// <summary>Report contacts through <see cref="BodyEntered"/>/<see cref="BodyExited"/>.</summary>
    [Export]
    public bool ContactMonitor
    {
        get => _contactMonitor;
        set
        {
            if (_contactMonitor == value)
                return;
            _contactMonitor = value;
            Record?.Space.OnContactMonitorChanged(Record);
        }
    }

    /// <summary>Axes the body may not move or rotate along (LinearX, LinearY, AngularZ).</summary>
    [Export(Flags = true)]
    public AxisLock AxisLocks
    {
        get => _locks;
        set
        {
            _locks = value;
            Record?.ApplyProperties();
        }
    }

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

    /// <summary>Velocity in px/s. For kinematic bodies it moves the body every step until changed.</summary>
    [Export]
    public Vector2 LinearVelocity
    {
        get => Record is { } record ? record.GetLinearVelocity() : _linearVelocity;
        set
        {
            _linearVelocity = value;
            Record?.SetLinearVelocity(value);
        }
    }

    /// <summary>Angular velocity in rad/s (positive turns clockwise on screen: 2D is Y-down).</summary>
    [Export]
    public float AngularVelocity
    {
        get => Record is { } record ? record.GetAngularVelocity() : _angularVelocity;
        set
        {
            _angularVelocity = value;
            Record?.SetAngularVelocity(value);
        }
    }

    public bool Sleeping => Record is { IsSleeping: true };

    [Signal]
    public event Action<Node2D>? BodyEntered;

    [Signal]
    public event Action<Node2D>? BodyExited;

    [Signal]
    public event Action? SleepingStateChanged;

    internal override PhysicsBodyKind Kind => _mode == RigidBodyMode.Dynamic ? PhysicsBodyKind.Dynamic : PhysicsBodyKind.Kinematic;

    internal Vector2 InitialLinearVelocity => _linearVelocity;
    internal float InitialAngularVelocity => _angularVelocity;

    /// <summary>Applies <paramref name="force"/> (kg·px/s²) during the next step, at <paramref name="position"/> (world px) or the centre of mass.</summary>
    public void ApplyForce(Vector2 force, Vector2? position = null) => Record?.ApplyForce(force, position);

    /// <summary>Applies an instantaneous impulse (kg·px/s) at <paramref name="position"/> (world px) or the centre of mass.</summary>
    public void ApplyImpulse(Vector2 impulse, Vector2? position = null) => Record?.ApplyImpulse(impulse, position);

    /// <summary>Applies <paramref name="torque"/> (kg·px²/s²) during the next step.</summary>
    public void ApplyTorque(float torque) => Record?.ApplyTorque(torque);

    /// <summary>Moves the body to <paramref name="transform"/> (world; scale ignored) now and restarts interpolation from there.</summary>
    public void Teleport(in Transform2D transform)
    {
        if (Record is { } record)
            record.Teleport(transform.Origin, transform.Rotation);
        else
            GlobalTransform = transform;
    }

    protected override void OnEnterTree()
    {
        _material.Activate();
        base.OnEnterTree();
    }

    protected override void OnExitTree()
    {
        if (Record is { } record)
        {
            _linearVelocity = record.GetLinearVelocity();
            _angularVelocity = record.GetAngularVelocity();
        }

        base.OnExitTree();
        _material.Deactivate();
    }

    private void OnMaterialChanged() => Record?.ApplyMaterial();

    internal override void EmitBodyEntered(Node2D body) => BodyEntered?.Invoke(body);

    internal override void EmitBodyExited(Node2D body) => BodyExited?.Invoke(body);

    internal override void EmitSleepingStateChanged() => SleepingStateChanged?.Invoke();
}

/// <summary>
/// A 2D region that detects bodies (Godot's <c>Area2D</c>): a Box2D sensor. <see cref="BodyEntered"/>/<see cref="BodyExited"/>
/// fire for static, rigid and character bodies whose layer is in <see cref="CollisionObject2D.CollisionMask"/>.
/// </summary>
/// <remarks>Areas detect bodies, not other areas (Box2D 3.1 sensors don't detect sensors).</remarks>
[EditorIcon("border-corners")]
public class Area2D : CollisionObject2D
{
    /// <summary>Detect bodies (when false, current overlaps are dropped with <see cref="BodyExited"/>).</summary>
    [Export]
    public bool Monitoring { get; set; } = true;

    [Signal]
    public event Action<Node2D>? BodyEntered;

    [Signal]
    public event Action<Node2D>? BodyExited;

    internal override PhysicsBodyKind Kind => PhysicsBodyKind.Area;

    public int GetOverlappingBodies(List<Node2D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return Record is { } record ? record.GetOverlaps(results) : 0;
    }

    public bool HasOverlappingBodies() => Record is { Overlaps.Count: > 0 };

    public bool OverlapsBody(Node2D body) =>
        body is CollisionObject2D { Record: { } other } && Record?.Overlaps is { } overlaps && overlaps.ContainsKey(other);

    internal override void EmitBodyEntered(Node2D body) => BodyEntered?.Invoke(body);

    internal override void EmitBodyExited(Node2D body) => BodyExited?.Invoke(body);
}
