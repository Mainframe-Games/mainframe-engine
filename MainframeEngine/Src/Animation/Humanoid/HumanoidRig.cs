using System.Numerics;

namespace MainframeEngine;

/// <summary>What a rig's rest pose looks like, from its upper arms' angle below horizontal.</summary>
public enum RestPoseKind : byte
{
    /// <summary>Arms within 15° of horizontal.</summary>
    TPose,

    /// <summary>Arms 15–65° below horizontal.</summary>
    APose,

    /// <summary>Anything else (arms down, raised, or unmapped).</summary>
    Other,
}

/// <summary>A rig's sizes in metres, measured on its rest pose.</summary>
/// <param name="Height">Ground to the highest bone.</param>
/// <param name="HipHeight">Ground to the hips bone.</param>
/// <param name="LegLength">Upper leg plus lower leg (hip to ankle).</param>
/// <param name="ArmLength">Upper arm plus lower arm (shoulder joint to wrist).</param>
/// <param name="ShoulderWidth">Between the two upper arm joints.</param>
public readonly record struct HumanoidProportions(float Height, float HipHeight, float LegLength, float ArmLength, float ShoulderWidth);

/// <summary>
/// A <see cref="HumanoidMap"/> bound to one skeleton's rest pose (G1c, ADR 0281): turns canonical joint angles into the rig's
/// local bone rotations and back. Built once per skeleton (the frames depend only on the rest pose).
/// </summary>
/// <remarks>
/// <para>The rig's character frame <see cref="CharacterRotation"/> maps the canonical axes (Y up, +Z forward, +X left) into
/// skeleton space, from the rest pose: up from the ankles to the hips, left from the right to the left upper leg.</para>
/// <para>In the canonical T-pose every bone's global rotation is identity. The rig's T-pose is its rest with the legs and
/// arms straightened (<see cref="HumanoidBones.IsDirectionCorrected"/>: each turned by the shortest arc from its rest
/// direction to the canonical one), every other bone keeping its rest relation to its parent; that is how an A-pose rig
/// takes the same angles as a T-pose rig. A canonical pose with global rotations <c>P</c> gives a mapped rig bone the global
/// rotation <c>C·P·K</c>, where <c>C</c> is the character frame and <c>K = Q_fix·C⁻¹·G_rest</c>; unmapped rig bones keep
/// their rest local rotation.</para>
/// </remarks>
public sealed class HumanoidRig
{
    private readonly int[] _rigIndex = new int[HumanoidBones.Count];
    private readonly Quaternion[] _fix = new Quaternion[HumanoidBones.Count];
    private readonly Quaternion[] _k = new Quaternion[HumanoidBones.Count];
    private readonly HumanoidBone?[] _canonicalOf;
    private readonly Quaternion[] _restLocal;
    private readonly Quaternion[] _restGlobal;
    private readonly Vector3[] _restPosition;
    private readonly Vector3[] _restLocalPosition;
    private readonly int[] _order;
    private readonly Quaternion _c;
    private readonly Quaternion _cInverse;

    public HumanoidRig(Skeleton3D skeleton, HumanoidMap map)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(map);
        Skeleton = skeleton;
        Map = map;

        var count = skeleton.GetBoneCount();
        _canonicalOf = new HumanoidBone?[count];
        _restLocal = new Quaternion[count];
        _restGlobal = new Quaternion[count];
        _restPosition = new Vector3[count];
        _restLocalPosition = new Vector3[count];
        var depth = new int[count];
        for (var i = 0; i < count; i++)
        {
            var rest = skeleton.GetBoneRest(i);
            _restLocal[i] = rest.Basis.GetRotation();
            _restLocalPosition[i] = rest.Origin;
            var global = skeleton.GetBoneGlobalRest(i);
            _restGlobal[i] = global.Basis.GetRotation();
            _restPosition[i] = global.Origin;
            for (var p = skeleton.GetBoneParent(i); p >= 0; p = skeleton.GetBoneParent(p))
                depth[i]++;
        }

        _order = [.. Enumerable.Range(0, count).OrderBy(i => depth[i])];

