using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A bone's rotation in the canonical humanoid space (ADR 0281), in degrees, relative to the canonical T-pose: <see cref="Flex"/>
/// in the sagittal plane, <see cref="Abduct"/> away from the midline, <see cref="Twist"/> about the bone. Composed
/// intrinsically: twist, then abduct, then flex, about the bone's joint-frame axes (<see cref="HumanoidBones.GetAxis"/>).
/// </summary>
public readonly record struct JointAngles(float Flex = 0f, float Abduct = 0f, float Twist = 0f)
{
    public static JointAngles Zero => default;

    /// <summary>The component's value.</summary>
    public float this[JointComponent component] => component switch
    {
        JointComponent.Flex => Flex,
        JointComponent.Abduct => Abduct,
        _ => Twist,
    };

    /// <summary>A copy with <paramref name="component"/> set to <paramref name="value"/>.</summary>
    public JointAngles With(JointComponent component, float value) => component switch
    {
        JointComponent.Flex => this with { Flex = value },
        JointComponent.Abduct => this with { Abduct = value },
        _ => this with { Twist = value },
    };

    /// <summary>True when every component is within <paramref name="toleranceDegrees"/> of <paramref name="other"/>'s.</summary>
    public bool ApproximatelyEquals(JointAngles other, float toleranceDegrees) =>
        MathF.Abs(Flex - other.Flex) <= toleranceDegrees && MathF.Abs(Abduct - other.Abduct) <= toleranceDegrees &&
        MathF.Abs(Twist - other.Twist) <= toleranceDegrees;

    public override string ToString() => $"flex {Flex:0.##} abduct {Abduct:0.##} twist {Twist:0.##}";
}

/// <summary>
/// The hips in the canonical humanoid space: <see cref="Offset"/> (metres, canonical axes, from the rest hips position) and
/// <see cref="Yaw"/>/<see cref="Pitch"/>/<see cref="Roll"/> (degrees; yaw turns left, pitch leans forward, roll leans to the
/// character's left; composed intrinsically in that order).
/// </summary>
public readonly record struct HipsPose(Vector3 Offset = default, float Yaw = 0f, float Pitch = 0f, float Roll = 0f)
{
    public bool ApproximatelyEquals(HipsPose other, float toleranceMetres, float toleranceDegrees) =>
        Vector3.Distance(Offset, other.Offset) <= toleranceMetres && MathF.Abs(Yaw - other.Yaw) <= toleranceDegrees &&
        MathF.Abs(Pitch - other.Pitch) <= toleranceDegrees && MathF.Abs(Roll - other.Roll) <= toleranceDegrees;
}

/// <summary>
/// A whole-body pose in the canonical humanoid space: the hips and every bone's <see cref="JointAngles"/> (zero = the canonical
/// T-pose). Bones a rig lacks keep their angles here; <see cref="HumanoidRig"/> folds them into the next mapped bone.
/// </summary>
public sealed class HumanoidPose
{
    private readonly JointAngles[] _joints = new JointAngles[HumanoidBones.Count];

    public HipsPose Hips { get; set; }

    public JointAngles this[HumanoidBone bone]
    {
        get => _joints[(int)bone];
        set => _joints[(int)bone] = value;
    }

    /// <summary>A copy.</summary>
    public HumanoidPose Clone()
    {
        var copy = new HumanoidPose { Hips = Hips };
        _joints.CopyTo(copy._joints, 0);
        return copy;
    }

    /// <summary>Copies the left bones' angles to the right ones (or right to left), which mirrors the motion.</summary>
    public void MirrorSide(HumanoidSide from)
    {
        foreach (var bone in HumanoidBones.All)
            if (HumanoidBones.GetSide(bone) == from)
                _joints[(int)HumanoidBones.Mirror(bone)] = _joints[(int)bone];
    }
}
