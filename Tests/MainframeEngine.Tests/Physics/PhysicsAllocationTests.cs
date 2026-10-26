using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// The physics allocation gate: steady-state physics frames over ~500 bodies (stepping, interpolation, contact
/// monitors, areas, characters, debug draw, queries) allocate no managed memory on the main thread. Single-threaded,
/// everything (Jitter2 included) must allocate nothing; multi-threaded, Jitter2's worker pool rarely allocates 56 B
/// inside <c>World.Step</c> (about once per 5 000 steps), so that run excludes what the library allocated inside its
/// steps. Runs alone: it allocates heavily while setting up, which would disturb other tests' allocation gates.
/// </summary>
[Collection(nameof(SerialAllocationGates))]
public sealed class PhysicsAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyState3DPhysicsDoesNotAllocate(bool multiThreaded)
    {
        using var h = new PhysicsHarness3D(new PhysicsSettings3D { MultiThreaded = multiThreaded });
        h.Server.DebugDrawEnabled = true;
        h.AddFloor(size: 200);
        // 480 boxes in a loose grid falling onto the floor and stacking a little; some monitor contacts.
        for (var i = 0; i < 480; i++)
        {
            var box = h.AddBox(new Vector3(i % 24 * 1.6f - 19, 0.5f + i / 240 * 1.2f, i / 24 % 10 * 1.6f - 8), name: $"Box{i}");
            box.ContactMonitor = i % 8 == 0;
            box.CanSleep = i % 2 == 0; // half keep simulating forever
        }

        var area = h.AddArea(new Vector3(0, 1, 0), new Vector3(10, 2, 10));
        var entered = 0;
        area.BodyEntered += _ => entered++;
        var pickup = h.AddArea(new Vector3(2, 1, 0), new Vector3(1, 1, 1), "Pickup"); // area-vs-area every step
        area.AreaEntered += _ => entered++;
        pickup.AreaEntered += _ => entered++;
        for (var i = 0; i < 4; i++)
        {
            var character = new WalkerCharacter3D { Name = $"Walker{i}", Position = new Vector3(-30 + i * 3, 1, 30), Walk = new Vector3(0.5f, 0, -0.2f) };
            character.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
            h.Add(character);
        }

        var query = h.Query;
        var results = new List<CollisionObject3D>(64);
        var probe = new SphereShape3D { Radius = 0.5f };
        var probeAt = Transform3D.FromTrs(new Vector3(0, 5, 0), Quaternion.Identity, Vector3.One);

        void Frame(float delta)
        {
            h.Run(1, delta);
            query.RayCast(new Vector3(0.3f, 10, 0.3f), new Vector3(0.3f, -10, 0.3f), out _);
            query.ShapeCast(probe, probeAt, new Vector3(0, -10, 0), out _);
            results.Clear();
            query.IntersectShape(probe, probeAt, results);
            query.IntersectPoint(new Vector3(0.1f, 0.1f, 0.1f), results);
            h.Tree.Root.DebugLines.Clear(); // what the render server does each frame
        }

        // Warm-up: bodies settle, lists and pools reach their size, everything is JIT-compiled. Mixed frame lengths
        // exercise interpolation with zero, one and two steps per frame.
        for (var i = 0; i < 240; i++)
            Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);

        PhysicsSpace3D.MeasureLibraryAllocations = multiThreaded;
        try
        {
            var library = h.Space.LibraryAllocatedBytes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 120; i++)
                Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            library = h.Space.LibraryAllocatedBytes - library;

            Assert.True(allocated - library == 0, $"steady-state 3D physics frames allocated {allocated} bytes ({library} inside Jitter2's step)");
            if (!multiThreaded)
                Assert.Equal(0, allocated);
        }
        finally
        {
            PhysicsSpace3D.MeasureLibraryAllocations = false;
        }


        Assert.True(h.Space.ObjectCount > 480);
    }
}

/// <summary>
/// The 3D joint allocation gate (ADR 0211): 320 joints (hinged chains with motors and springs, pinned chains, sprung
/// sliders, distance springs, cone-twists, breakable welds) step, read their state and draw their debug lines without
/// allocating.
/// </summary>
[Collection(nameof(SerialAllocationGates))]
public sealed class JointAllocationTests
{
    [Fact]
    public void SteadyState3DJointsDoNotAllocate()
    {
        using var h = new PhysicsHarness3D();
        h.Server.DebugDrawEnabled = true;
        h.AddFloor(size: 200);
        var joints = JointGate.AddJoints(h);
        Assert.True(joints.Count >= 300, $"{joints.Count} joints");

        var sum = 0f;
        void Frame()
        {
            h.Run(1);
            foreach (var joint in joints)
                sum += joint.GetAppliedForce() + joint.GetAppliedTorque() + joint.GetSeparation();
            h.Tree.Root.DebugLines.Clear();
        }

        // Warm-up: bodies settle, lists and pools reach their size, everything is JIT-compiled.
        for (var i = 0; i < 240; i++)
            Frame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++)
            Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
        Assert.Equal(joints.Count, h.Space.JointCount);
    }
}

