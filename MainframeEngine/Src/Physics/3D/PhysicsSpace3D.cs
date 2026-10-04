using System.Buffers;
using System.Numerics;
using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.DataStructures;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>The physics state of one collision object in a <see cref="PhysicsSpace3D"/>.</summary>
internal sealed class BodyRecord3D(CollisionObject3D node, PhysicsSpace3D space, long sequence)
{
    public readonly CollisionObject3D Node = node;
    public readonly PhysicsSpace3D Space = space;
    public readonly long Sequence = sequence;
    public PhysicsBodyKind Kind;
    public RigidBody? Body;
    public uint Layer;
    public uint Mask;
    public bool Removed;
    public bool TransformDirty;
    public bool ShapesDirty;
    public bool InDirtyList;
    public bool Interpolated;
    public bool RenderAtCurrent = true;
    public bool HasTarget;
    public bool WasSleeping;
    public bool LoggedConcave;
    public readonly List<RigidBodyShape> Shapes = [];
    public Vector3 Scale = Vector3.One;
    public Vector3 PreviousPosition, CurrentPosition, TargetPosition;
    public Quaternion PreviousRotation = Quaternion.Identity, CurrentRotation = Quaternion.Identity, TargetRotation = Quaternion.Identity;
    public Vector3 KinematicVelocity, KinematicAngularVelocity;

    /// <summary>Touching bodies (contact monitor) or overlapping bodies (area), with the step they were last seen.</summary>
    public Dictionary<BodyRecord3D, long>? Overlaps;

    public bool IsMoving => Kind is PhysicsBodyKind.Dynamic or PhysicsBodyKind.Kinematic or PhysicsBodyKind.Character;

    // ---- state the nodes read and write --------------------------------------------------------------------------

    public Vector3 GetLinearVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Body!.Velocity.ToNumerics(),
        PhysicsBodyKind.Kinematic => KinematicVelocity,
        _ => Vector3.Zero,
    };

    public Vector3 GetAngularVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Body!.AngularVelocity.ToNumerics(),
        PhysicsBodyKind.Kinematic => KinematicAngularVelocity,
        _ => Vector3.Zero,
    };

    public void SetLinearVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Body!.Velocity = velocity.ToJ();
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicVelocity = velocity;
    }

    public void SetAngularVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Body!.AngularVelocity = velocity.ToJ();
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicAngularVelocity = velocity;
    }

    public bool IsSleeping => Kind == PhysicsBodyKind.Dynamic && Body is { IsActive: false };

    public void ApplyForce(Vector3 force, Vector3? position)
    {
        if (Kind != PhysicsBodyKind.Dynamic || Body is not { } body)
            return;
        if (position is { } at)
            body.AddForce(force.ToJ(), at.ToJ());
        else
            body.AddForce(force.ToJ());
    }

    public void ApplyImpulse(Vector3 impulse, Vector3? position)
    {
        if (Kind != PhysicsBodyKind.Dynamic || Body is not { } body)
            return;
        if (position is { } at)
            body.ApplyImpulse(impulse.ToJ(), at.ToJ());
        else
            body.ApplyImpulse(impulse.ToJ());
    }

    public void ApplyTorque(Vector3 torque)
    {
        if (Kind != PhysicsBodyKind.Dynamic || Body is not { } body)
            return;
        body.Torque += torque.ToJ();
        body.SetActivationState(true);
    }

    /// <summary>Moves the body (and node) to a pose now and restarts interpolation from it.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        Node.ApplyPhysicsPose(position, rotation, Scale);
        if (Body is { } body)
        {
            body.Position = position.ToJ();
            body.Orientation = rotation.ToJ();
            if (Kind != PhysicsBodyKind.Static)
                body.SetActivationState(true);
        }

        TransformDirty = false;
        HasTarget = false;
        PreviousPosition = CurrentPosition = position;
        PreviousRotation = CurrentRotation = rotation;
        RenderAtCurrent = true;
    }

    /// <summary>Layer or mask changed on the node.</summary>
    public void OnFilterChanged()
    {
        Layer = Node.CollisionLayer;
        Mask = Node.CollisionMask;
        if (Body is { } body)
        {
            // Jitter2 keeps resting contacts until they break: drop them so the new filter applies now.
            PhysicsSpace3D.WakeTouching(body);
            body.ClearContactCache();
            if (Kind != PhysicsBodyKind.Static)
                body.SetActivationState(true);
        }
    }

    /// <summary>The node's physics material (or its values) changed.</summary>
    public void ApplyMaterial()
    {
        if (Body is not { } body)
            return;
        var material = Node switch
        {
            RigidBody3D rigid => rigid.PhysicsMaterial,
            StaticBody3D stat => stat.PhysicsMaterial,
            _ => null,
        };
        body.Friction = material?.Friction ?? PhysicsMaterial.DefaultFriction;
        body.Restitution = material?.Bounce ?? 0f;
    }

    /// <summary>Adds the overlapping/touching nodes to <paramref name="results"/>.</summary>
    public int GetOverlaps(List<Node3D> results)
    {
        if (Overlaps is not { } overlaps)
            return 0;
        foreach (var other in overlaps.Keys)
            results.Add(other.Node);
        return overlaps.Count;
    }
}

