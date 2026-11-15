using System.Globalization;
using System.Numerics;
using System.Text;

namespace MainframeEngine;

/// <summary>Where an IK effector (a foot or a hand) is at one time of a described clip.</summary>
/// <param name="Position">Character space (metres from the skeleton origin, canonical axes): the space of IK <c>pos</c> targets.</param>
/// <param name="Height">Height of its lowest point (the toes for a foot) above the ground (metres).</param>
/// <param name="Contact">Within <see cref="ClipDescriber.ContactDistance"/> of the ground.</param>
public readonly record struct EffectorState(Vector3 Position, float Height, bool Contact);

/// <summary>One time of a <see cref="ClipDescription"/>: the pose in canonical terms.</summary>
public sealed class ClipDescriptionTime
{
    public required float Time { get; init; }

    /// <summary>Some track has a key at this time; false for a sampled time.</summary>
    public required bool IsKey { get; init; }

    /// <summary>The canonical bones keyed at this time when only some tracks are (refinement keys, hand-placed keys); empty when all or none are.</summary>
    public required IReadOnlyList<HumanoidBone> PartialKeys { get; init; }

    /// <summary>The ease of the segment after this key (<see cref="InterpolatedTrack.Transitions"/>), the most common one among the tracks keyed here; null when sampled.</summary>
    public required float? Ease { get; init; }

    /// <summary>The pose: hips and every bone's angles (unmapped bones are zero).</summary>
    public required HumanoidPose Pose { get; init; }

    public required IReadOnlyDictionary<IkEffector, EffectorState> Effectors { get; init; }
}

/// <summary>A clip read back in canonical humanoid terms (G1c, <c>mfe anim describe</c>).</summary>
public sealed class ClipDescription
{
    public required string Name { get; init; }

    public required float Length { get; init; }

    public required AnimationLoopMode Loop { get; init; }

    /// <summary>Made by the baker and unchanged since (<see cref="PoseLibrary.IsUntouchedBake"/>).</summary>
    public required bool UntouchedBake { get; init; }

    /// <summary>Has a <see cref="Animation.BakedHash"/> (baked once, maybe edited since).</summary>
    public required bool Baked { get; init; }

    /// <summary>The mapped canonical bones (the ones whose angles mean something on this rig).</summary>
    public required IReadOnlyList<HumanoidBone> MappedBones { get; init; }

    /// <summary>Transform tracks on the skeleton whose bone is not mapped (their motion is not described).</summary>
    public required IReadOnlyList<string> UnmappedTracks { get; init; }

    public required IReadOnlyList<ClipDescriptionTime> Times { get; init; }

    /// <summary>The clip as text, for people and agents: one block per time, only non-zero angles.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        var state = UntouchedBake ? "baked, untouched" : Baked ? "baked, changed since (hand edits)" : "not baked";
        text.Append(CultureInfo.InvariantCulture,
            $"{Name}: {F(Length)} s, loop {Loop}, {Times.Count(t => t.IsKey)} key time(s), {state}\n");
        text.Append("Angles in degrees from the canonical T-pose (flex/abduct/twist; bones not listed are 0); positions in metres, character space.\n");
        if (UnmappedTracks.Count > 0)
            text.Append("Tracks on unmapped bones (not described): ").Append(string.Join(", ", UnmappedTracks)).Append('\n');

        foreach (var time in Times)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nt = {F(time.Time)} s");
            if (!time.IsKey)
                text.Append(" (sampled)");
            else if (time.PartialKeys.Count > 0)
                text.Append(" (keys on ").Append(string.Join(", ", time.PartialKeys)).Append(" only)");
            if (time.Ease is { } ease && ease != 1f)
                text.Append(", ease ").Append(EaseName(ease));
            text.Append('\n');

            var hips = time.Pose.Hips;
            text.Append(CultureInfo.InvariantCulture,
                $"  hips: x {M(hips.Offset.X)} y {M(hips.Offset.Y)} z {M(hips.Offset.Z)} yaw {D(hips.Yaw)} pitch {D(hips.Pitch)} roll {D(hips.Roll)}\n");
            foreach (var bone in MappedBones)
            {
                if (bone == HumanoidBone.Hips)
                    continue;
                var a = time.Pose[bone];
                if (MathF.Abs(a.Flex) < 0.05f && MathF.Abs(a.Abduct) < 0.05f && MathF.Abs(a.Twist) < 0.05f)
                    continue;
                text.Append("  ").Append(bone).Append(':');
                if (MathF.Abs(a.Flex) >= 0.05f)
                    text.Append(" flex ").Append(D(a.Flex));
                if (MathF.Abs(a.Abduct) >= 0.05f)
                    text.Append(" abduct ").Append(D(a.Abduct));
                if (MathF.Abs(a.Twist) >= 0.05f)
                    text.Append(" twist ").Append(D(a.Twist));
                text.Append('\n');
            }

