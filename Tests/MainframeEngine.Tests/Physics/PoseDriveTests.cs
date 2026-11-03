using System.Numerics;
using System.Runtime.CompilerServices;
using Jitter2;

namespace MainframeEngine.Tests.Physics;

/// <summary>The active-ragdoll drive (G9.10c, ADR 0235): <see cref="PoseDrive"/> through its <see cref="BoneDrive3D"/> node.</summary>
public sealed class PoseDriveTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private RigidBody3D AddCube(Vector3 position, string name = "Box", float mass = 1f, float gravityScale = 0f, Vector3? size = null)
    {
        var body = new RigidBody3D { Name = name, Position = position, Mass = mass, LinearDamp = 0, AngularDamp = 0, GravityScale = gravityScale };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size ?? new Vector3(0.3f) } });
        return _h.Add(body);
    }

    private BoneDrive3D AddDrive(string body, string? world = null)
    {
        var drive = new BoneDrive3D { Name = body + "Drive" };
        _h.Add(drive);
        drive.NodeA = world is null ? default(NodePath) : "../" + world;
        drive.NodeB = "../" + body;
        return drive;
    }

    private static float AngleBetween(Quaternion a, Quaternion b) => float.RadiansToDegrees(PoseDrive.RotationError(a, b).Length());

    [Fact]
    public void PoseDrivesDataFitsJitter2sConstraintData() =>
        Assert.InRange(Unsafe.SizeOf<PoseDrive.Values>(), 1, Precision.ConstraintSizeFull);

    [Fact]
    public void AnAngularDriveTurnsItsBodyOntoTheTargetRotation()
    {
        var box = AddCube(Vector3.Zero);
        var drive = AddDrive("Box");
        var target = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0, 1, 1)), 1.5f);
        _h.Run(1);
        drive.Drive(target, Vector3.Zero, 4f, 1f, 100f, 0f, 0f, 0f);
        _h.RunSeconds(2f);
        Assert.InRange(AngleBetween(box.Rotation, target), 0f, 2f);
        Assert.InRange(drive.GetErrorDegrees(), 0f, 2f);
        Assert.InRange(box.Position.Length(), 0f, 1e-3f); // the angular part pushes nothing
    }

    [Fact]
    public void AHoldKeepsItsBodyUpAgainstGravity()
    {
        var box = AddCube(new Vector3(0, 2, 0), gravityScale: 1f);
        var drive = AddDrive("Box");
        _h.Run(1);
        drive.Drive(Quaternion.Identity, new Vector3(0, 2, 0), 4f, 1f, 100f, 4f, 1f, 1000f);
        _h.RunSeconds(2f);
        // A soft hold sags by about g / ω² = 9.81 / (2π·4)² ≈ 1.6 cm.
        Assert.InRange(box.Position.Y, 1.97f, 2.0f);
        Assert.InRange(drive.GetPositionError(), 0f, 0.03f);
    }

    [Theory]
    [InlineData(1f, 30f, 180f)]
    [InlineData(100f, 0f, 5f)]
    public void ADrivesMaxTorqueLimitsWhatItCanLift(float maxTorque, float minSag, float maxSag)
    {
        // A 1 kg, 0.5 m arm pinned at its end: holding it level takes 1 × 9.81 × 0.25 ≈ 2.5 N·m. A drive's stiffness is
        // the body's inertia (about its centre, 0.021 kg·m²) × ω²: at 10 Hz about 82 N·m/rad, a 1.7° sag.
        var arm = AddCube(new Vector3(0.25f, 1, 0), "Arm", gravityScale: 1f, size: new Vector3(0.5f, 0.05f, 0.05f));
        var pin = new PinJoint3D { Name = "Pin", Position = new Vector3(0, 1, 0) };
        _h.Add(pin);
        pin.NodeB = "../Arm";
        var drive = AddDrive("Arm");
        _h.Run(1);
        drive.Drive(Quaternion.Identity, Vector3.Zero, 10f, 1f, maxTorque, 0f, 0f, 0f);
        _h.RunSeconds(2f);
        var sag = AngleBetween(arm.Rotation, Quaternion.Identity);
        Assert.InRange(sag, minSag, maxSag);
    }

    [Fact]
    public void ADrivesMotionDependsOnItsFrequencyNotTheMass()
    {
        var light = AddCube(Vector3.Zero, "Light", mass: 1f);
        var heavy = AddCube(new Vector3(3, 0, 0), "Heavy", mass: 10f);
        var lightDrive = AddDrive("Light");
        var heavyDrive = AddDrive("Heavy");
        var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        _h.Run(1);
        lightDrive.Drive(target, Vector3.Zero, 2f, 0.5f, 1e6f, 0f, 0f, 0f);
        heavyDrive.Drive(target, Vector3.Zero, 2f, 0.5f, 1e6f, 0f, 0f, 0f);
        for (var i = 0; i < 30; i++)
        {
            _h.Run(1);
            Assert.InRange(MathF.Abs(AngleBetween(light.Rotation, target) - AngleBetween(heavy.Rotation, target)), 0f, 0.5f);
        }

        Assert.InRange(AngleBetween(light.Rotation, Quaternion.Identity), 20f, 80f); // it moved, not done yet
    }

    [Fact]
    public void AStoppedDriveLetsItsBodyGo()
    {
        var box = AddCube(new Vector3(0, 2, 0), gravityScale: 1f);
        var drive = AddDrive("Box");
        _h.Run(1);
        drive.Drive(Quaternion.Identity, new Vector3(0, 2, 0), 4f, 1f, 100f, 4f, 1f, 1000f);
        _h.RunSeconds(1f);
        Assert.True(drive.IsDriving);
        drive.Stop();
        _h.RunSeconds(1f);
        Assert.False(drive.IsDriving);
        Assert.True(box.Position.Y < 0f, $"it fell: {box.Position}");
    }

    [Fact]
    public void ALocalDriveTurnsBodyBToItsTargetRelativeToATurnedBodyA()
    {
        // Body A turned arbitrarily and held by a weld; B pinned to it and driven 40° about A's local X.
        var turned = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 1.1f));
        var a = AddCube(new Vector3(0, 2, 0), "A");
        a.Rotation = turned;
        var b = AddCube(new Vector3(0, 2, 0) + Vector3.Transform(new Vector3(0.4f, 0, 0), turned), "B");
        b.Rotation = turned;
        var weld = new WeldJoint3D { Name = "Weld", Position = a.Position };
        _h.Add(weld);
        weld.NodeB = "../A";
        var pin = new PinJoint3D { Name = "Pin", Position = (a.Position + b.Position) / 2 };
        _h.Add(pin);
        pin.NodeA = "../A";
        pin.NodeB = "../B";
        var drive = new BoneDrive3D { Name = "Drive" };
        _h.Add(drive);
        drive.NodeA = "../A";
        drive.NodeB = "../B";
        _h.Run(1);
        var relative = Quaternion.CreateFromAxisAngle(Vector3.UnitX, float.DegreesToRadians(40f));
        drive.Drive(relative, Vector3.Zero, 10f, 1f, 1000f, 0f, 0f, 0f);
        _h.RunSeconds(2f);
        var actual = Quaternion.Normalize(Quaternion.Conjugate(a.Rotation) * b.Rotation);
        Assert.InRange(AngleBetween(actual, relative), 0f, 2f);
        Assert.InRange(drive.GetErrorDegrees(), 0f, 2f);
    }
}
