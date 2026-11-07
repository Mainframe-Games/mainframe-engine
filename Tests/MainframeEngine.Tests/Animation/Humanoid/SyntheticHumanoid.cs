using System.Numerics;

namespace MainframeEngine.Tests.Animation.Humanoid;

/// <summary>A rig naming family for <see cref="SyntheticHumanoid"/>.</summary>
public enum RigFamily
{
    Mixamo,
    Unreal,
    Blender,
    VRoid,
    Unnamed,
}

/// <summary>
/// Builds humanoid <see cref="Skeleton3D"/>s in code for the G1c humanoid map tests (ADR 0281): one body (1.8 m, the character
/// facing +Z, its left +X, like the generated test character) named after a rig family, in a T- or A-pose, with identity or
/// Blender-style bone frames (each bone's +Y along it, rolled), turned to face any way and in any unit.
/// </summary>
public static class SyntheticHumanoid
{
    private sealed record Role(string Key, string? Parent, Vector3 Position, bool Sided);

    // Left side and centre, canonical T-pose positions in metres.
    private static readonly Role[] Roles =
    [
        new("Hips", null, new(0, 1.00f, 0), false),
        new("Spine", "Hips", new(0, 1.12f, 0), false),
        new("Chest", "Spine", new(0, 1.27f, 0), false),
        new("UpperChest", "Chest", new(0, 1.42f, 0), false),
        new("Neck", "UpperChest", new(0, 1.54f, 0), false),
        new("Head", "Neck", new(0, 1.62f, 0), false),
        new("HeadEnd", "Head", new(0, 1.84f, 0), false),
        new("Shoulder", "UpperChest", new(0.06f, 1.50f, 0), true),
        new("UpperArm", "Shoulder", new(0.18f, 1.50f, 0), true),
        new("LowerArm", "UpperArm", new(0.46f, 1.50f, 0), true),
        new("Hand", "LowerArm", new(0.72f, 1.50f, 0), true),
        new("Thumb1", "Hand", new(0.75f, 1.50f, 0.04f), true),
        new("Thumb2", "Thumb1", new(0.78f, 1.50f, 0.07f), true),
        new("Thumb3", "Thumb2", new(0.80f, 1.50f, 0.09f), true),
        new("Index1", "Hand", new(0.81f, 1.50f, 0.025f), true),
        new("Index2", "Index1", new(0.85f, 1.50f, 0.025f), true),
        new("Index3", "Index2", new(0.88f, 1.50f, 0.025f), true),
        new("Middle1", "Hand", new(0.81f, 1.50f, 0f), true),
        new("Middle2", "Middle1", new(0.86f, 1.50f, 0f), true),
        new("Middle3", "Middle2", new(0.89f, 1.50f, 0f), true),
        new("UpperLeg", "Hips", new(0.10f, 0.95f, 0), true),
        new("LowerLeg", "UpperLeg", new(0.10f, 0.52f, 0), true),
        new("Foot", "LowerLeg", new(0.10f, 0.10f, 0), true),
        new("Toes", "Foot", new(0.10f, 0.03f, 0.12f), true),
        new("ToeEnd", "Toes", new(0.10f, 0.03f, 0.20f), true),
    ];

    /// <summary>The expected rig bone name of a canonical bone in <paramref name="family"/> (null: the family lacks it).</summary>
    public static string? NameOf(RigFamily family, HumanoidBone bone)
    {
        var name = bone.ToString();
        var side = name.StartsWith("Left", StringComparison.Ordinal) ? 'L' : name.StartsWith("Right", StringComparison.Ordinal) ? 'R' : 'C';
        var role = side == 'C' ? name : name[(side == 'L' ? 4 : 5)..];
        role = role switch
        {
            "ThumbMetacarpal" => "Thumb1",
            "ThumbProximal" => "Thumb2",
            "ThumbDistal" => "Thumb3",
            "IndexProximal" => "Index1",
            "IndexIntermediate" => "Index2",
            "IndexDistal" => "Index3",
            "MiddleProximal" => "Middle1",
            "MiddleIntermediate" => "Middle2",
            "MiddleDistal" => "Middle3",
            _ => role,
        };
        return Roles.Any(r => r.Key == role) ? Name(family, role, side, Array.FindIndex(Roles, r => r.Key == role)) : null;
    }