/// <summary>
/// One Jitter2 world: the physics side of a <see cref="World3D"/>, owned by the <see cref="PhysicsServer3D"/>. Holds
/// the bodies of the collision objects in that world, steps them, writes poses back to the nodes, interpolates them
/// for rendering and queues contact/area/sleep signals, dispatched on the main thread after each step.
/// </summary>
/// <remarks>Not thread-safe: nodes and queries use it from the main thread; Jitter2 may use its worker pool inside a step.</remarks>
public sealed class PhysicsSpace3D : IDisposable
{
    private readonly World _world;
    private readonly PhysicsSettings3D _settings;
    private readonly List<BodyRecord3D> _records = [];
    private readonly List<BodyRecord3D> _dirty = [];
    private readonly List<BodyRecord3D> _moving = [];
    private readonly List<BodyRecord3D> _areas = [];
    private readonly List<BodyRecord3D> _monitors = [];
    private readonly List<BodyRecord3D> _gravityScaled = [];
    private List<PhysicsEvent<BodyRecord3D>> _events = [];
    private List<PhysicsEvent<BodyRecord3D>> _dispatching = [];
    private readonly Dictionary<RigidBodyShape, CollisionShape3D> _shapeOwners = [];
    private readonly List<RigidBodyShape> _shapeScratch = [];
    private readonly List<IDynamicTreeProxy> _proxies = [];
    private readonly QueryFilters _filters;
    private int _dispatchIndex = -1;
    private long _sequence;
    private long _step;
    private float _delta = 1f / 60f;
    private bool _disposed;

    internal PhysicsSpace3D(SceneViewport viewport, PhysicsSettings3D settings)
    {
        Viewport = viewport;
        _settings = settings;
        _world = new World
        {
            Gravity = settings.Gravity.ToJ(),
            SubstepCount = settings.SubstepCount,
            SolverIterations = (settings.SolverIterations, settings.RelaxationIterations),
            AllowDeactivation = settings.AllowDeactivation,
            BroadPhaseFilter = new LayerFilter(),
            SolveMode = settings.Deterministic ? SolveMode.Deterministic : SolveMode.Regular,
        };
        _filters = new QueryFilters();
        World3D.PhysicsSpace = this;
    }

    /// <summary>The viewport whose world this space simulates (debug lines go to its batch).</summary>
    public SceneViewport Viewport { get; }

    public World3D World3D => Viewport.World3D;

    /// <summary>Collision objects in the space.</summary>
    public int ObjectCount => _records.Count;

    /// <summary>Bodies currently awake (simulated).</summary>
    public int ActiveBodyCount => Math.Max(0, _world.RigidBodies.ActiveCount);

    /// <summary>Physics steps run so far.</summary>
    public long StepCount => _step;

    /// <summary>Gravity in m/s².</summary>
    public Vector3 Gravity
    {
        get => _world.Gravity.ToNumerics();
        set => _world.Gravity = value.ToJ();
    }

    /// <summary>Queries against this space (also <see cref="World3D.DirectSpaceState"/>).</summary>
    public PhysicsDirectSpaceState3D DirectSpaceState => World3D.DirectSpaceState;

    internal DynamicTree Tree => _world.DynamicTree;

    // ------------------------------------------------------------------------------------------------------------
    // Objects
    // ------------------------------------------------------------------------------------------------------------

    internal void AddObject(CollisionObject3D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var record = new BodyRecord3D(node, this, ++_sequence)
        {
            Kind = node.Kind,
            Layer = node.CollisionLayer,
            Mask = node.CollisionMask,
        };
        node.Record = record;
        _records.Add(record);

        node.GlobalTransform.Decompose(out var position, out var rotation, out var scale);
        record.Scale = scale;
        record.PreviousPosition = record.CurrentPosition = position;
        record.PreviousRotation = record.CurrentRotation = rotation;

        if (record.Kind != PhysicsBodyKind.Area)
        {
            var body = _world.CreateRigidBody();
            body.Tag = record;
            body.Position = position.ToJ();
            body.Orientation = rotation.ToJ();
            record.Body = body;
            ApplyKind(record);
            ApplyProperties(record);
            record.ApplyMaterial();
            if (node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (rigid.InitialLinearVelocity != Vector3.Zero)
                    body.Velocity = rigid.InitialLinearVelocity.ToJ();
                if (rigid.InitialAngularVelocity != Vector3.Zero)
                    body.AngularVelocity = rigid.InitialAngularVelocity.ToJ();
            }
            else if (node is RigidBody3D kinematic)
            {
                record.KinematicVelocity = kinematic.InitialLinearVelocity;
                record.KinematicAngularVelocity = kinematic.InitialAngularVelocity;
            }
        }

        UpdateMembership(record);
        MarkShapesDirty(record);
    }

