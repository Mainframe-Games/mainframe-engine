using System.Buffers;
using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Geometries;
using static Box2D.NET.B2Manifolds;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Shapes;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;

namespace MainframeEngine;

/// <summary>One Box2D shape of a 2D collision object.</summary>
internal struct ShapeEntry2D
{
    public B2ShapeId Id;
    public ShapeGeometry2D Geometry;
    public CollisionShape2D Owner;
}

/// <summary>The physics state of one 2D collision object in a <see cref="PhysicsSpace2D"/> (Box2D units: metres).</summary>
internal sealed class BodyRecord2D(CollisionObject2D node, PhysicsSpace2D space, long sequence)
{
    public readonly CollisionObject2D Node = node;
    public readonly PhysicsSpace2D Space = space;
    public readonly long Sequence = sequence;
    public PhysicsBodyKind Kind;
    public B2BodyId Body;
    public bool HasBody;
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
    public readonly List<ShapeEntry2D> Shapes = [];
    public Vector2 Scale = Vector2.One;
    public Vector2 PreviousPosition, CurrentPosition, TargetPosition; // pixels
    public float PreviousRotation, CurrentRotation, TargetRotation;   // radians
    public Vector2 KinematicVelocity;                                  // px/s
    public float KinematicAngularVelocity;
    public Dictionary<BodyRecord2D, long>? Overlaps;

    public bool IsMoving => Kind is PhysicsBodyKind.Dynamic or PhysicsBodyKind.Kinematic or PhysicsBodyKind.Character;

    private float Ppm => Space.PixelsPerMeter;

    public Vector2 GetLinearVelocity()
    {
        if (Kind == PhysicsBodyKind.Kinematic)
            return KinematicVelocity;
        if (Kind != PhysicsBodyKind.Dynamic || !HasBody)
            return Vector2.Zero;
        var v = b2Body_GetLinearVelocity(Body);
        return new Vector2(v.X, v.Y) * Ppm;
    }

    public float GetAngularVelocity() => Kind switch
    {
        PhysicsBodyKind.Kinematic => KinematicAngularVelocity,
        PhysicsBodyKind.Dynamic when HasBody => b2Body_GetAngularVelocity(Body),
        _ => 0f,
    };

    public void SetLinearVelocity(Vector2 velocity)
    {
        if (Kind == PhysicsBodyKind.Kinematic)
            KinematicVelocity = velocity;
        else if (Kind == PhysicsBodyKind.Dynamic && HasBody)
            b2Body_SetLinearVelocity(Body, PhysicsSpace2D.ToB2(velocity / Ppm));
    }

    public void SetAngularVelocity(float velocity)
    {
        if (Kind == PhysicsBodyKind.Kinematic)
            KinematicAngularVelocity = velocity;
        else if (Kind == PhysicsBodyKind.Dynamic && HasBody)
            b2Body_SetAngularVelocity(Body, velocity);
    }

    public bool IsSleeping => Kind == PhysicsBodyKind.Dynamic && HasBody && !b2Body_IsAwake(Body);

    public void ApplyForce(Vector2 force, Vector2? position)
    {
        if (Kind != PhysicsBodyKind.Dynamic || !HasBody)
            return;
        var f = PhysicsSpace2D.ToB2(force / Ppm);
        if (position is { } at)
            b2Body_ApplyForce(Body, f, PhysicsSpace2D.ToB2(at / Ppm), true);
        else
            b2Body_ApplyForceToCenter(Body, f, true);
    }

    public void ApplyImpulse(Vector2 impulse, Vector2? position)
    {
        if (Kind != PhysicsBodyKind.Dynamic || !HasBody)
            return;
        var j = PhysicsSpace2D.ToB2(impulse / Ppm);
        if (position is { } at)
            b2Body_ApplyLinearImpulse(Body, j, PhysicsSpace2D.ToB2(at / Ppm), true);
        else
            b2Body_ApplyLinearImpulseToCenter(Body, j, true);
    }

    public void ApplyTorque(float torque)
    {
        if (Kind == PhysicsBodyKind.Dynamic && HasBody)
            b2Body_ApplyTorque(Body, torque / (Ppm * Ppm), true);
    }

    public void Teleport(Vector2 position, float rotation)
    {
        Node.ApplyPhysicsPose(position, rotation, Scale);
        if (HasBody)
        {
            b2Body_SetTransform(Body, PhysicsSpace2D.ToB2(position / Ppm), b2MakeRot(rotation));
            if (Kind != PhysicsBodyKind.Static)
                b2Body_SetAwake(Body, true);
        }

        TransformDirty = false;
        HasTarget = false;
        PreviousPosition = CurrentPosition = position;
        PreviousRotation = CurrentRotation = rotation;
        RenderAtCurrent = true;
    }

    public void OnFilterChanged()
    {
        Layer = Node.CollisionLayer;
        Mask = Node.CollisionMask;
        if (!HasBody)
            return;
        var filter = PhysicsSpace2D.PackFilter(Layer, Mask);
        foreach (var shape in Shapes)
            b2Shape_SetFilter(shape.Id, filter); // re-runs pair filtering (and the custom filter) for this shape
    }

    public void ApplyMaterial()
    {
        if (!HasBody)
            return;
        var material = Node switch
        {
            RigidBody2D rigid => rigid.PhysicsMaterial,
            StaticBody2D stat => stat.PhysicsMaterial,
            _ => null,
        };
        foreach (var shape in Shapes)
        {
            b2Shape_SetFriction(shape.Id, material?.Friction ?? PhysicsMaterial.DefaultFriction);
            b2Shape_SetRestitution(shape.Id, material?.Bounce ?? 0f);
        }
    }

