using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The canonical humanoid bones (G1c, ADR 0281): VRM 1.0's humanoid set, which most rigs can fill. Poses are written against
/// these, whatever the rig calls its bones; <see cref="HumanoidMap"/> says which rig bone each one is.
/// </summary>
public enum HumanoidBone : byte
{
    Hips,
    Spine,
    Chest,
    UpperChest,
    Neck,
    Head,
    LeftEye,
    RightEye,
    Jaw,
    LeftUpperLeg,
    LeftLowerLeg,
    LeftFoot,
    LeftToes,
    RightUpperLeg,
    RightLowerLeg,
    RightFoot,
    RightToes,
    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,
    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,
    LeftThumbMetacarpal,
    LeftThumbProximal,
    LeftThumbDistal,
    LeftIndexProximal,
    LeftIndexIntermediate,
    LeftIndexDistal,
    LeftMiddleProximal,
    LeftMiddleIntermediate,
    LeftMiddleDistal,
    LeftRingProximal,
    LeftRingIntermediate,
    LeftRingDistal,
    LeftLittleProximal,
    LeftLittleIntermediate,
    LeftLittleDistal,
    RightThumbMetacarpal,
    RightThumbProximal,
    RightThumbDistal,
    RightIndexProximal,
    RightIndexIntermediate,
    RightIndexDistal,
    RightMiddleProximal,
    RightMiddleIntermediate,
    RightMiddleDistal,
    RightRingProximal,
    RightRingIntermediate,
    RightRingDistal,
    RightLittleProximal,
    RightLittleIntermediate,
    RightLittleDistal,
}

/// <summary>Which side of the body a <see cref="HumanoidBone"/> is on.</summary>
public enum HumanoidSide : byte
{
    Centre,
    Left,
    Right,
}

/// <summary>A joint-angle component of a <see cref="HumanoidBone"/> (see <see cref="JointAngles"/>).</summary>
public enum JointComponent : byte
{
    Flex,
    Abduct,
    Twist,
}

/// <summary>
/// Facts about every <see cref="HumanoidBone"/> in the canonical humanoid space (ADR 0281): Y up, +Z forward, +X the
/// character's left, the canonical T-pose (legs straight down, arms straight out along ±X, palms down) as the reference.
/// </summary>
/// <remarks>
/// Each bone has a twist, abduct and flex axis in canonical space (the joint frame). Left and centre bones are written out;
/// right bones are the left ones mirrored (an axis <c>(x, y, z)</c> becomes <c>(x, −y, −z)</c>), so the same numbers mean
/// the same anatomical motion on both sides. Positive flex is anatomical flexion (hip and shoulder swing forward, knee and
/// elbow bend, spine and neck bend forward, ankle points the toes up), positive abduct moves away from the midline (a leg
/// sideways, an arm up; for the spine and head a bend to the character's left), positive twist is internal rotation (for
/// the spine and head a turn to the left).
/// </remarks>
public static class HumanoidBones
{
    /// <summary>Number of canonical bones.</summary>
    public const int Count = (int)HumanoidBone.RightLittleDistal + 1;

    private readonly record struct Info(HumanoidBone? Parent, bool Required, bool Hinge, bool Corrected,
        Vector3 Direction, Vector3 Twist, Vector3 Abduct, Vector3 Flex);

    /// <summary>Every canonical bone in declaration order (parents before children).</summary>
    public static IReadOnlyList<HumanoidBone> All { get; } = [.. Enum.GetValues<HumanoidBone>()];

    private static readonly HumanoidSide[] Sides = [.. All.Select(static b => SideOfName(b.ToString()))];

    private static readonly HumanoidBone[] Mirrors = [.. All.Select(static b => MirrorOfName(b))];

    private static readonly Info[] Infos = Build();

    /// <summary>The canonical parent (null for <see cref="HumanoidBone.Hips"/>).</summary>
    public static HumanoidBone? GetParent(HumanoidBone bone) => Infos[(int)bone].Parent;

    /// <summary>
    /// True for the bones a map must have (VRM 1.0's required set: hips, spine, head, and per side upper leg, lower leg,
    /// foot, upper arm, lower arm, hand).
    /// </summary>
    public static bool IsRequired(HumanoidBone bone) => Infos[(int)bone].Required;