        foreach (var bone in HumanoidBones.All)
        {
            var index = map.Get(bone) is { } name ? skeleton.FindBone(name) : -1;
            _rigIndex[(int)bone] = index;
            if (index >= 0)
                _canonicalOf[index] = bone;
        }

        foreach (var required in new[]
                 {
                     HumanoidBone.Hips, HumanoidBone.LeftUpperLeg, HumanoidBone.RightUpperLeg, HumanoidBone.LeftFoot,
                     HumanoidBone.RightFoot,
                 })
        {
            if (_rigIndex[(int)required] < 0)
                throw new ArgumentException($"The humanoid map has no {required} on this skeleton.", nameof(map));
        }

        MetresPerUnit = MeasureScale(skeleton);
        _c = CharacterFrame();
        _cInverse = Quaternion.Conjugate(_c);

        foreach (var bone in HumanoidBones.All)
        {
            var fix = HumanoidBones.GetParent(bone) is { } parent ? _fix[(int)parent] : Quaternion.Identity;
            if (HumanoidBones.IsDirectionCorrected(bone) && RestDirection(bone) is { } direction)
                fix = FromTo(direction, HumanoidBones.GetDirection(bone));
            _fix[(int)bone] = fix;
            var index = _rigIndex[(int)bone];
            if (index >= 0)
                _k[(int)bone] = Quaternion.Normalize(fix * _cInverse * _restGlobal[index]);
        }