    internal void RemoveObject(BodyRecord3D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        record.Node.Record = null;
        _records.Remove(record);
        _moving.Remove(record);
        _areas.Remove(record);
        _monitors.Remove(record);
        _gravityScaled.Remove(record);

        if (record.Body is { } body)
        {
            foreach (var shape in record.Shapes)
                _shapeOwners.Remove(shape);
            WakeTouching(body);
            if (!_disposed)
                _world.Remove(body);
            record.Body = null;
        }

        record.Shapes.Clear();
        record.Overlaps = null;
        EmitExitsForRemoved(record);
    }

    // Areas containing the removed body and monitors touching it get BodyExited now (Godot semantics).
    private void EmitExitsForRemoved(BodyRecord3D removed)
    {
        var count = _areas.Count + _monitors.Count;
        if (count == 0)
            return;
        var receivers = ArrayPool<BodyRecord3D>.Shared.Rent(count);
        var n = 0;
        try
        {
            // An overlap whose "entered" signal is still queued in this dispatch was never reported: drop it silently
            // (the queued enter is skipped too), so entered/exited always pair up.
            foreach (var area in _areas)
                if (area.Overlaps is { } overlaps && overlaps.Remove(removed) && !IsEnterPending(area, removed))
                    receivers[n++] = area;
            foreach (var monitor in _monitors)
                if (monitor.Overlaps is { } touching && touching.Remove(removed) && !IsEnterPending(monitor, removed))
                    receivers[n++] = monitor;
            for (var i = 0; i < n; i++)
                if (!receivers[i].Removed)
                    receivers[i].Node.EmitBodyExited(removed.Node);
        }
        finally
        {
            Array.Clear(receivers, 0, n);
            ArrayPool<BodyRecord3D>.Shared.Return(receivers);
        }
    }

    private bool IsEnterPending(BodyRecord3D receiver, BodyRecord3D other)
    {
        // Not yet dispatched: everything after the current dispatch index, plus anything queued and not dispatched.
        for (var i = _dispatchIndex + 1; i < _dispatching.Count; i++)
        {
            var e = _dispatching[i];
            if (e.Kind is PhysicsEventKind.ContactEntered or PhysicsEventKind.AreaEntered && e.Receiver == receiver && e.Other == other)
                return true;
        }

        foreach (var e in _events)
            if (e.Kind is PhysicsEventKind.ContactEntered or PhysicsEventKind.AreaEntered && e.Receiver == receiver && e.Other == other)
                return true;
        return false;
    }

    internal void MarkTransformDirty(BodyRecord3D record)
    {
        record.TransformDirty = true;
        AddDirty(record);
    }

    internal void MarkShapesDirty(BodyRecord3D record)
    {
        record.ShapesDirty = true;
        AddDirty(record);
    }

    private void AddDirty(BodyRecord3D record)
    {
        if (record.InDirtyList || record.Removed)
            return;
        record.InDirtyList = true;
        _dirty.Add(record);
    }


    internal void OnKindChanged(BodyRecord3D record)
    {
        record.Kind = record.Node.Kind;
        if (record.Body is not null)
        {
            ApplyKind(record);
            ApplyProperties(record);
            record.ShapesDirty = true; // mass (dynamic) or concave-shape eligibility may have changed
            AddDirty(record);
        }

        UpdateMembership(record);
    }

    internal void OnBodyPropertiesChanged(BodyRecord3D record)
    {
        if (record.Body is not null)
            ApplyProperties(record);
        UpdateMembership(record);
    }


    internal void OnContactMonitorChanged(BodyRecord3D record)
    {
        record.Overlaps = null;
        UpdateMembership(record);
    }

    private void UpdateMembership(BodyRecord3D record)
    {
        record.Interpolated = record.IsMoving && record.Node.PhysicsInterpolation;
        SetMember(_moving, record, record.IsMoving);
        SetMember(_areas, record, record.Kind == PhysicsBodyKind.Area);
        var monitor = record.Node is RigidBody3D { ContactMonitor: true };
        SetMember(_monitors, record, monitor);
        var scaled = record.Kind == PhysicsBodyKind.Dynamic && record.Node is RigidBody3D { GravityScale: not 1f and not 0f };
        SetMember(_gravityScaled, record, scaled);
        if ((monitor || record.Kind == PhysicsBodyKind.Area) && record.Overlaps is null)
            record.Overlaps = new(8);
    }

    private static void SetMember(List<BodyRecord3D> list, BodyRecord3D record, bool member)
    {
        var index = list.IndexOf(record);
        if (member && index < 0)
            list.Add(record);
        else if (!member && index >= 0)
            list.RemoveAt(index);
    }