    private static string? Name(RigFamily family, string role, char side, int index)
    {
        var s = side switch { 'L' => "Left", 'R' => "Right", _ => "" };
        var lower = side switch { 'L' => "l", 'R' => "r", _ => "" };
        var dot = side switch { 'L' => ".L", 'R' => ".R", _ => "" };
        var vroid = side switch { 'L' => "L", 'R' => "R", _ => "C" };
        return family switch
        {
            RigFamily.Mixamo => "mixamorig:" + role switch
            {
                "Hips" or "Spine" or "Neck" or "Head" => role,
                "Chest" => "Spine1",
                "UpperChest" => "Spine2",
                "HeadEnd" => "HeadTop_End",
                "Shoulder" => s + "Shoulder",
                "UpperArm" => s + "Arm",
                "LowerArm" => s + "ForeArm",
                "Hand" => s + "Hand",
                "UpperLeg" => s + "UpLeg",
                "LowerLeg" => s + "Leg",
                "Foot" => s + "Foot",
                "Toes" => s + "ToeBase",
                "ToeEnd" => s + "Toe_End",
                _ => s + "Hand" + role, // HandThumb1, HandIndex2
            },
            RigFamily.Unreal => role switch
            {
                "Hips" => "pelvis",
                "Spine" => "spine_01",
                "Chest" => "spine_02",
                "UpperChest" => "spine_03",
                "Neck" => "neck_01",
                "Head" => "head",
                "HeadEnd" or "ToeEnd" => null,
                "Shoulder" => $"clavicle_{lower}",
                "UpperArm" => $"upperarm_{lower}",
                "LowerArm" => $"lowerarm_{lower}",
                "Hand" => $"hand_{lower}",
                "UpperLeg" => $"thigh_{lower}",
                "LowerLeg" => $"calf_{lower}",
                "Foot" => $"foot_{lower}",
                "Toes" => $"ball_{lower}",
                _ => $"{role[..^1].ToLowerInvariant()}_0{role[^1]}_{lower}", // thumb_01_l
            },
            RigFamily.Blender => "DEF-" + role switch
            {
                "Hips" => "hips",
                "Spine" => "spine",
                "Chest" => "spine.001",
                "UpperChest" => "spine.002",
                "Neck" => "neck",
                "Head" => "head",
                "HeadEnd" or "ToeEnd" => null,
                "Shoulder" => "shoulder" + dot,
                "UpperArm" => "upper_arm" + dot,
                "LowerArm" => "forearm" + dot,
                "Hand" => "hand" + dot,
                "UpperLeg" => "thigh" + dot,
                "LowerLeg" => "shin" + dot,
                "Foot" => "foot" + dot,
                "Toes" => "toe" + dot,
                _ when role.StartsWith("Thumb", StringComparison.Ordinal) => $"thumb.0{role[^1]}{dot}",
                _ => $"f_{role[..^1].ToLowerInvariant()}.0{role[^1]}{dot}",
            },
            RigFamily.VRoid => role switch
            {
                "HeadEnd" or "ToeEnd" => null,
                "Toes" => $"J_Bip_{vroid}_ToeBase",
                _ => $"J_Bip_{vroid}_{role}",
            },
            _ => index == 0 && side == 'C' ? "Bone" : null, // numbered in Build
        } is { } named && !named.EndsWith('-') && !named.EndsWith(':')
            ? named
            : null;
    }

