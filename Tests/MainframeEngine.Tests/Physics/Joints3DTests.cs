using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>3D joints on Jitter2 (ADR 0211): accuracy, limits, motors, lifecycle and collision exclusion.</summary>
public sealed class Joints3DTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private T AddJoint<T>(T joint, Vector3 position, string? nodeA, string? nodeB, Quaternion? rotation = null) where T : Joint3D
    {
        joint.Position = position;
        if (rotation is { } r)
            joint.Rotation = r;
        _h.Add(joint);
        joint.NodeA = nodeA is null ? default(NodePath) : "../" + nodeA;
        joint.NodeB = nodeB is null ? default(NodePath) : "../" + nodeB;
        return joint;
    }

    private RigidBody3D AddBall(Vector3 position, string name, float radius = 0.1f, float mass = 1f)
    {
        var body = new RigidBody3D { Name = name, Position = position, Mass = mass, LinearDamp = 0, AngularDamp = 0 };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = radius } });
        return _h.Add(body);
    }

    [Fact]
    public void APinnedPendulumSwingsWithTheTextbookPeriod()
    {
        const float length = 2f;
        var angle = float.DegreesToRadians(8f);
        var bob = AddBall(new Vector3(MathF.Sin(angle) * length, -MathF.Cos(angle) * length, 0), "Bob", radius: 0.05f);
        var pin = AddJoint(new PinJoint3D { Name = "Pin" }, Vector3.Zero, nodeA: null, nodeB: "Bob");

        // Upward crossings of x = 0 (interpolated between steps) over several swings.
        var crossings = new List<float>();
        var previous = bob.Position.X;
        for (var i = 1; i <= 60 * 12; i++)
        {
            _h.Run(1);
            var x = bob.Position.X;
            if (previous < 0 && x >= 0)
                crossings.Add((i - 1 + -previous / (x - previous)) * PhysicsHarness3D.Step);
            previous = x;
            Assert.InRange(bob.Position.Length(), length - 0.01f, length + 0.01f); // the anchor holds within 1 cm
        }

        Assert.True(pin.IsActive);
        Assert.Null(pin.BodyA);
        Assert.Same(bob, pin.BodyB);
        Assert.True(crossings.Count >= 3, $"{crossings.Count} crossings");
        var period = (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        var expected = MathF.Tau * MathF.Sqrt(length / 9.81f);
        Assert.InRange(period, expected * 0.98f, expected * 1.02f);
    }

    [Fact]
    public void AHeavyChainOfPinsStaysConnected()
    {
        const int links = 8;
        for (var i = 0; i < links; i++)
            AddBall(new Vector3(0.5f * (i + 1), 5, 0), $"Link{i}", radius: 0.1f, mass: 5f);
        var joints = new List<PinJoint3D>();
        for (var i = 0; i < links; i++)
            joints.Add(AddJoint(new PinJoint3D { Name = $"Pin{i}" }, new Vector3(i == 0 ? 0 : 0.5f * i + 0.25f, 5, 0),
                i == 0 ? null : $"Link{i - 1}", $"Link{i}"));
        _h.RunSeconds(4f);

        foreach (var joint in joints)
            Assert.True(joint.IsActive);
        foreach (var joint in joints)
            Assert.InRange(joint.GetSeparation(), 0f, 0.01f); // every anchor holds within 1 cm under the load
        var last = _h.Scene.GetNode<RigidBody3D>($"Link{links - 1}");
        Assert.True(last.Position.Y < 4f, $"the chain swung down: {last.Position}");

        Assert.True(joints[0].GetAppliedForce() > 0f);
    }

    [Fact]
    public void AHingeLimitHoldsWithinADegree()
    {
        var door = _h.AddBox(new Vector3(1, 0, 0), size: 0.5f, name: "Door");
        door.AngularDamp = 0;
        var hinge = AddJoint(new HingeJoint3D { Name = "Hinge", UseLimit = true, LowerAngleDegrees = -30, UpperAngleDegrees = 30 },
            Vector3.Zero, nodeA: null, nodeB: "Door");
        var lowest = 0f;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            lowest = MathF.Min(lowest, hinge.GetAngle());
        }

        // Jitter2's limits are soft (library defaults, ADR 0211): the swing into it overshoots briefly, then rests on it.
        Assert.True(lowest > float.DegreesToRadians(-32f), $"swung past the limit: {float.RadiansToDegrees(lowest)}°");
        Assert.InRange(float.RadiansToDegrees(hinge.GetAngle()), -31f, -29f); // resting on the lower limit (gravity)
        Assert.InRange(float.RadiansToDegrees(MathF.Atan2(door.Position.Y, door.Position.X)), -31f, -29f);
        Assert.InRange(door.Position.Z, -0.01f, 0.01f); // about Z only

        hinge.UseLimit = false; // live: the door drops to hang straight down
        _h.RunSeconds(4f);
        Assert.True(door.Position.Y < -0.6f, $"no limit: {door.Position}");
    }

    [Fact]
    public void TheMotorReachesItsSpeedWithinItsTorque()
    {
        var wheel = _h.AddBox(Vector3.Zero, size: 1f, name: "Wheel");
        wheel.GravityScale = 0;
        wheel.AngularDamp = 0;
        var axle = AddJoint(new HingeJoint3D { Name = "Axle", MotorEnabled = true, MotorTargetVelocity = 3f, MotorMaxTorque = 1000f },
            Vector3.Zero, nodeA: null, nodeB: "Wheel");
        _h.RunSeconds(0.5f);
        Assert.InRange(wheel.AngularVelocity.Z, 2.9f, 3.1f);

        // A weak motor accelerates at τ / I: a 1 kg, 1 m cube has I = 1/6 kg·m² about Z.
        axle.MotorTargetVelocity = 100f;
        axle.MotorMaxTorque = 0.1f;
        var before = wheel.AngularVelocity.Z;
        _h.RunSeconds(1f);
        var gained = wheel.AngularVelocity.Z - before;
        Assert.InRange(gained, 0.6f * 0.9f, 0.6f * 1.1f);

        axle.MotorEnabled = false; // live: coasts
        var coasting = wheel.AngularVelocity.Z;
        _h.RunSeconds(0.5f);
        Assert.InRange(wheel.AngularVelocity.Z, coasting - 0.05f, coasting + 0.05f);
    }

    [Fact]
    public void AJointBeforeItsBodiesInTreeOrderJoinsThem()
    {
        var hinge = new HingeJoint3D { Name = "Hinge", NodeA = "../Frame", NodeB = "../Door" };
        _h.Add(hinge);
        _h.Run(1);
        Assert.False(hinge.IsActive);

        _h.AddStaticBox(new Vector3(0, 0, 0), new Vector3(0.2f, 2, 0.2f), name: "Frame");
        var door = _h.AddBox(new Vector3(0.6f, 0, 0), size: 0.8f, name: "Door");
        _h.RunSeconds(1f);
        Assert.True(hinge.IsActive);
        Assert.InRange(door.Position.Length(), 0.59f, 0.61f);
        Assert.NotNull(hinge.BodyA);
        Assert.Same(door, hinge.BodyB);
    }

    [Fact]
    public void ABodyThatLeavesAndReturnsIsJoinedAgainAtTheAuthoredFrame()
    {
        _h.AddStaticBox(Vector3.Zero, new Vector3(0.2f, 0.2f, 0.2f), name: "Anchor");
        var b = AddBall(new Vector3(0, -1, 0), "B");
        var joint = AddJoint(new PinJoint3D { Name = "Pin" }, new Vector3(0, -0.5f, 0), "Anchor", "B");
        _h.Run(2);
        Assert.True(joint.IsActive);
        Assert.Equal(1, _h.Space.ActiveJointCount);

        _h.Scene.RemoveChild(b);
        Assert.False(joint.IsActive);
        Assert.Equal(0, _h.Space.ActiveJointCount);
        _h.Run(2);
        _h.Scene.AddChild(b);
        _h.RunSeconds(0.5f);
        Assert.True(joint.IsActive);
        Assert.InRange(b.Position.Y, -1.02f, -0.98f);

        b.ProcessMode = ProcessMode.Disabled; // a disabled body leaves the space too
        _h.Run(1);
        Assert.False(joint.IsActive);
        b.ProcessMode = ProcessMode.Inherit;
        _h.Run(1);
        Assert.True(joint.IsActive);

        b.Free();
        Assert.False(joint.IsActive);
        _h.Run(2); // no dangling constraint
        joint.Free();
        Assert.Equal(0, _h.Space.JointCount);
    }

    [Fact]
    public void DisablingAJointKeepsItAndReleasesTheBody()
    {
        var bob = AddBall(new Vector3(0, -1, 0), "Bob");
        var pin = AddJoint(new PinJoint3D { Name = "Pin" }, Vector3.Zero, null, "Bob");
        _h.Run(2);
        var socket = pin.Record!.Socket;

        pin.Enabled = false;
        _h.RunSeconds(0.5f);
        Assert.True(bob.Position.Y < -1.5f, "the bob falls while the pin is off");
        Assert.True(pin.IsActive); // kept, only disabled
        Assert.Same(socket, pin.Record!.Socket);

        pin.Enabled = true;
        pin.ProcessMode = ProcessMode.Disabled; // the node disabled: still off
        _h.Run(1);
        var y = bob.Position.Y;
        _h.Run(10);
        Assert.True(bob.Position.Y < y, "still falling");
        pin.ProcessMode = ProcessMode.Inherit;
        bob.Teleport(Transform3D.FromTrs(new Vector3(0, -1, 0), Quaternion.Identity, Vector3.One));
        bob.LinearVelocity = Vector3.Zero;
        _h.RunSeconds(1f);
        Assert.Same(socket, pin.Record!.Socket); // never re-created
        Assert.InRange(bob.Position.Length(), 0.98f, 1.02f);
    }

    [Fact]
    public void JointsBetweenKinematicBodiesWaitAndComeBackWithTheirLimits()
    {
        var a = _h.AddBox(new Vector3(0, 0, 0), size: 0.5f, name: "A");
        var b = _h.AddBox(new Vector3(1, 0, 0), size: 0.5f, name: "B");
        a.GravityScale = 0;
        var hinge = AddJoint(new HingeJoint3D { Name = "Hinge", UseLimit = true, LowerAngleDegrees = -20, UpperAngleDegrees = 20 },
            new Vector3(0.5f, 0, 0), "A", "B");
        _h.Run(2);
        Assert.True(hinge.IsActive);

        a.Mode = RigidBodyMode.Kinematic;
        b.Mode = RigidBodyMode.Kinematic;
        _h.Run(2);
        Assert.False(hinge.IsActive); // Jitter2 keeps no constraint between two non-dynamic bodies

        b.Mode = RigidBodyMode.Dynamic; // B hangs from kinematic A again, within the authored limits
        _h.RunSeconds(2f);
        Assert.True(hinge.IsActive);
        Assert.InRange(float.RadiansToDegrees(hinge.GetAngle()), -21f, -19f);
        Assert.InRange(Vector3.Distance(b.Position, new Vector3(0.5f, 0, 0)), 0.48f, 0.52f);
    }

    [Fact]
    public void InvalidPathsLogOnce()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Error };
        Log.AddSink(sink);
        try
        {
            _h.Add(new Node3D { Name = "NotABody" });
            AddBall(Vector3.Zero, "Ball");
            var joint = AddJoint(new PinJoint3D { Name = "BadPinXyz" }, Vector3.Zero, "NotABody", "Ball");
            var same = AddJoint(new PinJoint3D { Name = "SamePinXyz" }, Vector3.Zero, "Ball", "Ball");
            _h.Run(10);
            Assert.False(joint.IsActive);
            Assert.False(same.IsActive);
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        var entries = sink.Snapshot();
        Assert.Single(entries, e => e.Message.Contains("BadPinXyz", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Message.Contains("SamePinXyz", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExcludeNodesFromCollisionStopsContactsBetweenTheJoinedBodies(bool exclude)
    {
        _h.AddStaticBox(Vector3.Zero, new Vector3(1, 1, 1), name: "Wall");
        var box = _h.AddBox(new Vector3(0.6f, 0, 0), size: 0.5f, name: "Box"); // overlapping the wall
        box.GravityScale = 0;
        box.ContactMonitor = true;
        var touches = 0;
        box.BodyEntered += _ => touches++;
        AddJoint(new PinJoint3D { Name = "Pin", ExcludeNodesFromCollision = exclude }, new Vector3(0.6f, 0, 0), "Wall", "Box");
        _h.Run(30);
        if (exclude)
            Assert.Equal(0, touches);
        else
            Assert.True(touches > 0);
    }

    [Fact]
    public void JointPropertiesRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        root.AddChild(new PinJoint3D { Name = "Pin", NodeA = "../A", NodeB = "../B", ExcludeNodesFromCollision = false, Enabled = false, BreakForce = 50, BreakTorque = 20 });
        root.AddChild(new HingeJoint3D
        {
            Name = "Hinge",
            UseLimit = true,
            LowerAngleDegrees = -45,
            UpperAngleDegrees = 10,
            MotorEnabled = true,
            MotorTargetVelocity = 2,
            MotorMaxTorque = 7,
        });
        foreach (var child in root.Children)
            child.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var pin = copy.GetNode<PinJoint3D>("Pin");
        Assert.Equal(("../A", "../B"), (pin.NodeA.ToString(), pin.NodeB.ToString()));
        Assert.Equal((false, false, 50f, 20f), (pin.ExcludeNodesFromCollision, pin.Enabled, pin.BreakForce, pin.BreakTorque));
        var hinge = copy.GetNode<HingeJoint3D>("Hinge");
        Assert.True(hinge.UseLimit);
        Assert.Equal(-45f, hinge.LowerAngleDegrees, 3);
        Assert.Equal(10f, hinge.UpperAngleDegrees, 3);
        Assert.Equal((true, 2f, 7f), (hinge.MotorEnabled, hinge.MotorTargetVelocity, hinge.MotorMaxTorque));
        root.Free();
        copy.Free();
    }
}