    public void ApplyProperties()
    {
        if (!HasBody)
            return;
        if (Node is RigidBody2D rigid && Kind == PhysicsBodyKind.Dynamic)
        {
            b2Body_SetGravityScale(Body, rigid.GravityScale);
            b2Body_SetLinearDamping(Body, rigid.LinearDamp);
            b2Body_SetAngularDamping(Body, rigid.AngularDamp);
            b2Body_SetBullet(Body, rigid.Ccd);
            b2Body_EnableSleep(Body, rigid.CanSleep);
            var locks = rigid.AxisLocks;
            b2Body_SetMotionLocks(Body, new B2MotionLocks((locks & AxisLock.LinearX) != 0, (locks & AxisLock.LinearY) != 0, (locks & AxisLock.AngularZ) != 0));
        }
        else
        {
            b2Body_SetGravityScale(Body, 0f);
            b2Body_SetMotionLocks(Body, default); // locks are a dynamic-body feature; kinematic moves must reach their target
            // Areas must stay awake for their sensor to update; characters and kinematic bodies are moved every step.
            b2Body_EnableSleep(Body, Kind is PhysicsBodyKind.Static);
        }
    }

    public int GetOverlaps(List<Node2D> results)
    {
        if (Overlaps is not { } overlaps)
            return 0;
        foreach (var other in overlaps.Keys)
            results.Add(other.Node);
        return overlaps.Count;
    }
}

/// <summary>
/// One Box2D world: the physics side of a <see cref="World2D"/>, owned by the <see cref="PhysicsServer2D"/>. Mirrors
/// <see cref="PhysicsSpace3D"/>: steps the bodies, writes poses back (pixels ↔ metres), interpolates them and queues
/// contact/area/sleep signals for dispatch after each step.
/// </summary>
/// <remarks>
/// Box2D.NET 3.1.654 allocates a little managed memory inside every <c>b2World_Step</c> (a step context and solver
/// arrays); the engine's own per-step code does not allocate (ADR 0023).
/// </remarks>
public sealed class PhysicsSpace2D : IDisposable
{
    // Static callbacks: one delegate each, no per-call allocation. The context object is the space.
    private static readonly b2CustomFilterFcn CustomFilter = ShouldCollide;
    private static readonly b2CastResultFcn RayCallback = OnRayHit;
    private static readonly b2CastResultFcn CastCallback = OnCastHit;
    private static readonly b2OverlapResultFcn OverlapCallback = OnOverlap;

    private readonly B2WorldId _world;
    private readonly PhysicsSettings2D _settings;
    private readonly List<BodyRecord2D> _records = [];
    private readonly List<BodyRecord2D> _dirty = [];
    private readonly List<BodyRecord2D> _moving = [];
    private readonly List<BodyRecord2D> _areas = [];
    private readonly List<BodyRecord2D> _monitors = [];
    private List<PhysicsEvent<BodyRecord2D>> _events = [];
    private List<PhysicsEvent<BodyRecord2D>> _dispatching = [];
    private readonly Dictionary<ulong, ShapeRef> _shapes = [];
    private readonly List<ShapeGeometry2D> _geometryScratch = [];
    private readonly List<B2ShapeId> _shapeScratch = [];
    private B2ContactData[] _contactScratch = new B2ContactData[16];
    private B2ShapeId[] _visitorScratch = new B2ShapeId[16];
    private int _dispatchIndex = -1;
    private long _sequence;
    private long _step;
    private bool _disposed;

    // Query state read by the static callbacks.
    private BodyRecord2D? _queryExclude;
    private float _queryFraction;
    private B2ShapeId _queryShape;
    private B2Vec2 _queryPoint;
    private B2Vec2 _queryNormal;
    private bool _queryHit;

    private readonly record struct ShapeRef(BodyRecord2D Record, CollisionShape2D Owner, int Index);

    internal PhysicsSpace2D(SceneViewport viewport, PhysicsSettings2D settings)
    {
        Viewport = viewport;
        _settings = settings;
        PixelsPerMeter = settings.PixelsPerMeter;
        var def = b2DefaultWorldDef();
        def.gravity = ToB2(settings.Gravity / PixelsPerMeter);
        def.enableSleep = settings.AllowSleep;
        def.enableContinuous = settings.EnableContinuous;
        _world = b2CreateWorld(def);
        b2World_SetCustomFilterCallback(_world, CustomFilter, this);
        World2D.PhysicsSpace = this;
    }

    public SceneViewport Viewport { get; }

    public World2D World2D => Viewport.World2D;

    /// <summary>The conversion factor used by this space (fixed when it was created).</summary>
    public float PixelsPerMeter { get; }

    public int ObjectCount => _records.Count;

    public long StepCount => _step;

    /// <summary>Gravity in px/s².</summary>
    public Vector2 Gravity
    {
        get
        {
            var g = b2World_GetGravity(_world);
            return new Vector2(g.X, g.Y) * PixelsPerMeter;
        }
        set => b2World_SetGravity(_world, ToB2(value / PixelsPerMeter));
    }

    public PhysicsDirectSpaceState2D DirectSpaceState => World2D.DirectSpaceState;

    /// <summary>
    /// Test hook: when set, bytes allocated inside <c>b2World_Step</c> (Box2D.NET's own allocations) are added to
    /// <see cref="LibraryAllocatedBytes"/>, so the engine's overhead can be measured separately.
    /// </summary>
    internal static bool MeasureLibraryAllocations { get; set; }

    internal long LibraryAllocatedBytes { get; private set; }

    internal static B2Vec2 ToB2(Vector2 v) => new(v.X, v.Y);

