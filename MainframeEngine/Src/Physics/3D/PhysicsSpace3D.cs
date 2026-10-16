using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>The physics state of one collision object in a <see cref="PhysicsSpace3D"/>.</summary>
internal sealed class BodyRecord3D(CollisionObject3D node, PhysicsSpace3D space, long sequence)
{
    public readonly CollisionObject3D Node = node;
    public readonly PhysicsSpace3D Space = space;
    public readonly long Sequence = sequence;
    public PhysicsBodyKind Kind;
    public BackendBody Handle;
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

    /// <summary>The <see cref="CollisionShape3D"/> of each description in the last <see cref="PhysicsBackend3D.SetShapes"/> (<see cref="BackendHit3D.ShapeIndex"/>).</summary>
    public readonly List<CollisionShape3D> ShapeOwners = [];

    public Vector3 Scale = Vector3.One;
    public Vector3 PreviousPosition, CurrentPosition, TargetPosition;
    public Quaternion PreviousRotation = Quaternion.Identity, CurrentRotation = Quaternion.Identity, TargetRotation = Quaternion.Identity;
    public Vector3 KinematicVelocity, KinematicAngularVelocity;

    /// <summary>Touching bodies (contact monitor) or overlapping bodies (area), with the step they were last seen.</summary>
    public Dictionary<BodyRecord3D, long>? Overlaps;

    public bool IsMoving => Kind is PhysicsBodyKind.Dynamic or PhysicsBodyKind.Kinematic or PhysicsBodyKind.Character;

    /// <summary>A live backend body that collides (areas only detect).</summary>
    public bool HasBody => !Removed && Kind != PhysicsBodyKind.Area;

    public CollisionShape3D? ShapeOwner(int index) => (uint)index < (uint)ShapeOwners.Count ? ShapeOwners[index] : null;

    // ---- state the nodes read and write --------------------------------------------------------------------------

