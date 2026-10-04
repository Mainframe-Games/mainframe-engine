using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Degenerate data, signal pairing and space lifecycle edge cases (code-review regressions).</summary>
[Collection(nameof(SerialBox2D))]
public sealed class PhysicsEdgeCaseTests
{
    [Fact]
    public void DegenerateShapesAreSkippedWithoutBreakingTheStep()
    {
        using var h = new PhysicsHarness3D();
        var flat = new StaticBody3D { Name = "Flat" };
        flat.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, Vector3.One with { Y = 0 }] } });
        flat.AddChild(new CollisionShape3D { Shape = new BoxShape3D(), Position = new Vector3(5, 0, 0) });
        h.Add(flat);
        var squashed = h.AddBox(new Vector3(0, 5, 0), name: "Squashed");
        ((CollisionShape3D)squashed.GetChild(0)).Scale = new Vector3(1, 0, 1);
        h.Run(5); // logs, does not throw

        Assert.True(h.Query.RayCast(new Vector3(5, 5, 0), new Vector3(5, -5, 0), out var hit)); // the valid sibling shape works
        Assert.Same(flat, hit.Collider);
    }

    [Fact]
    public void TinyTwoDimensionalShapesDoNotCrash()
    {
        using var h = new PhysicsHarness2D();
        var area = new Area2D { Name = "Area" };
        area.AddChild(new CollisionShape2D { Shape = new SegmentShape2D { A = Vector2.Zero, B = new Vector2(0.1f, 0) } });
        area.AddChild(new CollisionShape2D { Shape = new CapsuleShape2D { Radius = 10, Height = 20 } }); // a circle
        h.Add(area);
        var wall = new StaticBody2D { Name = "Wall" };
        wall.AddChild(new CollisionShape2D { Shape = new ConcavePolygonShape2D { Segments = [Vector2.Zero, new Vector2(0.2f, 0), new(0, 0), new(100, 0)] } });
        h.Add(wall);
        h.Run(3);

        area.CollisionMask = 3;
        wall.CollisionLayer = 2;
        wall.PhysicsMaterial = new PhysicsMaterial { Friction = 1 };
        ((CollisionShape2D)wall.GetChild(0)).Position = new Vector2(0, 5); // rebuild
        h.Run(3);
        Assert.True(area.OverlapsBody(wall));
    }

    [Fact]
    public void MovingAStaticPlatformWakesBodiesResting2D()
    {
        using var h = new PhysicsHarness2D();
        var platform = h.AddStaticBox(new Vector2(0, -50), new Vector2(400, 100), name: "Platform");
        var box = h.AddBox(new Vector2(0, 25));
        h.RunSeconds(3f);
        Assert.True(box.Sleeping);

        platform.Position = new Vector2(0, -250);
        h.RunSeconds(1f);
        Assert.True(box.Position.Y < -100, $"box at {box.Position}");
    }

    [Fact]
    public void ExitIsDeliveredWhenTheOtherBodyIsFreedEarlierInTheSameDispatch()
    {
        using var h = new PhysicsHarness3D();
        var a = h.AddArea(new Vector3(0, 0, 0), new Vector3(2, 2, 2), "A");
        var b = h.AddArea(new Vector3(0.5f, 0, 0), new Vector3(2, 2, 2), "B");
        var x = h.AddBox(Vector3.Zero, 0.2f, "X");
        x.GravityScale = 0;
        var log = new List<string>();
        a.BodyExited += n =>
        {
            log.Add("A-" + n.Name);
            n.Free(); // frees X while B's exit for it is still queued
        };
        b.BodyExited += n => log.Add($"B-{n.Name}{(Node.IsInstanceValid(n) ? "" : " (freed)")}");
        h.Run(1);

        x.Teleport(Transform3D.FromTrs(new Vector3(50, 0, 0), Quaternion.Identity, Vector3.One));
        h.Run(1);
        Assert.Equal(["A-X", "B-X (freed)"], log);
    }

    [Fact]
    public void TurningContactMonitorOffReportsTheExits()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor();
        var box = h.AddBox(new Vector3(0, 0.5f, 0));
        box.ContactMonitor = true;
        var log = new List<string>();
        box.BodyEntered += n => log.Add("enter " + n.Name);
        box.BodyExited += n => log.Add("exit " + n.Name);
        h.Run(10);
        box.ContactMonitor = false;
        h.Run(2);
        Assert.Equal(["enter Floor", "exit Floor"], log);
    }

    [Fact]
    public void KinematicBodiesIgnoreAxisLocks()
    {
        using var h = new PhysicsHarness3D();
        var body = h.AddBox(Vector3.Zero);
        body.AxisLocks = AxisLock.LinearX;
        body.Mode = RigidBodyMode.Kinematic;
        body.PhysicsInterpolation = false;
        h.Run(1);
        body.Position = new Vector3(3, 0, 0);
        h.Run(2);
        Assert.Equal(3, body.Position.X, 3);
    }

    [Fact]
    public void SpacesAreCreatedMidStepAndReleasedWithTheirViewport()
    {
        using var h = new PhysicsHarness3D();
        var area = h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var box = h.AddBox(Vector3.Zero);
        box.GravityScale = 0;
        var viewport = new SceneViewport { Name = "Sub" };
        h.Add(viewport);
        area.BodyEntered += _ => viewport.AddChild(new StaticBody3D { Name = "Late" }); // first body of a new world, mid-step
        h.Run(2);
        Assert.Equal(2, h.Server.Spaces.Count);
        Assert.NotNull(viewport.World3D.PhysicsSpace);

        viewport.Free();
        h.Run(1);
        Assert.Single(h.Server.Spaces);
        Assert.Null(viewport.World3D.PhysicsSpace);
    }

    [Fact]
    public void PhysicsInterpolationCanBeToggledWithoutARebuild()
    {
        using var h = new PhysicsHarness3D();
        var box = h.AddBox(new Vector3(0, 100, 0));
        h.Run(5);
        h.Run(1, PhysicsHarness3D.Step * 1.5f); // render pose is between steps now
        box.PhysicsInterpolation = false;          // snaps to the physics pose
        var physics = box.Position;
        h.Run(1, PhysicsHarness3D.Step * 0.25f);   // no step: stays at the physics pose
        Assert.Equal(physics, box.Position);
    }
}
