using System.Numerics;
using System.Runtime.CompilerServices;
using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.DataStructures;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>A Jitter2 backend body, the tag of its <see cref="RigidBody"/>. Areas have no Jitter2 body: they hold shapes and a pose, queried after each step.</summary>
internal sealed class JitterBody(int index)
{
    public readonly int Index = index;
    public RigidBody? Body;
    public PhysicsBodyKind Kind;
    public uint Layer;
    public uint Mask;
    public float GravityScale = 1f;
    public bool GravityScaled;
    public Vector3 AreaPosition;
    public Quaternion AreaRotation = Quaternion.Identity;

    /// <summary>The built Jitter2 shapes (a concave description adds one triangle shape per triangle).</summary>
    public readonly List<RigidBodyShape> Shapes = [];

    /// <summary>For each entry of <see cref="Shapes"/>, the index of the description it was built from.</summary>
    public readonly List<int> ShapeDescriptions = [];

    public BackendBody Handle => new(Index);

    public bool IsArea => Kind == PhysicsBodyKind.Area;
}

/// <summary>
/// The Jitter2 2.9 backend (B2): one Jitter2 <see cref="World"/> per space. Jitter2-specific behaviour lives here: the
/// broad-phase filter over <see cref="PhysicsSpace3D.Collides"/>, the sphere contact filter (ADR 0134), the gravity-scale
/// force (Jitter2 has on/off gravity only), contact caches dropped after filter changes, areas as shape sets queried by
/// tree + narrow phase, and the EPA → distance → MPR chain of the character queries (ADR 0128) with Jitter2's log muted.
/// Shapes are built per body (a Jitter2 shape is one body's tree proxy, never shared); query shapes are cached per
/// resource and <see cref="Shape3D.ChangeCount"/> in a weak table (a game's throwaway query shapes are not kept alive).
/// </summary>
internal sealed class Jitter2Backend : PhysicsBackend3D
{
    private readonly World _world;
    private readonly PhysicsSettings3D _settings;
    private readonly List<JitterBody?> _bodies = [null]; // index 0 is BackendBody.None
    private readonly List<int> _free = [];
    private readonly List<JitterBody> _gravityScaled = [];
    private readonly Dictionary<RigidBodyShape, int> _shapeIndex = []; // an attached shape → its index in JitterBody.Shapes
    private readonly List<RigidBodyShape> _shapeScratch = [];
    private readonly List<IDynamicTreeProxy> _proxies = [];
    private readonly ConditionalWeakTable<Shape3D, QueryShape> _queryShapes = new();
    private readonly QueryFilters _filters = new();
    private bool _disposed;

    // PhysicsSpace3D.cs:197-206
    public Jitter2Backend(PhysicsSettings3D settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JitterLog.EnsureInstalled();
        _settings = settings; // MultiThreaded is read at every step, as before
        _world = new World
        {
            Gravity = settings.Gravity.ToJ(),
            SubstepCount = settings.SubstepCount,
            SolverIterations = (settings.Jitter2.SolverIterations, settings.Jitter2.RelaxationIterations),
            AllowDeactivation = settings.AllowDeactivation,
            BroadPhaseFilter = new LayerFilter(),
            SolveMode = settings.Deterministic ? SolveMode.Deterministic : SolveMode.Regular,
        };
        _world.NarrowPhaseFilter = new SphereContactFilter(_world, _world.NarrowPhaseFilter);
    }

    public override PhysicsBackendKind Kind => PhysicsBackendKind.Jitter2;

    public override Vector3 Gravity
    {
        get => _world.Gravity.ToNumerics();
        set => _world.Gravity = value.ToJ();
    }

    public override int ActiveBodyCount => Math.Max(0, _world.RigidBodies.ActiveCount);

    private JitterBody Slot(BackendBody body) =>
        (uint)body.Index < (uint)_bodies.Count && _bodies[body.Index] is { } slot
            ? slot
            : throw new InvalidOperationException($"No Jitter2 backend body {body.Index}.");

    private JitterBody? SlotOrNull(BackendBody body) => (uint)body.Index < (uint)_bodies.Count ? _bodies[body.Index] : null;