    /// <summary>Builds the skeleton.</summary>
    /// <param name="family">The naming family.</param>
    /// <param name="aPose">Arms 45° below horizontal at rest.</param>
    /// <param name="boneFrames">Blender-style rests: each bone's +Y points along it, rolled per bone.</param>
    /// <param name="facing">Turns the whole character (identity: facing +Z).</param>
    /// <param name="unitsPerMetre">Skeleton units per metre (100: a centimetre rig); the node's scale undoes it.</param>
    public static Skeleton3D Build(RigFamily family, bool aPose = false, bool boneFrames = false, Quaternion? facing = null,
        float unitsPerMetre = 1f)
    {
        var turn = facing ?? Quaternion.Identity;
        var bones = new List<(string Name, int Parent, Vector3 Position)>();
        var indexOf = new Dictionary<(string Role, char Side), int>();
        var unnamed = 0;

        void Add(string name, int parent, Vector3 canonicalPosition) =>
            bones.Add((name, parent, Vector3.Transform(canonicalPosition, turn) * unitsPerMetre));

        var root = -1;
        if (family == RigFamily.Unreal)
        {
            Add("root", -1, Vector3.Zero);
            root = 0;
        }

        foreach (var side in new[] { 'C', 'L', 'R' })
        {
            foreach (var role in Roles)
            {
                if (role.Sided != (side != 'C'))
                    continue;
                var position = role.Position;
                if (aPose && side != 'C' && IsArm(role.Key))
                {
                    var joint = Roles.First(r => r.Key == "UpperArm").Position;
                    position = joint + Vector3.Transform(position - joint, Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, MathF.PI / 4f));
                }

                if (side == 'R')
                    position = position with { X = -position.X };
                var parent = role.Parent is null ? root
                    : indexOf.TryGetValue((role.Parent, Roles.First(r => r.Key == role.Parent).Sided ? side : 'C'), out var p) ? p : -1;
                var name = family == RigFamily.Unnamed
                    ? unnamed++ == 0 ? "Bone" : $"Bone.{unnamed - 1:000}"
                    : Name(family, role.Key, side, 0);
                if (name is null)
                    continue;
                indexOf[(role.Key, side)] = bones.Count;
                Add(name, parent, position);
            }
        }

        if (family == RigFamily.Unreal)
        {
            // Twist bones beside the limbs and IK bones under the root, as the UE mannequin has.
            foreach (var (side, x) in new[] { ('l', 1f), ('r', -1f) })
            {
                var c = side == 'l' ? 'L' : 'R';
                Add($"upperarm_twist_01_{side}", indexOf[("UpperArm", c)], new Vector3(0.32f * x, 1.50f, 0));
                Add($"lowerarm_twist_01_{side}", indexOf[("LowerArm", c)], new Vector3(0.60f * x, 1.50f, 0));
                Add($"thigh_twist_01_{side}", indexOf[("UpperLeg", c)], new Vector3(0.10f * x, 0.75f, 0));
                Add($"calf_twist_01_{side}", indexOf[("LowerLeg", c)], new Vector3(0.10f * x, 0.30f, 0));
            }

            Add("ik_foot_root", 0, Vector3.Zero);
            Add("ik_foot_l", bones.Count - 1, new Vector3(0.10f, 0.10f, 0));
        }

        // Global rotations: identity, or each bone's +Y along it (to its first child) with a per-bone roll.
        var count = bones.Count;
        var rotations = new Quaternion[count];
        for (var i = 0; i < count; i++)
        {
            if (!boneFrames)
            {
                rotations[i] = turn;
                continue;
            }

            var child = bones.FindIndex(b => b.Parent == i);
            var direction = child >= 0 ? bones[child].Position - bones[i].Position : Vector3.Zero;
            if (direction.LengthSquared() < 1e-10f)
                direction = bones[i].Parent >= 0 ? bones[i].Position - bones[bones[i].Parent].Position : Vector3.UnitY;
            if (direction.LengthSquared() < 1e-10f)
                direction = Vector3.UnitY;
            var along = FromTo(Vector3.UnitY, Vector3.Normalize(direction));
            rotations[i] = Quaternion.Normalize(along * Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 0.3f));
        }

        var skeleton = new Skeleton3D { Name = "Armature" };
        if (unitsPerMetre != 1f)
            skeleton.Transform = Transform3D.FromTrs(Vector3.Zero, Quaternion.Identity, new Vector3(1f / unitsPerMetre));
        var globals = new Transform3D[count];
        for (var i = 0; i < count; i++)
        {
            globals[i] = Transform3D.FromTrs(bones[i].Position, rotations[i], Vector3.One);
            var parent = bones[i].Parent;
            var local = parent >= 0 ? globals[parent].AffineInverse() * globals[i] : globals[i];
            skeleton.AddBone(bones[i].Name, parent, local);
        }

        return skeleton;
    }

    // Everything from the elbow out turns about the shoulder joint in an A-pose.
    private static bool IsArm(string role) =>
        role is "LowerArm" or "Hand" || role.StartsWith("Thumb", StringComparison.Ordinal) ||
        role.StartsWith("Index", StringComparison.Ordinal) || role.StartsWith("Middle", StringComparison.Ordinal);

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot < -0.999999f)
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
    }
}
