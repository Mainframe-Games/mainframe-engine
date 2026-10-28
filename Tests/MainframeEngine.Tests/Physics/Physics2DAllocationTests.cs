using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// 2D allocation gate. Box2D.NET 3.1.654 allocates inside <c>b2World_Step</c> itself (a step context and solver arrays,
/// a few hundred bytes per step; ADR 0023), so the gate measures the engine's share: everything a steady-state frame
/// allocates minus what the library allocated inside its steps must be zero.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class Physics2DAllocationTests
{
    [Fact]
    public void SteadyState2DPhysicsAllocatesNothingBeyondBox2DItself()
    {
        using var h = new PhysicsHarness2D();
        h.Server.DebugDrawEnabled = true;
        h.AddGround(width: 20000);
        for (var i = 0; i < 480; i++)
        {
            var box = h.AddBox(new Vector2(i % 60 * 70 - 2100, -25 - i / 60 * 60), 50, $"Box{i}");
            box.ContactMonitor = i % 8 == 0;
            box.CanSleep = i % 2 == 0;
        }

        var area = h.AddArea(new Vector2(0, -100), new Vector2(1000, 200));
        var entered = 0;
        area.BodyEntered += _ => entered++;
        var pickup = h.AddArea(new Vector2(200, -100), new Vector2(50, 50), "Pickup"); // area-vs-area every step
        // A swinging chain of pins from the world and a spring on its end (joints: creation, debug draw, events).
        for (var i = 0; i < 8; i++)
        {
            h.AddBox(new Vector2(-3000 + (i + 1) * 30, -800), 20, $"Link{i}");
            var pin = new PinJoint2D { Name = $"Pin{i}", Position = new Vector2(-3000 + i * 30 + 15, -800) };
            h.Add(pin);
            pin.NodeA = i == 0 ? default(NodePath) : $"../Link{i - 1}";
            pin.NodeB = $"../Link{i}";
        }

        h.AddBox(new Vector2(-2730, -800), 20, "Bob");
        var spring = new DampedSpringJoint2D { Name = "Spring", Position = new Vector2(-2760, -800), Rotation = -MathF.PI / 2, Length = 60 };
        h.Add(spring);
        spring.NodeA = "../Link7";
        spring.NodeB = "../Bob";
        // One of each other type, hung from the world well away from everything (G9.4).
        Joint2D Hang(Joint2D joint, string body, Vector2 at)
        {
            h.AddBox(at, 20, body);
            joint.Position = at;
            h.Add(joint);
            joint.NodeB = "../" + body;
            return joint;
        }

        Hang(new SliderJoint2D { Name = "Slider", UseLimit = true, MotorEnabled = true, MotorTargetVelocity = 30, SpringEnabled = true }, "Carriage", new Vector2(-2500, -800));
        Hang(new GrooveJoint2D { Name = "Groove", Length = 100, InitialOffset = 0 }, "Bead", new Vector2(-2400, -800));
        Hang(new WeldJoint2D { Name = "Weld", AngularHertz = 2, BreakForce = 1e9f }, "Sign", new Vector2(-2300, -800));
        Hang(new WheelJoint2D { Name = "Wheel", Rotation = -MathF.PI / 2, MotorEnabled = true, MotorTargetVelocity = 3 }, "Tyre", new Vector2(-2200, -800));
        area.AreaEntered += _ => entered++;
        pickup.AreaEntered += _ => entered++;
        for (var i = 0; i < 4; i++)
        {
            var character = new WalkerCharacter2D { Name = $"Walker{i}", Position = new Vector2(3000 + i * 200, -100), Walk = new Vector2(40, 0) };
            character.AddChild(new CollisionShape2D { Shape = new CapsuleShape2D { Radius = 20, Height = 80 } });
            h.Add(character);
        }

        var query = h.Query;
        var results = new List<Node2D>(64);
        var probe = new CircleShape2D { Radius = 20 };
        var probeAt = Transform2D.FromTrs(new Vector2(0, -600), 0, Vector2.One);

        void Frame(float delta)
        {
            h.Run(1, delta);
            query.RayCast(new Vector2(30, -1000), new Vector2(30, 1000), out _);
            query.ShapeCast(probe, probeAt, new Vector2(0, 1000), out _);
            results.Clear();
            query.IntersectShape(probe, probeAt, results);
            query.IntersectPoint(new Vector2(10, -10), results);
            h.Tree.Root.DebugLines.Clear();
        }

        PhysicsSpace2D.MeasureLibraryAllocations = true;
        try
        {
            for (var i = 0; i < 240; i++)
                Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);

            var libraryBefore = h.Space.LibraryAllocatedBytes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 120; i++)
                Frame(i % 3 == 0 ? 1f / 90 : 1f / 40);
            var total = GC.GetAllocatedBytesForCurrentThread() - before;
            var library = h.Space.LibraryAllocatedBytes - libraryBefore;

            Assert.True(total - library == 0, $"2D frames allocated {total} bytes, {library} of them inside b2World_Step");
        }
        finally
        {
            PhysicsSpace2D.MeasureLibraryAllocations = false;
        }
    }
}
