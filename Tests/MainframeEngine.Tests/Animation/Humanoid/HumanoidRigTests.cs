using System.Numerics;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Humanoid;

/// <summary>
/// G1.12 (ADR 0281): <see cref="HumanoidRig"/> turns canonical angles into rig rotations and back, the same way on T-pose,
/// A-pose, rotated-frame, sideways-facing and centimetre rigs, with the documented anatomical directions.
/// </summary>
[Collection(nameof(SerialResources))]
public class HumanoidRigTests
{
    public enum Variant
    {
        TPose,
        APose,
        BoneFrames,
        FacingX,
        Centimetres,
        Unreal,
    }

    public static TheoryData<Variant> Variants => [.. Enum.GetValues<Variant>()];

    private static HumanoidRig Rig(Variant variant)
    {
        var skeleton = variant switch
        {
            Variant.APose => SyntheticHumanoid.Build(RigFamily.Mixamo, aPose: true),
            Variant.BoneFrames => SyntheticHumanoid.Build(RigFamily.Blender, boneFrames: true, aPose: true),
            Variant.FacingX => SyntheticHumanoid.Build(RigFamily.VRoid, boneFrames: true,
                facing: Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)),
            Variant.Centimetres => SyntheticHumanoid.Build(RigFamily.Mixamo, boneFrames: true, unitsPerMetre: 100f),
            Variant.Unreal => SyntheticHumanoid.Build(RigFamily.Unreal, boneFrames: true),
            _ => SyntheticHumanoid.Build(RigFamily.Mixamo),
        };
        var map = HumanoidMap.Detect(skeleton, null, out var report);
        Assert.True(report.IsComplete);
        return new HumanoidRig(skeleton, map);
    }

    // Angle sets inside every bone's limits and away from the abduct ±90° singularity.
    private static IEnumerable<JointAngles> Samples(HumanoidBone bone)
    {
        var flex = HumanoidLimits.Get(bone, JointComponent.Flex);
        var abduct = HumanoidLimits.Get(bone, JointComponent.Abduct);
        var twist = HumanoidLimits.Get(bone, JointComponent.Twist);
        float At(AngleRange r, float t) => Math.Clamp(r.Min + (r.Max - r.Min) * t, -80f, 170f);
        foreach (var (a, b, c) in new[] { (0.5f, 0.5f, 0.5f), (0.2f, 0.8f, 0.3f), (0.9f, 0.1f, 0.7f), (0.35f, 0.65f, 0.95f) })
        {
            var angles = new JointAngles(At(flex, a), At(abduct, b), At(twist, c));
            yield return HumanoidBones.IsHinge(bone) ? new JointAngles(angles.Flex) : angles;
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void AnglesRoundTripThroughRigRotationsForEveryMappedBone(Variant variant)
    {
        var rig = Rig(variant);
        foreach (var bone in HumanoidBones.All.Where(b => rig.GetRigBone(b) >= 0 && b != HumanoidBone.Hips))
        {
            foreach (var angles in Samples(bone))
            {
                var back = rig.FromBoneRotation(bone, rig.ToBoneRotation(bone, angles));
                Assert.True(back.ApproximatelyEquals(angles, 0.01f), $"{variant} {bone}: {angles} came back as {back}");
            }
        }
    }

    [Fact]
    public void JointAnglesRoundTripForEveryBoneInCanonicalSpace()
    {
        foreach (var bone in HumanoidBones.All.Where(b => b != HumanoidBone.Hips))
            foreach (var angles in Samples(bone))
                Assert.True(HumanoidRig.JointAnglesOf(bone, HumanoidRig.JointRotation(bone, angles)).ApproximatelyEquals(angles, 0.01f),
                    $"{bone} {angles}");
    }

    [Fact]
    public void HipsAnglesRoundTrip()
    {
        var hips = new HipsPose(default, 35f, -20f, 12f);
        var (yaw, pitch, roll) = HumanoidRig.HipsAnglesOf(HumanoidRig.HipsRotation(hips));
        Assert.Equal(35f, yaw, 0.01f);
        Assert.Equal(-20f, pitch, 0.01f);
        Assert.Equal(12f, roll, 0.01f);
    }

    [Fact]
    public void TheCanonicalTPoseIsTheRestOfATPoseRig()
    {
        var rig = Rig(Variant.TPose);
        rig.ApplyPose(new HumanoidPose());

        for (var i = 0; i < rig.Skeleton.GetBoneCount(); i++)
            AssertNear(rig.Skeleton.GetBoneGlobalRest(i).Origin, rig.Skeleton.GetBoneGlobalPose(i).Origin, 1e-4f, rig.Skeleton.GetBoneName(i));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void OnePoseGivesTheSameBodyOnEveryRig(Variant variant)
    {
        var pose = SamplePose();
        var expected = CanonicalJoints(Rig(Variant.TPose), pose);

        var actual = CanonicalJoints(Rig(variant), pose);

        foreach (var (bone, position) in expected)
            AssertNear(position, actual[bone], 2e-3f, $"{variant} {bone}");
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void PosesReadBackAsTheyWereApplied(Variant variant)
    {
        var rig = Rig(variant);
        var pose = SamplePose();

        rig.ApplyPose(pose);
        var read = rig.ReadPose();

        Assert.True(read.Hips.ApproximatelyEquals(pose.Hips, 1e-3f, 0.02f), $"hips {read.Hips} vs {pose.Hips}");
        foreach (var bone in HumanoidBones.All.Where(b => b != HumanoidBone.Hips && rig.GetRigBone(b) >= 0))
            Assert.True(read[bone].ApproximatelyEquals(pose[bone], 0.02f), $"{variant} {bone}: {read[bone]} vs {pose[bone]}");
    }

    [Fact]
    public void MirroredAnglesGiveAMirroredBody()
    {
        var rig = Rig(Variant.BoneFrames);
        var pose = new HumanoidPose();
        pose[HumanoidBone.LeftUpperLeg] = new JointAngles(40f, 15f, 10f);
        pose[HumanoidBone.LeftLowerLeg] = new JointAngles(60f);
        pose[HumanoidBone.LeftUpperArm] = new JointAngles(30f, -40f, 20f);
        pose[HumanoidBone.LeftLowerArm] = new JointAngles(70f);
        pose[HumanoidBone.LeftHand] = new JointAngles(20f, 10f, -15f);
        pose.MirrorSide(HumanoidSide.Left);

        var joints = CanonicalJoints(rig, pose);

        foreach (var bone in new[] { HumanoidBone.LeftFoot, HumanoidBone.LeftHand, HumanoidBone.LeftIndexProximal })
        {
            var left = joints[bone];
            var right = joints[HumanoidBones.Mirror(bone)];
            AssertNear(left with { X = -left.X }, right, 1e-3f, bone.ToString());
        }
    }

    [Fact]
    public void AnglesMoveTheBodyTheDocumentedWay()
    {
        var rig = Rig(Variant.APose);
        Vector3 After(HumanoidBone bone, JointAngles angles, HumanoidBone measured)
        {
            var pose = new HumanoidPose();
            pose[bone] = angles;
            return CanonicalJoints(rig, pose)[measured];
        }

        var rest = CanonicalJoints(rig, new HumanoidPose());

        // Hip flex swings the foot forward; knee flex bends it back; hip abduct lifts it sideways (left: +X).
        Assert.True(After(HumanoidBone.LeftUpperLeg, new(Flex: 45f), HumanoidBone.LeftFoot).Z > 0.2f);
        Assert.True(After(HumanoidBone.LeftLowerLeg, new(Flex: 90f), HumanoidBone.LeftFoot).Z < -0.2f);
        Assert.True(After(HumanoidBone.LeftUpperLeg, new(Abduct: 30f), HumanoidBone.LeftFoot).X > rest[HumanoidBone.LeftFoot].X + 0.1f);
        Assert.True(After(HumanoidBone.RightUpperLeg, new(Abduct: 30f), HumanoidBone.RightFoot).X < rest[HumanoidBone.RightFoot].X - 0.1f);
        // Internal rotation of the hip turns the toes in; dorsiflexion lifts them.
        Assert.True(After(HumanoidBone.LeftUpperLeg, new(Twist: 30f), HumanoidBone.LeftToes).X < rest[HumanoidBone.LeftToes].X - 0.02f);
        Assert.True(After(HumanoidBone.LeftFoot, new(Flex: 25f), HumanoidBone.LeftToes).Y > rest[HumanoidBone.LeftToes].Y + 0.02f);
        // Shoulder abduct lifts the hand from the T-pose, flex swings it forward; the elbow bends it forward.
        Assert.True(After(HumanoidBone.LeftUpperArm, new(Abduct: 45f), HumanoidBone.LeftHand).Y > rest[HumanoidBone.LeftHand].Y + 0.2f);
        Assert.True(After(HumanoidBone.LeftUpperArm, new(Flex: 45f), HumanoidBone.LeftHand).Z > 0.2f);
        Assert.True(After(HumanoidBone.RightUpperArm, new(Flex: 45f), HumanoidBone.RightHand).Z > 0.2f);
        Assert.True(After(HumanoidBone.LeftLowerArm, new(Flex: 90f), HumanoidBone.LeftHand).Z > 0.2f);
        // The spine bends the head forward, to the left, and turns the left shoulder back.
        Assert.True(After(HumanoidBone.Spine, new(Flex: 30f), HumanoidBone.Head).Z > 0.1f);
        Assert.True(After(HumanoidBone.Spine, new(Abduct: 20f), HumanoidBone.Head).X > 0.1f);
        Assert.True(After(HumanoidBone.Spine, new(Twist: 30f), HumanoidBone.LeftUpperArm).Z < -0.05f);
        // The canonical T-pose straightens an A-pose rig's arms.
        Assert.Equal(rest[HumanoidBone.LeftUpperArm].Y, rest[HumanoidBone.LeftHand].Y, 0.002f);
    }

    [Fact]
    public void HipsOffsetsMoveInMetresWhateverTheRigsUnits()
    {
        foreach (var variant in new[] { Variant.TPose, Variant.Centimetres, Variant.FacingX })
        {
            var rig = Rig(variant);
            var pose = new HumanoidPose { Hips = new HipsPose(new Vector3(0.1f, -0.3f, 0.2f)) };
            var before = CanonicalJoints(rig, new HumanoidPose())[HumanoidBone.Hips];

            var after = CanonicalJoints(rig, pose)[HumanoidBone.Hips];

            AssertNear(new Vector3(0.1f, -0.3f, 0.2f), after - before, 1e-3f, variant.ToString());
        }
    }

    [Fact]
    public void RestKindsAndRestPoses()
    {
        var t = Rig(Variant.TPose);
        var a = Rig(Variant.APose);

        Assert.Equal(RestPoseKind.TPose, t.RestKind);
        Assert.Equal(RestPoseKind.APose, a.RestKind);
        Assert.Equal(45f, a.RestArmAngle, 0.5f);
        Assert.Equal(0f, t.RestPose()[HumanoidBone.LeftUpperArm].Abduct, 0.01f);
        Assert.Equal(-45f, a.RestPose()[HumanoidBone.LeftUpperArm].Abduct, 0.05f);
        Assert.Equal(-45f, a.RestPose()[HumanoidBone.RightUpperArm].Abduct, 0.05f);
    }

    [Fact]
    public void TheTestCharactersProportions()
    {
        var skeleton = HumanoidMapTests.ImportCharacter();
        var rig = new HumanoidRig(skeleton, HumanoidMap.Detect(skeleton, null, out _));

        Assert.Equal(RestPoseKind.TPose, rig.RestKind);
        Assert.Equal(1.80f, rig.Proportions.Height, 0.005f);
        Assert.Equal(0.96f, rig.Proportions.HipHeight, 0.005f);
        Assert.Equal(0.85f, rig.Proportions.LegLength, 0.005f);
        Assert.Equal(0.54f, rig.Proportions.ArmLength, 0.005f);
        Assert.Equal(0.36f, rig.Proportions.ShoulderWidth, 0.005f);
        foreach (var (bone, angles) in HumanoidBones.All.Select(b => (b, rig.RestPose()[b])))
            Assert.True(angles.ApproximatelyEquals(default, 0.01f), $"{bone}: {angles}");
    }

    [Fact]
    public void CentimetreRigsMeasureInMetres()
    {
        var metres = Rig(Variant.TPose).Proportions;
        var centimetres = Rig(Variant.Centimetres).Proportions;

        Assert.Equal(0.01f, Rig(Variant.Centimetres).MetresPerUnit, 1e-6f);
        Assert.Equal(metres.Height, centimetres.Height, 1e-3f);
        Assert.Equal(metres.LegLength, centimetres.LegLength, 1e-3f);
    }

    [Fact]
    public void TheMapMustHaveTheHipsLegsAndFeet()
    {
        var skeleton = SyntheticHumanoid.Build(RigFamily.Mixamo);
        var map = new HumanoidMap(new Dictionary<HumanoidBone, string> { [HumanoidBone.Hips] = "mixamorig:Hips" });

        Assert.Throws<ArgumentException>(() => new HumanoidRig(skeleton, map));
    }

    // A pose touching every part of the body.
    private static HumanoidPose SamplePose()
    {
        var pose = HumanoidPoses.Get(HumanoidPoses.Stand)!;
        pose.Hips = new HipsPose(new Vector3(0.02f, -0.15f, 0.05f), 20f, 15f, -5f);
        pose[HumanoidBone.Spine] = new JointAngles(10f, 5f, -8f);
        pose[HumanoidBone.Chest] = new JointAngles(8f, -3f, 6f);
        pose[HumanoidBone.Neck] = new JointAngles(-10f, 4f, 15f);
        pose[HumanoidBone.Head] = new JointAngles(12f, -6f, -20f);
        pose[HumanoidBone.LeftUpperLeg] = new JointAngles(70f, 10f, 12f);
        pose[HumanoidBone.LeftLowerLeg] = new JointAngles(95f);
        pose[HumanoidBone.LeftFoot] = new JointAngles(-20f, 8f, 5f);
        pose[HumanoidBone.RightUpperLeg] = new JointAngles(-15f, 5f, -10f);
        pose[HumanoidBone.RightLowerLeg] = new JointAngles(20f);
        pose[HumanoidBone.LeftShoulder] = new JointAngles(10f, 15f, 0f);
        pose[HumanoidBone.LeftUpperArm] = new JointAngles(60f, -30f, 25f);
        pose[HumanoidBone.LeftLowerArm] = new JointAngles(85f);
        pose[HumanoidBone.RightUpperArm] = new JointAngles(-40f, -70f, -15f);
        pose[HumanoidBone.RightHand] = new JointAngles(30f, -10f, 40f);
        pose[HumanoidBone.LeftIndexProximal] = new JointAngles(45f, 5f, 0f);
        pose[HumanoidBone.LeftThumbMetacarpal] = new JointAngles(20f, 30f, -10f);
        return pose;
    }

    // Applies the pose and returns the mapped joints' positions in canonical space (metres), relative to the rest hips.
    private static Dictionary<HumanoidBone, Vector3> CanonicalJoints(HumanoidRig rig, HumanoidPose pose)
    {
        rig.ApplyPose(pose);
        var inverse = Quaternion.Conjugate(rig.CharacterRotation);
        var hipsRest = rig.Skeleton.GetBoneGlobalRest(rig.GetRigBone(HumanoidBone.Hips)).Origin;
        var joints = new Dictionary<HumanoidBone, Vector3>();
        foreach (var bone in HumanoidBones.All)
        {
            var index = rig.GetRigBone(bone);
            if (index >= 0)
                joints[bone] = Vector3.Transform(rig.Skeleton.GetBoneGlobalPose(index).Origin - hipsRest, inverse) * rig.MetresPerUnit;
        }

        return joints;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");
}
