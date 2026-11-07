using System.Numerics;

namespace MainframeEngine;

/// <summary>How a baked clip treats the character's travel (G1c, G1e).</summary>
public enum PoseRootMode : byte
{
    /// <summary>The clip never moves the character: arcs are ignored, the keys' hips offsets stay (a physics-driven jump).</summary>
    InPlace,

    /// <summary>Horizontal hips motion and arcs stay, for <see cref="AnimationPlayer"/> root motion with the hips as its bone.</summary>
    Motion,
}

/// <summary>An IK effector of a pose script.</summary>
public enum IkEffector : byte
{
    LeftFoot,
    RightFoot,
    LeftHand,
    RightHand,
}

/// <summary>What a key does to an <see cref="IkEffector"/>.</summary>
public enum IkCommandKind : byte
{
    /// <summary>Hold the effector where it is at this key until a <see cref="Release"/>.</summary>
    Pin,

    /// <summary>Stop holding it.</summary>
    Release,

    /// <summary>Reach <see cref="IkCommand.Target"/> at this key (character space, metres).</summary>
    Target,
}

/// <summary>An IK instruction of a key.</summary>
public readonly record struct IkCommand(IkCommandKind Kind, Vector3 Target = default);

/// <summary>Joint angles a key gives (degrees); null components hold the previous key's value.</summary>
public readonly record struct PartialAngles(float? Flex = null, float? Abduct = null, float? Twist = null)
{
    public JointAngles ApplyTo(JointAngles angles) =>
        new(Flex ?? angles.Flex, Abduct ?? angles.Abduct, Twist ?? angles.Twist);
}

/// <summary>The hips a key gives; null components hold the previous key's value.</summary>
public readonly record struct PartialHips(float? X = null, float? Y = null, float? Z = null, float? Yaw = null, float? Pitch = null,
    float? Roll = null)
{
    public HipsPose ApplyTo(HipsPose hips) => new(
        new Vector3(X ?? hips.Offset.X, Y ?? hips.Offset.Y, Z ?? hips.Offset.Z),
        Yaw ?? hips.Yaw, Pitch ?? hips.Pitch, Roll ?? hips.Roll);
}

/// <summary>One key pose of a <see cref="PoseScript"/>.</summary>
public sealed record PoseKey
{
    /// <summary>Time in seconds.</summary>
    public required float Time { get; init; }

    /// <summary>A named pose (<see cref="HumanoidPoses"/>) as this key's base; null: the previous key's pose.</summary>
    public string? Pose { get; init; }

    public PartialHips? Hips { get; init; }

    public IReadOnlyDictionary<HumanoidBone, PartialAngles> Joints { get; init; } = new Dictionary<HumanoidBone, PartialAngles>();

    /// <summary>Copy this key's left-side joints to the right (and right to left where only the right is given).</summary>
    public bool Mirror { get; init; }

    public IReadOnlyDictionary<IkEffector, IkCommand> Ik { get; init; } = new Dictionary<IkEffector, IkCommand>();

    /// <summary>The ease of the segment after this key (<see cref="InterpolatedTrack.Transitions"/>; null: linear).</summary>
    public float? Ease { get; init; }
}

/// <summary>A ballistic hips arc between two times (only in <see cref="PoseRootMode.Motion"/>).</summary>
/// <param name="From">Take-off time (s).</param>
/// <param name="To">Landing time (s).</param>
/// <param name="Height">Apex above take-off (m); null: from the project's gravity and the flight time.</param>
public readonly record struct PoseArc(float From, float To, float? Height);

/// <summary>
/// A pose script (G1c, ADR 0281): one clip as a few key poses in canonical joint angles, baked by <see cref="PoseBaker"/> into
/// ordinary keys. Input only: the engine never keeps it.
/// </summary>
public sealed record PoseScript
{
    /// <summary>The rigged model (project path).</summary>
    public required string Model { get; init; }

    /// <summary>The library <c>.mfres</c> the clip goes into (project path).</summary>
    public required string Library { get; init; }

    /// <summary>The clip's name in the library.</summary>
    public required string Name { get; init; }

    /// <summary>Length in seconds.</summary>
    public required float Length { get; init; }

    public AnimationLoopMode Loop { get; init; }

    public PoseRootMode Root { get; init; }

    /// <summary>The keys, in time order.</summary>
    public required IReadOnlyList<PoseKey> Keys { get; init; }

    public IReadOnlyList<PoseArc> Arcs { get; init; } = [];
}
