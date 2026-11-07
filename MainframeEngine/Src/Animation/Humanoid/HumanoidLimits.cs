namespace MainframeEngine;

/// <summary>A component's range of motion in degrees.</summary>
public readonly record struct AngleRange(float Min, float Max)
{
    public bool Contains(float degrees, float slack = 0f) => degrees >= Min - slack && degrees <= Max + slack;

    public float Clamp(float degrees) => Math.Clamp(degrees, Min, Max);
}

/// <summary>
/// The range of motion of every canonical bone and component (ADR 0281), the same for every character in G1c. Checks report
/// angles outside it; the editor's sliders clamp to it. Hinges have a zero range for abduct and twist.
/// </summary>
public static class HumanoidLimits
{
    private static readonly AngleRange[] Ranges = Build();

    /// <summary>The range of <paramref name="component"/> of <paramref name="bone"/>.</summary>
    public static AngleRange Get(HumanoidBone bone, JointComponent component) =>
        Ranges[(int)bone * 3 + (int)component];

    /// <summary>The range of the hips' pitch and roll (yaw is free).</summary>
    public static AngleRange HipsTilt { get; } = new(-90f, 90f);

    private static AngleRange[] Build()
    {
        var ranges = new AngleRange[HumanoidBones.Count * 3];

        void Set(HumanoidBone bone, (float, float) flex, (float, float) abduct, (float, float) twist)
        {
            foreach (var b in new[] { bone, HumanoidBones.Mirror(bone) })
            {
                ranges[(int)b * 3 + (int)JointComponent.Flex] = new AngleRange(flex.Item1, flex.Item2);
                ranges[(int)b * 3 + (int)JointComponent.Abduct] = new AngleRange(abduct.Item1, abduct.Item2);
                ranges[(int)b * 3 + (int)JointComponent.Twist] = new AngleRange(twist.Item1, twist.Item2);
            }
        }

        var hinge = (0f, 0f);
        Set(HumanoidBone.Hips, (-90, 90), (-90, 90), (-180, 180));
        Set(HumanoidBone.Spine, (-30, 60), (-35, 35), (-30, 30));
        Set(HumanoidBone.Chest, (-30, 60), (-35, 35), (-30, 30));
        Set(HumanoidBone.UpperChest, (-30, 60), (-35, 35), (-30, 30));
        Set(HumanoidBone.Neck, (-50, 60), (-45, 45), (-70, 70));
        Set(HumanoidBone.Head, (-40, 40), (-30, 30), (-45, 45));
        Set(HumanoidBone.LeftEye, (-30, 30), (-35, 35), (-5, 5));
        Set(HumanoidBone.Jaw, (-5, 30), (-10, 10), (-10, 10));
        Set(HumanoidBone.LeftUpperLeg, (-30, 130), (-30, 60), (-40, 45));
        Set(HumanoidBone.LeftLowerLeg, (0, 150), hinge, hinge);
        Set(HumanoidBone.LeftFoot, (-50, 30), (-30, 30), (-35, 25));
        Set(HumanoidBone.LeftToes, (-40, 70), (-10, 10), (-10, 10));
        Set(HumanoidBone.LeftShoulder, (-20, 30), (-10, 40), (-10, 10));
        Set(HumanoidBone.LeftUpperArm, (-60, 180), (-90, 90), (-90, 90));
        Set(HumanoidBone.LeftLowerArm, (0, 150), hinge, hinge);
        Set(HumanoidBone.LeftHand, (-70, 80), (-20, 30), (-80, 90));
        foreach (var finger in new[]
                 {
                     HumanoidBone.LeftIndexProximal, HumanoidBone.LeftMiddleProximal, HumanoidBone.LeftRingProximal,
                     HumanoidBone.LeftLittleProximal,
                 })
        {
            Set(finger, (-20, 90), (-20, 20), (-10, 10));
            Set(finger + 1, (0, 110), (-5, 5), (-5, 5));
            Set(finger + 2, (0, 90), (-5, 5), (-5, 5));
        }

        Set(HumanoidBone.LeftThumbMetacarpal, (-30, 60), (-30, 60), (-30, 30));
        Set(HumanoidBone.LeftThumbProximal, (-10, 70), (-10, 10), (-10, 10));
        Set(HumanoidBone.LeftThumbDistal, (-10, 90), (-5, 5), (-5, 5));
        return ranges;
    }
}