    /// <summary>True for hinges (lower legs, lower arms): they take <see cref="JointAngles.Flex"/> only.</summary>
    public static bool IsHinge(HumanoidBone bone) => Infos[(int)bone].Hinge;

    /// <summary>
    /// True for the bones whose rest direction <see cref="HumanoidRig"/> straightens to the canonical T-pose (upper and lower
    /// legs and arms: legs straight down, arms straight out); every other bone keeps its rest relation to its parent.
    /// </summary>
    public static bool IsDirectionCorrected(HumanoidBone bone) => Infos[(int)bone].Corrected;

    /// <summary>The side of the body.</summary>
    public static HumanoidSide GetSide(HumanoidBone bone) => Sides[(int)bone];

    /// <summary>The bone on the other side (itself for centre bones).</summary>
    public static HumanoidBone Mirror(HumanoidBone bone) => Mirrors[(int)bone];

    private static HumanoidSide SideOfName(string name) =>
        name.StartsWith("Left", StringComparison.Ordinal) ? HumanoidSide.Left
        : name.StartsWith("Right", StringComparison.Ordinal) ? HumanoidSide.Right
        : HumanoidSide.Centre;

    private static HumanoidBone MirrorOfName(HumanoidBone bone)
    {
        var name = bone.ToString();
        if (name.StartsWith("Left", StringComparison.Ordinal))
            return Enum.Parse<HumanoidBone>("Right" + name[4..]);
        if (name.StartsWith("Right", StringComparison.Ordinal))
            return Enum.Parse<HumanoidBone>("Left" + name[5..]);
        return bone;
    }

    /// <summary>The bone's direction in the canonical T-pose (unit, canonical space).</summary>
    public static Vector3 GetDirection(HumanoidBone bone) => Infos[(int)bone].Direction;

    /// <summary>The rotation axis of <paramref name="component"/> for <paramref name="bone"/> (unit, canonical space).</summary>
    public static Vector3 GetAxis(HumanoidBone bone, JointComponent component)
    {
        ref readonly var info = ref Infos[(int)bone];
        return component switch
        {
            JointComponent.Flex => info.Flex,
            JointComponent.Abduct => info.Abduct,
            _ => info.Twist,
        };
    }

    /// <summary>Parses a bone name (case-insensitive, <c>LeftUpperLeg</c> or <c>leftUpperLeg</c>).</summary>
    public static bool TryParse(string? name, out HumanoidBone bone) =>
        Enum.TryParse(name, ignoreCase: true, out bone) && Enum.IsDefined(bone) && !int.TryParse(name, out _);

    /// <summary>The closest bone name to <paramref name="name"/> (for "did you mean" messages), or null.</summary>
    public static HumanoidBone? Suggest(string name)
    {
        HumanoidBone? best = null;
        var bestScore = int.MaxValue;
        foreach (var bone in All)
        {
            var score = EditDistance(name.ToLowerInvariant(), bone.ToString().ToLowerInvariant());
            if (score < bestScore)
            {
                bestScore = score;
                best = bone;
            }
        }

        return bestScore <= Math.Max(2, name.Length / 4) ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[b.Length];
    }