    internal static Vector2 ToNumerics(B2Vec2 v) => new(v.X, v.Y);

    /// <summary>
    /// Box2D only collides when <c>(A.category &amp; B.mask) != 0 &amp;&amp; (B.category &amp; A.mask) != 0</c>. To get Godot's
    /// OR semantics, every shape passes that test (mask = all) and carries layer (low 32 bits) and mask (high 32 bits)
    /// in its category for <see cref="ShouldCollide"/>. Queries mask the low bits (the layer) as usual.
    /// </summary>
    internal static B2Filter PackFilter(uint layer, uint mask) => new(layer | ((ulong)mask << 32), ulong.MaxValue, 0);

    private static bool ShouldCollide(B2ShapeId a, B2ShapeId b, object context)
    {
        var filterA = b2Shape_GetFilter(a);
        var filterB = b2Shape_GetFilter(b);
        uint layerA = (uint)filterA.categoryBits, maskA = (uint)(filterA.categoryBits >> 32);
        uint layerB = (uint)filterB.categoryBits, maskB = (uint)(filterB.categoryBits >> 32);
        var sensorA = b2Shape_IsSensor(a);
        var sensorB = b2Shape_IsSensor(b);
        if (sensorA || sensorB)
            return sensorA != sensorB && (sensorA ? (maskA & layerB) != 0 : (maskB & layerA) != 0); // areas: one-way, never area-area
        return (layerA & maskB) != 0 || (layerB & maskA) != 0;
    }

    private static ulong Key(B2ShapeId id) => ((ulong)(uint)id.index1 << 32) | ((ulong)id.world0 << 16) | id.generation;

    private bool TryGetShape(B2ShapeId id, out ShapeRef shape) => _shapes.TryGetValue(Key(id), out shape) && !shape.Record.Removed;

    // ------------------------------------------------------------------------------------------------------------
    // Objects
    // ------------------------------------------------------------------------------------------------------------

    internal void AddObject(CollisionObject2D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var record = new BodyRecord2D(node, this, ++_sequence)
        {
            Kind = node.Kind,
            Layer = node.CollisionLayer,
            Mask = node.CollisionMask,
        };
        node.Record = record;
        _records.Add(record);

        var global = node.GlobalTransform;
        record.Scale = global.Scale;
        record.PreviousPosition = record.CurrentPosition = global.Origin;
        record.PreviousRotation = record.CurrentRotation = global.Rotation;

        var def = b2DefaultBodyDef();
        def.type = record.Kind switch
        {
            PhysicsBodyKind.Static => B2BodyType.b2_staticBody,
            PhysicsBodyKind.Dynamic => B2BodyType.b2_dynamicBody,
            _ => B2BodyType.b2_kinematicBody,
        };
        def.position = ToB2(global.Origin / PixelsPerMeter);
        def.rotation = b2MakeRot(global.Rotation);
        def.userData = B2UserData.Ref(record);
        if (node is RigidBody2D rigid)
        {
            if (record.Kind == PhysicsBodyKind.Dynamic)
            {
                def.linearVelocity = ToB2(rigid.InitialLinearVelocity / PixelsPerMeter);
                def.angularVelocity = rigid.InitialAngularVelocity;
            }
            else
            {
                record.KinematicVelocity = rigid.InitialLinearVelocity;
                record.KinematicAngularVelocity = rigid.InitialAngularVelocity;
            }
        }

        record.Body = b2CreateBody(_world, def);
        record.HasBody = true;
        record.ApplyProperties();
        UpdateMembership(record);
        MarkShapesDirty(record);
    }

    internal void RemoveObject(BodyRecord2D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        record.Node.Record = null;
        _records.Remove(record);
        _moving.Remove(record);
        _areas.Remove(record);
        _monitors.Remove(record);
        foreach (var shape in record.Shapes)
            _shapes.Remove(Key(shape.Id));
        record.Shapes.Clear();
        if (record.HasBody && !_disposed)
            b2DestroyBody(record.Body);
        record.HasBody = false;
        record.Overlaps = null;
        EmitExitsForRemoved(record);
    }

    private void EmitExitsForRemoved(BodyRecord2D removed)
    {
        var count = _areas.Count + _monitors.Count;
        if (count == 0)
            return;
        var receivers = ArrayPool<BodyRecord2D>.Shared.Rent(count);
        var n = 0;
        try
        {
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
            ArrayPool<BodyRecord2D>.Shared.Return(receivers);
        }
    }

