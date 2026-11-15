using System.Numerics;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.14: a clip reads back in canonical terms — the script's angles, contacts, eases and which keys are partial.</summary>
[Collection(nameof(SerialResources))]
public class ClipDescriberTests
{
    private static readonly NodePath Armature = new("Armature");

    private static (MainframeEngine.Animation Clip, HumanoidRig Rig) BakedJump()
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        var clip = PoseBaker.Bake(PoseScriptParser.Parse(PoseScriptParserTests.Jump), rig, root.GetPathTo(skeleton)).Animation;
        return (clip, rig);
    }

    private static ClipDescriptionTime At(ClipDescription description, float time) =>
        description.Times.Single(t => MathF.Abs(t.Time - time) < 1e-3f);

    [Fact]
    public void TheScriptsKeysReadBackInCanonicalAngles()
    {
        var (clip, rig) = BakedJump();
        var description = ClipDescriber.Describe(clip, rig, Armature);

        Assert.True(description.UntouchedBake);
        Assert.Equal(1.1f, description.Length);
        foreach (var time in new[] { 0f, 0.12f, 0.24f, 0.55f, 0.82f, 1.1f })
            Assert.Contains(description.Times, t => MathF.Abs(t.Time - time) < 1e-3f && t.IsKey);

        var crouch = At(description, 0.12f);
        Assert.Equal(8f, crouch.Pose[HumanoidBone.Spine].Flex, 0.5f);
        Assert.Equal(-55f, crouch.Pose[HumanoidBone.LeftUpperArm].Flex, 0.5f);
        Assert.Equal(-55f, crouch.Pose[HumanoidBone.RightUpperArm].Flex, 0.5f);
        Assert.Equal(-0.22f, crouch.Pose.Hips.Offset.Y, 0.005f);
        Assert.Equal(20f, crouch.Pose.Hips.Pitch, 0.5f);
        Assert.Equal(0.5f, crouch.Ease);

        var tuck = At(description, 0.55f);
        Assert.Equal(72f, tuck.Pose[HumanoidBone.RightUpperLeg].Flex, 0.5f);
        Assert.Equal(92f, tuck.Pose[HumanoidBone.RightLowerLeg].Flex, 0.5f);
        Assert.Equal(-80f, At(description, 1.1f).Pose[HumanoidBone.LeftUpperArm].Abduct, 0.5f);
    }

    [Fact]
    public void FeetAreInContactWhilePinnedAndNotInTheAir()
    {
        var (clip, rig) = BakedJump();
        var description = ClipDescriber.Describe(clip, rig, Armature);

        Assert.True(At(description, 0f).Effectors[IkEffector.LeftFoot].Contact);
        Assert.True(At(description, 0.12f).Effectors[IkEffector.RightFoot].Contact);
        Assert.False(At(description, 0.55f).Effectors[IkEffector.LeftFoot].Contact);
        Assert.False(At(description, 0f).Effectors[IkEffector.LeftHand].Contact);
        // Character space: the left foot is on the character's left (+X), in front of nothing.
        Assert.True(At(description, 0f).Effectors[IkEffector.LeftFoot].Position.X > 0f);
    }

    [Fact]
    public void RefinementKeysListTheirBonesAndSampledTimesSaySo()
    {
        var (clip, rig) = BakedJump();
        var partial = ClipDescriber.Describe(clip, rig, Armature).Times.Where(t => t.PartialKeys.Count > 0).ToList();
        HumanoidBone[] legs =
        [
            HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot,
            HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot,
        ];
        foreach (var time in partial)
            Assert.All(time.PartialKeys, b => Assert.Contains(b, legs));

        var sampled = ClipDescriber.Describe(clip, rig, Armature, [0.4f]);
        Assert.False(Assert.Single(sampled.Times).IsKey);
        Assert.Null(sampled.Times[0].Ease);
    }

    [Fact]
    public void AHandMadeClipIsNotBakedAndListsUnmappedTracks()
    {
        var (_, skeleton, rig) = PoseBakerTests.Character();
        var spine = skeleton.FindBone("Spine");
        var rest = skeleton.GetBoneRest(spine).Basis.GetRotation();
        var clip = new MainframeEngine.Animation { ResourceName = "nod", Length = 1f };
        clip.Tracks.Add(new Rotation3DTrack
        {
            Path = Armature,
            Bone = "Spine",
            Interpolation = AnimationInterpolation.Cubic,
            Times = [0f, 1f],
            Values = [rest, rest * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)],
            Transitions = [1f, 1f],
        });
        clip.Tracks.Add(new Rotation3DTrack { Path = Armature, Bone = "HeadTop", Times = [0f], Values = [Quaternion.Identity] });

        var description = ClipDescriber.Describe(clip, rig, Armature);

        Assert.False(description.Baked);
        Assert.False(description.UntouchedBake);
        Assert.Equal(["HeadTop"], description.UnmappedTracks);
        Assert.Equal([0f, 1f], description.Times.Select(t => t.Time));
        var text = description.ToText();
        Assert.Contains("nod: 1 s, loop None, 2 key time(s), not baked", text, StringComparison.Ordinal);
        Assert.Contains("Spine:", text, StringComparison.Ordinal);
        Assert.Contains("HeadTop", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextNamesEveryKeyTimeAndContact()
    {
        var (clip, rig) = BakedJump();
        var text = ClipDescriber.Describe(clip, rig, Armature).ToText();

        Assert.Contains("baked, untouched", text, StringComparison.Ordinal);
        Assert.Contains("t = 0.12 s, ease out", text, StringComparison.Ordinal);
        Assert.Contains("LeftUpperArm: flex -55", text, StringComparison.Ordinal);
        Assert.Contains("leftFoot: [", text, StringComparison.Ordinal);
        Assert.Contains("contact", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribingLeavesTheSkeletonsPoseAlone()
    {
        var (clip, rig) = BakedJump();
        var spine = rig.Skeleton.FindBone("Spine");
        var posed = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        rig.Skeleton.SetBonePoseRotation(spine, posed);

        ClipDescriber.Describe(clip, rig, Armature);

        Assert.Equal(posed, rig.Skeleton.GetBonePoseRotation(spine));
    }
}