    private static Info[] Build()
    {
        var up = Vector3.UnitY;
        var down = -Vector3.UnitY;
        var left = Vector3.UnitX;
        var forward = Vector3.UnitZ;
        var infos = new Info[Count];

        void Set(HumanoidBone bone, HumanoidBone? parent, bool required, bool hinge, bool corrected, Vector3 direction,
            Vector3 twist, Vector3 abduct, Vector3 flex)
        {
            infos[(int)bone] = new Info(parent, required, hinge, corrected, direction, twist, abduct, flex);
            var mirror = Mirror(bone);
            if (mirror == bone)
                return;
            static Vector3 MirrorAxis(Vector3 v) => new(v.X, -v.Y, -v.Z);
            static Vector3 MirrorDirection(Vector3 v) => new(-v.X, v.Y, v.Z);
            infos[(int)mirror] = new Info(parent is { } p ? Mirror(p) : null, required, hinge, corrected,
                MirrorDirection(direction), MirrorAxis(twist), MirrorAxis(abduct), MirrorAxis(flex));
        }

        // Centre: twist turns left (+Y), abduct bends to the left (−Z), flex bends forward (+X).
        var centre = (Twist: up, Abduct: -Vector3.UnitZ, Flex: left);
        Set(HumanoidBone.Hips, null, true, false, false, up, centre.Twist, centre.Abduct, centre.Flex);
        Set(HumanoidBone.Spine, HumanoidBone.Hips, true, false, false, up, centre.Twist, centre.Abduct, centre.Flex);
        Set(HumanoidBone.Chest, HumanoidBone.Spine, false, false, false, up, centre.Twist, centre.Abduct, centre.Flex);
        Set(HumanoidBone.UpperChest, HumanoidBone.Chest, false, false, false, up, centre.Twist, centre.Abduct, centre.Flex);
        Set(HumanoidBone.Neck, HumanoidBone.UpperChest, false, false, false, up, centre.Twist, centre.Abduct, centre.Flex);
        Set(HumanoidBone.Head, HumanoidBone.Neck, true, false, false, up, centre.Twist, centre.Abduct, centre.Flex);

        // Eyes and jaw face forward: flex looks down / opens the jaw (+X), abduct looks outward.
        Set(HumanoidBone.LeftEye, HumanoidBone.Head, false, false, false, forward, forward, up, left);
        Set(HumanoidBone.Jaw, HumanoidBone.Head, false, false, false, forward, forward, up, left);

        // Legs point down: twist is internal rotation (−Y on the left), abduct lifts the leg sideways (+Z on the left), the
        // hip flexes forward (−X) and the knee bends back (+X).
        Set(HumanoidBone.LeftUpperLeg, HumanoidBone.Hips, true, false, true, down, down, forward, -left);
        Set(HumanoidBone.LeftLowerLeg, HumanoidBone.LeftUpperLeg, true, true, true, down, down, forward, left);

        // The foot points forward: twist is inversion (−Z), abduct turns the toes out (+Y), flex lifts the toes (−X).
        Set(HumanoidBone.LeftFoot, HumanoidBone.LeftLowerLeg, true, false, false, forward, -forward, up, -left);
        Set(HumanoidBone.LeftToes, HumanoidBone.LeftFoot, false, false, false, forward, -forward, up, -left);

        // Arms point out along +X on the left: twist is internal rotation (+X), abduct raises the arm (+Z), flex swings it
        // forward (−Y); the elbow bends forward (−Y).
        Set(HumanoidBone.LeftShoulder, HumanoidBone.UpperChest, false, false, false, left, left, forward, down);
        Set(HumanoidBone.LeftUpperArm, HumanoidBone.LeftShoulder, true, false, true, left, left, forward, down);
        Set(HumanoidBone.LeftLowerArm, HumanoidBone.LeftUpperArm, true, true, true, left, left, forward, down);

        // The hand and fingers (palm down): flex curls toward the palm (−Z), abduct moves toward the thumb side (−Y).
        var hand = (Twist: left, Abduct: down, Flex: -forward);
        Set(HumanoidBone.LeftHand, HumanoidBone.LeftLowerArm, true, false, false, left, hand.Twist, hand.Abduct, hand.Flex);
        HumanoidBone[][] fingers =
        [
            [HumanoidBone.LeftThumbMetacarpal, HumanoidBone.LeftThumbProximal, HumanoidBone.LeftThumbDistal],
            [HumanoidBone.LeftIndexProximal, HumanoidBone.LeftIndexIntermediate, HumanoidBone.LeftIndexDistal],
            [HumanoidBone.LeftMiddleProximal, HumanoidBone.LeftMiddleIntermediate, HumanoidBone.LeftMiddleDistal],
            [HumanoidBone.LeftRingProximal, HumanoidBone.LeftRingIntermediate, HumanoidBone.LeftRingDistal],
            [HumanoidBone.LeftLittleProximal, HumanoidBone.LeftLittleIntermediate, HumanoidBone.LeftLittleDistal],
        ];
        foreach (var chain in fingers)
        {
            HumanoidBone parent = HumanoidBone.LeftHand;
            foreach (var bone in chain)
            {
                Set(bone, parent, false, false, false, left, hand.Twist, hand.Abduct, hand.Flex);
                parent = bone;
            }
        }

        return infos;
    }
}