    private bool IsEnterPending(BodyRecord2D receiver, BodyRecord2D other)
    {
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

    internal void MarkTransformDirty(BodyRecord2D record)
    {
        record.TransformDirty = true;
        AddDirty(record);
    }

    internal void MarkShapesDirty(BodyRecord2D record)
    {
        record.ShapesDirty = true;
        AddDirty(record);
    }

    private void AddDirty(BodyRecord2D record)
    {
        if (record.InDirtyList || record.Removed)
            return;
        record.InDirtyList = true;
        _dirty.Add(record);
    }

    internal void OnKindChanged(BodyRecord2D record)
    {
        record.Kind = record.Node.Kind;
        if (record.HasBody)
        {
            b2Body_SetType(record.Body, record.Kind switch
            {
                PhysicsBodyKind.Static => B2BodyType.b2_staticBody,
                PhysicsBodyKind.Dynamic => B2BodyType.b2_dynamicBody,
                _ => B2BodyType.b2_kinematicBody,
            });
            record.ApplyProperties();
            MarkShapesDirty(record);
        }

        UpdateMembership(record);
    }

    internal void OnContactMonitorChanged(BodyRecord2D record)
    {
        // Turning the monitor off ends the reported contacts: their exits are queued for the next dispatch (pairs
        // whose enter is still pending are dropped with it), so entered/exited stay paired.
        if (record.Overlaps is { } touching)
        {
            foreach (var other in touching.Keys)
                if (!IsEnterPending(record, other))
                    _events.Add(new PhysicsEvent<BodyRecord2D>(PhysicsEventKind.ContactExited, record, other, record.Sequence, other.Sequence));
        }

        record.Overlaps = null;
        UpdateMembership(record);
    }

    internal void OnInterpolationChanged(BodyRecord2D record)
    {
        UpdateMembership(record);
        if (!record.Interpolated && !record.RenderAtCurrent && !record.TransformDirty)
        {
            record.Node.ApplyPhysicsPose(record.CurrentPosition, record.CurrentRotation, record.Scale);
            record.RenderAtCurrent = true;
        }
    }

    private void UpdateMembership(BodyRecord2D record)
    {
        record.Interpolated = record.IsMoving && record.Node.PhysicsInterpolation;
        SetMember(_moving, record, record.IsMoving);
        SetMember(_areas, record, record.Kind == PhysicsBodyKind.Area);
        var monitor = record.Node is RigidBody2D { ContactMonitor: true };
        SetMember(_monitors, record, monitor);
        if ((monitor || record.Kind == PhysicsBodyKind.Area) && record.Overlaps is null)
            record.Overlaps = new(8);
    }

    private static void SetMember(List<BodyRecord2D> list, BodyRecord2D record, bool member)
    {
        var index = list.IndexOf(record);
        if (member && index < 0)
            list.Add(record);
        else if (!member && index >= 0)
            list.RemoveAt(index);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Pushing node changes
    // ------------------------------------------------------------------------------------------------------------

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
                    _dirty[keep++] = record;
                    continue;
                }

                PushTransform(record);
            }