    // ---- world (PhysicsSpace3D.cs:700-709 and the gravity force of 747-755) ---------------------------------------

    public override void Step(float dt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gravityScaled.Count > 0)
        {
            // Jitter2 has on/off gravity only: a scale other than 0 or 1 opts out of it and gets mass · g · scale.
            var gravity = _world.Gravity.ToNumerics();
            foreach (var slot in _gravityScaled)
            {
                if (slot.Body is { IsActive: true } body)
                    body.AddForce((gravity * (slot.GravityScale * body.Mass)).ToJ(), wakeup: false);
            }
        }

        if (PhysicsSpace3D.MeasureLibraryAllocations)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _world.Step(dt, _settings.MultiThreaded);
            LibraryAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        else
        {
            _world.Step(dt, _settings.MultiThreaded);
        }
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var slot in _bodies)
        {
            if (slot is null)
                continue;
            slot.Body = null;
            slot.Shapes.Clear();
            slot.ShapeDescriptions.Clear();
        }

        _bodies.Clear();
        _free.Clear();
        _gravityScaled.Clear();
        _shapeIndex.Clear();
        _world.Dispose();
    }

    // ---- bodies (PhysicsSpace3D.cs:266-288, 306-316, 456-483, 574-642, BodyRecord3D.ApplyMaterial 140-152) -----------

    public override BackendBody CreateBody(in BodyDescription3D desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index;
        if (_free.Count > 0)
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            index = _bodies.Count;
            _bodies.Add(null);
        }

        var slot = new JitterBody(index) { Kind = desc.Kind, Layer = desc.Layer, Mask = desc.Mask };
        _bodies[index] = slot;
        if (desc.Kind == PhysicsBodyKind.Area)
        {
            slot.AreaPosition = desc.Position;
            slot.AreaRotation = desc.Rotation;
            return slot.Handle;
        }

        var body = _world.CreateRigidBody();
        body.Tag = slot;
        body.Position = desc.Position.ToJ();
        body.Orientation = desc.Rotation.ToJ();
        slot.Body = body;
        ApplyKind(slot);
        SetProperties(slot.Handle, desc.Properties);
        body.Friction = desc.Friction;
        body.Restitution = desc.Bounce;
        return slot.Handle;
    }

    public override void DestroyBody(BackendBody body)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            foreach (var shape in slot.Shapes)
                _shapeIndex.Remove(shape);
            if (!_disposed)
                _world.Remove(rigid);
            slot.Body = null;
        }

        if (slot.GravityScaled)
        {
            slot.GravityScaled = false;
            _gravityScaled.Remove(slot);
        }

        slot.Shapes.Clear();
        slot.ShapeDescriptions.Clear();
        _bodies[slot.Index] = null;
        _free.Add(slot.Index);
    }

    public override void SetKind(BackendBody body, PhysicsBodyKind kind)
    {
        var slot = Slot(body);
        slot.Kind = kind;
        if (slot.Body is not null)
            ApplyKind(slot);
    }

    private static void ApplyKind(JitterBody slot) =>
        slot.Body!.MotionType = slot.Kind switch
        {
            PhysicsBodyKind.Static => MotionType.Static,
            PhysicsBodyKind.Dynamic => MotionType.Dynamic,
            _ => MotionType.Kinematic,
        };

    public override void SetProperties(BackendBody body, in BodyProperties3D props)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            if (props.Simulated)
            {
                rigid.AffectedByGravity = props.GravityScale == 1f;
                rigid.Damping = (props.LinearDampingPerStep, props.AngularDampingPerStep);
                rigid.EnableSpeculativeContacts = props.Ccd;
                rigid.DeactivationTime = props.CanSleep ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1e7);
                rigid.AllowedMotion = (MotionAxes)((int)MotionAxes.All & ~(int)props.AxisLocks);
            }
            else
            {
                rigid.AffectedByGravity = false;
                rigid.AllowedMotion = MotionAxes.All; // locks are a dynamic-body feature; kinematic moves must reach their target
            }
        }

        slot.GravityScale = props.GravityScale;
        var scaled = props.Simulated && props.GravityScale is not 1f and not 0f;
        if (scaled == slot.GravityScaled)
            return;
        slot.GravityScaled = scaled;
        if (scaled)
            _gravityScaled.Add(slot);
        else
            _gravityScaled.Remove(slot);
    }

    public override void SetMaterial(BackendBody body, float friction, float bounce)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        rigid.Friction = friction;
        rigid.Restitution = bounce;
    }

    public override void SetFilter(BackendBody body, uint layer, uint mask)
    {
        var slot = Slot(body);
        slot.Layer = layer;
        slot.Mask = mask;
    }

    public override void SetShapes(BackendBody body, ReadOnlySpan<ShapeDescription3D> shapes, float dynamicMass, List<ShapeError3D> errors)
    {
        var slot = Slot(body);
        var rigid = slot.Body;
        if (rigid is not null && slot.Shapes.Count > 0)
        {
            foreach (var shape in slot.Shapes)
                _shapeIndex.Remove(shape);
            rigid.ClearShapes(MassInertiaUpdateMode.Preserve);
        }

        slot.Shapes.Clear();
        slot.ShapeDescriptions.Clear();
        for (var i = 0; i < shapes.Length; i++)
        {
            _shapeScratch.Clear();
            try
            {
                Build(shapes[i], _shapeScratch);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Degenerate data (coplanar hull points, zero scale, ...): the shape is skipped, the body still works.
                errors.Add(new ShapeError3D(i, e.Message));
                _shapeScratch.Clear();
                continue;
            }

            foreach (var created in _shapeScratch)
            {
                if (rigid is not null)
                {
                    _shapeIndex[created] = slot.Shapes.Count;
                    rigid.AddShape(created, MassInertiaUpdateMode.Preserve);
                }

                slot.Shapes.Add(created);
                slot.ShapeDescriptions.Add(i);
            }
        }

        _shapeScratch.Clear();
        if (rigid is not null && slot.Kind == PhysicsBodyKind.Dynamic && dynamicMass > 0f)
        {
            try
            {
                rigid.SetMassInertia(dynamicMass);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                errors.Add(new ShapeError3D(-1, e.Message));
                rigid.SetMassInertia(JSymmetricMatrix.Identity, dynamicMass);
            }
        }

        if (rigid is not null && slot.Kind != PhysicsBodyKind.Static)
            rigid.SetActivationState(true);
    }

    // Shape3D.CreateShapes/CreateBaseShape (Shapes3D.cs:24-34, 82, 106, 148, 187, 217-225) and AddTriangleShapes (280-296).
    private static void Build(in ShapeDescription3D d, List<RigidBodyShape> output)
    {
        if (d.Kind is ShapeKind3D.ConcaveMesh or ShapeKind3D.HeightMap)
        {
            AddTriangleShapes(output, d.Points!, d.Linear, d.Translation);
            return;
        }

        var shape = CreateConvex(d);
        if (JitterMath.IsIdentity(d.Linear) && d.Translation == Vector3.Zero)
            output.Add(shape);
        else
            output.Add(new TransformedShape(shape, d.Translation.ToJ(), d.Linear.ToJ()));
    }

    private static RigidBodyShape CreateConvex(in ShapeDescription3D d) => d.Kind switch
    {
        ShapeKind3D.Box => new BoxShape(d.Size.ToJ()),
        ShapeKind3D.Sphere => new SphereShape(d.Radius),
        ShapeKind3D.Capsule => new CapsuleShape(d.Radius, d.MidHeight),
        ShapeKind3D.Cylinder => new CylinderShape(d.Height, d.Radius), // Jitter2's argument order: (height, radius)
        ShapeKind3D.ConvexHull => CreatePointCloud(d.Points!),
        _ => throw new NotSupportedException($"{d.Resource.GetType().Name} has no single convex base shape."),
    };

    private static PointCloudShape CreatePointCloud(Vector3[] source)
    {
        Span<JVector> points = source.Length <= 256 ? stackalloc JVector[source.Length] : new JVector[source.Length];
        for (var i = 0; i < source.Length; i++)
            points[i] = source[i].ToJ();
        return new PointCloudShape(points);
    }

    private static void AddTriangleShapes(List<RigidBodyShape> output, ReadOnlySpan<Vector3> faces, in Basis linear, Vector3 translation)
    {
        if (faces.Length < 3)
            return;
        var soup = new JTriangle[faces.Length / 3];
        for (var i = 0; i < soup.Length; i++)
        {
            soup[i] = new JTriangle(
                (linear.Transform(faces[i * 3]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 1]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 2]) + translation).ToJ());
        }

        var mesh = new TriangleMesh(soup, ignoreDegenerated: true);
        for (var i = 0; i < mesh.Indices.Length; i++)
            output.Add(new TriangleShape(mesh, i));
    }

    // ---- state (BodyRecord3D 44-122, PhysicsSpace3D.cs:489-497, 559-563, 727-744, 791-806) ---------------------------

    public override void GetPose(BackendBody body, out Vector3 position, out Quaternion rotation)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            position = rigid.Position.ToNumerics();
            rotation = rigid.Orientation.ToNumerics();
        }
        else
        {
            position = slot.AreaPosition;
            rotation = slot.AreaRotation;
        }
    }

    public override void SetPose(BackendBody body, Vector3 position, Quaternion rotation)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            rigid.Position = position.ToJ();
            rigid.Orientation = rotation.ToJ();
        }
        else
        {
            slot.AreaPosition = position;
            slot.AreaRotation = rotation;
        }
    }

    public override void Teleport(BackendBody body, Vector3 position, Quaternion rotation)
    {
        SetPose(body, position, rotation);
        var slot = Slot(body);
        if (slot.Body is { } rigid && slot.Kind != PhysicsBodyKind.Static)
            rigid.SetActivationState(true);
    }

    public override void MoveKinematic(BackendBody body, Vector3 position, Quaternion rotation, float dt)
    {
        // Reach the target pose in exactly one step: contacts see a moving body, not a teleport.
        var rigid = Slot(body).Body!;
        var inverseDelta = 1f / dt;
        var current = rigid.Position.ToNumerics();
        var orientation = rigid.Orientation.ToNumerics();
        rigid.Velocity = ((position - current) * inverseDelta).ToJ();
        rigid.AngularVelocity = (PhysicsSpace3D.AngularVelocityBetween(orientation, rotation) * inverseDelta).ToJ();
    }

    public override Vector3 GetLinearVelocity(BackendBody body) => Slot(body).Body is { } rigid ? rigid.Velocity.ToNumerics() : Vector3.Zero;

    public override void SetLinearVelocity(BackendBody body, Vector3 velocity)
    {
        if (Slot(body).Body is { } rigid)
            rigid.Velocity = velocity.ToJ();
    }

    public override Vector3 GetAngularVelocity(BackendBody body) => Slot(body).Body is { } rigid ? rigid.AngularVelocity.ToNumerics() : Vector3.Zero;

    public override void SetAngularVelocity(BackendBody body, Vector3 velocity)
    {
        if (Slot(body).Body is { } rigid)
            rigid.AngularVelocity = velocity.ToJ();
    }

    public override void ApplyForce(BackendBody body, Vector3 force, Vector3? position)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        if (position is { } at)
            rigid.AddForce(force.ToJ(), at.ToJ());
        else
            rigid.AddForce(force.ToJ());
    }

    public override void ApplyImpulse(BackendBody body, Vector3 impulse, Vector3? position)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        if (position is { } at)
            rigid.ApplyImpulse(impulse.ToJ(), at.ToJ());
        else
            rigid.ApplyImpulse(impulse.ToJ());
    }

    public override void ApplyTorque(BackendBody body, Vector3 torque)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        rigid.Torque += torque.ToJ();
        rigid.SetActivationState(true);
    }

    public override bool IsSleeping(BackendBody body) => Slot(body).Body is { IsActive: false };

    public override void Wake(BackendBody body) => Slot(body).Body?.SetActivationState(true);

    public override void WakeTouching(BackendBody body)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        foreach (var arbiter in rigid.Contacts)
        {
            var other = ReferenceEquals(arbiter.Body1, rigid) ? arbiter.Body2 : arbiter.Body1;
            if (other.MotionType != MotionType.Static)
                other.SetActivationState(true);
        }
    }

    // Jitter2 keeps resting contacts until they break: drop them so a new filter applies now.
    public override void ClearContacts(BackendBody body) => Slot(body).Body?.ClearContactCache();

    // ---- scans (PhysicsSpace3D.cs:814-881) -----------------------------------------------------------------------

    public override void CollectTouching(BackendBody body, List<BackendBody> others)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        foreach (var arbiter in rigid.Contacts)
        {
            var other = ReferenceEquals(arbiter.Body1, rigid) ? arbiter.Body2 : arbiter.Body1;
            if (other.Tag is not JitterBody otherSlot)
                continue;
            ref var data = ref arbiter.Handle.Data;
            if ((data.UsageMask & ContactData.MaskContactAll) == 0)
                continue; // all contact points broken this step
            others.Add(otherSlot.Handle);
        }
    }

    public override bool CollectOverlaps(BackendBody area, List<BackendBody> others)
    {
        var slot = Slot(area);
        if (slot.Shapes.Count == 0)
            return false;
        var position = slot.AreaPosition.ToJ();
        var rotation = slot.AreaRotation.ToJ();
        var box = Bounds(slot.Shapes, rotation, position);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        foreach (var proxy in _proxies)
        {
            if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody other } otherBody } otherShape ||
                !PhysicsSpace3D.Collides(slot.Layer, slot.Mask, true, other.Layer, other.Mask, other.IsArea) || others.Contains(other.Handle))
                continue;
            var otherPosition = otherBody.Position;
            var otherRotation = otherBody.Orientation;
            foreach (var areaShape in slot.Shapes)
            {
                if (NarrowPhase.Overlap(areaShape, otherShape, rotation, otherRotation, position, otherPosition))
                {
                    others.Add(other.Handle);
                    break;
                }
            }
        }

        _proxies.Clear();
        return true;
    }

    private static JBoundingBox Bounds(List<RigidBodyShape> shapes, in JQuaternion rotation, in JVector position)
    {
        var box = JBoundingBox.SmallBox;
        for (var i = 0; i < shapes.Count; i++)
        {
            shapes[i].CalculateBoundingBox(rotation, position, out var shapeBox);
            box = i == 0 ? shapeBox : JBoundingBox.CreateMerged(box, shapeBox);
        }

        return box;
    }

    // ---- queries (PhysicsDirectSpaceState3D.cs:47-166) -------------------------------------------------------------

    private int ShapeIndexOf(JitterBody slot, RigidBodyShape shape) => _shapeIndex.TryGetValue(shape, out var i) ? slot.ShapeDescriptions[i] : -1;

    private RigidBodyShape GetQueryShape(in ShapeDescription3D d)
    {
        if (_queryShapes.TryGetValue(d.Resource, out var cached) && cached.ChangeCount == d.ChangeCount)
            return cached.Shape;
        var shape = CreateConvex(d);
        _queryShapes.AddOrUpdate(d.Resource, new QueryShape(d.ChangeCount, shape));
        return shape;
    }

    public override bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit)
    {
        hit = default;
        _filters.Begin(mask, SlotOrNull(exclude));
        try
        {
            if (!_world.DynamicTree.RayCast(from.ToJ(), motion.ToJ(), 1f, _filters.RayPre, hitFromInside ? null : _filters.RayPostOutside, out var proxy,
                    out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: JitterBody slot } shape)
                return false;
            hit.Body = slot.Handle;
            hit.ShapeIndex = ShapeIndexOf(slot, shape);
            hit.Position = from + motion * lambda;
            hit.Normal = normal.ToNumerics();
            hit.Fraction = lambda;
            return true;
        }
        finally
        {
            _filters.End();
        }
    }

    public override bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit)
    {
        hit = default;
        var query = GetQueryShape(shape);
        _filters.Begin(mask, SlotOrNull(exclude));
        try
        {
            if (!_world.DynamicTree.SweepCast(query, rotation.ToJ(), position.ToJ(), motion.ToJ(), 1f, _filters.SweepPre, null,
                    out var proxy, out _, out var pointB, out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: JitterBody slot } hitShape)
                return false;
            hit.Body = slot.Handle;
            hit.ShapeIndex = ShapeIndexOf(slot, hitShape);
            hit.Position = pointB.ToNumerics();
            hit.Normal = normal.ToNumerics() == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(-normal.ToNumerics());
            hit.Fraction = lambda;
            return true;
        }
        finally
        {
            _filters.End();
        }
    }

    public override void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output)
    {
        var query = GetQueryShape(shape);
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        query.CalculateBoundingBox(rotationJ, positionJ, out var box);
        var excluded = SlotOrNull(exclude);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        try
        {
            foreach (var proxy in _proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody slot } body } other || ReferenceEquals(slot, excluded) ||
                    (slot.Layer & mask) == 0 || output.Contains(slot.Handle))
                    continue;
                if (NarrowPhase.Overlap(query, other, rotationJ, body.Orientation, positionJ, body.Position))
                    output.Add(slot.Handle);
            }
        }
        finally
        {
            _proxies.Clear();
        }
    }

    public override void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output)
    {
        var p = point.ToJ();
        var box = new JBoundingBox(p - new JVector(1e-4f), p + new JVector(1e-4f));
        var excluded = SlotOrNull(exclude);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        try
        {
            foreach (var proxy in _proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody slot } body } other || ReferenceEquals(slot, excluded) ||
                    (slot.Layer & mask) == 0 || output.Contains(slot.Handle))
                    continue;
                if (NarrowPhase.PointTest(other, JMatrix.CreateFromQuaternion(body.Orientation), body.Position, p))
                    output.Add(slot.Handle);
            }
        }
        finally
        {
            _proxies.Clear();
        }
    }

    // ---- character primitives (PhysicsSpace3D.cs:959-1133) ----------------------------------------------------------

    /// <summary>
    /// Jitter2's sweep reports contacts within a few millimetres as "already touching" (fraction 0, no normal); the normal
    /// then comes from a distance (or penetration) query against the hit shape.
    /// </summary>
    public override bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit)
    {
        hit = default;
        var slot = Slot(body);
        if (slot.Body is null)
            return false;
        using var mute = new JitterLogMute();
        _filters.Begin(slot.Mask, slot);
        var best = float.MaxValue;
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        var motionJ = motion.ToJ();
        try
        {
            foreach (var shape in slot.Shapes)
            {
                if (!_world.DynamicTree.SweepCast(shape, rotationJ, positionJ, motionJ, 1f, _filters.SweepPre, null,
                        out var proxy, out _, out var pointB, out var normal, out var lambda) || lambda >= best)
                    continue;
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody other } otherBody } otherShape)
                    continue;
                best = lambda;
                hit.Fraction = lambda;
                hit.Position = pointB.ToNumerics();
                hit.Body = other.Handle;
                hit.ShapeIndex = ShapeIndexOf(other, otherShape);
                // Jitter2's sweep normal points from the swept shape towards the hit one; surfaces face the other way.
                var surface = -normal.ToNumerics();
                if (surface.LengthSquared() < 1e-12f &&
                    !ContactNormal(shape, otherShape, otherBody, rotationJ, positionJ, out surface, out hit.Position))
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

    // PhysicsSpace3D.cs:1001-1017, unchanged.
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

    public override int GetShapeCount(BackendBody body) => Slot(body).Shapes.Count;

    public override void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output)
    {
        var slot = Slot(body);
        if (slot.Body is not { } self || slot.Shapes.Count == 0)
            return;
        var box = Bounds(slot.Shapes, rotation.ToJ(), position.ToJ());
        box.Min -= new JVector(expand);
        box.Max += new JVector(expand);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        foreach (var proxy in _proxies)
        {
            if (proxy is not RigidBodyShape { RigidBody: { } otherBody } otherShape || ReferenceEquals(otherBody, self) ||
                otherBody.Tag is not JitterBody other || (slot.Mask & other.Layer) == 0 || !_shapeIndex.TryGetValue(otherShape, out var index))
                continue;
            output.Add(new BackendShapeRef(other.Handle, index));
        }

        _proxies.Clear();
    }

    /// <summary>
    /// EPA first (ADR 0128): one signed distance for touching, separated and overlapping shapes, accurate to well under a
    /// millimetre on large shapes where GJK and MPR answer near contact are off by millimetres; then GJK distance, then MPR.
    /// </summary>
    public override bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact)
    {
        using var mute = new JitterLogMute();
        var self = Slot(body).Shapes[shape];
        var target = Slot(other.Body);
        var otherBody = target.Body!;
        var otherShape = target.Shapes[other.Shape];
        var rotationJ = rotation.ToJ();
        if (NarrowPhase.Collision(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out var pointB, out var normal, out var epaDepth))
        {
            contact = new BackendContact3D(-epaDepth, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        if (NarrowPhase.Distance(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out pointB, out normal, out var distance))
        {
            contact = new BackendContact3D(distance, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        if (NarrowPhase.MprEpa(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out pointB, out normal, out var penetration) && penetration >= 0)
        {
            contact = new BackendContact3D(-penetration, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        contact = default;
        return false;
    }

    /// <summary>EPA's exact signed distance over every shape pair (ADR 0128: Jitter2's sweeps stop a few millimetres short on large shapes).</summary>
    public override bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;
        var self = Slot(body);
        var target = Slot(other);
        if (target.Body is not { } otherBody)
            return false;
        using var mute = new JitterLogMute();
        var rotationJ = rotation.ToJ();
        var best = float.MaxValue;
        var bestNormal = JVector.Zero;
        foreach (var shape in self.Shapes)
            foreach (var otherShape in target.Shapes)
                if (NarrowPhase.Collision(shape, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                        out _, out _, out var n, out var depth) && -depth < best)
                {
                    best = -depth; // separation
                    bestNormal = n;
                }

        if (best == float.MaxValue)
            return false;
        distance = best;
        normal = bestNormal.ToNumerics(); // from the body towards the other shape
        return true;
    }

    // ---- filters (PhysicsSpace3D.cs:1193-1247) ---------------------------------------------------------------------

    /// <summary>Collision layers on Jitter2's broad phase. Runs on worker threads: it only reads tags, changed on the main thread between steps.</summary>
    private sealed class LayerFilter : IBroadPhaseFilter
    {
        public bool Filter(IDynamicTreeProxy proxyA, IDynamicTreeProxy proxyB)
        {
            if (proxyA is not RigidBodyShape { RigidBody.Tag: JitterBody a } || proxyB is not RigidBodyShape { RigidBody.Tag: JitterBody b })
                return true;
            return PhysicsSpace3D.Collides(a.Layer, a.Mask, a.IsArea, b.Layer, b.Mask, b.IsArea);
        }
    }

    /// <summary>Cached query filter delegates over mutable state (no per-query allocation).</summary>
    private sealed class QueryFilters
    {
        private uint _mask;
        private JitterBody? _exclude;

        public QueryFilters()
        {
            RayPre = Accept;
            SweepPre = Accept;
            RayPostOutside = static result => result.Normal != JVector.Zero;
        }

        public DynamicTree.RayCastFilterPre RayPre { get; }

        /// <summary>Rejects shapes that contain the ray's origin (Jitter2 reports them with a zero normal).</summary>
        public DynamicTree.RayCastFilterPost RayPostOutside { get; }

        public DynamicTree.SweepCastFilterPre SweepPre { get; }

        public void Begin(uint mask, JitterBody? exclude)
        {
            _mask = mask;
            _exclude = exclude;
        }

        public void End() => _exclude = null;

        private bool Accept(IDynamicTreeProxy proxy) =>
            proxy is RigidBodyShape { RigidBody.Tag: JitterBody slot } && !ReferenceEquals(slot, _exclude) && (slot.Layer & _mask) != 0;
    }

    private sealed class QueryShape(int changeCount, RigidBodyShape shape)
    {
        public readonly int ChangeCount = changeCount;
        public readonly RigidBodyShape Shape = shape;
    }

    /// <summary>Collects tree query results into a reused list.</summary>
    private readonly struct ProxySink(List<IDynamicTreeProxy> list) : ISink<IDynamicTreeProxy>
    {
        public void Add(in IDynamicTreeProxy item) => list.Add(item);
    }
}