    private static void ApplyKind(BodyRecord3D record)
    {
        var body = record.Body!;
        body.MotionType = record.Kind switch
        {
            PhysicsBodyKind.Static => MotionType.Static,
            PhysicsBodyKind.Dynamic => MotionType.Dynamic,
            _ => MotionType.Kinematic,
        };
    }

    private void ApplyProperties(BodyRecord3D record)
    {
        var body = record.Body!;
        if (record.Node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic)
        {
            body.AffectedByGravity = rigid.GravityScale == 1f;
            body.Damping = (StepDamping(rigid.LinearDamp), StepDamping(rigid.AngularDamp));
            body.EnableSpeculativeContacts = rigid.Ccd;
            body.DeactivationTime = rigid.CanSleep ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1e7);
            body.AllowedMotion = (MotionAxes)((int)MotionAxes.All & ~(int)rigid.AxisLocks);
        }
        else
        {
            body.AffectedByGravity = false;
        }
    }

    // Godot-style per-second damping → Jitter2's per-step velocity multiplier (1 − d).
    private float StepDamping(float perSecond) => Math.Clamp(1f - MathF.Exp(-perSecond * _delta), 0f, 1f);


    internal static void WakeTouching(RigidBody body)
    {
        foreach (var arbiter in body.Contacts)
        {
            var other = ReferenceEquals(arbiter.Body1, body) ? arbiter.Body2 : arbiter.Body1;
            if (other.MotionType != MotionType.Static)
                other.SetActivationState(true);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Pushing node changes
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Applies pending shape and transform changes; kinematic moves wait for the step unless stepping.</summary>
    private void Flush(bool stepping)
    {
        if (_dirty.Count == 0)
            return;
        var keep = 0;
        for (var i = 0; i < _dirty.Count; i++)
        {
            var record = _dirty[i];
            if (record.Removed)
            {
                record.InDirtyList = false;
                continue;
            }

            if (record.ShapesDirty)
                RebuildShapes(record);
            if (record.TransformDirty)
            {
                if (!stepping && record.Kind is PhysicsBodyKind.Kinematic or PhysicsBodyKind.Character)
                {
                    _dirty[keep++] = record; // a kinematic move becomes a velocity at the step
                    continue;
                }

                PushTransform(record);
            }

            record.InDirtyList = false;
        }

        _dirty.RemoveRange(keep, _dirty.Count - keep);
    }

    internal void FlushForQuery() => Flush(stepping: false);

    private void PushTransform(BodyRecord3D record)
    {
        record.TransformDirty = false;
        record.Node.GlobalTransform.Decompose(out var position, out var rotation, out var scale);
        if (!ApproximatelyEqual(scale, record.Scale))
        {
            record.Scale = scale;
            RebuildShapes(record);
        }

        switch (record.Kind)
        {
            case PhysicsBodyKind.Kinematic:
            case PhysicsBodyKind.Character:
                record.HasTarget = true;
                record.TargetPosition = position;
                record.TargetRotation = rotation;
                break;
            default:
                // Static, area: move there. Dynamic: teleport (velocity kept) and restart interpolation.
                if (record.Body is { } body)
                {
                    body.Position = position.ToJ();
                    body.Orientation = rotation.ToJ();
                }

                record.PreviousPosition = record.CurrentPosition = position;
                record.PreviousRotation = record.CurrentRotation = rotation;
                record.RenderAtCurrent = true;
                break;
        }
    }

    private static bool ApproximatelyEqual(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-10f;

    private void RebuildShapes(BodyRecord3D record)
    {
        record.ShapesDirty = false;
        var body = record.Body;
        if (body is not null && record.Shapes.Count > 0)
        {
            foreach (var shape in record.Shapes)
                _shapeOwners.Remove(shape);
            body.ClearShapes(MassInertiaUpdateMode.Preserve);
        }

        record.Shapes.Clear();
        var node = record.Node;
        var scale = record.Scale;
        var scaleBasis = new Basis(Vector3.UnitX * scale.X, Vector3.UnitY * scale.Y, Vector3.UnitZ * scale.Z);
        for (var i = 0; i < node.ChildCount; i++)
        {
            if (node.GetChild(i) is not CollisionShape3D { Disabled: false, Shape: { } shape } owner)
                continue;
            if (shape.IsConcave && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (!record.LoggedConcave)
                    Log.Error($"[Physics] '{node.Name}': {shape.GetType().Name} is concave and only works on static or kinematic bodies; ignored.");
                record.LoggedConcave = true;
                continue;
            }

            var local = owner.Transform;
            _shapeScratch.Clear();
            shape.CreateShapes(_shapeScratch, scaleBasis * local.Basis, local.Origin * scale);
            foreach (var created in _shapeScratch)
            {
                record.Shapes.Add(created);
                if (body is not null)
                {
                    _shapeOwners[created] = owner;
                    body.AddShape(created, MassInertiaUpdateMode.Preserve);
                }
            }
        }

        _shapeScratch.Clear();
        if (body is not null && record.Kind == PhysicsBodyKind.Dynamic && record.Node is RigidBody3D rigid)
        {
            try
            {
                body.SetMassInertia(rigid.Mass);
            }
            catch (ArgumentException e)
            {
                Log.Error($"[Physics] '{node.Name}': cannot compute mass properties ({e.Message}).");
            }
        }

        if (body is not null && record.Kind != PhysicsBodyKind.Static)
            body.SetActivationState(true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Stepping
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Restores moving bodies from their interpolated render pose to the physics pose.</summary>
    internal void BeforeFixedSteps()
    {
        foreach (var record in _moving)
        {
            if (!record.RenderAtCurrent && !record.TransformDirty)
            {
                record.Node.ApplyPhysicsPose(record.CurrentPosition, record.CurrentRotation, record.Scale);
                record.RenderAtCurrent = true;
            }
        }
    }

    /// <summary>Writes the interpolated pose (previous → current step at <paramref name="alpha"/>) of moving bodies.</summary>
    internal void AfterFixedSteps(float alpha)
    {
        foreach (var record in _moving)
        {
            if (!record.Interpolated || record.TransformDirty)
                continue;
            if (record.PreviousPosition == record.CurrentPosition && record.PreviousRotation == record.CurrentRotation)
            {
                if (!record.RenderAtCurrent)
                {
                    record.Node.ApplyPhysicsPose(record.CurrentPosition, record.CurrentRotation, record.Scale);
                    record.RenderAtCurrent = true;
                }

                continue;
            }

            var position = Vector3.Lerp(record.PreviousPosition, record.CurrentPosition, alpha);
            var rotation = Quaternion.Slerp(record.PreviousRotation, record.CurrentRotation, alpha);
            record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
            record.RenderAtCurrent = alpha >= 1f;
        }
    }

    /// <summary>One fixed step: push node changes, step Jitter2, pull poses, compute contacts/overlaps, dispatch signals.</summary>
    internal void Step(float delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta != _delta)
        {
            _delta = delta;
            foreach (var record in _records)
                if (record.Body is not null)
                    ApplyProperties(record);
        }

        Flush(stepping: true);
        PreStep(delta);
        _world.Step(delta, _settings.MultiThreaded);
        _step++;
        PostStep();
        ScanContacts();
        ScanAreas();
        Dispatch();
    }

    private void PreStep(float delta)
    {
        var inverseDelta = 1f / delta;
        foreach (var record in _moving)
        {
            var body = record.Body;
            if (body is null || record.Kind == PhysicsBodyKind.Dynamic)
                continue;

            if (record.HasTarget)
            {
                // Reach the target pose in exactly one step: contacts see a moving body, not a teleport.
                var position = body.Position.ToNumerics();
                var rotation = body.Orientation.ToNumerics();
                body.Velocity = ((record.TargetPosition - position) * inverseDelta).ToJ();
                body.AngularVelocity = (AngularVelocityBetween(rotation, record.TargetRotation) * inverseDelta).ToJ();
            }
            else if (record.Kind == PhysicsBodyKind.Kinematic)
            {
                body.Velocity = record.KinematicVelocity.ToJ();
                body.AngularVelocity = record.KinematicAngularVelocity.ToJ();
            }
            else
            {
                body.Velocity = JVector.Zero;
                body.AngularVelocity = JVector.Zero;
            }
        }

        if (_gravityScaled.Count > 0)
        {
            var gravity = _world.Gravity.ToNumerics();
            foreach (var record in _gravityScaled)
            {
                if (record.Body is { IsActive: true } body && record.Node is RigidBody3D rigid)
                    body.AddForce((gravity * (rigid.GravityScale * body.Mass)).ToJ(), wakeup: false);
            }
        }
    }

    /// <summary>The rotation vector (axis × angle) taking <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Vector3 AngularVelocityBetween(Quaternion from, Quaternion to)
    {
        var delta = Quaternion.Normalize(to * Quaternion.Conjugate(from));
        if (delta.W < 0)
            delta = Quaternion.Negate(delta); // shortest arc
        var sinHalf = new Vector3(delta.X, delta.Y, delta.Z).Length();
        if (sinHalf < 1e-7f)
            return Vector3.Zero;
        var angle = 2f * MathF.Atan2(sinHalf, delta.W);
        return new Vector3(delta.X, delta.Y, delta.Z) / sinHalf * angle;
    }

    private void PostStep()
    {
        foreach (var record in _moving)
        {
            record.PreviousPosition = record.CurrentPosition;
            record.PreviousRotation = record.CurrentRotation;
            if (record.Kind == PhysicsBodyKind.Character)
            {
                // The node holds the authoritative pose (MoveAndSlide); the Jitter2 body follows it.
                if (record.HasTarget)
                {
                    record.CurrentPosition = record.TargetPosition;
                    record.CurrentRotation = record.TargetRotation;
                }

                record.HasTarget = false;
                continue;
            }

            record.HasTarget = false;
            var body = record.Body!;
            var position = body.Position.ToNumerics();
            var rotation = body.Orientation.ToNumerics();
            record.CurrentPosition = position;
            record.CurrentRotation = rotation;
            if (record.PreviousPosition != position || record.PreviousRotation != rotation || !record.RenderAtCurrent)
            {
                record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
                record.RenderAtCurrent = true;
            }

            if (record.Kind == PhysicsBodyKind.Dynamic)
            {
                var sleeping = !body.IsActive;
                if (sleeping != record.WasSleeping)
                {
                    record.WasSleeping = sleeping;
                    _events.Add(new PhysicsEvent<BodyRecord3D>(PhysicsEventKind.SleepChanged, record, null, record.Sequence, 0));
                }
            }
        }
    }

    private void ScanContacts()
    {
        foreach (var monitor in _monitors)
        {
            if (monitor.Body is not { } body || monitor.Overlaps is not { } touching)
                continue;
            foreach (var arbiter in body.Contacts)
            {
                var other = ReferenceEquals(arbiter.Body1, body) ? arbiter.Body2 : arbiter.Body1;
                if (other.Tag is not BodyRecord3D otherRecord || otherRecord.Removed)
                    continue;
                ref var data = ref arbiter.Handle.Data;
                if ((data.UsageMask & ContactData.MaskContactAll) == 0)
                    continue; // all contact points broken this step
                Touch(monitor, touching, otherRecord, PhysicsEventKind.ContactEntered);
            }

            Untouch(monitor, touching, PhysicsEventKind.ContactExited);
        }
    }

    private void ScanAreas()
    {
        foreach (var area in _areas)
        {
            if (area.Overlaps is not { } overlaps)
                continue;
            if (area.Node is Area3D { Monitoring: false } || area.Shapes.Count == 0)
            {
                Untouch(area, overlaps, PhysicsEventKind.AreaExited, everything: true);
                continue;
            }

            var position = area.CurrentPosition.ToJ();
            var rotation = area.CurrentRotation.ToJ();
            var box = JBoundingBox.SmallBox;
            for (var i = 0; i < area.Shapes.Count; i++)
            {
                area.Shapes[i].CalculateBoundingBox(rotation, position, out var shapeBox);
                box = i == 0 ? shapeBox : JBoundingBox.CreateMerged(box, shapeBox);
            }

            _proxies.Clear();
            var sink = new ProxySink(_proxies);
            _world.DynamicTree.Query(ref sink, box);
            foreach (var proxy in _proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { } otherBody } otherShape ||
                    otherBody.Tag is not BodyRecord3D other || other.Removed || (area.Mask & other.Layer) == 0)
                    continue;
                if (overlaps.TryGetValue(other, out var seen) && seen == _step)
                    continue;
                var otherPosition = otherBody.Position;
                var otherRotation = otherBody.Orientation;
                foreach (var areaShape in area.Shapes)
                {
                    if (NarrowPhase.Overlap(areaShape, otherShape, rotation, otherRotation, position, otherPosition))
                    {
                        Touch(area, overlaps, other, PhysicsEventKind.AreaEntered);
                        break;
                    }
                }
            }

            _proxies.Clear();
            Untouch(area, overlaps, PhysicsEventKind.AreaExited);
        }
    }

    private void Touch(BodyRecord3D receiver, Dictionary<BodyRecord3D, long> set, BodyRecord3D other, PhysicsEventKind entered)
    {
        if (set.TryAdd(other, _step))
            _events.Add(new PhysicsEvent<BodyRecord3D>(entered, receiver, other, receiver.Sequence, other.Sequence));
        else
            set[other] = _step;
    }

    private void Untouch(BodyRecord3D receiver, Dictionary<BodyRecord3D, long> set, PhysicsEventKind exited, bool everything = false)
    {
        if (set.Count == 0)
            return;
        foreach (var (other, seen) in set)
        {
            if (!everything && seen == _step)
                continue;
            set.Remove(other); // allowed while enumerating
            _events.Add(new PhysicsEvent<BodyRecord3D>(exited, receiver, other, receiver.Sequence, other.Sequence));
        }
    }

    private void Dispatch()
    {
        if (_events.Count == 0)
            return;
        _events.Sort(PhysicsEvent<BodyRecord3D>.Comparer.Instance);
        (_dispatching, _events) = (_events, _dispatching);
        try
        {
            // Index loop: handlers may free nodes (records are flagged Removed and skipped) or add new ones.
            for (_dispatchIndex = 0; _dispatchIndex < _dispatching.Count; _dispatchIndex++)
            {
                var e = _dispatching[_dispatchIndex];
                if (e.Receiver.Removed || e.Other is { Removed: true })
                    continue;
                switch (e.Kind)
                {
                    case PhysicsEventKind.ContactEntered:
                    case PhysicsEventKind.AreaEntered:
                        e.Receiver.Node.EmitBodyEntered(e.Other!.Node);
                        break;
                    case PhysicsEventKind.ContactExited:
                    case PhysicsEventKind.AreaExited:
                        e.Receiver.Node.EmitBodyExited(e.Other!.Node);
                        break;
                    case PhysicsEventKind.SleepChanged:
                        e.Receiver.Node.EmitSleepingStateChanged();
                        break;
                }
            }
        }
        finally
        {
            _dispatching.Clear();
            _dispatchIndex = -1;
        }
    }


    internal CollisionShape3D? FindShapeOwner(IDynamicTreeProxy proxy) =>
        proxy is RigidBodyShape shape && _shapeOwners.TryGetValue(shape, out var owner) ? owner : null;

    // ------------------------------------------------------------------------------------------------------------
    // Character queries
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sweeps every shape of <paramref name="record"/> from the given pose along <paramref name="motion"/>; the closest
    /// hit wins. Jitter2's sweep reports contacts within a few millimetres as "already touching" (fraction 0, no
    /// normal); the normal then comes from a distance (or penetration) query against the hit shape.
    /// </summary>
    internal bool CastBody(BodyRecord3D record, Vector3 position, Quaternion rotation, Vector3 motion, out BodyCastHit3D hit)
    {
        hit = default;
        if (record.Body is not { } self || motion.LengthSquared() < 1e-14f)
            return false;
        FlushForQuery();
        _filters.Begin(record.Mask, self);
        var best = float.MaxValue;
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        var motionJ = motion.ToJ();
        try
        {
            foreach (var shape in record.Shapes)
            {
                if (!_world.DynamicTree.SweepCast(shape, rotationJ, positionJ, motionJ, 1f, _filters.SweepPre, null,
                        out var proxy, out _, out var pointB, out var normal, out var lambda) || lambda >= best)
                    continue;
                if (proxy is not RigidBodyShape { RigidBody: { Tag: BodyRecord3D other } otherBody } otherShape)
                    continue;
                best = lambda;
                hit.Fraction = lambda;
                hit.Point = pointB.ToNumerics();
                hit.Collider = other.Node;
                // Jitter2's sweep normal points from the swept shape towards the hit one; surfaces face the other way.
                var surface = -normal.ToNumerics();
                if (surface.LengthSquared() < 1e-12f &&
                    !ContactNormal(shape, otherShape, otherBody, rotationJ, positionJ, out surface, out hit.Point))
                    surface = Vector3.Zero;
                hit.Normal = surface == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(surface);
            }
        }
        finally
        {
            _filters.End();
        }

        return best <= 1f;
    }

    /// <summary>The surface normal of <paramref name="other"/> facing <paramref name="shape"/> when they touch or overlap.</summary>
    private static bool ContactNormal(RigidBodyShape shape, RigidBodyShape other, RigidBody otherBody, in JQuaternion rotation,
        in JVector position, out Vector3 normal, out Vector3 point)
    {
        if (NarrowPhase.Distance(shape, other, rotation, otherBody.Orientation, position, otherBody.Position,
                out _, out var pointB, out var n, out _) ||
            NarrowPhase.MprEpa(shape, other, rotation, otherBody.Orientation, position, otherBody.Position,
                out _, out pointB, out n, out _))
        {
            normal = -n.ToNumerics(); // both report the direction from the shape towards the other one
            point = pointB.ToNumerics();
            return normal.LengthSquared() > 1e-12f;
        }

        normal = default;
        point = default;
        return false;
    }

    /// <summary>
    /// Recovery: pushes <paramref name="position"/> out of everything the body's shapes penetrate or come closer to than
    /// <paramref name="margin"/>, leaving <paramref name="margin"/> clearance. Everything within the margin counts as a
    /// contact (that is how a resting character stays "on the floor"). Returns how many contacts were found (written to
    /// <paramref name="contacts"/>, up to its length).
    /// </summary>
    internal int RecoverBody(BodyRecord3D record, ref Vector3 position, Quaternion rotation, float margin, Span<CharacterContact3D> contacts,
        out bool pushed)
    {
        pushed = false;
        if (record.Body is not { } self)
            return 0;
        FlushForQuery();
        if (record.Shapes.Count == 0)
            return 0;
        var rotationJ = rotation.ToJ();
        var box = JBoundingBox.SmallBox;
        for (var i = 0; i < record.Shapes.Count; i++)
        {
            record.Shapes[i].CalculateBoundingBox(rotationJ, position.ToJ(), out var shapeBox);
            box = i == 0 ? shapeBox : JBoundingBox.CreateMerged(box, shapeBox);
        }

        box.Min -= new JVector(margin * 2);
        box.Max += new JVector(margin * 2);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        var found = 0;
        foreach (var proxy in _proxies)
        {
            if (proxy is not RigidBodyShape { RigidBody: { } otherBody } otherShape || ReferenceEquals(otherBody, self) ||
                otherBody.Tag is not BodyRecord3D other || other.Removed || (record.Mask & other.Layer) == 0)
                continue;
            foreach (var shape in record.Shapes)
            {
                // Both queries report the normal from the body towards the other shape: back out along its negative.
                JVector normal, pointB;
                float push;
                if (NarrowPhase.Distance(shape, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                        out _, out pointB, out normal, out var distance))
                {
                    // Within two margins counts as touching (a resting or slope-walking body hovers at about one
                    // margin); only closer than one margin is pushed back out.
                    if (distance >= margin * 2)
                        continue;
                    push = MathF.Max(0f, margin - distance);
                }
                else if (NarrowPhase.MprEpa(shape, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                             out _, out pointB, out normal, out var penetration) && penetration >= 0)
                {
                    push = penetration + margin;
                }
                else
                {
                    continue;
                }

                var direction = -normal.ToNumerics();
                if (direction.LengthSquared() < 1e-12f)
                    continue;
                direction = Vector3.Normalize(direction);
                position += direction * push;
                pushed |= push > 0;
                if (found < contacts.Length)
                    contacts[found++] = new CharacterContact3D { Normal = direction, Point = pointB.ToNumerics(), Collider = other.Node };
            }
        }

        _proxies.Clear();
        return found;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Debug draw
    // ------------------------------------------------------------------------------------------------------------

    private static readonly Vector4 StaticColor = new(0.35f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 DynamicColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 SleepingColor = new(0.55f, 0.55f, 0.6f, 1f);
    private static readonly Vector4 KinematicColor = new(0.35f, 0.65f, 1f, 1f);
    private static readonly Vector4 AreaColor = new(0.2f, 0.9f, 0.9f, 0.7f);

    /// <summary>Adds the wireframe of every collision shape (at the nodes' current, interpolated pose) to <paramref name="lines"/>.</summary>
    internal void DrawDebug(DebugLines lines)
    {
        foreach (var record in _records)
        {
            var color = record.Kind switch
            {
                PhysicsBodyKind.Static => StaticColor,
                PhysicsBodyKind.Dynamic => record.Body is { IsActive: false } ? SleepingColor : DynamicColor,
                PhysicsBodyKind.Area => AreaColor,
                _ => KinematicColor,
            };
            var node = record.Node;
            for (var i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is CollisionShape3D { Disabled: false, Shape: { } shape } owner && owner.IsVisibleInTree())
                    shape.DrawDebug(lines, owner.GlobalTransform, color);
        }
    }

    /// <summary>Destroys the Jitter2 world; the space's objects are detached from it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var record in _records)
        {
            record.Removed = true;
            record.Node.Record = null;
            record.Body = null;
        }

        _records.Clear();
        _moving.Clear();
        _areas.Clear();
        _monitors.Clear();
        _gravityScaled.Clear();
        _dirty.Clear();
        _shapeOwners.Clear();
        if (ReferenceEquals(World3D.PhysicsSpace, this))
            World3D.PhysicsSpace = null;
        _world.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Filters
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Collision layers on Jitter2's broad phase (Godot OR semantics). Runs on worker threads: it only reads record
    /// fields, which change on the main thread between steps.
    /// </summary>
    private sealed class LayerFilter : IBroadPhaseFilter
    {
        public bool Filter(IDynamicTreeProxy proxyA, IDynamicTreeProxy proxyB)
        {
            if (proxyA is not RigidBodyShape { RigidBody.Tag: BodyRecord3D a } || proxyB is not RigidBodyShape { RigidBody.Tag: BodyRecord3D b })
                return true;
            return (a.Layer & b.Mask) != 0 || (b.Layer & a.Mask) != 0;
        }
    }

    /// <summary>Cached query filter delegates over mutable state (no per-query allocation).</summary>
    internal sealed class QueryFilters
    {
        private uint _mask;
        private RigidBody? _exclude;

        public QueryFilters()
        {
            RayPre = Accept;
            SweepPre = Accept;
        }

        public DynamicTree.RayCastFilterPre RayPre { get; }
        public DynamicTree.SweepCastFilterPre SweepPre { get; }

        public void Begin(uint mask, RigidBody? exclude)
        {
            _mask = mask;
            _exclude = exclude;
        }

        public void End() => _exclude = null;

        public bool Accept(IDynamicTreeProxy proxy) =>
            proxy is RigidBodyShape { RigidBody: { } body } && !ReferenceEquals(body, _exclude) &&
            body.Tag is BodyRecord3D { Removed: false } record && (record.Layer & _mask) != 0;
    }

    internal QueryFilters Filters => _filters;

    internal List<IDynamicTreeProxy> ProxyScratch => _proxies;

    /// <summary>Collects tree query results into a reused list.</summary>
    internal readonly struct ProxySink(List<IDynamicTreeProxy> list) : ISink<IDynamicTreeProxy>
    {
        public void Add(in IDynamicTreeProxy item) => list.Add(item);
    }
}
