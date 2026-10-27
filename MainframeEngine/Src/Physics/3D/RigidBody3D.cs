using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A simulated body (Godot's <c>RigidBody3D</c>): moved by gravity, forces and contacts (<see cref="RigidBodyMode.Dynamic"/>)
/// or by code (<see cref="RigidBodyMode.Kinematic"/>). Don't set a dynamic body's transform every frame — apply
/// forces/impulses or set velocities; <see cref="Teleport"/> moves it explicitly.
/// </summary>
[EditorIcon("ball-bowling")]
public class RigidBody3D : PhysicsBody3D
{
    private RigidBodyMode _mode;
    private bool _freeze;
    private FreezeMode _freezeMode;
    private float _mass = 1f;
    private float _gravityScale = 1f;
    private float _linearDamp = 0.1f;
    private float _angularDamp = 0.3f;
    private bool _ccd;
    private bool _canSleep = true;
    private bool _contactMonitor;
    private AxisLock _locks;
    private Vector3 _linearVelocity;
    private Vector3 _angularVelocity;
    private readonly ResourceSubscription<PhysicsMaterial> _material;

    public RigidBody3D()
    {
        _material = new ResourceSubscription<PhysicsMaterial>(OnMaterialChanged);
    }

    /// <summary>Dynamic (simulated) or kinematic (moved by code).</summary>
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

    /// <summary>
    /// Stops simulating the body (Godot's <c>freeze</c>): it becomes <see cref="FreezeMode"/> (static or kinematic) and
    /// overrides <see cref="Mode"/> while set. Freezing and unfreezing zero its velocities and keep its joints; unfrozen,
    /// it is simulated again from where it is.
    /// </summary>
    [Export]
    public bool Freeze
    {
        get => _freeze;
        set
        {
            if (_freeze == value)
                return;
            _freeze = value;
            OnFrozenChanged();
        }
    }

    /// <summary>What the body is while <see cref="Freeze"/> is set (Godot's <c>freeze_mode</c>; default static).</summary>
    [Export]
    public FreezeMode FreezeMode
    {
        get => _freezeMode;
        set
        {
            if (_freezeMode == value)
                return;
            _freezeMode = value;
            if (_freeze)
                OnFrozenChanged();
        }
    }

    /// <summary>Mass in kg; inertia follows from the shapes.</summary>
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

    /// <summary>Multiplier on the space's gravity (0 = floats).</summary>
    [Export(Range = "-8,8,0.01")]
    public float GravityScale
    {
        get => _gravityScale;
        set
        {
            _gravityScale = value;
            Record?.Space.OnBodyPropertiesChanged(Record);
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
            Record?.Space.OnBodyPropertiesChanged(Record);
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
            Record?.Space.OnBodyPropertiesChanged(Record);
        }
    }

    /// <summary>Continuous collision detection for fast bodies (Jitter2 speculative contacts).</summary>
    [Export]
    public bool Ccd
    {
        get => _ccd;
        set
        {
            _ccd = value;
            Record?.Space.OnBodyPropertiesChanged(Record);
        }
    }

    /// <summary>Whether the body may sleep when it comes to rest.</summary>
    [Export]
    public bool CanSleep
    {
        get => _canSleep;
        set
        {
            _canSleep = value;
            Record?.Space.OnBodyPropertiesChanged(Record);
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

    /// <summary>World axes the body may not move or rotate along.</summary>
    [Export(Flags = true)]
    public AxisLock AxisLocks
    {
        get => _locks;
        set
        {
            _locks = value;
            Record?.Space.OnBodyPropertiesChanged(Record);
        }
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

    /// <summary>Velocity in m/s (world space). For kinematic bodies it moves the body every step until changed.</summary>
    [Export]
    public Vector3 LinearVelocity
    {
        get => Record is { } record ? record.GetLinearVelocity() : _linearVelocity;
        set
        {
            _linearVelocity = value;
            if (Record is { } record)
                record.SetLinearVelocity(value);
        }
    }

    /// <summary>Angular velocity in rad/s (world space).</summary>
    [Export]
    public Vector3 AngularVelocity
    {
        get => Record is { } record ? record.GetAngularVelocity() : _angularVelocity;
        set
        {
            _angularVelocity = value;
            if (Record is { } record)
                record.SetAngularVelocity(value);
        }
    }

    /// <summary>True while the body is asleep (deactivated).</summary>
    public bool Sleeping => Record is { IsSleeping: true };

    /// <summary>Another body started touching this one (needs <see cref="ContactMonitor"/>).</summary>
    [Signal]
    public event Action<Node3D>? BodyEntered;

    /// <summary>A body stopped touching this one, or left the tree while touching (needs <see cref="ContactMonitor"/>).</summary>
    [Signal]
    public event Action<Node3D>? BodyExited;

    /// <summary>The body fell asleep or woke up (<see cref="Sleeping"/>).</summary>
    [Signal]
    public event Action? SleepingStateChanged;

    internal override PhysicsBodyKind Kind => _freeze
        ? _freezeMode == FreezeMode.Static ? PhysicsBodyKind.Static : PhysicsBodyKind.Kinematic
        : _mode == RigidBodyMode.Dynamic && !_heldKinematic ? PhysicsBodyKind.Dynamic : PhysicsBodyKind.Kinematic;

    private bool _heldKinematic;

    /// <summary>
    /// Kinematic while set, whatever <see cref="Mode"/> says (not saved): a ragdoll that is not simulating, or a physical
    /// bone following its animated bone (ADR 0212). The body keeps its joints; it moves with its node.
    /// </summary>
    internal bool HeldKinematic
    {
        get => _heldKinematic;
        set
        {
            if (_heldKinematic == value)
                return;
            _heldKinematic = value;
            if (Record is { } record)
                record.Space.OnKindChanged(record);
        }
    }

    // Godot: a body frozen or let go starts at rest.
    private void OnFrozenChanged()
    {
        if (Record is not { } record)
            return;
        record.Space.OnKindChanged(record);
        record.SetLinearVelocity(Vector3.Zero);
        record.SetAngularVelocity(Vector3.Zero);
    }

    internal Vector3 InitialLinearVelocity => _linearVelocity;
    internal Vector3 InitialAngularVelocity => _angularVelocity;

    /// <summary>
    /// Applies <paramref name="force"/> (N, world space) during the next step, at <paramref name="position"/>
    /// (world space) or the centre of mass. Call every physics step for a continuous force. Ignored outside a tree.
    /// </summary>
    public void ApplyForce(Vector3 force, Vector3? position = null) => Record?.ApplyForce(force, position);

    /// <summary>Applies an instantaneous impulse (N·s, world space) at <paramref name="position"/> or the centre of mass.</summary>
    public void ApplyImpulse(Vector3 impulse, Vector3? position = null) => Record?.ApplyImpulse(impulse, position);

    /// <summary>Applies <paramref name="torque"/> (N·m, world space) during the next step.</summary>
    public void ApplyTorque(Vector3 torque) => Record?.ApplyTorque(torque);

    /// <summary>
    /// Moves the body to <paramref name="transform"/> (world space; scale ignored) now, keeping its velocity, and
    /// restarts interpolation from there (no smoothing across the jump).
    /// </summary>
    public void Teleport(in Transform3D transform)
    {
        transform.Decompose(out var position, out var rotation, out _);
        if (Record is { } record)
            record.Teleport(position, rotation);
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
        base.OnExitTree();
        _material.Deactivate();
    }

    private protected override void LeaveSpace()
    {
        if (Record is { } record)
        {
            // Keep the simulated state for a later re-entry or re-enable (or for code reading it meanwhile).
            _linearVelocity = record.GetLinearVelocity();
            _angularVelocity = record.GetAngularVelocity();
        }

        base.LeaveSpace();
    }

    private void OnMaterialChanged() => Record?.ApplyMaterial();

    internal override void EmitBodyEntered(Node3D body) => BodyEntered?.Invoke(body);

    internal override void EmitBodyExited(Node3D body) => BodyExited?.Invoke(body);

    internal override void EmitSleepingStateChanged() => SleepingStateChanged?.Invoke();
}
