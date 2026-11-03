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
    public void ASliderStaysOnItsAxisWithinItsRange()
    {
        var carriage = _h.AddBox(Vector3.Zero, size: 0.4f, name: "Carriage");
        carriage.GravityScale = 0;
        var slider = AddJoint(new SliderJoint3D
        {
            Name = "Slider",
            UseLimit = true,
            LowerDistance = -0.25f,
            UpperDistance = 0.5f,
            MotorEnabled = true,
            MotorTargetVelocity = 1f,
        }, Vector3.Zero, nodeA: null, nodeB: "Carriage");
        carriage.ApplyImpulse(new Vector3(0, 2, 1)); // off the axis: held on it
        carriage.ApplyTorque(new Vector3(5, 0, 0)); // about it: no twist allowed
        _h.RunSeconds(1.5f);
        Assert.InRange(slider.GetPosition(), 0.48f, 0.52f); // driven to the upper limit
        Assert.InRange(carriage.Position.X, 0.48f, 0.52f);
        Assert.InRange(new Vector2(carriage.Position.Y, carriage.Position.Z).Length(), 0f, 0.01f);
        Assert.True(Quaternion.Dot(carriage.Rotation, Quaternion.Identity) > 0.9999f, $"turned: {carriage.Rotation}");

        slider.MotorTargetVelocity = -1f; // live
        _h.RunSeconds(1.5f);
        Assert.InRange(slider.GetPosition(), -0.27f, -0.23f);

        slider.AllowTwist = true; // re-created at the authored frame: may now turn about X
        slider.MotorEnabled = false;
        _h.Run(1);
        carriage.AngularVelocity = new Vector3(2, 0, 0);
        _h.RunSeconds(0.5f);
        Assert.True(MathF.Abs(carriage.AngularVelocity.X) > 1.5f, $"twisting freely: {carriage.AngularVelocity}");
        Assert.InRange(new Vector2(carriage.Position.Y, carriage.Position.Z).Length(), 0f, 0.01f);
    }

    [Fact]
    public void ConeTwistKeepsTheSwingAndTwistWithinTheirSpans()
    {
        // The joint's X points down (rotated −90° about Z): the arm hangs along it.
        var arm = _h.AddBox(new Vector3(0, -0.6f, 0), size: 0.3f, name: "Arm");
        var joint = AddJoint(new ConeTwistJoint3D { Name = "Shoulder", SwingSpanDegrees = 30, TwistSpanDegrees = 20 }, Vector3.Zero, null, "Arm",
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2));
        arm.ApplyImpulse(new Vector3(3, 0, 2)); // a hard sideways kick
        arm.ApplyTorque(new Vector3(0, 20, 0)); // and a twist about the arm
        var swing = 0f;
        var twist = 0f;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            swing = MathF.Max(swing, joint.GetSwingAngle());
            twist = MathF.Max(twist, MathF.Abs(joint.GetTwistAngle()));
        }

        Assert.True(swing > float.DegreesToRadians(20), $"it swung: {float.RadiansToDegrees(swing)}°");
        Assert.True(swing < float.DegreesToRadians(33), $"past the cone: {float.RadiansToDegrees(swing)}°");
        Assert.True(twist < float.DegreesToRadians(23), $"past the twist: {float.RadiansToDegrees(twist)}°");
        Assert.InRange(joint.GetSeparation(), 0f, 0.01f);

        joint.SwingSpanDegrees = 180; // live: no cone
        joint.TwistSpanDegrees = 180;
        arm.ApplyImpulse(new Vector3(4, 0, 0));
        swing = 0;
        for (var i = 0; i < 60; i++)
        {
            _h.Run(1);
            swing = MathF.Max(swing, joint.GetSwingAngle());
        }

        Assert.True(swing > float.DegreesToRadians(45), $"free: {float.RadiansToDegrees(swing)}°");
    }

    [Fact]
    public void ARopeAllowsSlackButNeverStretches()
    {
        var weight = AddBall(new Vector3(0, -1, 0), "Weight");
        var rope = AddJoint(new DistanceJoint3D { Name = "Rope", AnchorB = new Vector3(0, -1, 0) }, Vector3.Zero, null, "Weight");
        weight.LinearVelocity = new Vector3(0, 3, 0); // thrown up: the rope goes slack
        var shortest = float.MaxValue;
        var longest = 0f;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            shortest = MathF.Min(shortest, weight.Position.Length());
            longest = MathF.Max(longest, weight.Position.Length());
        }

        Assert.True(shortest < 0.7f, $"slack: {shortest}");
        Assert.True(longest < 1.02f, $"stretched to {longest} m");
        Assert.InRange(rope.GetLength(), 0.97f, 1.02f);

        rope.MinLength = 1f; // live: a rod
        weight.LinearVelocity = new Vector3(0, 3, 0);
        shortest = float.MaxValue;
        for (var i = 0; i < 30; i++)
        {
            _h.Run(1);
            shortest = MathF.Min(shortest, weight.Position.Length());
        }

        Assert.True(shortest > 0.97f, $"the rod shortened to {shortest}");
    }

    [Fact]
    public void ADistanceSpringOscillatesAtItsFrequency()
    {
        var weight = AddBall(new Vector3(0, -1.5f, 0), "Weight");
        weight.GravityScale = 0;
        var spring = AddJoint(new DistanceJoint3D { Name = "Spring", AnchorB = new Vector3(0, -1.5f, 0), RestLength = 1f, SpringFrequency = 1f, SpringDampingRatio = 0f },
            Vector3.Zero, null, "Weight");
        var crossings = new List<float>();
        var previous = weight.Position.Y + 1f;
        for (var i = 1; i <= 60 * 5; i++)
        {
            _h.Run(1);
            var y = weight.Position.Y + 1f; // displacement from the rest length
            if (previous < 0 && y >= 0)
                crossings.Add((i - 1 + -previous / (y - previous)) * PhysicsHarness3D.Step);
            previous = y;
        }

        Assert.True(spring.IsActive);
        Assert.True(crossings.Count >= 3, $"{crossings.Count} crossings");
        var period = (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        Assert.InRange(period, 0.97f, 1.03f);
    }

    [Fact]
    public void HingeAndSliderSpringsSettleAtTheirTargets()
    {
        var flap = _h.AddBox(new Vector3(0.5f, 0, 0), size: 0.4f, name: "Flap");
        var hinge = AddJoint(new HingeJoint3D { Name = "Hinge", SpringEnabled = true, SpringFrequency = 4f, SpringDampingRatio = 1f, SpringTargetAngleDegrees = 30 },
            Vector3.Zero, null, "Flap");
        flap.GravityScale = 0;
        var piston = _h.AddBox(new Vector3(0, 0, 3), size: 0.4f, name: "Piston");
        piston.GravityScale = 0;
        var slider = AddJoint(new SliderJoint3D { Name = "Slider", SpringEnabled = true, SpringFrequency = 3f, SpringDampingRatio = 1f, SpringTargetPosition = -0.4f },
            new Vector3(0, 0, 3), null, "Piston");
        _h.RunSeconds(3f);
        Assert.InRange(float.RadiansToDegrees(hinge.GetAngle()), 29f, 31f);
        Assert.InRange(slider.GetPosition(), -0.41f, -0.39f);

        hinge.SpringTargetAngleDegrees = -45; // live
        _h.RunSeconds(1.5f);
        Assert.InRange(float.RadiansToDegrees(hinge.GetAngle()), -46f, -44f);
    }

    [Fact]
    public void AWeldHoldsACantilever()
    {
        var beam = _h.AddBox(new Vector3(1f, 0, 0), size: 0.5f, name: "Beam");
        beam.Mass = 5;
        AddJoint(new WeldJoint3D { Name = "Weld" }, Vector3.Zero, null, "Beam");
        _h.RunSeconds(2f);
        Assert.InRange(Vector3.Distance(beam.Position, new Vector3(1f, 0, 0)), 0f, 0.02f);
        Assert.True(Quaternion.Dot(beam.Rotation, Quaternion.Identity) > 0.9998f, $"drooped: {beam.Rotation}");
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
        root.AddChild(new SliderJoint3D
        {
            Name = "Slider",
            AllowTwist = true,
            UseLimit = true,
            LowerDistance = -2,
            UpperDistance = 3,
            MotorEnabled = true,
            MotorMaxForce = 9,
            SpringEnabled = true,
            SpringTargetPosition = 0.5f,
        });
        root.AddChild(new ConeTwistJoint3D { Name = "Cone", SwingSpanDegrees = 25, TwistSpanDegrees = 15 });
        root.AddChild(new DistanceJoint3D { Name = "Rope", AnchorB = new Vector3(1, 2, 3), MinLength = 0.5f, MaxLength = 4, SpringFrequency = 3, RestLength = 2 });
        root.AddChild(new WeldJoint3D { Name = "Weld", BreakForce = 300 });
        foreach (var child in root.Children)
            child.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var slider = copy.GetNode<SliderJoint3D>("Slider");
        Assert.Equal((true, true, -2f, 3f, true, 9f, true, 0.5f), (slider.AllowTwist, slider.UseLimit, slider.LowerDistance, slider.UpperDistance,
            slider.MotorEnabled, slider.MotorMaxForce, slider.SpringEnabled, slider.SpringTargetPosition));
        var cone = copy.GetNode<ConeTwistJoint3D>("Cone");
        Assert.Equal(25f, cone.SwingSpanDegrees, 3);
        Assert.Equal(15f, cone.TwistSpanDegrees, 3);
        var rope = copy.GetNode<DistanceJoint3D>("Rope");
        Assert.Equal((new Vector3(1, 2, 3), 0.5f, 4f, 3f, 2f), (rope.AnchorB, rope.MinLength, rope.MaxLength, rope.SpringFrequency, rope.RestLength));
        Assert.Equal(300f, copy.GetNode<WeldJoint3D>("Weld").BreakForce);
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

    // Generic6DofJoint3D (G9.10b): the proposal's scenarios, in this file's naming.

    private RigidBody3D AddCube(Vector3 position, string name = "Box", float mass = 1f, float gravityScale = 1f, float size = 0.2f)
    {
        var body = new RigidBody3D { Name = name, Position = position, Mass = mass, LinearDamp = 0, AngularDamp = 0, GravityScale = gravityScale };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size) } });
        return _h.Add(body);
    }

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    [Fact]
    public void ADefault6DofJointLocksEveryAxisLikeAWeld()
    {
        AddCube(new Vector3(0.25f, 0, 0), size: 0.5f); // anchored at its edge: gravity twists the joint as well as pulling it
        var joint = AddJoint(new Generic6DofJoint3D { Name = "SixDof" }, Vector3.Zero, nodeA: null, nodeB: "Box");
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            Assert.InRange(joint.GetLinearOffset().Length(), 0f, 0.01f);
            Assert.InRange(MaxAbs(joint.GetAngularOffsetDegrees()), 0f, 1f);
        }

        Assert.True(joint.IsActive);
    }

    [Fact]
    public void AFree6DofAxisSlidesWhileTheOthersHold()
    {
        var box = AddCube(new Vector3(0.5f, 0, 0));
        var joint = AddJoint(new Generic6DofJoint3D { Name = "SixDof", LinearYMode = JointAxisMode.Free }, Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.RunSeconds(1f);
        var offset = joint.GetLinearOffset();
        Assert.True(offset.Y < -4.5f, $"it fell along −Y: {offset}");
        Assert.InRange(MathF.Abs(offset.X), 0f, 0.01f);
        Assert.InRange(MathF.Abs(offset.Z), 0f, 0.01f);
        Assert.InRange(MaxAbs(joint.GetAngularOffsetDegrees()), 0f, 1f);
        Assert.True(box.Position.Y < -4.5f);
    }

    [Fact]
    public void ALimited6DofAxisRestsOnItsLowerLimit()
    {
        AddCube(Vector3.Zero);
        var joint = AddJoint(new Generic6DofJoint3D { Name = "SixDof", LinearYMode = JointAxisMode.Limited, LinearYLower = -0.5f, LinearYUpper = 0.5f },
            Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.RunSeconds(2f);
        Assert.InRange(joint.GetLinearOffset().Y, -0.52f, -0.48f);
    }

    [Fact]
    public void Two6DofLinearLimitsMakeASquareRange()
    {
        var box = AddCube(Vector3.Zero);
        var joint = AddJoint(new Generic6DofJoint3D
        {
            Name = "SixDof",
            LinearXMode = JointAxisMode.Limited,
            LinearXLower = -0.3f,
            LinearXUpper = 0.3f,
            LinearZMode = JointAxisMode.Limited,
            LinearZLower = -0.3f,
            LinearZUpper = 0.3f,
        }, Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.Run(1);
        box.ApplyImpulse(new Vector3(5, 0, 5) / MathF.Sqrt(2));
        var reached = Vector2.Zero;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            var offset = joint.GetLinearOffset();
            Assert.InRange(MathF.Abs(offset.X), 0f, 0.32f);
            Assert.InRange(MathF.Abs(offset.Z), 0f, 0.32f);
            Assert.InRange(MathF.Abs(offset.Y), 0f, 0.01f);
            reached = Vector2.Max(reached, new Vector2(MathF.Abs(offset.X), MathF.Abs(offset.Z)));
        }

        Assert.True(reached.X > 0.28f && reached.Y > 0.28f, $"it reached the corner: {reached}");
    }

    [Fact]
    public void Each6DofAngularLimitHoldsItsOwnAxis()
    {
        var box = AddCube(Vector3.Zero, gravityScale: 0f);
        var joint = AddJoint(new Generic6DofJoint3D
        {
            Name = "SixDof",
            AngularXMode = JointAxisMode.Limited,
            AngularXLowerDegrees = -30,
            AngularXUpperDegrees = 30,
            AngularZMode = JointAxisMode.Limited,
            AngularZLowerDegrees = -60,
            AngularZUpperDegrees = 60,
        }, Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.Run(1);
        box.AngularVelocity = new Vector3(3, 0, 5);
        var reached = Vector3.Zero;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            var angles = joint.GetAngularOffsetDegrees();
            Assert.InRange(angles.X, -32f, 32f);
            Assert.InRange(angles.Z, -62f, 62f);
            Assert.InRange(angles.Y, -2f, 2f);
            reached = Vector3.Max(reached, new Vector3(MathF.Abs(angles.X), 0, MathF.Abs(angles.Z)));
        }

        Assert.True(reached.X > 28f && reached.Z > 58f, $"it reached both limits: {reached}");
    }

    [Fact]
    public void A6DofAngularMotorReachesItsSpeed()
    {
        var box = AddCube(Vector3.Zero, gravityScale: 0f);
        AddJoint(new Generic6DofJoint3D
        {
            Name = "SixDof",
            AngularZMode = JointAxisMode.Free,
            AngularZMotorEnabled = true,
            AngularZMotorTargetVelocity = 2f,
            AngularZMotorMaxTorque = 100f,
        }, Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.RunSeconds(1f);
        Assert.InRange(box.AngularVelocity.Z, 1.96f, 2.04f);
        Assert.InRange(MathF.Abs(box.AngularVelocity.X) + MathF.Abs(box.AngularVelocity.Y), 0f, 0.01f);
    }

    [Fact]
    public void A6DofLinearSpringOscillatesAtItsFrequency()
    {
        AddCube(Vector3.Zero, gravityScale: 0f);
        var joint = AddJoint(new Generic6DofJoint3D
        {
            Name = "SixDof",
            LinearYMode = JointAxisMode.Free,
            LinearYSpringEnabled = true,
            LinearYSpringFrequency = 1f,
            LinearYSpringDampingRatio = 0f,
            LinearYSpringTarget = 0.2f,
        }, Vector3.Zero, nodeA: null, nodeB: "Box");

        var crossings = new List<float>();
        var previous = joint.GetLinearOffset().Y - 0.2f;
        for (var i = 1; i <= 60 * 5; i++)
        {
            _h.Run(1);
            var y = joint.GetLinearOffset().Y - 0.2f;
            if (previous < 0 && y >= 0)
                crossings.Add((i - 1 + -previous / (y - previous)) * PhysicsHarness3D.Step);
            previous = y;
        }

        Assert.True(crossings.Count >= 3, $"{crossings.Count} crossings");
        var period = (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        Assert.InRange(period, 0.95f, 1.05f);
    }

    [Fact]
    public void A6DofModeChangeIsLiveAndKeepsTheJoint()
    {
        var box = AddCube(Vector3.Zero);
        var joint = AddJoint(new Generic6DofJoint3D { Name = "SixDof" }, Vector3.Zero, nodeA: null, nodeB: "Box");
        _h.RunSeconds(0.5f);
        Assert.InRange(box.Position.Y, -0.01f, 0.01f);
        var record = joint.Record!;
        var linear = record.SixDofLinear;

        joint.LinearYMode = JointAxisMode.Free;
        _h.RunSeconds(0.5f);
        Assert.True(box.Position.Y < -1f, $"it falls once Y is free: {box.Position}");
        Assert.Same(linear, joint.Record!.SixDofLinear);
        Assert.True(joint.IsActive);
        Assert.False(joint.IsBroken);
    }

    [Fact]
    public void A6DofJointOverItsBreakForceBreaks()
    {
        AddCube(new Vector3(0, -0.5f, 0), mass: 10f);
        var joint = AddJoint(new Generic6DofJoint3D { Name = "SixDof", BreakForce = 50f }, Vector3.Zero, nodeA: null, nodeB: "Box");
        var broken = 0;
        joint.Broken += () => broken++;
        _h.Run(2);
        Assert.Equal(1, broken);
        Assert.True(joint.IsBroken);
        Assert.False(joint.IsActive);
        _h.RunSeconds(0.5f);
        Assert.Equal(1, broken);
    }

    [Fact]
    public void A6DofJointRoundTripsThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        root.AddChild(new Generic6DofJoint3D
        {
            Name = "SixDof",
            LinearXMode = JointAxisMode.Free,
            LinearYMode = JointAxisMode.Limited,
            LinearYLower = -0.25f,
            LinearYUpper = 0.75f,
            LinearZMotorEnabled = true,
            LinearZMotorTargetVelocity = 1.5f,
            LinearZMotorMaxForce = 20f,
            LinearXSpringEnabled = true,
            LinearXSpringFrequency = 3f,
            LinearXSpringDampingRatio = 0.2f,
            LinearXSpringTarget = 0.1f,
            AngularXMode = JointAxisMode.Limited,
            AngularXLowerDegrees = -20,
            AngularXUpperDegrees = 35,
            AngularYMode = JointAxisMode.Free,
            AngularZMotorEnabled = true,
            AngularZMotorTargetVelocity = -2f,
            AngularZMotorMaxTorque = 7f,
            AngularYSpringEnabled = true,
            AngularYSpringFrequency = 4f,
            AngularYSpringTargetDegrees = 10,
        });
        root.GetChild(0).Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var j = copy.GetNode<Generic6DofJoint3D>("SixDof");
        Assert.Equal((JointAxisMode.Free, JointAxisMode.Limited, JointAxisMode.Locked), (j.LinearXMode, j.LinearYMode, j.LinearZMode));
        Assert.Equal((-0.25f, 0.75f), (j.LinearYLower, j.LinearYUpper));
        Assert.Equal((true, 1.5f, 20f), (j.LinearZMotorEnabled, j.LinearZMotorTargetVelocity, j.LinearZMotorMaxForce));
        Assert.Equal((true, 3f, 0.2f, 0.1f), (j.LinearXSpringEnabled, j.LinearXSpringFrequency, j.LinearXSpringDampingRatio, j.LinearXSpringTarget));
        Assert.Equal((JointAxisMode.Limited, JointAxisMode.Free, JointAxisMode.Locked), (j.AngularXMode, j.AngularYMode, j.AngularZMode));
        Assert.Equal(-20f, j.AngularXLowerDegrees, 3);
        Assert.Equal(35f, j.AngularXUpperDegrees, 3);
        Assert.Equal((true, -2f, 7f), (j.AngularZMotorEnabled, j.AngularZMotorTargetVelocity, j.AngularZMotorMaxTorque));
        Assert.Equal((true, 4f), (j.AngularYSpringEnabled, j.AngularYSpringFrequency));
        Assert.Equal(10f, j.AngularYSpringTargetDegrees, 3);
        root.Free();
        copy.Free();
    }

    [Fact]
    public void A6DofSceneOnWorkerThreadsStepsTheSameEveryRun()
    {
        // Jitter2 runs the 6-DOF constraints' Prepare/Iterate on its worker threads (ADR 0234): in deterministic mode two
        // runs of the same scene must agree to the bit.
        static Vector3[] Run()
        {
            using var h = new PhysicsHarness3D(new PhysicsSettings3D { MultiThreaded = true, Deterministic = true });
            var bodies = new List<RigidBody3D>();
            for (var i = 0; i < 40; i++)
            {
                var box = h.AddBox(new Vector3(i % 8 * 2f, 3, i / 8 * 2f), size: 0.3f, name: $"Box{i}");
                bodies.Add(box);
                var joint = new Generic6DofJoint3D
                {
                    Name = $"SixDof{i}",
                    Position = box.Position + Vector3.UnitY * 0.5f,
                    AngularXMode = JointAxisMode.Limited,
                    AngularZMode = JointAxisMode.Free,
                    AngularZMotorEnabled = i % 2 == 0,
                    AngularZMotorTargetVelocity = 1f,
                    LinearYMode = JointAxisMode.Limited,
                    LinearYLower = -0.3f,
                    LinearYUpper = 0.1f,
                    LinearXSpringEnabled = i % 3 == 0,
                    LinearXMode = JointAxisMode.Free,
                };
                h.Add(joint);
                joint.NodeB = "../" + box.Name;
                box.AngularVelocity = new Vector3(i % 5 - 2, 1, 0);
            }

            h.RunSeconds(2f);
            return [.. bodies.Select(b => b.Position)];
        }

        Assert.Equal(Run(), Run());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALong6DofChainFlailingFor30SecondsStaysFinite(bool slidingX)
    {
        // The joint benchmark's chain: 100 light links swinging from the world flail wildly (Jitter2's own pin chain
        // does too). With every linear axis locked the rows are a ball socket's; with X limited they follow frame A.
        // Either way nothing may blow up (an earlier frame-axis-only version went NaN within 4 s).
        var link = new SphereShape3D { Radius = 0.1f };
        var bodies = new List<RigidBody3D>();
        for (var i = 0; i < 100; i++)
        {
            var body = new RigidBody3D { Name = $"Link{i}", Position = new Vector3((i + 1) * 0.3f, 40, 0), CanSleep = false };
            body.AddChild(new CollisionShape3D { Shape = link });
            bodies.Add(_h.Add(body));
            var joint = new Generic6DofJoint3D
            {
                Name = $"Joint{i}",
                Position = new Vector3(i * 0.3f + (i == 0 ? 0 : 0.15f), 40, 0),
                AngularXMode = JointAxisMode.Free,
                AngularYMode = JointAxisMode.Free,
                AngularZMode = JointAxisMode.Free,
                LinearXMode = slidingX ? JointAxisMode.Limited : JointAxisMode.Locked,
                LinearXLower = -0.05f,
                LinearXUpper = 0.05f,
            };
            _h.Add(joint);
            joint.NodeA = i == 0 ? default(NodePath) : $"../Link{i - 1}";
            joint.NodeB = $"../Link{i}";
        }

        for (var second = 0; second < 30; second++)
        {
            _h.RunSeconds(1f);
            foreach (var body in bodies)
                Assert.True(float.IsFinite(body.Position.X + body.Position.Y + body.Position.Z), $"{body.Name} at {second + 1} s: {body.Position}");
        }
    }
}
