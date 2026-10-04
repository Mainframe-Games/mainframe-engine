using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Bodies, gravity, layers, signals and lifecycle on the Jitter2-backed 3D server.</summary>
public sealed class Physics3DTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void FreeFallFollowsGravity()
    {
        var box = _h.AddBox(new Vector3(0, 100, 0));
        box.LinearDamp = 0;
        _h.RunSeconds(1f);

        var v = box.LinearVelocity;
        Assert.InRange(v.Y, -9.81f * 1.03f, -9.81f * 0.97f);
        // s ≈ g t² / 2; the node shows the interpolated pose (one step behind at a whole-step frame) and a new body
        // starts simulating on its second step.
        Assert.InRange(box.Position.Y, 100 - 4.95f, 100 - 4.5f);
        Assert.Equal(0, box.Position.X, 3);
    }

    [Fact]
    public void BoxFallsAndComesToRestOnTheFloorThenSleeps()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 4, 0));
        var sleepChanges = 0;
        box.SleepingStateChanged += () => sleepChanges++;

        _h.RunSeconds(0.3f);
        Assert.True(box.Position.Y < 4);
        Assert.True(box.LinearVelocity.Y < 0);

        _h.RunSeconds(4f);
        Assert.InRange(box.Position.Y, 0.45f, 0.55f);
        Assert.True(box.LinearVelocity.Length() < 0.05f);
        Assert.True(box.Sleeping);
        Assert.Equal(1, sleepChanges);

        // An impulse wakes it.
        box.ApplyImpulse(new Vector3(0, 5, 0));
        _h.Run(2);
        Assert.False(box.Sleeping);
        Assert.Equal(2, sleepChanges);
    }

    [Fact]
    public void StaticBodiesDoNotMoveAndMovingThemByCodeTeleports()
    {
        var floor = _h.AddFloor();
        _h.RunSeconds(0.5f);
        Assert.Equal(-0.5f, floor.Position.Y);

        floor.Position = new Vector3(0, -10.5f, 0);
        var box = _h.AddBox(new Vector3(0, -9, 0));
        _h.RunSeconds(2f);
        Assert.InRange(box.Position.Y, -9.55f, -9.45f);
    }

    [Fact]
    public void LayersAndMasksUseOrSemantics()
    {
        _h.AddFloor(layer: CollisionLayers.Layer(1), mask: CollisionLayers.Layer(1));
        // Different layer, scans nothing: the floor scans layer 1 only → no collision.
        var ghost = _h.AddBox(new Vector3(-3, 2, 0), name: "Ghost");
        ghost.CollisionLayer = CollisionLayers.Layer(2);
        ghost.CollisionMask = 0;
        // Layer 1 but scans nothing: the floor scans layer 1 → collides (either side scanning is enough).
        var oneWay = _h.AddBox(new Vector3(3, 2, 0), name: "OneWay");
        oneWay.CollisionMask = 0;

        _h.RunSeconds(2f);
        Assert.True(ghost.Position.Y < -5, $"ghost at {ghost.Position}");
        Assert.InRange(oneWay.Position.Y, 0.45f, 0.55f);
    }

    [Fact]
    public void ChangingTheMaskOfARestingBodyAppliesImmediately()
    {
        var floor = _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.5f, 0));
        _h.RunSeconds(3f); // asleep on the floor
        Assert.True(box.Sleeping);

        floor.CollisionMask = 0;
        box.CollisionMask = 0;
        box.CollisionLayer = CollisionLayers.Layer(3);
        _h.RunSeconds(1f);
        Assert.True(box.Position.Y < -1, $"box at {box.Position}");
    }

    [Fact]
    public void GravityScaleAndAxisLocks()
    {
        var floating = _h.AddBox(new Vector3(-5, 10, 0), name: "Floating");
        floating.GravityScale = 0;
        var heavy = _h.AddBox(new Vector3(0, 100, 0), name: "Heavy");
        heavy.GravityScale = 2;
        heavy.LinearDamp = 0;
        var locked = _h.AddBox(new Vector3(5, 10, 0), name: "Locked");
        locked.GravityScale = 0;
        locked.AxisLocks = AxisLock.LinearX | AxisLock.AngularX | AxisLock.AngularY | AxisLock.AngularZ;

        _h.Run(1);
        locked.ApplyImpulse(new Vector3(3, 3, 0), locked.GlobalPosition + new Vector3(0, 0.5f, 0.5f));
        _h.RunSeconds(1f);

        Assert.Equal(10, floating.Position.Y, 3);
        Assert.InRange(heavy.LinearVelocity.Y, -19.62f * 1.03f, -19.62f * 0.97f);
        Assert.Equal(5, locked.Position.X, 3);
        Assert.True(locked.Position.Y > 11);
        Assert.Equal(Quaternion.Identity, locked.Rotation);
    }

    [Fact]
    public void ContactMonitorReportsBodyEnteredAndExitedOnce()
    {
        var floor = _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 1.5f, 0));
        box.ContactMonitor = true;
        var log = new List<string>();
        box.BodyEntered += n => log.Add("enter " + n.Name);
        box.BodyExited += n => log.Add("exit " + n.Name);

        _h.RunSeconds(1.5f);
        Assert.Equal(["enter Floor"], log);

        box.Teleport(Transform3D.FromTrs(new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One));
        _h.Run(2);
        Assert.Equal(["enter Floor", "exit Floor"], log);
        Assert.Same(floor, floor); // keep the floor alive for the exit
    }

    [Fact]
    public void FreeingATouchedBodyReportsExitImmediately()
    {
        var floor = _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.5f, 0));
        box.ContactMonitor = true;
        var log = new List<string>();
        box.BodyExited += n => log.Add("exit " + n.Name + (Node.IsInstanceValid(n) ? "" : " (freed)"));
        _h.RunSeconds(0.5f);

        floor.Free();
        Assert.Equal(["exit Floor"], log);
        _h.RunSeconds(0.5f);
        Assert.Single(log);
        Assert.True(box.Position.Y < 0); // falling now
    }

    [Fact]
    public void AreaReportsBodiesEnteringAndLeaving()
    {
        var area = _h.AddArea(new Vector3(0, 5, 0), new Vector3(4, 2, 4));
        var box = _h.AddBox(new Vector3(0, 9, 0));
        var log = new List<string>();
        area.BodyEntered += n => log.Add($"enter {n.Name}");
        area.BodyExited += n => log.Add($"exit {n.Name}");

        var overlapping = new List<Node3D>();
        var sawInside = false;
        for (var i = 0; i < 180; i++)
        {
            _h.Run(1);
            overlapping.Clear();
            if (area.GetOverlappingBodies(overlapping) == 1)
            {
                sawInside = true;
                Assert.Same(box, overlapping[0]);
                Assert.True(area.OverlapsBody(box));
            }
        }

        Assert.True(sawInside);
        Assert.Equal(["enter Box", "exit Box"], log);
        Assert.False(area.HasOverlappingBodies());
    }

    [Fact]
    public void AreasDetectStaticAndCharacterBodiesFilteredByTheirMask()
    {
        var area = _h.AddArea(new Vector3(0, 0, 0), new Vector3(10, 10, 10));
        var wall = _h.AddStaticBox(new Vector3(2, 0, 0), Vector3.One);
        var hidden = _h.AddStaticBox(new Vector3(-2, 0, 0), Vector3.One, name: "Hidden");
        hidden.CollisionLayer = CollisionLayers.Layer(4);
        var character = _h.Add(new CharacterBody3D { Name = "Character", Position = new Vector3(0, 2, 0) });
        character.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D() });

        var entered = new List<Node3D>();
        area.BodyEntered += entered.Add;
        _h.Run(2);

        Assert.Equal(2, entered.Count);
        Assert.Contains(wall, entered);
        Assert.Contains(character, entered);

        // Monitoring off drops the overlaps with exits.
        var exited = new List<Node3D>();
        area.BodyExited += exited.Add;
        area.Monitoring = false;
        _h.Run(1);
        Assert.Equal(2, exited.Count);
        Assert.False(area.HasOverlappingBodies());
    }

    [Fact]
    public void LeavingTheTreeInsideAnAreaReportsExitAndQueueFreeDuringSignalsIsSafe()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(6, 6, 6));
        var a = _h.AddBox(new Vector3(-1, 0, 0), name: "A");
        var b = _h.AddBox(new Vector3(1, 0, 0), name: "B");
        a.GravityScale = b.GravityScale = 0;
        var log = new List<string>();
        area.BodyEntered += n =>
        {
            log.Add("enter " + n.Name);
            if (n == a)
            {
                b.Free();      // immediate: B leaves the tree while its own "enter" is still queued
                a.QueueFree(); // deferred to the end of the frame
            }
        };
        area.BodyExited += n => log.Add("exit " + n.Name);

        _h.Run(1);
        // B left while its "enter" was still queued: neither is reported. A leaves at the end of the frame.
        Assert.Equal(["enter A", "exit A"], log);
        Assert.False(Node.IsInstanceValid(a));
        Assert.Equal(1, _h.Space.ObjectCount);
        _h.Run(3);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void SignalsAreDispatchedExitsFirstThenEntersInCreationOrder()
    {
        var left = _h.AddArea(new Vector3(-5, 0, 0), new Vector3(2, 2, 2), "Left");
        var right = _h.AddArea(new Vector3(5, 0, 0), new Vector3(2, 2, 2), "Right");
        var first = _h.AddBox(new Vector3(-5, 0, 0), 0.5f, "First");
        var second = _h.AddBox(new Vector3(-5, 0.2f, 0), 0.5f, "Second");
        first.GravityScale = second.GravityScale = 0;
        first.CollisionLayer = CollisionLayers.Layer(2); // the boxes don't collide with each other
        first.CollisionMask = 0;
        second.CollisionLayer = CollisionLayers.Layer(3);
        second.CollisionMask = 0;
        left.CollisionMask = right.CollisionMask = CollisionLayers.Layer(2) | CollisionLayers.Layer(3);

        var log = new List<string>();
        foreach (var area in new[] { left, right })
        {
            var name = area.Name;
            area.BodyEntered += n => log.Add($"{name}+{n.Name}");
            area.BodyExited += n => log.Add($"{name}-{n.Name}");
        }

        _h.Run(1);
        Assert.Equal(["Left+First", "Left+Second"], log);
        log.Clear();

        // Both move from Left to Right in one step: every exit is dispatched before any enter.
        second.Teleport(Transform3D.FromTrs(new Vector3(5, 0.2f, 0), Quaternion.Identity, Vector3.One));
        first.Teleport(Transform3D.FromTrs(new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One));
        _h.Run(1);
        Assert.Equal(["Left-First", "Left-Second", "Right+First", "Right+Second"], log);
    }

    [Fact]
    public void SignalsFireAfterTheStepOnTheMainThread()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.6f, 0));
        box.ContactMonitor = true;
        var thread = Environment.CurrentManagedThreadId;
        var steps = -1L;
        box.BodyEntered += _ =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            steps = _h.Space.StepCount;
        };
        _h.Run(30);
        Assert.True(steps >= 1);
    }

    [Fact]
    public void BodiesJoinAndLeaveTheSpaceWithTheTree()
    {
        var box = new RigidBody3D { Name = "Box" };
        box.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        Assert.Null(box.Space);

        _h.Add(box);
        Assert.NotNull(box.Space);
        Assert.Equal(1, _h.Space.ObjectCount);

        _h.Scene.RemoveChild(box);
        Assert.Null(box.Space);
        Assert.Equal(0, _h.Space.ObjectCount);

        _h.Add(box); // re-entry recreates the body at the node's pose
        Assert.Equal(1, _h.Space.ObjectCount);
        box.QueueFree();
        _h.Run(1);
        Assert.Equal(0, _h.Space.ObjectCount);
    }

    [Fact]
    public void QueueFreeFromPhysicsProcessIsSafe()
    {
        _h.AddFloor();
        var victim = _h.AddBox(new Vector3(0, 3, 0), name: "Victim");
        var killer = _h.Add(new FreeOnPhysicsStep(victim, atStep: 5));
        _h.Run(20);
        Assert.True(killer.Done);
        Assert.False(Node.IsInstanceValid(victim));
        Assert.Equal(1, _h.Space.ObjectCount);
    }

    [Fact]
    public void ShapeChangesRebuildTheBody()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.5f, 0));
        _h.RunSeconds(1f);
        Assert.InRange(box.Position.Y, 0.45f, 0.55f);

        var shape = (BoxShape3D)((CollisionShape3D)box.GetChild(0)).Shape!;
        shape.Size = new Vector3(3);
        _h.RunSeconds(1.5f);
        Assert.InRange(box.Position.Y, 1.45f, 1.55f);

        ((CollisionShape3D)box.GetChild(0)).Position = new Vector3(0, 1, 0); // shape raised inside the body
        _h.RunSeconds(1.5f);
        Assert.InRange(box.Position.Y, 0.45f, 0.55f);
    }

    [Fact]
    public void KinematicBodiesMoveByVelocityOrByCodeAndPushDynamicBodies()
    {
        _h.AddFloor();
        var pusher = new RigidBody3D { Name = "Pusher", Mode = RigidBodyMode.Kinematic, Position = new Vector3(-3, 0.5f, 0) };
        pusher.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        _h.Add(pusher);
        var crate = _h.AddBox(new Vector3(0, 0.5f, 0), name: "Crate");
        _h.RunSeconds(0.5f);

        pusher.LinearVelocity = new Vector3(2, 0, 0);
        _h.RunSeconds(2f);
        Assert.InRange(pusher.Position.X, 0.9f, 1.1f); // -3 + 2 × 2 s (velocity starts the next step)
        Assert.True(crate.Position.X > 1.4f, $"crate at {crate.Position}");
        Assert.InRange(pusher.Position.Y, 0.49f, 0.51f); // not pushed back, no gravity

        pusher.LinearVelocity = Vector3.Zero;
        pusher.Position = new Vector3(-10, 0.5f, 0); // moved by code: reached in one step
        _h.Run(2);
        Assert.Equal(-10, pusher.Position.X, 2);
    }

    [Fact]
    public void ServerDisposalDetachesNodes()
    {
        var box = _h.AddBox(Vector3.Zero);
        Assert.NotNull(box.Space);
        _h.Server.Dispose();
        Assert.Null(box.Space);
        Assert.Null(_h.Tree.Root.World3D.PhysicsSpace);
        Assert.False(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out _));
    }

    [Fact]
    public void PhysicsServerForRegistersADefaultServer()
    {
        var tree = new SceneTree();
        try
        {
            var box = new RigidBody3D();
            tree.Root.AddChild(box);
            var server = tree.Servers.Get<PhysicsServer3D>();
            Assert.NotNull(server);
            Assert.Same(server, PhysicsServer3D.For(tree));
            Assert.Same(server.Spaces[0], box.Space);
        }
        finally
        {
            tree.Shutdown();
            tree.Servers.Dispose();
        }
    }

    private sealed class FreeOnPhysicsStep(Node victim, int atStep) : Node
    {
        private int _steps;

        public bool Done { get; private set; }

        protected override void OnPhysicsProcess(float delta)
        {
            if (++_steps != atStep)
                return;
            victim.QueueFree();
            Done = true;
        }
    }
}
