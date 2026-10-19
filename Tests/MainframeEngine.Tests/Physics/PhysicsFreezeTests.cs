using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Godot's <c>freeze</c>/<c>freeze_mode</c> on rigid bodies: static and kinematic freezing, live switches, joints kept,
/// starting at rest, and no interpolation step left over.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class PhysicsFreezeTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    private (RigidBody2D Box, PinJoint2D Pin) Pendulum()
    {
        var box = _h.AddBox(new Vector2(100, 0), size: 20);
        var pin = new PinJoint2D { Name = "Pin" };
        _h.Add(pin);
        pin.NodeB = "../Box";
        return (box, pin);
    }

    [Fact]
    public void AStaticFreezeHoldsASwingingJointedBodyAndUnfreezingLetsItGoFromRest()
    {
        var (box, pin) = Pendulum();
        _h.RunSeconds(0.3f); // swinging down
        Assert.True(box.LinearVelocity.Length() > 50);

        box.Freeze = true; // FreezeMode.Static by default
        Assert.Equal(Vector2.Zero, box.LinearVelocity);
        Assert.Equal(0f, box.AngularVelocity);
        var held = box.Position;
        _h.Run(1);
        Assert.Equal(held, box.Position); // no interpolation step left to play out
        _h.RunSeconds(1f);
        Assert.Equal(held, box.Position);
        Assert.True(pin.IsActive);
        Assert.Equal(1, _h.Space.ActiveJointCount);

        box.Freeze = false;
        Assert.Equal(Vector2.Zero, box.LinearVelocity); // starts at rest, not with the speed it had
        _h.Run(1);
        Assert.InRange(Vector2.Distance(held, box.Position), 0f, 1f); // no jump from a stale interpolation pose
        _h.RunSeconds(0.5f);
        Assert.True(pin.IsActive);
        Assert.InRange(box.Position.Length(), 97f, 103f); // still on its pin
        Assert.True(Vector2.Distance(held, box.Position) > 5f, "swinging again");
    }

    [Fact]
    public void AKinematicFreezeIgnoresGravityFollowsCodeAndKeepsItsJoints()
    {
        var (box, pin) = Pendulum();
        box.FreezeMode = FreezeMode.Kinematic;
        box.Freeze = true;
        _h.RunSeconds(0.5f);
        Assert.Equal(new Vector2(100, 0), box.Position); // no gravity, held by nothing but code
        Assert.Equal(Vector2.Zero, box.LinearVelocity);

        // ToolStations: a held piece moves with the gesture (absolute targets: outside OnPhysicsProcess a moving body's
        // Position reads its interpolated render pose, a step behind).
        for (var i = 1; i <= 30; i++)
        {
            box.Position = new Vector2(100, 2 * i);
            _h.Run(1);
        }

        Assert.InRange(box.Position.Y, 57.5f, 60.5f); // the 60 px target, its render pose up to a step (2 px) behind
        Assert.True(pin.IsActive); // the pin pulls on nothing while the body is kinematic, but it is kept

        box.Freeze = false;
        Assert.Equal(Vector2.Zero, box.LinearVelocity); // at rest, not carrying the speed that reached its last target
        _h.RunSeconds(1f);
        Assert.True(pin.IsActive);
        Assert.InRange(box.Position.Length(), 97f, 103f); // the pin pulls it back to its radius once dynamic
    }

    [Fact]
    public void ChangingTheFreezeModeWhileFrozenSwitchesLiveAndModeIsOverriddenWhileFrozen()
    {
        var box = _h.AddBox(Vector2.Zero, size: 20);
        box.Freeze = true;
        Assert.Equal(PhysicsBodyKind.Static, box.Record!.Kind);
        box.FreezeMode = FreezeMode.Kinematic;
        Assert.Equal(PhysicsBodyKind.Kinematic, box.Record.Kind);
        box.Mode = RigidBodyMode.Kinematic;
        box.FreezeMode = FreezeMode.Static;
        Assert.Equal(PhysicsBodyKind.Static, box.Record.Kind); // Freeze wins over Mode
        box.Freeze = false;
        Assert.Equal(PhysicsBodyKind.Kinematic, box.Record.Kind); // back to its Mode
    }

    [Fact]
    public void DynamicBodiesRestOnAStaticFrozenBody()
    {
        var plate = _h.AddBox(new Vector2(0, 0), size: 200, name: "Plate");
        plate.Freeze = true;
        var crate = _h.AddBox(new Vector2(0, -200), size: 20, name: "Crate");
        _h.RunSeconds(2f);
        Assert.Equal(Vector2.Zero, plate.Position);
        Assert.InRange(crate.Position.Y, -112f, -108f); // resting on the plate's top (−100) at half its size
    }

    [Fact]
    public void A3DStaticFreezeHoldsTheBodyAndUnfreezingDropsItFromRest()
    {
        using var h = new PhysicsHarness3D();
        var box = h.AddBox(new Vector3(0, 10, 0));
        h.RunSeconds(0.3f);
        box.Freeze = true;
        Assert.Equal(Vector3.Zero, box.LinearVelocity);
        var held = box.Position;
        h.RunSeconds(1f);
        Assert.Equal(held, box.Position);

        box.FreezeMode = FreezeMode.Kinematic;
        box.Position += new Vector3(1, 0, 0);
        h.Run(2);
        Assert.InRange(box.Position.X, 0.99f, 1.01f);

        box.Freeze = false;
        Assert.Equal(Vector3.Zero, box.LinearVelocity);
        h.RunSeconds(0.5f);
        Assert.True(box.Position.Y < held.Y - 0.5f, "falling again");
    }
}
