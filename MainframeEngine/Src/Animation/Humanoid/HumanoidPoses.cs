namespace MainframeEngine;

/// <summary>
/// The named poses that ship with the engine (ADR 0281), the base poses of pose scripts: <c>tpose</c> (all zeros, the
/// canonical T-pose), <c>stand</c> (arms down at the sides, the start pose of most clips) and <c>rest</c> (the rig's own rest
/// pose, which only a <see cref="HumanoidRig"/> can give: <see cref="HumanoidRig.RestPose"/>).
/// </summary>
public static class HumanoidPoses
{
    public const string TPose = "tpose";
    public const string Stand = "stand";
    public const string Rest = "rest";

    /// <summary>Every named pose, in the order <c>mfe anim poses</c> lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = [TPose, Stand, Rest];

    /// <summary>
    /// The pose named <paramref name="name"/> (a fresh copy), or null for an unknown name. <c>rest</c> needs the rig:
    /// pass it as <paramref name="rig"/> (null returns null for <c>rest</c>).
    /// </summary>
    public static HumanoidPose? Get(string name, HumanoidRig? rig = null) => name switch
    {
        TPose => new HumanoidPose(),
        Stand => CreateStand(),
        Rest => rig?.RestPose(),
        _ => null,
    };

    private static HumanoidPose CreateStand()
    {
        var pose = new HumanoidPose();
        pose[HumanoidBone.LeftUpperArm] = new JointAngles(Abduct: -80f);
        pose[HumanoidBone.LeftLowerArm] = new JointAngles(Flex: 10f);
        pose.MirrorSide(HumanoidSide.Left);
        return pose;
    }
}
