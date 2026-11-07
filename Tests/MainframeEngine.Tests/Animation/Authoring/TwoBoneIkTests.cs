using System.Numerics;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.13: two-bone IK reaches, straightens, bends towards its pole, and guards zero lengths.</summary>
public class TwoBoneIkTests
{
    private static readonly Vector3 Hip = new(0, 1, 0);
    private static readonly Vector3 Knee = new(0, 0.55f, 0);
    private static readonly Vector3 Ankle = new(0, 0.1f, 0);

    [Fact]
    public void AReachableTargetIsHitAndTheBonesKeepTheirLengths()
    {
        var target = new Vector3(0.1f, 0.35f, 0.2f);

        var knee = TwoBoneIk.SolveMid(Hip, Knee, Ankle, target, Vector3.UnitZ, out var reached);

        Assert.True(Vector3.Distance(target, reached) < 1e-3f);
        Assert.Equal(0.45f, Vector3.Distance(Hip, knee), 1e-4f);
        Assert.Equal(0.45f, Vector3.Distance(knee, reached), 1e-4f);
    }

    [Fact]
    public void TheMiddleJointBendsTowardsThePole()
    {
        var target = new Vector3(0, 0.4f, 0);

        var forward = TwoBoneIk.SolveMid(Hip, Knee, Ankle, target, Vector3.UnitZ, out _);
        var sideways = TwoBoneIk.SolveMid(Hip, Knee, Ankle, target, Vector3.UnitX, out _);

        Assert.True(forward.Z > 0.2f && MathF.Abs(forward.X) < 1e-4f);
        Assert.True(sideways.X > 0.2f && MathF.Abs(sideways.Z) < 1e-4f);
    }

    [Fact]
    public void AnUnreachableTargetStraightensTheLimbTowardsIt()
    {
        var target = new Vector3(0, -2f, 0.5f);

        var knee = TwoBoneIk.SolveMid(Hip, Knee, Ankle, target, Vector3.UnitZ, out var reached);

        var direction = Vector3.Normalize(target - Hip);
        Assert.Equal(0.9f, Vector3.Distance(Hip, reached), 1e-3f);
        Assert.True(Vector3.Dot(Vector3.Normalize(reached - Hip), direction) > 0.9999f);
        Assert.True(Vector3.Dot(Vector3.Normalize(knee - Hip), direction) > 0.999f);
    }

    [Fact]
    public void ZeroLengthBonesLeaveTheChainAlone()
    {
        var knee = TwoBoneIk.SolveMid(Hip, Hip, Ankle, Vector3.Zero, Vector3.UnitZ, out var reached);

        Assert.Equal(Hip, knee);
        Assert.Equal(Ankle, reached);
    }

    [Fact]
    public void ApplyPosesASkeletonsLegOntoTheTarget()
    {
        var skeleton = new Skeleton3D();
        var hip = skeleton.AddBone("Hip", -1, Transform3D.FromTrs(Hip, Quaternion.Identity, Vector3.One));
        var knee = skeleton.AddBone("Knee", hip, Transform3D.FromTrs(Knee - Hip, Quaternion.Identity, Vector3.One));
        var ankle = skeleton.AddBone("Ankle", knee, Transform3D.FromTrs(Ankle - Knee, Quaternion.Identity, Vector3.One));
        var target = new Vector3(0.05f, 0.3f, 0.25f);
        var level = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);

        var miss = TwoBoneIk.Apply(skeleton, hip, knee, ankle, target, Vector3.UnitZ, level);

        Assert.True(miss < 1e-3f, $"missed by {miss}");
        Assert.True(Vector3.Distance(target, skeleton.GetBoneGlobalPose(ankle).Origin) < 1e-3f);
        Assert.True(MathF.Abs(Quaternion.Dot(level, skeleton.GetBoneGlobalPose(ankle).Basis.GetRotation())) > 0.99999f);
    }
}
