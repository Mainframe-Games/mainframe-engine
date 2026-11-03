using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Active ragdolls (G9.10c, ADR 0235): local drives and world holds on a <see cref="PhysicalBoneSimulator3D"/>, on a
/// horizontal four-bone arm. The humanoid scenarios (standing, recovering, fading) are the Demo's tests.
/// </summary>
public sealed class ActiveRagdollTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();
    private readonly Skeleton3D _skeleton;

    public ActiveRagdollTests()
    {
        _h.AddFloor();
        // An arm pointing along +X from (0, 2, 0): Root → A → B → C → Tip, 0.5 m each. Build gives Root, A, B and C a body
        // (Root's held by default, HoldStrength 1) and each child a cone-twist to its parent.
        _skeleton = new Skeleton3D { Name = "Skeleton", Position = new Vector3(0, 2, 0) };
        var parent = _skeleton.AddBone("Root", -1, Transform3D.Identity);
        foreach (var name in new[] { "A", "B", "C", "Tip" })
            parent = _skeleton.AddBone(name, parent, Transform3D.FromTrs(new Vector3(0.5f, 0, 0), Quaternion.Identity, Vector3.One));
        _skeleton.ResetBonePoses();
    }

    public void Dispose() => _h.Dispose();

    private PhysicalBoneSimulator3D AddSimulator(Action<PhysicalBoneSimulator3D>? configure = null)
    {
        var simulator = PhysicalBoneSimulator3D.Build(_skeleton);
        configure?.Invoke(simulator);
        _skeleton.AddChild(simulator);
        _h.Add(_skeleton);
        _h.Run(2);
        return simulator;
    }

    private static float MaxDriveError(PhysicalBoneSimulator3D simulator) => simulator.PhysicalBones.ToArray().Max(b => b.GetDriveErrorDegrees());

    [Fact]
    public void BuildHoldsTheRootBodyAndLeavesEveryStrengthAtItsPassiveDefault()
    {
        var simulator = PhysicalBoneSimulator3D.Build(_skeleton);
        var bones = simulator.Children.OfType<PhysicalBone3D>().ToList();
        Assert.Equal(1f, bones[0].HoldStrength);
        Assert.All(bones.Skip(1), b => Assert.Equal(0f, b.HoldStrength));
        Assert.All(bones, b => Assert.Equal(1f, b.DriveStrength));
        Assert.Equal((0f, 0f, 30f, 15f), (simulator.DriveStrength, simulator.HoldStrength, simulator.DriveFrequency, simulator.HoldFrequency));
        simulator.Free();
    }

    [Fact]
    public void ByDefaultASimulatedSkeletonIsPassiveAndFalls()
    {
        var simulator = AddSimulator();
        simulator.PhysicalBonesStartSimulation();
        _h.RunSeconds(2f);
        foreach (var body in simulator.PhysicalBones)
        {
            Assert.False(body.LocalDrive?.IsDriving ?? false, body.Name);
            Assert.False(body.Hold!.IsDriving, body.Name);
            Assert.True(body.GlobalPosition.Y < 0.5f, $"{body.Name} fell: {body.GlobalPosition}");
        }
    }

    [Fact]
    public void LocalDrivesKeepTheArmStraightAsItFalls()
    {
        var simulator = AddSimulator(s => s.DriveStrength = 1f);
        simulator.PhysicalBonesStartSimulation();
        // The arm is knocked as it starts: the drives must straighten it while it falls and lands.
        simulator.PhysicalBones[3].AngularVelocity = new Vector3(0, 0, 6);
        _h.RunSeconds(2f);
        Assert.True(simulator.PhysicalBones[0].GlobalPosition.Y < 0.5f, "no hold: it fell");
        Assert.InRange(MaxDriveError(simulator), 0f, 10f);
    }

    [Fact]
    public void AHeldRootAndLocalDrivesKeepTheArmOutAndAWeakDriveSags()
    {
        var strong = AddSimulator(s => (s.DriveStrength, s.HoldStrength, s.DriveFrequency) = (1f, 1f, 30f));
        strong.PhysicalBonesStartSimulation();
        _h.RunSeconds(2f);
        var root = strong.PhysicalBones[0];
        Assert.InRange(root.GetHoldError(), 0f, 0.05f);
        Assert.InRange(MaxDriveError(strong), 0f, 10f);

        strong.DriveMaxTorque = 1f; // far less than the 9.81 × (0.25 + 0.75 + 1.25) ≈ 22 N·m the first joint carries
        _h.RunSeconds(2f);
        Assert.True(MaxDriveError(strong) > 30f, $"it sagged: {MaxDriveError(strong)}°");
    }

    [Fact]
    public void StoppingTheSimulationTurnsTheDrivesOffAndHoldsTheBodiesAgain()
    {
        var simulator = AddSimulator(s => (s.DriveStrength, s.HoldStrength) = (1f, 1f));
        simulator.PhysicalBonesStartSimulation();
        _h.RunSeconds(1f);
        Assert.True(simulator.PhysicalBones[1].LocalDrive!.IsDriving);
        Assert.True(simulator.PhysicalBones[0].Hold!.IsDriving);

        simulator.PhysicalBonesStopSimulation();
        _h.Run(1);
        foreach (var body in simulator.PhysicalBones)
        {
            Assert.False(body.LocalDrive?.IsDriving ?? false, body.Name);
            Assert.False(body.Hold!.IsDriving, body.Name);
            Assert.False(body.IsSimulatingPhysics);
            Assert.Equal(0f, body.GetHoldError());
        }
    }

    [Fact]
    public void TheHoldErrorReadsHowFarABodyIsFromItsAnimatedPoseEvenWithTheHoldOff()
    {
        // A hold's stiffness is its body's mass × ω²; the root carries the whole arm, so it needs a stiff hold.
        var simulator = AddSimulator(s => (s.DriveStrength, s.HoldStrength, s.DriveFrequency, s.HoldFrequency) = (1f, 1f, 30f, 12f));
        simulator.PhysicalBonesStartSimulation();
        _h.RunSeconds(1f);
        var root = simulator.PhysicalBones[0];
        Assert.InRange(root.GetHoldError(), 0f, 0.05f);

        simulator.HoldStrength = 0f; // loses its balance
        _h.RunSeconds(2f);
        Assert.False(root.Hold!.IsDriving);
        Assert.True(root.GetHoldError() > 0.3f, $"it fell away from its pose: {root.GetHoldError()} m");
    }

    [Fact]
    public void DriveTargetsComeFromTheAnimationNotFromThePoseTheBodiesWroteBack()
    {
        // "Animate" A to 30° about Z once. With Influence 1 the simulator writes the bodies' poses into the skeleton every
        // frame; the drive's target must stay the animated 30°, not follow the written pose.
        var simulator = AddSimulator(s => (s.DriveStrength, s.HoldStrength, s.Influence) = (1f, 1f, 1f));
        simulator.PhysicalBonesStartSimulation();
        var animated = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(30f));
        _skeleton.SetBonePoseRotation(1, animated);
        _h.RunSeconds(2f);
        var drive = simulator.PhysicalBones[1].LocalDrive!;
        var targetDegrees = float.RadiansToDegrees(PoseDrive.RotationError(Quaternion.Identity, drive.TargetRotation).Length());
        Assert.InRange(targetDegrees, 29.5f, 30.5f);
    }

    [Fact]
    public void DriveSettingsRoundTripThroughScenesAndTheDrivesAreNeverSaved()
    {
        var simulator = AddSimulator(s =>
        {
            (s.DriveStrength, s.DriveFrequency, s.DriveDampingRatio, s.DriveMaxTorque) = (0.5f, 12f, 0.8f, 150f);
            (s.HoldStrength, s.HoldFrequency, s.HoldDampingRatio, s.HoldMaxForce, s.HoldMaxTorque) = (0.25f, 3f, 0.9f, 900f, 300f);
        });
        simulator.PhysicalBones[2].DriveStrength = 0.4f;
        foreach (var node in new Node[] { simulator }.Concat(simulator.Children.OfType<PhysicalBone3D>()))
            node.Owner = _skeleton;
        Assert.Contains(simulator.Children, c => c is BoneDrive3D); // created at ready

        var json = SceneSaver.ToJson(_skeleton);
        Assert.DoesNotContain("BoneDrive3D", System.Text.Encoding.UTF8.GetString(json));
        var copy = PackedScene.Parse(json).Instantiate();
        var loaded = copy.GetNode<PhysicalBoneSimulator3D>("PhysicalBoneSimulator3D");
        Assert.Equal((0.5f, 12f, 0.8f, 150f), (loaded.DriveStrength, loaded.DriveFrequency, loaded.DriveDampingRatio, loaded.DriveMaxTorque));
        Assert.Equal((0.25f, 3f, 0.9f, 900f, 300f), (loaded.HoldStrength, loaded.HoldFrequency, loaded.HoldDampingRatio, loaded.HoldMaxForce, loaded.HoldMaxTorque));
        var bones = loaded.Children.OfType<PhysicalBone3D>().ToList();
        Assert.Equal((1f, 0.4f), (bones[0].HoldStrength, bones[2].DriveStrength));
        copy.Free();
    }

    [Fact]
    public void AnActiveRagdollAllocatesNothingPerStep()
    {
        var simulator = AddSimulator(s => (s.DriveStrength, s.HoldStrength) = (1f, 1f));
        foreach (var body in simulator.PhysicalBones)
            body.CanSleep = false;
        simulator.PhysicalBonesStartSimulation();
        _h.Run(240);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            _ = simulator.PhysicalBones[2].GetDriveErrorDegrees() + simulator.PhysicalBones[0].GetHoldError();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