            record.InDirtyList = false;
        }

        _dirty.RemoveRange(keep, _dirty.Count - keep);
    }

    internal void FlushForQuery() => Flush(stepping: false);

    private void PushTransform(BodyRecord2D record)
    {
        record.TransformDirty = false;
        var global = record.Node.GlobalTransform;
        var scale = global.Scale;
        if (Vector2.DistanceSquared(scale, record.Scale) > 1e-10f)
        {
            record.Scale = scale;
            RebuildShapes(record);
        }

        var position = global.Origin;
        var rotation = global.Rotation;
        switch (record.Kind)
        {
            case PhysicsBodyKind.Kinematic:
            case PhysicsBodyKind.Character:
                record.HasTarget = true;
                record.TargetPosition = position;
                record.TargetRotation = rotation;
                break;
            case PhysicsBodyKind.Area:
                // Areas are kinematic sensors: move them like a teleport (no velocity).
                b2Body_SetTransform(record.Body, ToB2(position / PixelsPerMeter), b2MakeRot(rotation));
                record.PreviousPosition = record.CurrentPosition = position;
                record.PreviousRotation = record.CurrentRotation = rotation;
                break;
            default:
                b2Body_SetTransform(record.Body, ToB2(position / PixelsPerMeter), b2MakeRot(rotation));
                if (record.Kind == PhysicsBodyKind.Dynamic)
                    b2Body_SetAwake(record.Body, true);
                else
                    WakeTouching(record); // Box2D doesn't wake bodies resting on a static body that moved
                record.PreviousPosition = record.CurrentPosition = position;
                record.PreviousRotation = record.CurrentRotation = rotation;
                record.RenderAtCurrent = true;
                break;
        }
    }

    private void WakeTouching(BodyRecord2D record)
    {
        var capacity = b2Body_GetContactCapacity(record.Body);
        if (capacity > _contactScratch.Length)
            _contactScratch = new B2ContactData[Math.Max(capacity, _contactScratch.Length * 2)];
        var count = b2Body_GetContactData(record.Body, _contactScratch, _contactScratch.Length);
        for (var i = 0; i < count; i++)
        {
            ref var contact = ref _contactScratch[i];
            var other = b2Shape_GetBody(contact.shapeIdA);
            if (other.Equals(record.Body))
                other = b2Shape_GetBody(contact.shapeIdB);
            if (b2Body_GetType(other) != B2BodyType.b2_staticBody)
                b2Body_SetAwake(other, true);
        }
    }

    private void RebuildShapes(BodyRecord2D record)
    {
        record.ShapesDirty = false;
        if (!record.HasBody)
            return;
        foreach (var shape in record.Shapes)
        {
            _shapes.Remove(Key(shape.Id));
            b2DestroyShape(shape.Id, false);
        }

        record.Shapes.Clear();
        var node = record.Node;
        var scale = record.Scale;
        var scaleTransform = new Transform2D(new Vector2(scale.X, 0), new Vector2(0, scale.Y), Vector2.Zero);
        var material = node switch
        {
            RigidBody2D rigid => rigid.PhysicsMaterial,
            StaticBody2D stat => stat.PhysicsMaterial,
            _ => null,
        };
        var def = b2DefaultShapeDef();
        def.filter = PackFilter(record.Layer, record.Mask);
        def.enableCustomFiltering = true;
        def.isSensor = record.Kind == PhysicsBodyKind.Area;
        def.enableSensorEvents = true;
        def.enableContactEvents = false; // contacts are read with b2Body_GetContactData after the step
        def.updateBodyMass = false;
        def.material.friction = material?.Friction ?? PhysicsMaterial.DefaultFriction;
        def.material.restitution = material?.Bounce ?? 0f;
        def.userData = B2UserData.Ref(record);

        for (var i = 0; i < node.ChildCount; i++)
        {
            if (node.GetChild(i) is not CollisionShape2D { Disabled: false, Shape: { } shape } owner)
                continue;
            if (shape.IsConcave && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (!record.LoggedConcave)
                    Log.Error($"[Physics2D] '{node.Name}': {shape.GetType().Name} only works on static or kinematic bodies; ignored.");
                record.LoggedConcave = true;
                continue;
            }

            _geometryScratch.Clear();
            shape.CreateGeometry(_geometryScratch, scaleTransform * owner.Transform, PixelsPerMeter);
            foreach (var geometry in _geometryScratch)
            {
                var g = geometry;
                var id = g.Type switch
                {
                    B2ShapeType.b2_circleShape => b2CreateCircleShape(record.Body, def, g.Circle),
                    B2ShapeType.b2_capsuleShape => b2CreateCapsuleShape(record.Body, def, g.Capsule),
                    B2ShapeType.b2_segmentShape => b2CreateSegmentShape(record.Body, def, g.Segment),
                    _ => b2CreatePolygonShape(record.Body, def, g.Polygon),
                };
                if (id.index1 == 0) // B2_IS_NULL: Box2D refused the geometry (degenerate at this scale)
                {
                    Log.Error($"[Physics2D] '{node.Name}/{owner.Name}': {shape.GetType().Name} is too small for Box2D; ignored.");
                    continue;
                }

                record.Shapes.Add(new ShapeEntry2D { Id = id, Geometry = g, Owner = owner });
                _shapes[Key(id)] = new ShapeRef(record, owner, record.Shapes.Count - 1);
            }
        }

        _geometryScratch.Clear();
        if (record.Kind == PhysicsBodyKind.Dynamic && node is RigidBody2D body)
        {
            b2Body_ApplyMassFromShapes(record.Body);
            var mass = b2Body_GetMassData(record.Body);
            if (mass.mass > 0)
            {
                var factor = body.Mass / mass.mass;
                mass.rotationalInertia *= factor;
                mass.mass = body.Mass;
            }
            else
            {
                mass = new B2MassData(body.Mass, default, body.Mass * 0.1f);
            }

            b2Body_SetMassData(record.Body, mass);
        }

        if (record.Kind != PhysicsBodyKind.Static)
            b2Body_SetAwake(record.Body, true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Stepping
    // ------------------------------------------------------------------------------------------------------------

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

            var position = Vector2.Lerp(record.PreviousPosition, record.CurrentPosition, alpha);
            var rotation = record.PreviousRotation + AngleDelta(record.PreviousRotation, record.CurrentRotation) * alpha;
            record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
            record.RenderAtCurrent = alpha >= 1f;
        }
    }

    private static float AngleDelta(float from, float to)
    {
        var d = (to - from) % MathF.Tau;
        if (d > MathF.PI)
            d -= MathF.Tau;
        else if (d < -MathF.PI)
            d += MathF.Tau;
        return d;
    }

    internal void Step(float delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Flush(stepping: true);
        PreStep(delta);
        if (MeasureLibraryAllocations)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            b2World_Step(_world, delta, _settings.SubstepCount);
            LibraryAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        else
        {
            b2World_Step(_world, delta, _settings.SubstepCount);
        }

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
            if (!record.HasBody || record.Kind == PhysicsBodyKind.Dynamic)
                continue;
            if (record.HasTarget)
            {
                var target = new B2Transform(ToB2(record.TargetPosition / PixelsPerMeter), b2MakeRot(record.TargetRotation));
                b2Body_SetTargetTransform(record.Body, target, delta, true);
            }
            else if (record.Kind == PhysicsBodyKind.Kinematic)
            {
                b2Body_SetLinearVelocity(record.Body, ToB2(record.KinematicVelocity / PixelsPerMeter));
                b2Body_SetAngularVelocity(record.Body, record.KinematicAngularVelocity);
            }
            else
            {
                b2Body_SetLinearVelocity(record.Body, b2Vec2_zero);
                b2Body_SetAngularVelocity(record.Body, 0f);
            }
        }
    }

    private void PostStep()
    {
        foreach (var record in _moving)
        {
            record.PreviousPosition = record.CurrentPosition;
            record.PreviousRotation = record.CurrentRotation;
            if (record.Kind == PhysicsBodyKind.Character)
            {
                if (record.HasTarget)
                {
                    record.CurrentPosition = record.TargetPosition;
                    record.CurrentRotation = record.TargetRotation;
                }

                record.HasTarget = false;
                continue;
            }

            record.HasTarget = false;
            var transform = b2Body_GetTransform(record.Body);
            var position = ToNumerics(transform.p) * PixelsPerMeter;
            var rotation = b2Rot_GetAngle(transform.q);
            record.CurrentPosition = position;
            record.CurrentRotation = rotation;
            if (record.PreviousPosition != position || record.PreviousRotation != rotation || !record.RenderAtCurrent)
            {
                record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
                record.RenderAtCurrent = true;
            }

            if (record.Kind == PhysicsBodyKind.Dynamic)
            {
                var sleeping = !b2Body_IsAwake(record.Body);
                if (sleeping != record.WasSleeping)
                {
                    record.WasSleeping = sleeping;
                    _events.Add(new PhysicsEvent<BodyRecord2D>(PhysicsEventKind.SleepChanged, record, null, record.Sequence, 0));
                }
            }
        }
    }

    private void ScanContacts()
    {
        foreach (var monitor in _monitors)
        {
            if (!monitor.HasBody || monitor.Overlaps is not { } touching)
                continue;
            var capacity = b2Body_GetContactCapacity(monitor.Body);
            if (capacity > _contactScratch.Length)
                _contactScratch = new B2ContactData[Math.Max(capacity, _contactScratch.Length * 2)];
            var count = b2Body_GetContactData(monitor.Body, _contactScratch, _contactScratch.Length);
            for (var i = 0; i < count; i++)
            {
                ref var contact = ref _contactScratch[i];
                if (contact.manifold.pointCount == 0)
                    continue;
                if (!TryGetShape(contact.shapeIdA, out var a) || !TryGetShape(contact.shapeIdB, out var b))
                    continue;
                var other = ReferenceEquals(a.Record, monitor) ? b.Record : a.Record;
                if (!ReferenceEquals(other, monitor))
                    Touch(monitor, touching, other, PhysicsEventKind.ContactEntered);
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
            if (area.Node is Area2D { Monitoring: false })
            {
                Untouch(area, overlaps, PhysicsEventKind.AreaExited, everything: true);
                continue;
            }

            foreach (var shape in area.Shapes)
            {
                var capacity = b2Shape_GetSensorCapacity(shape.Id);
                if (capacity > _visitorScratch.Length)
                    _visitorScratch = new B2ShapeId[Math.Max(capacity, _visitorScratch.Length * 2)];
                var count = b2Shape_GetSensorData(shape.Id, _visitorScratch, _visitorScratch.Length);
                for (var i = 0; i < count; i++)
                {
                    if (TryGetShape(_visitorScratch[i], out var visitor) && !ReferenceEquals(visitor.Record, area) &&
                        visitor.Record.Kind != PhysicsBodyKind.Area && (area.Mask & visitor.Record.Layer) != 0)
                        Touch(area, overlaps, visitor.Record, PhysicsEventKind.AreaEntered);
                }
            }

            Untouch(area, overlaps, PhysicsEventKind.AreaExited);
        }
    }

    private void Touch(BodyRecord2D receiver, Dictionary<BodyRecord2D, long> set, BodyRecord2D other, PhysicsEventKind entered)
    {
        if (set.TryAdd(other, _step))
            _events.Add(new PhysicsEvent<BodyRecord2D>(entered, receiver, other, receiver.Sequence, other.Sequence));
        else
            set[other] = _step;
    }

    private void Untouch(BodyRecord2D receiver, Dictionary<BodyRecord2D, long> set, PhysicsEventKind exited, bool everything = false)
    {
        if (set.Count == 0)
            return;
        foreach (var (other, seen) in set)
        {
            if (!everything && seen == _step)
                continue;
            set.Remove(other);
            _events.Add(new PhysicsEvent<BodyRecord2D>(exited, receiver, other, receiver.Sequence, other.Sequence));
        }
    }

    private void Dispatch()
    {
        if (_events.Count == 0)
            return;
        _events.Sort(PhysicsEvent<BodyRecord2D>.Comparer.Instance);
        (_dispatching, _events) = (_events, _dispatching);
        try
        {
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
    // Queries (pixels in, pixels out)
    // ------------------------------------------------------------------------------------------------------------

    private static B2QueryFilter QueryFilter(uint mask) => new(ulong.MaxValue, mask);

    private static float OnRayHit(B2ShapeId shapeId, B2Vec2 point, B2Vec2 normal, float fraction, object context)
    {
        var space = (PhysicsSpace2D)context;
        if (!space.TryGetShape(shapeId, out var shape) || shape.Record.Kind == PhysicsBodyKind.Area ||
            ReferenceEquals(shape.Record, space._queryExclude))
            return -1f; // ignore and continue
        if (fraction < space._queryFraction)
        {
            space._queryHit = true;
            space._queryFraction = fraction;
            space._queryShape = shapeId;
            space._queryPoint = point;
            space._queryNormal = normal;
        }

        return fraction; // clip to the closest so far
    }

    private static float OnCastHit(B2ShapeId shapeId, B2Vec2 point, B2Vec2 normal, float fraction, object context) =>
        OnRayHit(shapeId, point, normal, fraction, context);

    private static bool OnOverlap(B2ShapeId shapeId, object context)
    {
        var space = (PhysicsSpace2D)context;
        if (space.TryGetShape(shapeId, out var shape) && shape.Record.Kind != PhysicsBodyKind.Area &&
            !ReferenceEquals(shape.Record, space._queryExclude))
            space._shapeScratch.Add(shapeId);
        return true;
    }

    private void BeginQuery(BodyRecord2D? exclude)
    {
        _queryExclude = exclude;
        _queryFraction = float.MaxValue;
        _queryHit = false;
    }

    internal bool RayCast(Vector2 from, Vector2 to, out RayHit2D hit, uint mask, CollisionObject2D? exclude)
    {
        hit = default;
        var translation = (to - from) / PixelsPerMeter;
        if (translation.LengthSquared() < 1e-14f)
            return false;
        FlushForQuery();
        BeginQuery(exclude?.Record);
        b2World_CastRay(_world, ToB2(from / PixelsPerMeter), ToB2(translation), QueryFilter(mask), RayCallback, this);
        _queryExclude = null;
        if (!_queryHit || !TryGetShape(_queryShape, out var shape))
            return false;
        hit = new RayHit2D(shape.Record.Node, shape.Owner, ToNumerics(_queryPoint) * PixelsPerMeter, ToNumerics(_queryNormal), _queryFraction);
        return true;
    }

    internal bool ShapeCast(in B2ShapeProxy proxy, Vector2 motion, out ShapeCastHit2D hit, uint mask, CollisionObject2D? exclude)
    {
        hit = default;
        if (motion.LengthSquared() < 1e-10f)
            return false;
        FlushForQuery();
        var p = proxy;
        BeginQuery(exclude?.Record);
        b2World_CastShape(_world, ref p, ToB2(motion / PixelsPerMeter), QueryFilter(mask), CastCallback, this);
        _queryExclude = null;
        if (!_queryHit || !TryGetShape(_queryShape, out var shape))
            return false;
        hit = new ShapeCastHit2D(shape.Record.Node, shape.Owner, ToNumerics(_queryPoint) * PixelsPerMeter, ToNumerics(_queryNormal), _queryFraction);
        return true;
    }

    internal int IntersectShape(in B2ShapeProxy proxy, List<CollisionObject2D> results, uint mask, CollisionObject2D? exclude)
    {
        FlushForQuery();
        var p = proxy;
        BeginQuery(exclude?.Record);
        _shapeScratch.Clear();
        b2World_OverlapShape(_world, ref p, QueryFilter(mask), OverlapCallback, this);
        _queryExclude = null;
        var added = 0;
        foreach (var id in _shapeScratch)
        {
            if (TryGetShape(id, out var shape) && !results.Contains(shape.Record.Node))
            {
                results.Add(shape.Record.Node);
                added++;
            }
        }

        _shapeScratch.Clear();
        return added;
    }

    internal int IntersectPoint(Vector2 point, List<CollisionObject2D> results, uint mask, CollisionObject2D? exclude)
    {
        FlushForQuery();
        var p = ToB2(point / PixelsPerMeter);
        var box = new B2AABB(new B2Vec2(p.X - 1e-4f, p.Y - 1e-4f), new B2Vec2(p.X + 1e-4f, p.Y + 1e-4f));
        BeginQuery(exclude?.Record);
        _shapeScratch.Clear();
        b2World_OverlapAABB(_world, box, QueryFilter(mask), OverlapCallback, this);
        _queryExclude = null;
        var added = 0;
        foreach (var id in _shapeScratch)
        {
            if (TryGetShape(id, out var shape) && b2Shape_TestPoint(id, p) && !results.Contains(shape.Record.Node))
            {
                results.Add(shape.Record.Node);
                added++;
            }
        }

        _shapeScratch.Clear();
        return added;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Character queries
    // ------------------------------------------------------------------------------------------------------------

    internal bool CastBody(BodyRecord2D record, Vector2 position, float rotation, Vector2 motion, out BodyCastHit2D hit)
    {
        hit = default;
        if (!record.HasBody || motion.LengthSquared() < 1e-10f)
            return false;
        FlushForQuery();
        var origin = ToB2(position / PixelsPerMeter);
        var rot = b2MakeRot(rotation);
        var translation = ToB2(motion / PixelsPerMeter);
        var best = float.MaxValue;
        var filter = QueryFilter(record.Mask);
        foreach (var shape in record.Shapes)
        {
            var proxy = shape.Geometry.MakeProxy(origin, rot);
            BeginQuery(record);
            b2World_CastShape(_world, ref proxy, translation, filter, CastCallback, this);
            if (!_queryHit || _queryFraction >= best || !TryGetShape(_queryShape, out var other))
                continue;
            best = _queryFraction;
            hit.Fraction = _queryFraction;
            hit.Point = ToNumerics(_queryPoint) * PixelsPerMeter;
            hit.Collider = other.Record.Node;
            var normal = ToNumerics(_queryNormal);
            if (normal.LengthSquared() < 1e-12f && Penetration(shape.Geometry, origin, rot, _queryShape, out var n, out _, out var point))
            {
                normal = n;
                hit.Point = point * PixelsPerMeter;
            }

            hit.Normal = normal.LengthSquared() < 1e-12f ? Vector2.Zero : Vector2.Normalize(normal);
        }

        _queryExclude = null;
        return best <= 1f;
    }

    internal int RecoverBody(BodyRecord2D record, ref Vector2 position, float rotation, float margin, Span<CharacterContact2D> contacts, out bool pushed)
    {
        pushed = false;
        if (!record.HasBody)
            return 0;
        FlushForQuery();
        if (record.Shapes.Count == 0)
            return 0;
        var rot = b2MakeRot(rotation);
        var found = 0;
        var contactDistance = margin * 2 / PixelsPerMeter;
        foreach (var shape in record.Shapes)
        {
            var origin = ToB2(position / PixelsPerMeter);
            var proxy = shape.Geometry.MakeProxy(origin, rot);
            proxy.radius += contactDistance; // candidates within the contact distance
            BeginQuery(record);
            _shapeScratch.Clear();
            b2World_OverlapShape(_world, ref proxy, QueryFilter(record.Mask), OverlapCallback, this);
            _queryExclude = null;
            foreach (var otherId in _shapeScratch)
            {
                if (!TryGetShape(otherId, out var other))
                    continue;
                if (!Penetration(shape.Geometry, ToB2(position / PixelsPerMeter), rot, otherId, out var normal, out var separation, out var point))
                    continue;
                var separationPx = separation * PixelsPerMeter;
                if (separationPx >= margin * 2)
                    continue;
                var push = MathF.Max(0f, margin - separationPx);
                position += normal * push;
                pushed |= push > 0;
                if (found < contacts.Length)
                    contacts[found++] = new CharacterContact2D { Normal = normal, Point = point * PixelsPerMeter, Collider = other.Record.Node };
            }

            _shapeScratch.Clear();
        }

        return found;
    }

    /// <summary>
    /// Separation (metres; negative when penetrating) and the normal pushing our geometry away from shape
    /// <paramref name="otherId"/>, from Box2D's manifold functions (which also report nearby, speculative contacts).
    /// </summary>
    private static bool Penetration(in ShapeGeometry2D own, B2Vec2 position, B2Rot rotation, B2ShapeId otherId,
        out Vector2 normal, out float separation, out Vector2 point)
    {
        normal = default;
        separation = 0;
        point = default;
        var xfB = new B2Transform(position, rotation);
        var xfA = b2Body_GetTransform(b2Shape_GetBody(otherId));
        var ownGeometry = own;
        B2Manifold m;
        var flip = false; // the functions put the other shape first (A); flipped calls put ours first
        switch (b2Shape_GetType(otherId), own.Type)
        {
            case (B2ShapeType.b2_circleShape, B2ShapeType.b2_circleShape):
                m = b2CollideCircles(b2Shape_GetCircle(otherId), xfA, own.Circle, xfB);
                break;
            case (B2ShapeType.b2_circleShape, B2ShapeType.b2_capsuleShape):
                m = b2CollideCapsuleAndCircle(own.Capsule, xfB, b2Shape_GetCircle(otherId), xfA);
                flip = true;
                break;
            case (B2ShapeType.b2_circleShape, B2ShapeType.b2_polygonShape):
                m = b2CollidePolygonAndCircle(ref ownGeometry.Polygon, xfB, b2Shape_GetCircle(otherId), xfA);
                flip = true;
                break;
            case (B2ShapeType.b2_capsuleShape, B2ShapeType.b2_circleShape):
                m = b2CollideCapsuleAndCircle(b2Shape_GetCapsule(otherId), xfA, own.Circle, xfB);
                break;
            case (B2ShapeType.b2_capsuleShape, B2ShapeType.b2_capsuleShape):
                m = b2CollideCapsules(b2Shape_GetCapsule(otherId), xfA, own.Capsule, xfB);
                break;
            case (B2ShapeType.b2_capsuleShape, B2ShapeType.b2_polygonShape):
                m = b2CollidePolygonAndCapsule(ref ownGeometry.Polygon, xfB, b2Shape_GetCapsule(otherId), xfA);
                flip = true;
                break;
            case (B2ShapeType.b2_polygonShape, B2ShapeType.b2_circleShape):
                {
                    var polygon = b2Shape_GetPolygon(otherId);
                    m = b2CollidePolygonAndCircle(ref polygon, xfA, own.Circle, xfB);
                    break;
                }
            case (B2ShapeType.b2_polygonShape, B2ShapeType.b2_capsuleShape):
                {
                    var polygon = b2Shape_GetPolygon(otherId);
                    m = b2CollidePolygonAndCapsule(ref polygon, xfA, own.Capsule, xfB);
                    break;
                }
            case (B2ShapeType.b2_polygonShape, B2ShapeType.b2_polygonShape):
                {
                    var polygon = b2Shape_GetPolygon(otherId);
                    m = b2CollidePolygons(ref polygon, xfA, ref ownGeometry.Polygon, xfB);
                    break;
                }
            case (B2ShapeType.b2_segmentShape, B2ShapeType.b2_circleShape):
                m = b2CollideSegmentAndCircle(b2Shape_GetSegment(otherId), xfA, own.Circle, xfB);
                break;
            case (B2ShapeType.b2_segmentShape, B2ShapeType.b2_capsuleShape):
                m = b2CollideSegmentAndCapsule(b2Shape_GetSegment(otherId), xfA, own.Capsule, xfB);
                break;
            case (B2ShapeType.b2_segmentShape, B2ShapeType.b2_polygonShape):
                m = b2CollideSegmentAndPolygon(b2Shape_GetSegment(otherId), xfA, ref ownGeometry.Polygon, xfB);
                break;
            default:
                return false; // segment characters and chain shapes are not supported
        }

        if (m.pointCount == 0)
            return false;
        separation = float.MaxValue;
        for (var i = 0; i < m.pointCount; i++)
        {
            ref var p = ref m.points[i];
            if (p.separation < separation)
            {
                separation = p.separation;
                point = ToNumerics(p.point);
            }
        }

        // The manifold normal points from shape A to shape B: from the other shape towards ours unless flipped.
        normal = ToNumerics(m.normal);
        if (flip)
            normal = -normal;
        return normal.LengthSquared() > 1e-12f;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Debug draw
    // ------------------------------------------------------------------------------------------------------------

    private static readonly Vector4 StaticColor = new(0.35f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 DynamicColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 SleepingColor = new(0.55f, 0.55f, 0.6f, 1f);
    private static readonly Vector4 KinematicColor = new(0.35f, 0.65f, 1f, 1f);
    private static readonly Vector4 AreaColor = new(0.2f, 0.9f, 0.9f, 0.7f);

    internal void DrawDebug(DebugLines lines)
    {
        foreach (var record in _records)
        {
            var color = record.Kind switch
            {
                PhysicsBodyKind.Static => StaticColor,
                PhysicsBodyKind.Dynamic => record.IsSleeping ? SleepingColor : DynamicColor,
                PhysicsBodyKind.Area => AreaColor,
                _ => KinematicColor,
            };
            var node = record.Node;
            for (var i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is CollisionShape2D { Disabled: false, Shape: { } shape } owner && owner.IsVisibleInTree())
                    shape.DrawDebug(lines, owner.GlobalTransform, color);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var record in _records)
        {
            record.Removed = true;
            record.Node.Record = null;
            record.HasBody = false;
        }

        _records.Clear();
        _moving.Clear();
        _areas.Clear();
        _monitors.Clear();
        _dirty.Clear();
        _shapes.Clear();
        if (ReferenceEquals(World2D.PhysicsSpace, this))
            World2D.PhysicsSpace = null;
        b2DestroyWorld(_world);
    }
}
