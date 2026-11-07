using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine;

/// <summary>
/// Poses a skeleton from a clip's bone tracks at a time, as <see cref="AnimationPlayer"/> would (G1c authoring tools: the
/// baker's refinement pass, checks, contact sheets). Bones without tracks keep their current pose.
/// </summary>
public static class ClipSampler
{
    /// <summary>Applies every transform track of <paramref name="animation"/> on <paramref name="skeletonPath"/> at <paramref name="time"/>.</summary>
    public static void Apply(Animation animation, Skeleton3D skeleton, NodePath skeletonPath, double time)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(skeleton);
        var loop = animation.LoopMode != AnimationLoopMode.None;
        foreach (var track in animation.Tracks)
        {
            if (!track.Enabled || track.Path != skeletonPath)
                continue;
            var cursor = -1;
            switch (track)
            {
                case Rotation3DTrack rotation when skeleton.FindBone(rotation.Bone) is var bone and >= 0:
                    skeleton.SetBonePoseRotation(bone, rotation.Sample(time, ref cursor, animation.Length, loop));
                    break;
                case Position3DTrack position when skeleton.FindBone(position.Bone) is var bone and >= 0:
                    skeleton.SetBonePosePosition(bone, position.Sample(time, ref cursor, animation.Length, loop));
                    break;
                case Scale3DTrack scale when skeleton.FindBone(scale.Bone) is var bone and >= 0:
                    skeleton.SetBonePoseScale(bone, scale.Sample(time, ref cursor, animation.Length, loop));
                    break;
            }
        }
    }
}

/// <summary>
/// The hash of a clip's keys (G1c, <see cref="Animation.BakedHash"/>): SHA-256 over its length, loop mode and every transform
/// track's target, interpolation, times, values and transitions, first 16 hex digits. The baker stores it; a clip whose hash
/// no longer matches was edited by hand.
/// </summary>
public static class AnimationHash
{
    public static string Compute(Animation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var text = new StringBuilder();
        text.Append(animation.Length.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append((int)animation.LoopMode).Append('\n');
        foreach (var track in animation.Tracks)
        {
            if (track is not InterpolatedTrack interpolated)
                continue;
            text.Append(track.GetType().Name).Append('|').Append(track.Path.ToString()).Append('|').Append((int)interpolated.Interpolation)
                .Append('|').Append(track.Enabled ? '1' : '0').Append('|');
            switch (track)
            {
                case Rotation3DTrack r:
                    text.Append(r.Bone).Append('|');
                    foreach (var q in r.Values)
                        Append(text, q.X, q.Y, q.Z, q.W);
                    break;
                case Position3DTrack p:
                    text.Append(p.Bone).Append('|');
                    foreach (var v in p.Values)
                        Append(text, v.X, v.Y, v.Z);
                    break;
                case Scale3DTrack s:
                    text.Append(s.Bone).Append('|');
                    foreach (var v in s.Values)
                        Append(text, v.X, v.Y, v.Z);
                    break;
                case ValueTrack value:
                    text.Append(value.Property).Append('|');
                    Append(text, value.Values);
                    break;
            }

            text.Append('|');
            Append(text, track.Times);
            text.Append('|');
            Append(text, interpolated.Transitions);
            text.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static void Append(StringBuilder text, params ReadOnlySpan<float> values)
    {
        foreach (var value in values)
            text.Append(BitConverter.SingleToInt32Bits(value)).Append(',');
    }
}
