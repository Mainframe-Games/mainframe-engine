using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Two-bone IK (G1c): an upper bone and a hinge (thigh and shin, upper arm and forearm) reaching for a target, the middle
/// joint kept in the plane of the root, the target and a pole. Shared by the pose baker and, later, the editor's IK handles.
/// </summary>
public static class TwoBoneIk
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Where the middle joint goes for the chain <paramref name="root"/> → <paramref name="mid"/> → <paramref name="end"/> to
    /// reach <paramref name="target"/>, bending towards <paramref name="pole"/> (a direction). A target out of reach
    /// straightens the chain towards it; one closer than the bones allow folds it as far as it goes. Returns
    /// <paramref name="mid"/> unchanged for a zero-length bone. <paramref name="reached"/> is where the end lands.
    /// </summary>
    public static Vector3 SolveMid(Vector3 root, Vector3 mid, Vector3 end, Vector3 target, Vector3 pole, out Vector3 reached)
    {
        var upper = Vector3.Distance(root, mid);
        var lower = Vector3.Distance(mid, end);
        var toTarget = target - root;
        var distance = toTarget.Length();
        if (upper < Epsilon || lower < Epsilon || distance < Epsilon)
        {
            reached = end;
            return mid;
        }

        var axis = toTarget / distance;
        var bend = pole - axis * Vector3.Dot(pole, axis);
        if (bend.LengthSquared() < Epsilon)
        {
            bend = mid - root;
            bend -= axis * Vector3.Dot(bend, axis);
        }

        if (bend.LengthSquared() < Epsilon)
            bend = Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        bend = Vector3.Normalize(bend);

        var clamped = Math.Clamp(distance, MathF.Abs(upper - lower) + Epsilon, upper + lower - Epsilon);
        var cosRoot = Math.Clamp((upper * upper + clamped * clamped - lower * lower) / (2f * upper * clamped), -1f, 1f);
        var sinRoot = MathF.Sqrt(MathF.Max(0f, 1f - cosRoot * cosRoot));
        var newMid = root + axis * (upper * cosRoot) + bend * (upper * sinRoot);
        reached = newMid + Vector3.Normalize(root + axis * clamped - newMid) * lower;
        return newMid;
    }

    /// <summary>
    /// Turns <paramref name="upper"/> and <paramref name="lower"/> of <paramref name="skeleton"/> (in its current pose) so
    /// <paramref name="end"/>'s joint reaches <paramref name="target"/> (skeleton space), each by the shortest arc (so the
    /// bones' own twist is kept). When <paramref name="endRotation"/> is given, <paramref name="end"/>'s global rotation is
    /// set to it afterwards (a pinned foot staying level). Returns the distance left to the target.
    /// </summary>
    public static float Apply(Skeleton3D skeleton, int upper, int lower, int end, Vector3 target, Vector3 pole,
        Quaternion? endRotation = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var rootPose = skeleton.GetBoneGlobalPose(upper);
        var midPose = skeleton.GetBoneGlobalPose(lower);
        var endPose = skeleton.GetBoneGlobalPose(end);
        var newMid = SolveMid(rootPose.Origin, midPose.Origin, endPose.Origin, target, pole, out var reached);

        Turn(skeleton, upper, midPose.Origin - rootPose.Origin, newMid - rootPose.Origin);
        var movedMid = skeleton.GetBoneGlobalPose(lower);
        var movedEnd = skeleton.GetBoneGlobalPose(end);
        Turn(skeleton, lower, movedEnd.Origin - movedMid.Origin, reached - movedMid.Origin);

        if (endRotation is { } rotation)
            SetGlobalRotation(skeleton, end, rotation);
        return Vector3.Distance(skeleton.GetBoneGlobalPose(end).Origin, target);
    }

    /// <summary>Sets a bone's local rotation so its global (skeleton-space) rotation is <paramref name="global"/>.</summary>
    public static void SetGlobalRotation(Skeleton3D skeleton, int bone, Quaternion global)
    {
        var parent = skeleton.GetBoneParent(bone);
        var parentRotation = parent >= 0 ? skeleton.GetBoneGlobalPose(parent).Basis.GetRotation() : Quaternion.Identity;
        skeleton.SetBonePoseRotation(bone, Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * global));
    }

    // Rotates `bone` (globally, about its own joint) by the shortest arc taking `from` to `to`.
    private static void Turn(Skeleton3D skeleton, int bone, Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < Epsilon || to.LengthSquared() < Epsilon)
            return;
        var global = skeleton.GetBoneGlobalPose(bone).Basis.GetRotation();
        SetGlobalRotation(skeleton, bone, Quaternion.Normalize(FromTo(from, to) * global));
    }

    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Vector3.Dot(from, to);
        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
    }
}