            foreach (var (effector, e) in time.Effectors)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"  {ClipDescriber.EffectorName(effector)}: [{M(e.Position.X)}, {M(e.Position.Y)}, {M(e.Position.Z)}] height {M(e.Height)}");
                if (e.Contact)
                    text.Append(" contact");
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string M(float v) => (MathF.Abs(v) < 5e-4f ? 0f : v).ToString("0.###", CultureInfo.InvariantCulture);

    private static string D(float v) => (MathF.Abs(v) < 0.05f ? 0f : v).ToString("0.#", CultureInfo.InvariantCulture);

    private static string EaseName(float ease) => ease switch
    {
        2f => "in",
        0.5f => "out",
        -2f => "inOut",
        _ => ease.ToString("0.###", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Reads a clip on a <see cref="HumanoidRig"/> back in canonical terms (G1c): per key time (or chosen times) the hips, every
/// mapped bone's joint angles, the feet's and hands' positions and ground contact, and the ease. Keys a person placed are
/// just keys: they read like any other. The rig's skeleton pose is restored afterwards.
/// </summary>
public static class ClipDescriber
{
    /// <summary>An effector this close to the ground (metres) is in contact.</summary>
    public const float ContactDistance = 0.02f;

    private static readonly IkEffector[] Effectors = [IkEffector.LeftFoot, IkEffector.RightFoot, IkEffector.LeftHand, IkEffector.RightHand];

    /// <summary>
    /// Describes <paramref name="clip"/> (its tracks on <paramref name="skeletonPath"/>) at its key times, or at
    /// <paramref name="times"/> when given.
    /// </summary>
    public static ClipDescription Describe(Animation clip, HumanoidRig rig, NodePath skeletonPath, IReadOnlyList<float>? times = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(rig);
        var skeleton = rig.Skeleton;
        var keyTimes = KeyTimes(clip, skeletonPath);
        var tracks = SkeletonTracks(clip, skeletonPath).ToList();
        var saved = SavePose(skeleton);
        try
        {
            skeleton.ResetBonePoses();
            var ground = Ground(rig);
            var described = new List<ClipDescriptionTime>();
            foreach (var time in times ?? keyTimes)
            {
                var t = Math.Clamp(time, 0f, clip.Length);
                skeleton.ResetBonePoses();
                ClipSampler.Apply(clip, skeleton, skeletonPath, t);
                var effectors = new Dictionary<IkEffector, EffectorState>();
                foreach (var effector in Effectors)
                    if (Effector(rig, effector, ground) is { } state)
                        effectors[effector] = state;

                var keyed = tracks.Where(track => TrackKeys.Find(track.Times, t) >= 0).ToList();
                var partial = keyed.Count > 0 && keyed.Count < tracks.Count
                    ? keyed.Select(track => rig.GetCanonicalBone(skeleton.FindBone(BoneOf(track)))).OfType<HumanoidBone>().Distinct().Order().ToList()
                    : [];
                float? ease = keyed.Count == 0
                    ? null
                    : keyed.Select(track => TrackKeys.Find(track.Times, t) is var i && i < track.Transitions.Length ? track.Transitions[i] : 1f)
                        .GroupBy(e => e).OrderByDescending(g => g.Count()).First().Key;
                described.Add(new ClipDescriptionTime
                {
                    Time = t,
                    IsKey = keyed.Count > 0,
                    PartialKeys = partial,
                    Ease = ease,
                    Pose = rig.ReadPose(),
                    Effectors = effectors,
                });
            }

            return new ClipDescription
            {
                Name = clip.ResourceName ?? "",
                Length = clip.Length,
                Loop = clip.LoopMode,
                Baked = clip.BakedHash.Length > 0,
                UntouchedBake = PoseLibrary.IsUntouchedBake(clip),
                MappedBones = [.. HumanoidBones.All.Where(b => rig.GetRigBone(b) >= 0)],
                UnmappedTracks = [.. tracks.Select(BoneOf).Distinct().Where(name => skeleton.FindBone(name) is var i && (i < 0 || rig.GetCanonicalBone(i) is null))],
                Times = described,
            };
        }
        finally
        {
            RestorePose(skeleton, saved);
        }
    }

    /// <summary>The clip's key times: the union of its transform tracks' key times on the skeleton, merged within 1 ms.</summary>
    public static IReadOnlyList<float> KeyTimes(Animation clip, NodePath skeletonPath)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var times = new List<float>();
        foreach (var time in SkeletonTracks(clip, skeletonPath).SelectMany(t => t.Times).Order())
            if (times.Count == 0 || time - times[^1] > TrackKeys.TimeTolerance)
                times.Add(time);
        return times;
    }

    /// <summary>The script name of an effector (<c>leftFoot</c>, …).</summary>
    public static string EffectorName(IkEffector effector) => effector switch
    {
        IkEffector.LeftFoot => "leftFoot",
        IkEffector.RightFoot => "rightFoot",
        IkEffector.LeftHand => "leftHand",
        _ => "rightHand",
    };

    /// <summary>The end bone of an effector's chain (a foot or a hand).</summary>
    public static HumanoidBone EndBone(IkEffector effector) => effector switch
    {
        IkEffector.LeftFoot => HumanoidBone.LeftFoot,
        IkEffector.RightFoot => HumanoidBone.RightFoot,
        IkEffector.LeftHand => HumanoidBone.LeftHand,
        _ => HumanoidBone.RightHand,
    };

    /// <summary>
    /// The ground height in character space: the lowest foot or toe of the rig's rest pose. Poses <paramref name="rig"/>'s
    /// skeleton at its rest.
    /// </summary>
    public static float Ground(HumanoidRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        rig.Skeleton.ResetBonePoses();
        var ground = float.PositiveInfinity;
        foreach (var bone in new[] { HumanoidBone.LeftFoot, HumanoidBone.RightFoot, HumanoidBone.LeftToes, HumanoidBone.RightToes })
            if (rig.GetRigBone(bone) is var index and >= 0)
                ground = MathF.Min(ground, rig.ToCharacterSpace(rig.Skeleton.GetBoneGlobalPose(index).Origin).Y);
        return float.IsFinite(ground) ? ground : 0f;
    }

    private static EffectorState? Effector(HumanoidRig rig, IkEffector effector, float ground)
    {
        var end = rig.GetRigBone(EndBone(effector));
        if (end < 0)
            return null;
        var position = rig.ToCharacterSpace(rig.Skeleton.GetBoneGlobalPose(end).Origin);
        var lowest = position.Y;
        var toes = effector switch
        {
            IkEffector.LeftFoot => HumanoidBone.LeftToes,
            IkEffector.RightFoot => HumanoidBone.RightToes,
            _ => (HumanoidBone?)null,
        };
        if (toes is { } t && rig.GetRigBone(t) is var toeIndex and >= 0)
            lowest = MathF.Min(lowest, rig.ToCharacterSpace(rig.Skeleton.GetBoneGlobalPose(toeIndex).Origin).Y);
        var height = lowest - ground;
        return new EffectorState(position, height, height <= ContactDistance);
    }

    internal static IEnumerable<InterpolatedTrack> SkeletonTracks(Animation clip, NodePath skeletonPath) =>
        clip.Tracks.OfType<InterpolatedTrack>().Where(t => t.Path == skeletonPath && t is Rotation3DTrack or Position3DTrack or Scale3DTrack);

    internal static string BoneOf(AnimationTrack track) => track switch
    {
        Rotation3DTrack r => r.Bone,
        Position3DTrack p => p.Bone,
        Scale3DTrack s => s.Bone,
        _ => "",
    };

    internal static (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] SavePose(Skeleton3D skeleton)
    {
        var count = skeleton.GetBoneCount();
        var saved = new (Vector3, Quaternion, Vector3)[count];
        for (var i = 0; i < count; i++)
            saved[i] = (skeleton.GetBonePosePosition(i), skeleton.GetBonePoseRotation(i), skeleton.GetBonePoseScale(i));
        return saved;
    }

    internal static void RestorePose(Skeleton3D skeleton, (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] saved)
    {
        for (var i = 0; i < saved.Length; i++)
        {
            skeleton.SetBonePosePosition(i, saved[i].Position);
            skeleton.SetBonePoseRotation(i, saved[i].Rotation);
            skeleton.SetBonePoseScale(i, saved[i].Scale);
        }
    }
}