        (RestKind, RestArmAngle) = MeasureRestKind();
        Proportions = MeasureProportions();
    }

    /// <summary>The skeleton the rig is bound to.</summary>
    public Skeleton3D Skeleton { get; }

    /// <summary>The map the rig uses.</summary>
    public HumanoidMap Map { get; }

    /// <summary>Rotation from the canonical axes to the skeleton's (the character frame).</summary>
    public Quaternion CharacterRotation => _c;

    /// <summary>World metres per skeleton unit (the skeleton's global scale; 0.01 for a centimetre rig imported at 0.01).</summary>
    public float MetresPerUnit { get; }

    /// <summary>The rest pose's kind, from <see cref="RestArmAngle"/>.</summary>
    public RestPoseKind RestKind { get; }

    /// <summary>The upper arms' mean angle below horizontal in the rest pose (degrees; NaN when unmapped).</summary>
    public float RestArmAngle { get; }

    /// <summary>The rig's sizes in metres.</summary>
    public HumanoidProportions Proportions { get; }

    /// <summary>The rig bone index of <paramref name="bone"/>, or −1 when unmapped.</summary>
    public int GetRigBone(HumanoidBone bone) => _rigIndex[(int)bone];

    /// <summary>The canonical bone a rig bone is mapped to, or null.</summary>
    public HumanoidBone? GetCanonicalBone(int rigBone) => _canonicalOf[rigBone];

    // ── Single bones ────────────────────────────────────────────────────────

    /// <summary>
    /// The rig-local rotation of <paramref name="bone"/> for <paramref name="angles"/>, with every other bone in the canonical
    /// T-pose. Throws for an unmapped bone. For <see cref="HumanoidBone.Hips"/> the angles are read as yaw (twist), roll
    /// (abduct) and pitch (flex), composed as <see cref="HipsRotation"/>; use <see cref="ApplyPose"/> for whole poses.
    /// </summary>
    public Quaternion ToBoneRotation(HumanoidBone bone, in JointAngles angles)
    {
        var index = RequireRigBone(bone);
        var local = JointRotation(bone, angles);
        return Quaternion.Normalize(Quaternion.Conjugate(ParentFrame(index)) * _c * local * _k[(int)bone]);
    }

    /// <summary>The inverse of <see cref="ToBoneRotation"/>: a rig-local rotation of <paramref name="bone"/> as joint angles.</summary>
    public JointAngles FromBoneRotation(HumanoidBone bone, Quaternion local)
    {
        var index = RequireRigBone(bone);
        var canonical = _cInverse * ParentFrame(index) * local * Quaternion.Conjugate(_k[(int)bone]);
        return JointAnglesOf(bone, canonical);
    }

    /// <summary>
    /// <paramref name="canonicalDirection"/> (a direction in the canonical T-pose frame of <paramref name="bone"/>, e.g. +Z,
    /// the knee's forward) turned by the bone's current pose into skeleton space.
    /// </summary>
    public Vector3 CurrentDirection(HumanoidBone bone, Vector3 canonicalDirection)
    {
        var index = RequireRigBone(bone);
        var global = Skeleton.GetBoneGlobalPose(index).Basis.GetRotation();
        var canonical = Quaternion.Normalize(_cInverse * global * Quaternion.Conjugate(_k[(int)bone]));
        return Vector3.Transform(Vector3.Transform(canonicalDirection, canonical), _c);
    }

    /// <summary>A position in the character's canonical space (metres, from the skeleton origin) in skeleton space.</summary>
    public Vector3 ToSkeletonSpace(Vector3 canonicalPosition) => Vector3.Transform(canonicalPosition, _c) / MetresPerUnit;

    // The T-pose global rotation of a rig bone's parent (skeleton space): a mapped ancestor's C·K times the rest locals of
    // the unmapped bones below it, or the rest when no ancestor is mapped.
    private Quaternion ParentFrame(int index)
    {
        var parent = Skeleton.GetBoneParent(index);
        var chain = Quaternion.Identity;
        for (var at = parent; at >= 0; at = Skeleton.GetBoneParent(at))
        {
            if (_canonicalOf[at] is { } mapped)
                return _c * _k[(int)mapped] * chain;
            chain = _restLocal[at] * chain;
        }

        return chain;
    }

    private int RequireRigBone(HumanoidBone bone) =>
        _rigIndex[(int)bone] is var index and >= 0
            ? index
            : throw new ArgumentException($"{bone} is not mapped on this rig.", nameof(bone));

    // ── Whole poses ─────────────────────────────────────────────────────────

    /// <summary>
    /// Poses <see cref="Skeleton"/> as <paramref name="pose"/>: every mapped bone's rotation, the hips' position, every other
    /// bone at its rest. Canonical bones the rig lacks pass their rotation on to their mapped children.
    /// </summary>
    public void ApplyPose(HumanoidPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var global = CanonicalGlobals(pose);
        var count = Skeleton.GetBoneCount();
        var rigGlobal = new Quaternion[count];
        foreach (var i in _order)
        {
            var parent = Skeleton.GetBoneParent(i);
            var parentGlobal = parent >= 0 ? rigGlobal[parent] : Quaternion.Identity;
            rigGlobal[i] = _canonicalOf[i] is { } bone
                ? Quaternion.Normalize(_c * global[(int)bone] * _k[(int)bone])
                : parentGlobal * _restLocal[i];
            Skeleton.SetBonePoseRotation(i, Quaternion.Normalize(Quaternion.Conjugate(parentGlobal) * rigGlobal[i]));
            Skeleton.SetBonePosePosition(i, _restLocalPosition[i]);
        }

        var hips = _rigIndex[(int)HumanoidBone.Hips];
        var hipsParent = Skeleton.GetBoneParent(hips);
        var position = _restPosition[hips] + Vector3.Transform(pose.Hips.Offset, _c) / MetresPerUnit;
        if (hipsParent >= 0)
            position = Skeleton.GetBoneGlobalRest(hipsParent).AffineInverse().TransformPoint(position);
        Skeleton.SetBonePosePosition(hips, position);
    }

    /// <summary>Reads <see cref="Skeleton"/>'s current pose as a canonical pose (unmapped canonical bones read as zero).</summary>
    public HumanoidPose ReadPose()
    {
        var count = Skeleton.GetBoneCount();
        var rigGlobal = new Quaternion[count];
        foreach (var i in _order)
        {
            var parent = Skeleton.GetBoneParent(i);
            var local = Skeleton.GetBonePoseRotation(i);
            rigGlobal[i] = parent >= 0 ? rigGlobal[parent] * local : local;
        }

        var pose = new HumanoidPose();
        var global = new Quaternion[HumanoidBones.Count];
        foreach (var bone in HumanoidBones.All)
        {
            var parentGlobal = HumanoidBones.GetParent(bone) is { } parent ? global[(int)parent] : Quaternion.Identity;
            var index = _rigIndex[(int)bone];
            if (index < 0)
            {
                global[(int)bone] = parentGlobal;
                continue;
            }

            global[(int)bone] = Quaternion.Normalize(_cInverse * rigGlobal[index] * Quaternion.Conjugate(_k[(int)bone]));
            if (bone == HumanoidBone.Hips)
                continue;
            pose[bone] = JointAnglesOf(bone, Quaternion.Conjugate(parentGlobal) * global[(int)bone]);
        }

        var hips = _rigIndex[(int)HumanoidBone.Hips];
        var hipsAngles = HipsAnglesOf(global[(int)HumanoidBone.Hips]);
        var offset = Vector3.Transform(Skeleton.GetBoneGlobalPose(hips).Origin - _restPosition[hips], _cInverse) * MetresPerUnit;
        pose.Hips = new HipsPose(offset, hipsAngles.Yaw, hipsAngles.Pitch, hipsAngles.Roll);
        return pose;
    }

    /// <summary>The rig's own rest pose as a canonical pose (the <c>rest</c> named pose).</summary>
    public HumanoidPose RestPose()
    {
        var saved = SavePose();
        Skeleton.ResetBonePoses();
        var pose = ReadPose();
        RestorePose(saved);
        return pose;
    }

    private (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] SavePose()
    {
        var count = Skeleton.GetBoneCount();
        var saved = new (Vector3, Quaternion, Vector3)[count];
        for (var i = 0; i < count; i++)
            saved[i] = (Skeleton.GetBonePosePosition(i), Skeleton.GetBonePoseRotation(i), Skeleton.GetBonePoseScale(i));
        return saved;
    }

    private void RestorePose((Vector3 Position, Quaternion Rotation, Vector3 Scale)[] saved)
    {
        for (var i = 0; i < saved.Length; i++)
        {
            Skeleton.SetBonePosePosition(i, saved[i].Position);
            Skeleton.SetBonePoseRotation(i, saved[i].Rotation);
            Skeleton.SetBonePoseScale(i, saved[i].Scale);
        }
    }

    // Global rotations of every canonical bone (canonical space) for a pose.
    private static Quaternion[] CanonicalGlobals(HumanoidPose pose)
    {
        var global = new Quaternion[HumanoidBones.Count];
        foreach (var bone in HumanoidBones.All)
        {
            if (bone == HumanoidBone.Hips)
            {
                global[0] = HipsRotation(pose.Hips);
                continue;
            }

            var parent = HumanoidBones.GetParent(bone)!.Value;
            global[(int)bone] = Quaternion.Normalize(global[(int)parent] * JointRotation(bone, pose[bone]));
        }

        return global;
    }

    // ── Angle conventions ───────────────────────────────────────────────────

    /// <summary>
    /// The canonical local rotation of <paramref name="bone"/> for <paramref name="angles"/>: intrinsic twist, then abduct,
    /// then flex about the bone's joint-frame axes. Hinges use flex only.
    /// </summary>
    public static Quaternion JointRotation(HumanoidBone bone, in JointAngles angles)
    {
        if (bone == HumanoidBone.Hips)
            return HipsRotation(new HipsPose(default, angles.Twist, angles.Flex, angles.Abduct));
        var flex = Rotation(HumanoidBones.GetAxis(bone, JointComponent.Flex), angles.Flex);
        if (HumanoidBones.IsHinge(bone))
            return flex;
        var twist = Rotation(HumanoidBones.GetAxis(bone, JointComponent.Twist), angles.Twist);
        var abduct = Rotation(HumanoidBones.GetAxis(bone, JointComponent.Abduct), angles.Abduct);
        return Quaternion.Normalize(twist * abduct * flex);
    }

    /// <summary>
    /// The inverse of <see cref="JointRotation"/> for any rotation (hinges included: off-axis motion shows up in abduct and
    /// twist, which checks report). At abduct ±90° the decomposition is singular; twist is then 0.
    /// </summary>
    public static JointAngles JointAnglesOf(HumanoidBone bone, Quaternion canonicalLocal)
    {
        if (bone == HumanoidBone.Hips)
        {
            var (yaw, pitch, roll) = HipsAnglesOf(canonicalLocal);
            return new JointAngles(pitch, roll, yaw);
        }

        var (t, a, f) = Decompose(canonicalLocal, HumanoidBones.GetAxis(bone, JointComponent.Twist),
            HumanoidBones.GetAxis(bone, JointComponent.Abduct), HumanoidBones.GetAxis(bone, JointComponent.Flex));
        return new JointAngles(f, a, t);
    }

    /// <summary>The hips' canonical global rotation: intrinsic yaw (turn left), pitch (lean forward), roll (lean left).</summary>
    public static Quaternion HipsRotation(in HipsPose hips) =>
        Quaternion.Normalize(Rotation(Vector3.UnitY, hips.Yaw) * Rotation(Vector3.UnitX, hips.Pitch) *
                             Rotation(-Vector3.UnitZ, hips.Roll));

    /// <summary>The inverse of <see cref="HipsRotation"/> (offset not included).</summary>
    public static (float Yaw, float Pitch, float Roll) HipsAnglesOf(Quaternion rotation)
    {
        var (yaw, pitch, roll) = Decompose(rotation, Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        return (yaw, pitch, roll);
    }

    private static Quaternion Rotation(Vector3 axis, float degrees) =>
        degrees == 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(axis, degrees * (MathF.PI / 180f));

    // q = R(first, x)·R(second, y)·R(third, z) about three orthogonal unit axes; returns (x, y, z) in degrees.
    private static (float First, float Second, float Third) Decompose(Quaternion q, Vector3 first, Vector3 second, Vector3 third)
    {
        q = Quaternion.Normalize(q);
        // A left-handed axis set turns into a right-handed one by flipping the third axis (and its angle).
        var sign = Vector3.Dot(first, Vector3.Cross(second, third)) < 0f ? -1f : 1f;
        var e2 = third * sign;
        Vector3[] basis = [first, second, e2];
        float M(int i, int j) => Vector3.Dot(basis[i], Vector3.Transform(basis[j], q));

        // M = Rx(x)·Ry(y)·Rz(z) in the basis.
        var m02 = Math.Clamp(M(0, 2), -1f, 1f);
        var y = MathF.Asin(m02);
        float x, z;
        if (MathF.Abs(m02) < 0.999999f)
        {
            x = MathF.Atan2(-M(1, 2), M(2, 2));
            z = MathF.Atan2(-M(0, 1), M(0, 0));
        }
        else
        {
            x = 0f;
            z = MathF.Atan2(M(1, 0), M(1, 1));
        }

        const float toDegrees = 180f / MathF.PI;
        return (x * toDegrees, y * toDegrees, z * sign * toDegrees);
    }

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Vector3.Dot(from, to);
        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
    }

    // ── Measuring the rest ──────────────────────────────────────────────────

    private static float MeasureScale(Skeleton3D skeleton)
    {
        var scale = skeleton.GlobalTransform.Basis.X.Length();
        return scale > 1e-6f && float.IsFinite(scale) ? scale : 1f;
    }

    private Vector3 RestPositionOf(HumanoidBone bone) => _restPosition[_rigIndex[(int)bone]];

    private Quaternion CharacterFrame()
    {
        var ankles = (RestPositionOf(HumanoidBone.LeftFoot) + RestPositionOf(HumanoidBone.RightFoot)) * 0.5f;
        var up = RestPositionOf(HumanoidBone.Hips) - ankles;
        up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : Vector3.UnitY;
        var lateral = RestPositionOf(HumanoidBone.LeftUpperLeg) - RestPositionOf(HumanoidBone.RightUpperLeg);
        if (lateral.LengthSquared() < 1e-10f && _rigIndex[(int)HumanoidBone.LeftUpperArm] >= 0 &&
            _rigIndex[(int)HumanoidBone.RightUpperArm] >= 0)
            lateral = RestPositionOf(HumanoidBone.LeftUpperArm) - RestPositionOf(HumanoidBone.RightUpperArm);
        lateral -= up * Vector3.Dot(lateral, up);
        var left = lateral.LengthSquared() > 1e-10f ? Vector3.Normalize(lateral) : Vector3.UnitX;
        var forward = Vector3.Cross(left, up);
        return new Basis(left, up, forward).GetRotation();
    }

    // A limb bone's rest direction in canonical space: from its joint to the next joint of the limb.
    private Vector3? RestDirection(HumanoidBone bone)
    {
        var index = _rigIndex[(int)bone];
        if (index < 0 || EndOf(bone) is not { } end || _rigIndex[(int)end] < 0)
            return null;
        var direction = _restPosition[_rigIndex[(int)end]] - _restPosition[index];
        return direction.LengthSquared() > 1e-12f ? Vector3.Transform(Vector3.Normalize(direction), _cInverse) : null;
    }

    private static HumanoidBone? EndOf(HumanoidBone bone) => bone switch
    {
        HumanoidBone.LeftUpperLeg => HumanoidBone.LeftLowerLeg,
        HumanoidBone.LeftLowerLeg => HumanoidBone.LeftFoot,
        HumanoidBone.LeftUpperArm => HumanoidBone.LeftLowerArm,
        HumanoidBone.LeftLowerArm => HumanoidBone.LeftHand,
        HumanoidBone.RightUpperLeg => HumanoidBone.RightLowerLeg,
        HumanoidBone.RightLowerLeg => HumanoidBone.RightFoot,
        HumanoidBone.RightUpperArm => HumanoidBone.RightLowerArm,
        HumanoidBone.RightLowerArm => HumanoidBone.RightHand,
        _ => null,
    };

    private (RestPoseKind, float) MeasureRestKind()
    {
        var angles = new List<float>();
        foreach (var arm in new[] { HumanoidBone.LeftUpperArm, HumanoidBone.RightUpperArm })
            if (RestDirection(arm) is { } direction)
                angles.Add(MathF.Asin(Math.Clamp(-direction.Y, -1f, 1f)) * (180f / MathF.PI));
        if (angles.Count == 0)
            return (RestPoseKind.Other, float.NaN);
        var angle = angles.Average();
        var kind = MathF.Abs(angle) < 15f ? RestPoseKind.TPose : angle is >= 15f and <= 65f ? RestPoseKind.APose : RestPoseKind.Other;
        return (kind, angle);
    }

    private HumanoidProportions MeasureProportions()
    {
        var up = Vector3.Transform(Vector3.UnitY, _c);
        var ground = float.PositiveInfinity;
        foreach (var foot in new[] { HumanoidBone.LeftFoot, HumanoidBone.RightFoot, HumanoidBone.LeftToes, HumanoidBone.RightToes })
            if (_rigIndex[(int)foot] >= 0)
                ground = MathF.Min(ground, Vector3.Dot(RestPositionOf(foot), up));
        var top = _restPosition.Max(p => Vector3.Dot(p, up));

        float Distance(HumanoidBone a, HumanoidBone b) =>
            _rigIndex[(int)a] >= 0 && _rigIndex[(int)b] >= 0 ? Vector3.Distance(RestPositionOf(a), RestPositionOf(b)) : 0f;

        var s = MetresPerUnit;
        return new HumanoidProportions(
            Height: (top - ground) * s,
            HipHeight: (Vector3.Dot(RestPositionOf(HumanoidBone.Hips), up) - ground) * s,
            LegLength: (Distance(HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg) +
                        Distance(HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot)) * s,
            ArmLength: (Distance(HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm) +
                        Distance(HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand)) * s,
            ShoulderWidth: Distance(HumanoidBone.LeftUpperArm, HumanoidBone.RightUpperArm) * s);
    }
}
