using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Breaking 3D joints (ADR 0211): thresholds, the Broken signal, freeing in handlers, Rebuild.</summary>
public sealed class JointBreakTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private T AddJoint<T>(T joint, Vector3 position, string? nodeA, string nodeB) where T : Joint3D
    {
        joint.Position = position;
        _h.Add(joint);
        joint.NodeA = nodeA is null ? default(NodePath) : "../" + nodeA;
        joint.NodeB = "../" + nodeB;
        return joint;
    }

    [Fact]
    public void AWeldedShelfBreaksOnceUnderAFallingCrate()
    {
        _h.AddStaticBox(new Vector3(0, 2, 0), new Vector3(0.2f, 4, 2), name: "Wall");
        var shelf = _h.AddBox(new Vector3(0.6f, 2, 0), size: 1f, name: "Shelf");
        shelf.Mass = 1;
        var weld = AddJoint(new WeldJoint3D { Name = "Weld", BreakForce = 400 }, new Vector3(0.1f, 2, 0), "Wall", "Shelf");
        var broken = 0;
        var stepAtBreak = -1L;
        weld.Broken += () =>
        {
            broken++;
            stepAtBreak = _h.Space.StepCount;
            Assert.True(weld.IsBroken);
            Assert.False(weld.IsActive);
            Assert.True(weld.GetAppliedForce() > weld.BreakForce || weld.GetAppliedTorque() > 0);
        };

        _h.RunSeconds(1f);
        Assert.Equal(0, broken); // its own weight (10 N) is far below the threshold
        Assert.InRange(shelf.Position.Y, 1.98f, 2.02f);

        var crate = _h.AddBox(new Vector3(0.8f, 4.5f, 0), size: 0.5f, name: "Crate");
        crate.Mass = 40;
        _h.RunSeconds(2f);
        Assert.Equal(1, broken);
        Assert.True(stepAtBreak > 0);
        Assert.True(weld.IsBroken);
        Assert.True(shelf.Position.Y < 1f, $"the shelf fell: {shelf.Position}");
        Assert.Equal(1, _h.Space.BrokenJointTotal);

        // Rebuild restores it at its current pose; a threshold of 0 never breaks.
        shelf.Teleport(Transform3D.FromTrs(new Vector3(0.6f, 2, 0), Quaternion.Identity, Vector3.One));
        shelf.LinearVelocity = Vector3.Zero;
        shelf.AngularVelocity = Vector3.Zero;
        crate.Free();
        weld.BreakForce = 0;
        weld.Rebuild();
        Assert.False(weld.IsBroken);
        _h.Run(1);
        Assert.True(weld.IsActive);
        var heavy = _h.AddBox(new Vector3(0.8f, 4.5f, 0), size: 0.5f, name: "Heavy");
        heavy.Mass = 40;
        _h.RunSeconds(2f);
        Assert.Equal(1, broken);
        Assert.True(weld.IsActive);
        Assert.InRange(shelf.Position.Y, 1.9f, 2.1f);
    }

    [Fact]
    public void BreakTorqueBreaksATwistedHinge()
    {
        var wheel = _h.AddBox(Vector3.Zero, size: 1f, name: "Wheel");
        wheel.GravityScale = 0;
        var hinge = AddJoint(new HingeJoint3D { Name = "Hinge", UseLimit = true, LowerAngleDegrees = -10, UpperAngleDegrees = 10, BreakTorque = 50 },
            Vector3.Zero, null, "Wheel");
        var broken = 0;
        hinge.Broken += () => broken++;
        _h.RunSeconds(0.5f);
        Assert.Equal(0, broken);
        for (var i = 0; i < 30; i++)
        {
            wheel.ApplyTorque(new Vector3(0, 0, 400)); // against the limit
            _h.Run(1);
        }

        Assert.Equal(1, broken);
        Assert.True(hinge.GetAppliedTorque() > 50);
        Assert.True(MathF.Abs(wheel.AngularVelocity.Z) > 1f, "spins freely once broken");
    }

    [Fact]
    public void JointsBrokenInOneStepSignalInCreationOrderAndMayBeFreedByHandlers()
    {
        var names = new List<string>();
        var joints = new List<PinJoint3D>();
        for (var i = 0; i < 4; i++)
        {
            var box = _h.AddBox(new Vector3(i * 2, -1, 0), size: 0.5f, name: $"Box{i}");
            box.Mass = 10;
            var pin = AddJoint(new PinJoint3D { Name = $"Pin{i}", BreakForce = 1 }, new Vector3(i * 2, 0, 0), null, box.Name);
            pin.Broken += () =>
            {
                names.Add(pin.Name);
                // A handler frees the next joint and its body: its signal is skipped, nothing dangles.
                if (pin.Name == "Pin1")
                {
                    _h.Scene.GetNode<PinJoint3D>("Pin2").Free();
                    _h.Scene.GetNode<RigidBody3D>("Box2").Free();
                }
            };
            joints.Add(pin);
        }

        _h.Run(3);
        Assert.Equal(["Pin0", "Pin1", "Pin3"], names);
        Assert.Equal(3, _h.Space.JointCount);
        _h.Run(3);
        Assert.Equal(3, names.Count); // once each
    }
}