    public Vector3 GetLinearVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Space.Backend.GetLinearVelocity(Handle),
        PhysicsBodyKind.Kinematic => KinematicVelocity,
        _ => Vector3.Zero,
    };

    public Vector3 GetAngularVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Space.Backend.GetAngularVelocity(Handle),
        PhysicsBodyKind.Kinematic => KinematicAngularVelocity,
        _ => Vector3.Zero,
    };

    public void SetLinearVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Space.Backend.SetLinearVelocity(Handle, velocity);
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicVelocity = velocity;
    }

    public void SetAngularVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Space.Backend.SetAngularVelocity(Handle, velocity);
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicAngularVelocity = velocity;
    }

    public bool IsSleeping => Kind == PhysicsBodyKind.Dynamic && !Removed && Space.Backend.IsSleeping(Handle);

    public void ApplyForce(Vector3 force, Vector3? position)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyForce(Handle, force, position);
    }

    public void ApplyImpulse(Vector3 impulse, Vector3? position)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyImpulse(Handle, impulse, position);
    }

    public void ApplyTorque(Vector3 torque)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyTorque(Handle, torque);
    }

    /// <summary>Moves the body (and node) to a pose now and restarts interpolation from it.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        Node.ApplyPhysicsPose(position, rotation, Scale);
        if (HasBody)
            Space.Backend.Teleport(Handle, position, rotation); // wakes it unless static

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
        if (Removed)
            return;
        var backend = Space.Backend;
        backend.SetFilter(Handle, Layer, Mask);
        if (Kind == PhysicsBodyKind.Area)
            return;
        // Resting contacts are dropped so the new filter applies now; what touched the body wakes.
        backend.WakeTouching(Handle);
        backend.ClearContacts(Handle);
        if (Kind != PhysicsBodyKind.Static)
            backend.Wake(Handle);
    }

    /// <summary>The node's physics material (or its values) changed.</summary>
    public void ApplyMaterial()
    {
        if (!HasBody)
            return;
        PhysicsSpace3D.MaterialOf(Node, out var friction, out var bounce);
        Space.Backend.SetMaterial(Handle, friction, bounce);
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
/// The physics side of a <see cref="World3D"/>, owned by the <see cref="PhysicsServer3D"/>: the engine's bookkeeping over
/// one <see cref="PhysicsBackend3D"/> (Jitter2; Jolt in B3). Holds the collision objects in that world, steps them, writes
/// poses back to the nodes, interpolates them for rendering and queues contact/area/sleep signals, dispatched on the main
/// thread after each step.
/// </summary>
/// <remarks>Not thread-safe: nodes and queries use it from the main thread; the backend may use worker threads inside a step.</remarks>
public sealed class PhysicsSpace3D : IDisposable
{
    private readonly PhysicsBackend3D _backend;
    private readonly List<BodyRecord3D> _records = [];
    private readonly List<BodyRecord3D> _dirty = [];
    private readonly List<BodyRecord3D> _moving = [];
    private readonly List<BodyRecord3D> _areas = [];
    private readonly List<BodyRecord3D> _monitors = [];
    private List<PhysicsEvent<BodyRecord3D>> _events = [];
    private List<PhysicsEvent<BodyRecord3D>> _dispatching = [];
    private BodyRecord3D?[] _byHandle = new BodyRecord3D?[64];
    private readonly List<BackendBody> _others = [];
    private readonly List<BackendBody> _queryHits = [];
    private readonly List<BackendShapeRef> _nearby = [];
    private readonly List<ShapeDescription3D> _shapeDescriptions = [];
    private readonly List<ShapeError3D> _shapeErrors = [];
    private int _dispatchIndex = -1;
    private long _sequence;
    private long _step;
    private float _delta = 1f / 60f;
    private bool _disposed;

    internal PhysicsSpace3D(SceneViewport viewport, PhysicsSettings3D settings, PhysicsBackendKind backend)
    {
        Viewport = viewport;
        _backend = PhysicsBackend3D.Create(backend, settings);
        World3D.PhysicsSpace = this;
    }

    /// <summary>The viewport whose world this space simulates (debug lines go to its batch).</summary>
    public SceneViewport Viewport { get; }

    public World3D World3D => Viewport.World3D;

    /// <summary>The library this space runs on.</summary>
    public PhysicsBackendKind BackendKind => _backend.Kind;

    /// <summary>Collision objects in the space.</summary>
    public int ObjectCount => _records.Count;

    /// <summary>Bodies currently awake (simulated).</summary>
    public int ActiveBodyCount => _backend.ActiveBodyCount;

    /// <summary>Physics steps run so far.</summary>
    public long StepCount => _step;

    /// <summary>Gravity in m/s².</summary>
    public Vector3 Gravity
    {
        get => _backend.Gravity;
        set => _backend.Gravity = value;
    }

    /// <summary>Queries against this space (also <see cref="World3D.DirectSpaceState"/>).</summary>
    public PhysicsDirectSpaceState3D DirectSpaceState => World3D.DirectSpaceState;

    internal PhysicsBackend3D Backend => _backend;

    /// <summary>
    /// Test hook: when set, bytes allocated on this thread inside the library's step (Jitter2's worker pool occasionally
    /// allocates a few bytes when stepping multi-threaded) are added to <see cref="LibraryAllocatedBytes"/>.
    /// </summary>
    internal static bool MeasureLibraryAllocations { get; set; }

    internal long LibraryAllocatedBytes => _backend.LibraryAllocatedBytes;

    /// <summary>Scratch for <see cref="PhysicsDirectSpaceState3D"/>'s overlap queries.</summary>
    internal List<BackendBody> QueryScratch => _queryHits;

    internal BodyRecord3D? RecordOf(BackendBody body) => (uint)body.Index < (uint)_byHandle.Length ? _byHandle[body.Index] : null;

    /// <summary>The backend handle of a node in this space, or none (another space's node excludes nothing here).</summary>
    internal BackendBody HandleOf(CollisionObject3D? node) =>
        node?.Record is { Removed: false } record && ReferenceEquals(record.Space, this) ? record.Handle : BackendBody.None;

    private void MapHandle(BodyRecord3D record)
    {
        var index = record.Handle.Index;
        if (index >= _byHandle.Length)
            Array.Resize(ref _byHandle, Math.Max(index + 1, _byHandle.Length * 2));
        _byHandle[index] = record;
    }

    /// <summary>Adds the live nodes of <paramref name="hits"/> to <paramref name="results"/> (each once) and clears <paramref name="hits"/>.</summary>
    internal int AddHits(List<BackendBody> hits, List<CollisionObject3D> results)
    {
        var added = 0;
        foreach (var hit in hits)
        {
            if (RecordOf(hit) is not { Removed: false } record || results.Contains(record.Node))
                continue;
            results.Add(record.Node);
            added++;
        }

        hits.Clear();
        return added;
    }

    internal static void MaterialOf(CollisionObject3D node, out float friction, out float bounce)
    {
        var material = node switch
        {
            RigidBody3D rigid => rigid.PhysicsMaterial,
            StaticBody3D stat => stat.PhysicsMaterial,
            _ => null,
        };
        friction = material?.Friction ?? PhysicsMaterial.DefaultFriction;
        bounce = material?.Bounce ?? 0f;
    }

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

        MaterialOf(node, out var friction, out var bounce);
        record.Handle = _backend.CreateBody(new BodyDescription3D(record.Kind, position, rotation, record.Layer, record.Mask, PropertiesOf(record),
            friction, bounce));
        MapHandle(record);
        if (record.Kind != PhysicsBodyKind.Area)
        {
            if (node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (rigid.InitialLinearVelocity != Vector3.Zero)
                    _backend.SetLinearVelocity(record.Handle, rigid.InitialLinearVelocity);
                if (rigid.InitialAngularVelocity != Vector3.Zero)
                    _backend.SetAngularVelocity(record.Handle, rigid.InitialAngularVelocity);
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

        if (!_disposed)
        {
            if (record.Kind != PhysicsBodyKind.Area)
                _backend.WakeTouching(record.Handle);
            _backend.DestroyBody(record.Handle);
        }

        _byHandle[record.Handle.Index] = null; // before any handler can create a body that reuses the index
        record.Handle = BackendBody.None;
        record.ShapeOwners.Clear();
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
        if (record.Kind != PhysicsBodyKind.Area)
        {
            _backend.SetKind(record.Handle, record.Kind);
            ApplyProperties(record);
            record.ShapesDirty = true; // mass (dynamic) or concave-shape eligibility may have changed
            AddDirty(record);
        }

        UpdateMembership(record);
    }

    internal void OnBodyPropertiesChanged(BodyRecord3D record)
    {
        if (record.Kind != PhysicsBodyKind.Area)
            ApplyProperties(record);
        UpdateMembership(record);
    }


    internal void OnContactMonitorChanged(BodyRecord3D record)
    {
        // Turning the monitor off ends the reported contacts: their exits are queued for the next dispatch (pairs
        // whose enter is still pending are dropped with it), so entered/exited stay paired.
        if (record.Overlaps is { } touching)
        {
            foreach (var other in touching.Keys)
                if (!IsEnterPending(record, other))
                    _events.Add(new PhysicsEvent<BodyRecord3D>(PhysicsEventKind.ContactExited, record, other, record.Sequence, other.Sequence));
        }

        record.Overlaps = null;
        UpdateMembership(record);
    }

    internal void OnInterpolationChanged(BodyRecord3D record)
    {
        UpdateMembership(record);
        if (!record.Interpolated && !record.RenderAtCurrent && !record.TransformDirty)
        {
            record.Node.ApplyPhysicsPose(record.CurrentPosition, record.CurrentRotation, record.Scale);
            record.RenderAtCurrent = true;
        }
    }

    private void UpdateMembership(BodyRecord3D record)
    {
        record.Interpolated = record.IsMoving && record.Node.PhysicsInterpolation;
        SetMember(_moving, record, record.IsMoving);
        SetMember(_areas, record, record.Kind == PhysicsBodyKind.Area);
        var monitor = record.Node is RigidBody3D { ContactMonitor: true };
        SetMember(_monitors, record, monitor);
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
    private void ApplyProperties(BodyRecord3D record) => _backend.SetProperties(record.Handle, PropertiesOf(record));

    // Dynamic rigid bodies are simulated (gravity scale, damping per step, CCD, sleep, axis locks); everything else is not.
    private BodyProperties3D PropertiesOf(BodyRecord3D record) =>
        record.Node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic
            ? new BodyProperties3D(true, StepDamping(rigid.LinearDamp), StepDamping(rigid.AngularDamp), rigid.GravityScale, rigid.AxisLocks, rigid.Ccd,
                rigid.CanSleep)
            : default;

    // Godot-style per-second damping → the velocity removed per step (1 − e^(−d·dt)).
    private float StepDamping(float perSecond) => Math.Clamp(1f - MathF.Exp(-perSecond * _delta), 0f, 1f);

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
                _backend.SetPose(record.Handle, position, rotation);
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
        var node = record.Node;
        var descriptions = _shapeDescriptions;
        descriptions.Clear();
        record.ShapeOwners.Clear();
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

            BakeShapeTransform(record.Scale, owner.Transform, out var linear, out var translation);
            var start = descriptions.Count;
            try
            {
                shape.Describe(descriptions, linear, translation);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Error($"[Physics] '{node.Name}/{owner.Name}': {shape.GetType().Name} is invalid ({e.Message}); ignored.");
                descriptions.RemoveRange(start, descriptions.Count - start);
                continue;
            }

            for (var d = start; d < descriptions.Count; d++)
                record.ShapeOwners.Add(owner);
        }

        var mass = record.Kind == PhysicsBodyKind.Dynamic && node is RigidBody3D rigid ? rigid.Mass : 0f;
        _shapeErrors.Clear();
        _backend.SetShapes(record.Handle, CollectionsMarshal.AsSpan(descriptions), mass, _shapeErrors);
        foreach (var error in _shapeErrors)
        {
            if (error.ShapeIndex < 0)
                Log.Error($"[Physics] '{node.Name}': cannot compute mass properties ({error.Message}); using a unit inertia.");
            else
                Log.Error($"[Physics] '{node.Name}/{record.ShapeOwners[error.ShapeIndex].Name}': {descriptions[error.ShapeIndex].Resource.GetType().Name} is invalid ({error.Message}); ignored.");
        }

        _shapeErrors.Clear();
        descriptions.Clear(); // don't keep the resources' arrays alive
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

    /// <summary>One fixed step: push node changes, step the backend, pull poses, compute contacts/overlaps, dispatch signals.</summary>
    internal void Step(float delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta != _delta)
        {
            _delta = delta;
            foreach (var record in _records)
                if (record.Kind != PhysicsBodyKind.Area)
                    ApplyProperties(record);
        }

        Flush(stepping: true);
        PreStep(delta);
        _backend.Step(delta); // the gravity-scale forces and the allocation measurement are the backend's
        _step++;
        PostStep();
        ScanContacts();
        ScanAreas();
        Dispatch();
    }

    private void PreStep(float delta)
    {
        foreach (var record in _moving)
        {
            if (record.Kind == PhysicsBodyKind.Dynamic)
                continue;
            if (record.HasTarget)
            {
                _backend.MoveKinematic(record.Handle, record.TargetPosition, record.TargetRotation, delta);
            }
            else if (record.Kind == PhysicsBodyKind.Kinematic)
            {
                _backend.SetLinearVelocity(record.Handle, record.KinematicVelocity);
                _backend.SetAngularVelocity(record.Handle, record.KinematicAngularVelocity);
            }
            else
            {
                _backend.SetLinearVelocity(record.Handle, Vector3.Zero);
                _backend.SetAngularVelocity(record.Handle, Vector3.Zero);
            }
        }
    }

    /// <summary>The rotation vector (axis × angle) taking <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal static Vector3 AngularVelocityBetween(Quaternion from, Quaternion to)
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

    /// <summary>
    /// The layer rule (ADR 0021), shared by every backend's filters: two bodies collide when either one's mask has the
    /// other's layer; an area detects a body when the area's mask has the body's layer; areas never detect areas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Collides(uint layerA, uint maskA, bool areaA, uint layerB, uint maskB, bool areaB)
    {
        if (areaA)
            return !areaB && (maskA & layerB) != 0;
        if (areaB)
            return (maskB & layerA) != 0;
        return CollisionLayers.ShouldCollide(layerA, maskA, layerB, maskB);
    }

    /// <summary>A shape child's transform in its body's frame with the body's global scale baked in (as the shapes are built).</summary>
    internal static void BakeShapeTransform(Vector3 bodyScale, in Transform3D local, out Basis linear, out Vector3 translation)
    {
        var scaleBasis = new Basis(Vector3.UnitX * bodyScale.X, Vector3.UnitY * bodyScale.Y, Vector3.UnitZ * bodyScale.Z);
        linear = scaleBasis * local.Basis;
        translation = local.Origin * bodyScale;
    }

    private void PostStep()
    {
        foreach (var record in _moving)
        {
            record.PreviousPosition = record.CurrentPosition;
            record.PreviousRotation = record.CurrentRotation;
            if (record.Kind == PhysicsBodyKind.Character)
            {
                // The node holds the authoritative pose (MoveAndSlide); the backend body follows it.
                if (record.HasTarget)
                {
                    record.CurrentPosition = record.TargetPosition;
                    record.CurrentRotation = record.TargetRotation;
                }

                record.HasTarget = false;
                continue;
            }

            record.HasTarget = false;
            _backend.GetPose(record.Handle, out var position, out var rotation);
            record.CurrentPosition = position;
            record.CurrentRotation = rotation;
            if (record.PreviousPosition != position || record.PreviousRotation != rotation || !record.RenderAtCurrent)
            {
                record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
                record.RenderAtCurrent = true;
            }

            if (record.Kind == PhysicsBodyKind.Dynamic)
            {
                var sleeping = _backend.IsSleeping(record.Handle);
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
            if (monitor.Overlaps is not { } touching)
                continue;
            _others.Clear();
            _backend.CollectTouching(monitor.Handle, _others);
            foreach (var handle in _others)
                if (RecordOf(handle) is { Removed: false } other)
                    Touch(monitor, touching, other, PhysicsEventKind.ContactEntered);
            _others.Clear();
            Untouch(monitor, touching, PhysicsEventKind.ContactExited);
        }
    }

    private void ScanAreas()
    {
        foreach (var area in _areas)
        {
            if (area.Overlaps is not { } overlaps)
                continue;
            _others.Clear();
            if (area.Node is Area3D { Monitoring: false } || !_backend.CollectOverlaps(area.Handle, _others))
            {
                _others.Clear();
                Untouch(area, overlaps, PhysicsEventKind.AreaExited, everything: true);
                continue;
            }

            foreach (var handle in _others)
                if (RecordOf(handle) is { Removed: false } other)
                    Touch(area, overlaps, other, PhysicsEventKind.AreaEntered);
            _others.Clear();
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
                if (e.Receiver.Removed)
                    continue;
                // An enter is stale once its pair is gone (other body freed, monitor or area turned off). An exit is
                // still delivered when the other body was freed after it was queued (no other exit is emitted then).
                if (e.Kind is PhysicsEventKind.ContactEntered or PhysicsEventKind.AreaEntered &&
                    (e.Other!.Removed || e.Receiver.Overlaps is not { } current || !current.ContainsKey(e.Other)))
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

    // ------------------------------------------------------------------------------------------------------------
    // Character queries
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Sweeps every shape of <paramref name="record"/> from the given pose along <paramref name="motion"/>; the closest hit wins.</summary>
    internal bool CastBody(BodyRecord3D record, Vector3 position, Quaternion rotation, Vector3 motion, out BodyCastHit3D hit)
    {
        hit = default;
        if (!record.HasBody || motion.LengthSquared() < 1e-14f)
            return false;
        FlushForQuery();
        if (!_backend.CastBody(record.Handle, position, rotation, motion, out var result))
            return false;
        hit.Fraction = result.Fraction;
        hit.Point = result.Position;
        hit.Normal = result.Normal;
        hit.Collider = RecordOf(result.Body)!.Node;
        return true;
    }

    /// <summary>
    /// Recovery: pushes <paramref name="position"/> out of everything the body's shapes penetrate or come closer to than
    /// <paramref name="margin"/>, leaving <paramref name="margin"/> clearance; each push moves the pose the next pair is
    /// measured from. Everything within two margins counts as a contact (that is how a resting character stays "on the
    /// floor"). Returns how many contacts were found (written to <paramref name="contacts"/>, up to its length).
    /// </summary>
    internal int RecoverBody(BodyRecord3D record, ref Vector3 position, Quaternion rotation, float margin, Span<CharacterContact3D> contacts,
        out bool pushed)
    {
        pushed = false;
        if (!record.HasBody)
            return 0;
        FlushForQuery();
        var shapeCount = _backend.GetShapeCount(record.Handle);
        if (shapeCount == 0)
            return 0;
        _nearby.Clear();
        _backend.CollectNearbyShapes(record.Handle, position, rotation, margin * 2, _nearby);
        var found = 0;
        foreach (var candidate in _nearby)
        {
            if (RecordOf(candidate.Body) is not { Removed: false } other)
                continue;
            for (var shape = 0; shape < shapeCount; shape++)
            {
                // Within two margins counts as touching (a resting or slope-walking body hovers at about one margin); only
                // closer than one margin is pushed back out, along the negative of the normal towards the other shape.
                if (!_backend.Penetration(record.Handle, shape, position, rotation, candidate, margin * 2, out var contact) ||
                    contact.Distance >= margin * 2)
                    continue;
                var push = MathF.Max(0f, margin - contact.Distance);
                var direction = -contact.Normal;
                if (direction.LengthSquared() < 1e-12f)
                    continue;
                direction = Vector3.Normalize(direction);
                position += direction * push;
                pushed |= push > 0;
                if (found < contacts.Length)
                    contacts[found++] = new CharacterContact3D { Normal = direction, Point = contact.Point, Collider = other.Node };
            }
        }

        _nearby.Clear();
        return found;
    }

    /// <summary>
    /// Moves a character body that a cast left a little short of <paramref name="collider"/> onto the safe margin
    /// (ADR 0128), along the backend's exact signed distance; gaps larger than <paramref name="maxGap"/> beyond the margin
    /// are left alone. Returns true if the body moved.
    /// </summary>
    internal bool SettleBody(BodyRecord3D record, ref Vector3 position, Quaternion rotation, float margin, CollisionObject3D collider, float maxGap)
    {
        if (!record.HasBody || collider.Record is not { HasBody: true } other || !ReferenceEquals(other.Space, this))
            return false;
        FlushForQuery();
        if (!_backend.Distance(record.Handle, position, rotation, other.Handle, out var best, out var normal))
            return false;
        if (best <= margin || best > margin + maxGap)
            return false;
        if (normal.LengthSquared() < 1e-12f)
            return false;
        position += Vector3.Normalize(normal) * (best - margin); // the normal points from the body towards the other shape
        return true;
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
                PhysicsBodyKind.Dynamic => _backend.IsSleeping(record.Handle) ? SleepingColor : DynamicColor,
                PhysicsBodyKind.Area => AreaColor,
                _ => KinematicColor,
            };
            var node = record.Node;
            for (var i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is CollisionShape3D { Disabled: false, Shape: { } shape } owner && owner.IsVisibleInTree())
                    shape.DrawDebug(lines, owner.GlobalTransform, color);
        }
    }

    /// <summary>Detaches the space's objects, then disposes the backend.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var record in _records)
        {
            record.Removed = true;
            record.Node.Record = null;
            record.Handle = BackendBody.None;
        }

        _records.Clear();
        _moving.Clear();
        _areas.Clear();
        _monitors.Clear();
        _dirty.Clear();
        Array.Clear(_byHandle);
        if (ReferenceEquals(World3D.PhysicsSpace, this))
            World3D.PhysicsSpace = null;
        _backend.Dispose();
    }
}
