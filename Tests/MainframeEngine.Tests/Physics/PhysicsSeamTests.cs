using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Behaviour the B2 backend seam must keep (ADR 0192): shared resources and handles across spaces, kind changes and
/// removals inside signal handlers, sub-viewport disposal, shape owners of compound and concave bodies, overlap order,
/// runtime area masks and the shape error log. Written against the Jitter2 code before the seam existed.
/// </summary>
public sealed class PhysicsSeamTests : IDisposable
{
    private static readonly Vector3 Down = new(0, -20, 0);
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static StaticBody3D StaticWith(string name, Shape3D shape, Vector3 position = default)
    {
        var body = new StaticBody3D { Name = name, Position = position };
        body.AddChild(new CollisionShape3D { Shape = shape });
        return body;
    }

    [Fact]
    public void EditingASharedShapeRebuildsItsBodiesInEverySpace()
    {
        var shared = new BoxShape3D { Size = new Vector3(2) };
        _h.Add(StaticWith("Main", shared));
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        viewport.AddChild(StaticWith("SubBody", shared));
        _h.Run(1);
        var sub = viewport.World3D.DirectSpaceState;

        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out var hit));
        Assert.Equal(1f, hit.Position.Y, 3);
        Assert.True(sub.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(1f, hit.Position.Y, 3);

        shared.Size = new Vector3(4); // queries flush the rebuild: no step needed
        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(2f, hit.Position.Y, 3);
        Assert.True(sub.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(2f, hit.Position.Y, 3);

        // One query shape used in both spaces sees its own edits in each.
        var probe = new SphereShape3D { Radius = 0.5f };
        var from = Transform3D.FromTrs(new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One);
        Assert.True(_h.Query.ShapeCast(probe, from, Down, out var cast));
        Assert.True(sub.ShapeCast(probe, from, Down, out var subCast));
        Assert.Equal(cast.Fraction, subCast.Fraction, 4);
        var before = cast.Fraction;
        probe.Radius = 1f;
        Assert.True(_h.Query.ShapeCast(probe, from, Down, out cast));
        Assert.True(sub.ShapeCast(probe, from, Down, out subCast));
        Assert.True(cast.Fraction < before - 0.02f, $"{cast.Fraction} vs {before}");
        Assert.Equal(cast.Fraction, subCast.Fraction, 4);
    }

    [Fact]
    public void ExcludingABodyOfAnotherSpaceExcludesNothingHere()
    {
        var floor = _h.AddFloor();
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        var other = StaticWith("Other", new BoxShape3D());
        viewport.AddChild(other); // the first (and only) body of the sub-viewport's space
        _h.Run(1);

        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out var hit, exclude: other));
        Assert.Same(floor, hit.Collider);
        var results = new List<CollisionObject3D>();
        _h.Query.IntersectPoint(new Vector3(0, -0.25f, 0), results, exclude: other);
        Assert.Equal([floor], results);
        var from = Transform3D.FromTrs(new Vector3(0, 5, 0), Quaternion.Identity, Vector3.One);
        Assert.True(_h.Query.ShapeCast(new SphereShape3D(), from, Down, out var cast, exclude: other));
        Assert.Same(floor, cast.Collider);
    }

    [Fact]
    public void ChangingABodysModeInsideASignalHandlerAppliesAtTheNextStep()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.6f, 0));
        box.ContactMonitor = true;
        box.GravityScale = 0.5f; // Jitter2 applies this one as a force: it must stop with the switch
        box.PhysicsInterpolation = false;
        var switched = false;
        box.BodyEntered += _ =>
        {
            if (switched)
                return;
            switched = true;
            box.Mode = RigidBodyMode.Kinematic;
            box.LinearVelocity = new Vector3(1, 0, 0);
        };
        _h.Run(30);
        Assert.True(switched);

        var start = box.Position;
        _h.Run(60);
        Assert.Equal(start.X + 1f, box.Position.X, 2); // moves by its velocity
        Assert.Equal(start.Y, box.Position.Y, 4);       // no gravity, no gravity-scale force
        Assert.False(box.Sleeping);

        box.Mode = RigidBodyMode.Dynamic;
        box.LinearVelocity = Vector3.Zero;
        box.Teleport(Transform3D.FromTrs(new Vector3(5, 3, 0), Quaternion.Identity, Vector3.One));
        _h.Run(30);
        Assert.True(box.Position.Y < 3f, $"dynamic again, falls: {box.Position}");
    }

    [Fact]
    public void QueriesInASignalHandlerNoLongerSeeABodyFreedEarlierInIt()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var target = _h.AddStaticBox(new Vector3(10, 0, 0), Vector3.One, name: "Target");
        var box = _h.AddBox(Vector3.Zero, 0.5f);
        box.GravityScale = 0;
        bool? rayAfterFree = null;
        var results = new List<CollisionObject3D>();
        area.BodyEntered += entered =>
        {
            target.Free();
            rayAfterFree = _h.Query.RayCast(new Vector3(10, 5, 0), new Vector3(10, -5, 0), out _);
            _h.Query.IntersectPoint(new Vector3(10, 0, 0), results);
        };
        _h.Run(1);

        Assert.False(rayAfterFree);
        Assert.Empty(results);
    }

    [Fact]
    public void ABodyAddedRightAfterAnotherIsFreedDoesNotInheritItsSignals()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var log = new List<string>();
        area.BodyEntered += n => log.Add("enter " + n.Name);
        area.BodyExited += n => log.Add("exit " + n.Name);
        var first = _h.AddBox(Vector3.Zero, 0.5f, "First");
        first.GravityScale = 0;
        _h.Run(2);

        first.Free();
        var second = _h.AddBox(new Vector3(20, 0, 0), 0.5f, "Second"); // takes the freed body's place in the library
        second.GravityScale = 0;
        _h.Run(2);
        Assert.Equal(["enter First", "exit First"], log);
        Assert.False(area.OverlapsBody(second));
        Assert.False(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out _));
        Assert.True(_h.Query.RayCast(new Vector3(20, 5, 0), new Vector3(20, -5, 0), out var hit));
        Assert.Same(second, hit.Collider);

        second.Teleport(Transform3D.FromTrs(Vector3.Zero, Quaternion.Identity, Vector3.One));
        _h.Run(1);
        Assert.Equal(["enter First", "exit First", "enter Second"], log);
    }

    [Fact]
    public void FreeingASubViewportReleasesItsSpaceAndItsQueriesFindNothing()
    {
        var floor = _h.AddFloor();
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        var area = new Area3D { Name = "SubArea" };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4) } });
        var box = new RigidBody3D { Name = "SubBox", GravityScale = 0, ContactMonitor = true };
        box.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        viewport.AddChild(area);
        viewport.AddChild(box);
        _h.Run(2);
        var world = viewport.World3D;
        Assert.True(area.OverlapsBody(box));
        Assert.Equal(2, _h.Server.Spaces.Count);

        viewport.Free();
        Assert.Null(box.Space);
        Assert.Null(area.Space);
        _h.Run(1); // the server releases the space in Process
        Assert.Single(_h.Server.Spaces);
        Assert.Null(world.PhysicsSpace);
        Assert.False(world.DirectSpaceState.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out _));

        var again = _h.Add(new SceneViewport { Name = "Again" });
        again.AddChild(StaticWith("AgainBody", new BoxShape3D()));
        _h.Run(1);
        Assert.Equal(2, _h.Server.Spaces.Count);
        Assert.True(again.World3D.DirectSpaceState.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out var hit));
        Assert.Equal("AgainBody", hit.Collider.Name);
        Assert.True(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out hit));
        Assert.Same(floor, hit.Collider);
    }

    [Fact]
    public void RayHitsReportTheOwningShapeOfCompoundAndConcaveBodies()
    {
        var body = new StaticBody3D { Name = "Compound" };
        var box = new CollisionShape3D { Name = "Box", Shape = new BoxShape3D(), Position = new Vector3(-3, 0, 0) };
        var mesh = new CollisionShape3D
        {
            Name = "Mesh",
            Position = new Vector3(3, 0, 0),
            Shape = new ConcavePolygonShape3D { Faces = [new(-2, 0, -2), new(-2, 0, 2), new(2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)] },
        };
        body.AddChild(box);
        body.AddChild(mesh);
        _h.Add(body);

        Assert.True(_h.Query.RayCast(new Vector3(-3, 5, 0), new Vector3(-3, -5, 0), out var hit));
        Assert.Same(box, hit.Shape);
        Assert.True(_h.Query.RayCast(new Vector3(3.5f, 5, 0.5f), new Vector3(3.5f, -5, 0.5f), out hit));
        Assert.Same(mesh, hit.Shape);

        box.Disabled = true; // rebuild: the mesh is now the first shape
        Assert.False(_h.Query.RayCast(new Vector3(-3, 5, 0), new Vector3(-3, -5, 0), out _));
        Assert.True(_h.Query.RayCast(new Vector3(2.5f, 5, -0.5f), new Vector3(2.5f, -5, -0.5f), out hit));
        Assert.Same(mesh, hit.Shape);
    }

    [Fact]
    public void AnAreaListsOverlappingBodiesInItsSetOrder()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(10, 4, 10));
        RigidBody3D Box(string name, Vector3 at)
        {
            var b = _h.AddBox(at, 0.5f, name);
            b.GravityScale = 0;
            _h.Run(1);
            return b;
        }

        Box("A", new Vector3(3, 0, 0));
        var b = Box("B", new Vector3(-3, 0, 0));
        Box("C", new Vector3(0, 0, 3));
        b.Free();
        Box("D", new Vector3(0, 0, -3));

        var bodies = new List<Node3D>();
        area.GetOverlappingBodies(bodies);
        // The per-area set is a Dictionary: D takes the slot B freed, so it enumerates before C.
        Assert.Equal(["A", "D", "C"], bodies.Select(n => n.Name));
    }

    [Fact]
    public void ChangingAnAreasMaskAppliesAtTheNextStep()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var box = _h.AddBox(Vector3.Zero, 0.5f);
        box.GravityScale = 0;
        box.CollisionLayer = CollisionLayers.Layer(3);
        _h.Run(1);
        Assert.False(area.OverlapsBody(box));

        area.CollisionMask = CollisionLayers.Layer(3);
        _h.Run(1);
        Assert.True(area.OverlapsBody(box));

        area.CollisionMask = CollisionLayers.Layer(1);
        _h.Run(1);
        Assert.False(area.OverlapsBody(box));
    }

    [Fact]
    public void InvalidShapesAndConcaveShapesOnDynamicBodiesAreLogged()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Error };
        Log.AddSink(sink);
        try
        {
            var flat = new StaticBody3D { Name = "Flat" };
            flat.AddChild(new CollisionShape3D { Name = "Hull", Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } });
            flat.AddChild(new CollisionShape3D { Name = "Coplanar", Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(1, 0, 1)] } });
            _h.Add(flat);
            var dynamic = _h.AddBox(new Vector3(5, 5, 0), name: "Dyn");
            dynamic.AddChild(new CollisionShape3D { Name = "Mesh", Shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } });
            _h.Run(1);

            var messages = sink.Snapshot().Select(e => e.Message).ToArray();
            Assert.Contains(messages, m => m.Contains("'Flat/Hull': ConvexPolygonShape3D is invalid (ConvexPolygonShape3D needs at least 4 points.); ignored.", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("'Flat/Coplanar': ConvexPolygonShape3D is invalid (", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("'Dyn': ConcavePolygonShape3D is concave and only works on static or kinematic bodies; ignored.", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
