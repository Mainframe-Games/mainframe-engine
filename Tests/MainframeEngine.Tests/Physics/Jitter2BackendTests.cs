using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>The Jitter2 backend on its own, through the members the space uses (B2).</summary>
public sealed class Jitter2BackendTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private static readonly BodyProperties3D Simulated = new(true, 0f, 0f, 1f, AxisLock.None, false, true);
    private readonly Jitter2Backend _backend = new(new PhysicsSettings3D { MultiThreaded = false });
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];

    public void Dispose() => _backend.Dispose();

    private BackendBody Create(PhysicsBodyKind kind, Vector3 position, uint layer = 1, uint mask = 1) =>
        _backend.CreateBody(new BodyDescription3D(kind, position, Quaternion.Identity, layer, mask,
            kind == PhysicsBodyKind.Dynamic ? Simulated : default, PhysicsMaterial.DefaultFriction, 0f));

    private void SetShapes(BackendBody body, PhysicsBodyKind kind)
    {
        _errors.Clear();
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), kind == PhysicsBodyKind.Dynamic ? 1f : 0f, _errors);
        _shapes.Clear();
    }

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, Shape3D shape, uint layer = 1, uint mask = 1)
    {
        var body = Create(kind, position, layer, mask);
        shape.Describe(_shapes, Basis.Identity, Vector3.Zero);
        SetShapes(body, kind);
        Assert.Empty(_errors);
        return body;
    }

    private BackendBody Floor() => Add(PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0), new BoxShape3D { Size = new Vector3(40, 1, 40) });

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
            _backend.Step(Dt);
    }

    [Fact]
    public void HandlesStartAtOneAndFreedOnesAreReused()
    {
        Assert.True(default(BackendBody).IsNone);
        var a = Floor();
        var b = Add(PhysicsBodyKind.Static, new Vector3(5, 0, 0), new BoxShape3D());
        Assert.Equal((1, 2), (a.Index, b.Index));
        _backend.DestroyBody(a);
        Assert.Equal(1, Add(PhysicsBodyKind.Static, new Vector3(9, 0, 0), new BoxShape3D()).Index);
    }

    [Fact]
    public void ADynamicBoxFallsRestsAndSleepsOnAStaticFloor()
    {
        Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 2, 0), new BoxShape3D());
        Run(20);
        Assert.True(_backend.GetLinearVelocity(box).Y < 0);
        Run(300);
        _backend.GetPose(box, out var position, out _);
        Assert.InRange(position.Y, 0.45f, 0.55f);
        Assert.True(_backend.IsSleeping(box));
        var asleep = _backend.ActiveBodyCount;
        _backend.Wake(box);
        Run(1); // Jitter2 activates a woken body at the next step (SetActivationState), as the engine always has
        Assert.False(_backend.IsSleeping(box));
        Assert.Equal(asleep + 1, _backend.ActiveBodyCount);
    }

    [Fact]
    public void RaysReportBodyAndShapeIndexAndHonourMaskAndExclude()
    {
        var floor = Floor();
        Assert.True(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, default, true, out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.Equal(0, hit.ShapeIndex);
        Assert.Equal(0f, hit.Position.Y, 3);
        Assert.Equal(Vector3.UnitY, hit.Normal);
        Assert.Equal(0.5f, hit.Fraction, 3);
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 2, default, true, out _));
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, floor, true, out _));

        var compound = Create(PhysicsBodyKind.Static, new Vector3(5, 2, 0));
        new BoxShape3D().Describe(_shapes, Basis.Identity, new Vector3(-1, 0, 0));
        new SphereShape3D().Describe(_shapes, Basis.Identity, new Vector3(1, 0, 0));
        SetShapes(compound, PhysicsBodyKind.Static);
        Assert.True(_backend.RayCast(new Vector3(6, 5, 0), new Vector3(0, -10, 0), 1, default, true, out hit));
        Assert.Equal((compound, 1), (hit.Body, hit.ShapeIndex));
    }

    [Fact]
    public void ContactsAndAreaOverlapsAreCollectedAfterAStep()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0), new BoxShape3D());
        var area = Add(PhysicsBodyKind.Area, new Vector3(0, 0.5f, 0), new BoxShape3D { Size = new Vector3(3) });
        Run(5);
        var others = new List<BackendBody>();
        _backend.CollectTouching(box, others);
        Assert.Contains(floor, others);

        others.Clear();
        Assert.True(_backend.CollectOverlaps(area, others));
        Assert.Equal(2, others.Count);
        Assert.Contains(floor, others);
        Assert.Contains(box, others);

        others.Clear();
        _backend.SetFilter(area, 1, 0);
        Assert.True(_backend.CollectOverlaps(area, others));
        Assert.Empty(others);
        Assert.False(_backend.CollectOverlaps(Create(PhysicsBodyKind.Area, Vector3.Zero), others));
    }

    [Fact]
    public void TheBroadPhaseUsesTheEnginesLayerRule()
    {
        Floor(); // layer 1, mask 1
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 1, 0), new BoxShape3D(), layer: 2, mask: 0); // neither scans the other
        Run(60);
        _backend.GetPose(box, out var position, out _);
        Assert.True(position.Y < -1f, $"fell through: {position}");

        _backend.SetFilter(box, 2, 1);
        _backend.Teleport(box, new Vector3(0, 1, 0), Quaternion.Identity);
        _backend.SetLinearVelocity(box, Vector3.Zero);
        Run(120);
        _backend.GetPose(box, out position, out _);
        Assert.InRange(position.Y, 0.45f, 0.55f);
    }

    [Fact]
    public void InvalidShapesAreReportedByDescriptionIndexAndSkipped()
    {
        var body = Create(PhysicsBodyKind.Static, Vector3.Zero);
        new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(1, 0, 1)] }.Describe(_shapes, Basis.Identity, Vector3.Zero);
        new BoxShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        SetShapes(body, PhysicsBodyKind.Static);

        Assert.Equal(0, Assert.Single(_errors).ShapeIndex);
        Assert.Equal(1, _backend.GetShapeCount(body));
        Assert.True(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, default, true, out var hit));
        Assert.Equal(1, hit.ShapeIndex);
    }

    [Fact]
    public void AKinematicTargetIsReachedInOneStep()
    {
        var body = Add(PhysicsBodyKind.Kinematic, Vector3.Zero, new BoxShape3D());
        var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        _backend.MoveKinematic(body, new Vector3(1, 0, 0), target, Dt);
        _backend.Step(Dt);
        _backend.GetPose(body, out var position, out var rotation);
        Assert.Equal(1f, position.X, 4);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, target)) > 0.9999f, $"{rotation} vs {target}");
    }

    [Fact]
    public void CharacterPrimitivesMeasureTheGapToTheFloor()
    {
        var floor = Floor();
        var start = new Vector3(0, 0.905f, 0); // a 1.8 m capsule 5 mm above the floor
        var body = Add(PhysicsBodyKind.Character, start, new CapsuleShape3D { Radius = 0.4f, Height = 1.8f });

        Assert.True(_backend.CastBody(body, start, Quaternion.Identity, new Vector3(0, -0.1f, 0), out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.999f, $"{hit.Normal}");

        var nearby = new List<BackendShapeRef>();
        _backend.CollectNearbyShapes(body, start, Quaternion.Identity, 0.02f, nearby);
        var shape = Assert.Single(nearby);
        Assert.Equal(floor, shape.Body);
        Assert.Equal(1, _backend.GetShapeCount(body));
        Assert.True(_backend.Penetration(body, 0, start, Quaternion.Identity, shape, 0.02f, out var contact));
        Assert.InRange(contact.Distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(contact.Normal), -Vector3.UnitY) > 0.999f); // from the body towards the floor

        Assert.True(_backend.Distance(body, start, Quaternion.Identity, floor, out var distance, out var normal));
        Assert.InRange(distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(normal), -Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void QueryShapesAreCachedUntilTheResourceChangesAndDoNotAllocate()
    {
        Floor();
        var probe = new SphereShape3D { Radius = 0.5f };
        Assert.True(_backend.ShapeCast(probe.DescribeQuery(), new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out var first));
        Assert.Equal(0.45f, first.Fraction, 2);
        probe.Radius = 1f;
        Assert.True(_backend.ShapeCast(probe.DescribeQuery(), new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out var second));
        Assert.Equal(0.4f, second.Fraction, 2);

        var query = probe.DescribeQuery();
        var output = new List<BackendBody>(8);
        for (var i = 0; i < 3; i++) // warm up
        {
            _backend.ShapeCast(query, new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out _);
            output.Clear();
            _backend.IntersectShape(query, new Vector3(0, 0.5f, 0), Quaternion.Identity, 1, default, output);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _backend.ShapeCast(query, new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out _);
            output.Clear();
            _backend.IntersectShape(query, new Vector3(0, 0.5f, 0), Quaternion.Identity, 1, default, output);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Single(output);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var backend = new Jitter2Backend(new PhysicsSettings3D { MultiThreaded = false });
        backend.CreateBody(new BodyDescription3D(PhysicsBodyKind.Static, Vector3.Zero, Quaternion.Identity, 1, 1, default, 0.5f, 0f));
        backend.Dispose();
        backend.Dispose();
    }
}