/// <summary>Builds the joint allocation gate's scene.</summary>
internal static class JointGate
{
    public static List<Joint3D> AddJoints(PhysicsHarness3D h)
    {
        var joints = new List<Joint3D>();
        T Join<T>(T joint, Vector3 at, string? a, string b) where T : Joint3D
        {
            // The parts collide with the floor only, not with each other: contacts between folding chains come and go
            // chaotically, and what the library allocates for new contact pairs is not what this gate measures.
            if (h.Scene.GetNodeOrNull(b) is CollisionObject3D part)
            {
                part.CollisionLayer = 2;
                part.CollisionMask = 1;
            }

            joint.Position = at;
            h.Add(joint);
            joint.NodeA = a is null ? default(NodePath) : "../" + a;
            joint.NodeB = "../" + b;
            joints.Add(joint);
            return joint;
        }

        // 10 hinged chains of 10 planks (the first with a motor), and 10 pinned chains of 10 balls.
        for (var c = 0; c < 10; c++)
        {
            var z = c * 3f - 15f;
            for (var i = 0; i < 10; i++)
            {
                var plank = h.AddBox(new Vector3(i * 0.6f + 0.3f, 10, z), size: 0.5f, name: $"Plank{c}_{i}");
                plank.CanSleep = c % 2 == 0;
                Join(new HingeJoint3D { Name = $"Hinge{c}_{i}", UseLimit = i % 2 == 0, MotorEnabled = i == 0, MotorTargetVelocity = 1f, SpringEnabled = i % 3 == 1 },
                    new Vector3(i * 0.6f, 10, z), i == 0 ? null : $"Plank{c}_{i - 1}", plank.Name);
            }

            for (var i = 0; i < 10; i++)
            {
                var ball = new RigidBody3D { Name = $"Ball{c}_{i}", Position = new Vector3(-i * 0.5f - 0.5f, 10, z) };
                ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.15f } });
                h.Add(ball);
                Join(new PinJoint3D { Name = $"Pin{c}_{i}" }, new Vector3(-i * 0.5f, 10, z), i == 0 ? null : $"Ball{c}_{i - 1}", ball.Name);
            }

            // A few of every other type: sprung sliders with motors, distance springs, cone-twists and breakable welds.
            for (var i = 0; i < 3; i++)
            {
                var x = -10f - i * 2f;
                var piston = h.AddBox(new Vector3(x, 3, z), size: 0.3f, name: $"Piston{c}_{i}");
                Join(new SliderJoint3D { Name = $"Slider{c}_{i}", UseLimit = true, MotorEnabled = true, SpringEnabled = true }, piston.Position, null, piston.Name);
                var weight = h.AddBox(new Vector3(x, 5, z), size: 0.3f, name: $"Weight{c}_{i}");
                Join(new DistanceJoint3D { Name = $"Spring{c}_{i}", AnchorB = new Vector3(0, -1, 0), SpringFrequency = 2f }, weight.Position + Vector3.UnitY,
                    null, weight.Name);
                var arm = h.AddBox(new Vector3(x, 8, z), size: 0.3f, name: $"Arm{c}_{i}");
                Join(new ConeTwistJoint3D { Name = $"Cone{c}_{i}", SwingSpanDegrees = 30, TwistSpanDegrees = 20 }, arm.Position + Vector3.UnitY * 0.3f,
                    null, arm.Name);
                var sign = h.AddBox(new Vector3(x + 1, 8, z), size: 0.3f, name: $"Sign{c}_{i}");
                Join(new WeldJoint3D { Name = $"Weld{c}_{i}", BreakForce = 1e6f, BreakTorque = 1e6f }, sign.Position, $"Arm{c}_{i}", sign.Name);
            }
        }

        return joints;
    }
}

/// <summary>Allocation gates that set up large scenes run without other tests in parallel.</summary>
[CollectionDefinition(nameof(SerialAllocationGates), DisableParallelization = true)]
public sealed class SerialAllocationGates;
