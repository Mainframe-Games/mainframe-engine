using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>PhysicalBone3D and PhysicalBoneSimulator3D (ADR 0212): kinematic follow, simulation, pose write-back, influence.</summary>
public sealed class PhysicalBonesTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();
    private readonly Skeleton3D _skeleton;

    public PhysicalBonesTests()
    {
        _h.AddFloor();
        // An upright three-bone chain from y = 2: Root → Mid (0.5 m up) → Tip (0.5 m up).
        _skeleton = new Skeleton3D { Name = "Skeleton", Position = new Vector3(0, 2, 0) };
        var root = _skeleton.AddBone("Root", -1, Transform3D.Identity);
        var mid = _skeleton.AddBone("Mid", root, Transform3D.FromTrs(new Vector3(0, 0.5f, 0), Quaternion.Identity, Vector3.One));
        _skeleton.AddBone("Tip.End", mid, Transform3D.FromTrs(new Vector3(0, 0.5f, 0), Quaternion.Identity, Vector3.One));
        _skeleton.ResetBonePoses();
    }

    public void Dispose() => _h.Dispose();

    private PhysicalBoneSimulator3D AddSimulator()
    {
        var simulator = PhysicalBoneSimulator3D.Build(_skeleton);
        _skeleton.AddChild(simulator);
        _h.Add(_skeleton);
        return simulator;
    }

    [Fact]
    public void BuildMakesACapsuleAndAConeTwistPerBoneWithAChild()
    {
        var simulator = PhysicalBoneSimulator3D.Build(_skeleton);
        var bones = simulator.Children.OfType<PhysicalBone3D>().ToList();
        Assert.Equal(["Bone_Root", "Bone_Mid"], bones.Select(b => b.Name));
        Assert.Equal(["Root", "Mid"], bones.Select(b => b.BoneName));
        var capsule = Assert.IsType<CapsuleShape3D>(bones[0].GetNode<CollisionShape3D>("Shape").Shape);
        Assert.Equal(0.1f, capsule.Radius, 4);
        Assert.Equal(0.5f, capsule.Height, 4);
        Assert.All(bones, b => Assert.Equal(PhysicalBoneJointType.ConeTwist, b.JointType));
        Assert.Equal(new Vector3(0, 0.25f, 0), bones[0].BodyOffset.Origin); // halfway along the bone
        Assert.Equal(new Vector3(0, 0.75f, 0), bones[1].Transform.Origin); // at the bone's rest, in skeleton space
        simulator.Free();
    }

    [Fact]
    public void HeldBodiesFollowTheAnimatedBones()
    {
        var simulator = AddSimulator();
        var root = simulator.GetNode<PhysicalBone3D>("Bone_Root");
        _h.Run(2);
        Assert.False(simulator.IsSimulating);
        Assert.InRange(Vector3.Distance(root.GlobalPosition, new Vector3(0, 2.25f, 0)), 0f, 0.001f);
        Assert.NotNull(simulator.GetNode<PhysicalBone3D>("Bone_Mid").Joint); // joined to Root's body; none for the root
        Assert.Null(root.Joint);

        // "Animate": tip the chain 90° about Z; the held bodies follow (a kinematic move).
        _skeleton.SetBonePoseRotation(0, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        _h.Run(3);
        Assert.InRange(Vector3.Distance(root.GlobalPosition, new Vector3(-0.25f, 2, 0)), 0f, 0.01f);
        _h.RunSeconds(1f);
        Assert.InRange(Vector3.Distance(root.GlobalPosition, new Vector3(-0.25f, 2, 0)), 0f, 0.01f); // still held: no gravity
    }

    [Fact]
    public void SimulatedBodiesFallAndTheBonesFollowThem()
    {
        var simulator = AddSimulator();
        _h.Run(2);
        simulator.PhysicalBonesStartSimulation();
        Assert.True(simulator.IsSimulating);
        _h.RunSeconds(2f);
        var root = _skeleton.GetBoneGlobalPose(0).Origin + _skeleton.Position;
        var mid = _skeleton.GetBoneGlobalPose(1).Origin + _skeleton.Position;
        Assert.True(root.Y < 0.7f, $"the root bone fell with its body: {root}");
        Assert.InRange(Vector3.Distance(root, mid), 0.47f, 0.53f); // the joint keeps the bones together
        var body = simulator.GetNode<PhysicalBone3D>("Bone_Mid");
        Assert.True(body.IsSimulatingPhysics);
        Assert.True(body.Joint!.IsActive);

        simulator.PhysicalBonesStopSimulation(); // held again where the animation (the last written pose) says
        var held = body.GlobalPosition;
        _h.RunSeconds(0.5f);
        Assert.False(body.IsSimulatingPhysics);
        Assert.InRange(Vector3.Distance(body.GlobalPosition, held), 0f, 0.02f);
    }

    [Fact]
    public void InfluenceBlendsTheAnimatedAndSimulatedPoses()
    {
        var simulator = AddSimulator();
        simulator.Influence = 0f;
        _h.Run(2);
        simulator.PhysicalBonesStartSimulation(["Mid"]); // only one bone
        Assert.False(simulator.GetNode<PhysicalBone3D>("Bone_Root").IsSimulatingPhysics);
        simulator.GetNode<PhysicalBone3D>("Bone_Mid").ApplyImpulse(new Vector3(0.5f, 0, 0)); // off its balance point
        _h.RunSeconds(1f);
        // Influence 0: the bone keeps its animated pose though its body fell over.
        Assert.Equal(Quaternion.Identity, _skeleton.GetBonePoseRotation(1));
        var fallen = simulator.GetNode<PhysicalBone3D>("Bone_Mid").GlobalPosition;
        Assert.True(fallen.Y < 2.7f, $"the Mid body swung down from the held Root: {fallen}");

        simulator.Influence = 1f;
        _h.Run(1);
        Assert.NotEqual(Quaternion.Identity, _skeleton.GetBonePoseRotation(1));
    }

    [Fact]
    public void SimulatingAPhysicalSkeletonDoesNotAllocate()
    {
        var simulator = AddSimulator();
        foreach (var body in simulator.PhysicalBones)
            body.CanSleep = false; // no sleep transition (the first one grows the space's event list) in the window
        _h.Run(2);
        simulator.PhysicalBonesStartSimulation();
        var log = new List<string>();
        _h.Run(240);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
        {
            var b = GC.GetAllocatedBytesForCurrentThread();
            _h.Run(1);
            var d = GC.GetAllocatedBytesForCurrentThread() - b;
            if (d != 0)
                log.Add($"{i}: {d}");
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before == 0, string.Join(", ", log));
    }

    [Fact]
    public void InfluenceDoesNotCompoundWhenNothingAnimatesTheBones()
    {
        // Review fix 2: with no animation rewriting the pose, the blend must mix the bodies with the animated (rest)
        // pose, not with last frame's blend, which would creep toward the bodies whatever Influence says.
        var simulator = AddSimulator();
        simulator.Influence = 0.5f;
        _h.Run(2);
        simulator.PhysicalBonesStartSimulation(["Mid"]);
        var mid = simulator.GetNode<PhysicalBone3D>("Bone_Mid");
        mid.ApplyImpulse(new Vector3(0.5f, 0, 0));
        _h.RunSeconds(3f);
        var bodyAngle = Angle(mid.GlobalTransform.Basis.GetRotation());
        var boneAngle = Angle(_skeleton.GetBonePoseRotation(1));
        Assert.True(bodyAngle > 0.3f, $"the body leant over: {float.RadiansToDegrees(bodyAngle)}°");
        Assert.InRange(boneAngle, bodyAngle * 0.5f - 0.03f, bodyAngle * 0.5f + 0.03f);
    }

    [Fact]
    public void RunTimeJointAndOffsetChangesUseTheReadyPose()
    {
        // Review fix 4: a joint re-created at run time takes its frames from the pose the simulator was ready in, and a
        // new BodyOffset applies to the pose write at once.
        var simulator = AddSimulator();
        _h.Run(2);
        simulator.PhysicalBonesStartSimulation();
        _h.RunSeconds(2f); // crumpled on the floor
        var mid = simulator.GetNode<PhysicalBone3D>("Bone_Mid");
        var offset = mid.JointOffset;
        mid.JointOffset = Transform3D.FromTrs(offset.Origin + new Vector3(0.01f, 0, 0), offset.Basis.GetRotation(), Vector3.One);
        _h.Run(1);
        var record = mid.Joint!.Record!;
        // At the ready pose Root's body was at (0, 2.25, 0) and Mid's at (0, 2.75, 0), both unrotated.
        Assert.InRange(Vector3.Distance(record.FramePositionA, new Vector3(0.01f, 0.25f, 0)), 0f, 1e-3f);
        Assert.True(mid.Joint.IsActive);

        var newOffset = Transform3D.FromTrs(new Vector3(0, 0.1f, 0), Quaternion.Identity, Vector3.One);
        mid.BodyOffset = newOffset;
        _h.Run(1);
        var expected = (_skeleton.GlobalTransform.AffineInverse() * mid.GlobalTransform * newOffset.AffineInverse()).Origin;
        Assert.InRange(Vector3.Distance(_skeleton.GetBoneGlobalPose(1).Origin, expected), 0f, 1e-3f);
    }

    private static float Angle(Quaternion q) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Normalize(q).W), 0f, 1f));
}
