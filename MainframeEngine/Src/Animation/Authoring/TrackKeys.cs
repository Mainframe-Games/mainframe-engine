using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Key edits on transform tracks shared by the G1c authoring tools (the baker's refinement, edit scripts): set the key at a
/// time or insert one there, keeping times sorted, transitions in step and rotations in one quaternion hemisphere.
/// </summary>
internal static class TrackKeys
{
    /// <summary>Two key times closer than this (seconds) are the same key.</summary>
    public const float TimeTolerance = 1e-3f;

    /// <summary>The index of the key within <see cref="TimeTolerance"/> of <paramref name="time"/>, or −1.</summary>
    public static int Find(float[] times, double time)
    {
        for (var i = 0; i < times.Length; i++)
            if (Math.Abs(times[i] - time) <= TimeTolerance)
                return i;
        return -1;
    }

    /// <summary>Sets the key at <paramref name="time"/> (inserting one when the track has none there); returns its index.</summary>
    public static int SetRotation(Rotation3DTrack track, float time, Quaternion value)
    {
        var at = Find(track.Times, time);
        if (at < 0)
        {
            at = InsertIndex(track.Times, time);
            track.Times = [.. track.Times[..at], time, .. track.Times[at..]];
            track.Values = [.. track.Values[..at], value, .. track.Values[at..]];
            track.Transitions = InsertTransition(track.Transitions, at);
        }

        if (at > 0 && Quaternion.Dot(value, track.Values[at - 1]) < 0f)
            value = -value;
        track.Values[at] = value;
        // Keep the following keys in the same hemisphere as this one.
        if (at + 1 < track.Values.Length && Quaternion.Dot(track.Values[at + 1], value) < 0f)
            for (var i = at + 1; i < track.Values.Length; i++)
                track.Values[i] = -track.Values[i];
        return at;
    }

    /// <summary>Sets the key at <paramref name="time"/> (inserting one when the track has none there); returns its index.</summary>
    public static int SetPosition(Position3DTrack track, float time, Vector3 value)
    {
        var at = Find(track.Times, time);
        if (at < 0)
        {
            at = InsertIndex(track.Times, time);
            track.Times = [.. track.Times[..at], time, .. track.Times[at..]];
            track.Values = [.. track.Values[..at], value, .. track.Values[at..]];
            track.Transitions = InsertTransition(track.Transitions, at);
        }

        track.Values[at] = value;
        return at;
    }

    /// <summary>Where a key at <paramref name="time"/> goes in sorted <paramref name="times"/>.</summary>
    public static int InsertIndex(float[] times, float time)
    {
        var at = 0;
        while (at < times.Length && times[at] < time)
            at++;
        return at;
    }

    // A new key splits the segment before it: it takes that segment's ease.
    private static float[] InsertTransition(float[] transitions, int at)
    {
        var ease = at > 0 && at - 1 < transitions.Length ? transitions[at - 1] : 1f;
        return [.. transitions[..Math.Min(at, transitions.Length)], ease, .. transitions[Math.Min(at, transitions.Length)..]];
    }
}
