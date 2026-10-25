using System.Numerics;

namespace MainframeEngine;

/// <summary>How an <see cref="Animation"/> continues past its end (Godot's <c>LoopMode</c>).</summary>
public enum AnimationLoopMode : byte
{
    /// <summary>Plays once and holds the last frame (raises <see cref="AnimationPlayer.AnimationFinished"/>).</summary>
    None,

    /// <summary>Wraps from the end back to the start.</summary>
    Linear,

    /// <summary>Plays forwards, then backwards, then forwards again.</summary>
    PingPong,
}

/// <summary>How a track's value moves between two keys (Godot's <c>InterpolationType</c>).</summary>
public enum AnimationInterpolation : byte
{
    /// <summary>Holds each key until the next.</summary>
    Nearest,

    /// <summary>Lerps vectors and slerps rotations (shortest arc).</summary>
    Linear,
}

/// <summary>
/// Keyed motion over time (Godot's <c>Animation</c>, ADR 0207): transform tracks for <see cref="Node3D"/>s and
/// <see cref="Skeleton3D"/> bones, and event tracks that raise <see cref="AnimationPlayer.AnimationEvent"/>. Played by an
/// <see cref="AnimationPlayer"/> from an <see cref="AnimationLibrary"/>; its name is its <see cref="Resource.ResourceName"/>.
/// Imported models carry one per clip (<see cref="ModelImporter"/>).
/// </summary>
/// <remarks>
/// Keys are parallel arrays (times ascending, in seconds), like <see cref="MeshSurface"/>'s. After editing arrays in place call
/// <see cref="NotifyChanged"/> so players re-bind.
/// </remarks>
[EditorIcon("movie")]
public sealed class Animation : Resource
{
    private List<AnimationTrack> _tracks = [];

    /// <summary>Length in seconds.</summary>
    [Export(Range = "0.001,3600,0.001")]
    public float Length
    {
        get;
        set
        {
            field = value;
            NotifyChanged();
        }
    } = 1f;

    [Export]
    public AnimationLoopMode LoopMode
    {
        get;
        set
        {
            field = value;
            NotifyChanged();
        }
    }

    [Export]
    public List<AnimationTrack> Tracks
    {
        get => _tracks;
        set
        {
            _tracks = value ?? [];
            NotifyChanged();
        }
    }

    /// <summary>Bumped by every change; players re-bind an animation whose version moved.</summary>
    public int Version { get; private set; } = 1;

    /// <summary>Call after editing tracks or key arrays in place.</summary>
    public void NotifyChanged()
    {
        Version++;
        EmitChanged();
    }

    /// <summary>Appends a track and returns its index.</summary>
    public int AddTrack(AnimationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _tracks.Add(track);
        NotifyChanged();
        return _tracks.Count - 1;
    }

    /// <summary>The event track (created on first use) and an event <paramref name="name"/> at <paramref name="time"/> seconds.</summary>
    public void AddEvent(string name, float time)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EventTrack? events = null;
        foreach (var track in _tracks)
            if (track is EventTrack e)
                events = e;
        if (events is null)
            _tracks.Add(events = new EventTrack());
        events.Insert(time, name);
        NotifyChanged();
    }

    /// <summary>Every event (name, time) of the animation, in time order per event track.</summary>
    public IEnumerable<(string Name, float Time)> GetEvents()
    {
        foreach (var track in _tracks)
            if (track is EventTrack events)
                for (var i = 0; i < events.Times.Length && i < events.Names.Length; i++)
                    yield return (events.Names[i], events.Times[i]);
    }
}

/// <summary>One animated channel of an <see cref="Animation"/>: a target path and key times.</summary>
public abstract class AnimationTrack : Resource
{
    /// <summary>The animated node, relative to the player's <see cref="AnimationPlayer.RootNode"/>.</summary>
    [Export]
    public NodePath Path { get; set; }

    /// <summary>Disabled tracks are not applied.</summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>Key times in seconds, ascending.</summary>
    [Export]
    public float[] Times { get; set; } = [];

    /// <summary>
    /// The key at or before <paramref name="time"/> (−1 before the first key). <paramref name="cursor"/> remembers the last
    /// segment: normal playback moves it forward a step at most, a jump binary-searches.
    /// </summary>
    internal static int FindKey(float[] times, double time, ref int cursor)
    {
        var count = times.Length;
        if (count == 0 || time < times[0])
            return cursor = -1;
        var c = (uint)cursor < (uint)count ? cursor : 0;
        if (times[c] <= time)
        {
            var steps = 0;
            while (c + 1 < count && times[c + 1] <= time)
            {
                c++;
                if (++steps > 2)
                    return cursor = BinarySearch(times, time);
            }

            return cursor = c;
        }

        return cursor = BinarySearch(times, time);
    }

    // The last index whose time is <= time (times[0] <= time).
    private static int BinarySearch(float[] times, double time)
    {
        int lo = 0, hi = times.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (times[mid] <= time)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    // Segment (key, fraction) for interpolating at time: (k, 0) on or before the first and on or after the last key.
    internal static (int Key, float Fraction) Segment(float[] times, double time, ref int cursor, AnimationInterpolation interpolation)
    {
        var key = FindKey(times, time, ref cursor);
        if (key < 0)
            return (0, 0f);
        if (key >= times.Length - 1 || interpolation == AnimationInterpolation.Nearest)
            return (key, 0f);
        var span = times[key + 1] - times[key];
        return (key, span > 0f ? (float)Math.Clamp((time - times[key]) / span, 0.0, 1.0) : 0f);
    }
}

/// <summary>
/// The position of a <see cref="Node3D"/> or of a bone (Godot's <c>TYPE_POSITION_3D</c>): <see cref="AnimationTrack.Path"/>
/// names the node or the <see cref="Skeleton3D"/>, <see cref="Bone"/> the bone (empty: the node itself).
/// </summary>
public sealed class Position3DTrack : AnimationTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Vector3[] Values { get; set; } = [];

    [Export]
    public AnimationInterpolation Interpolation { get; set; } = AnimationInterpolation.Linear;

    /// <summary>The value at <paramref name="time"/>.</summary>
    public Vector3 Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Vector3 Sample(double time, ref int cursor)
    {
        var values = Values;
        if (values.Length == 0)
            return Vector3.Zero;
        var (key, f) = Segment(Times, time, ref cursor, Interpolation);
        key = Math.Min(key, values.Length - 1);
        return f > 0f && key + 1 < values.Length ? Vector3.Lerp(values[key], values[key + 1], f) : values[key];
    }
}

/// <summary>The rotation of a <see cref="Node3D"/> or a bone (Godot's <c>TYPE_ROTATION_3D</c>); slerped (shortest arc).</summary>
public sealed class Rotation3DTrack : AnimationTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Quaternion[] Values { get; set; } = [];

    [Export]
    public AnimationInterpolation Interpolation { get; set; } = AnimationInterpolation.Linear;

    /// <summary>The value at <paramref name="time"/>.</summary>
    public Quaternion Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Quaternion Sample(double time, ref int cursor)
    {
        var values = Values;
        if (values.Length == 0)
            return Quaternion.Identity;
        var (key, f) = Segment(Times, time, ref cursor, Interpolation);
        key = Math.Min(key, values.Length - 1);
        return f > 0f && key + 1 < values.Length ? Quaternion.Slerp(values[key], values[key + 1], f) : values[key];
    }
}

/// <summary>The scale of a <see cref="Node3D"/> or a bone (Godot's <c>TYPE_SCALE_3D</c>).</summary>
public sealed class Scale3DTrack : AnimationTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Vector3[] Values { get; set; } = [];

    [Export]
    public AnimationInterpolation Interpolation { get; set; } = AnimationInterpolation.Linear;

    /// <summary>The value at <paramref name="time"/>.</summary>
    public Vector3 Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Vector3 Sample(double time, ref int cursor)
    {
        var values = Values;
        if (values.Length == 0)
            return Vector3.One;
        var (key, f) = Segment(Times, time, ref cursor, Interpolation);
        key = Math.Min(key, values.Length - 1);
        return f > 0f && key + 1 < values.Length ? Vector3.Lerp(values[key], values[key + 1], f) : values[key];
    }
}

/// <summary>
/// Named moments of an animation (ADR 0207): when playback passes a key, the player raises
/// <see cref="AnimationPlayer.AnimationEvent"/> with its name (a Spine-style event: a footstep, the frame a swing lands).
/// <see cref="AnimationTrack.Path"/> is unused. Imported from glTF animation extras or the model's <c>.meta</c> clips.
/// </summary>
public sealed class EventTrack : AnimationTrack
{
    /// <summary>The event name of each key (parallel to <see cref="AnimationTrack.Times"/>).</summary>
    [Export]
    public string[] Names { get; set; } = [];

    /// <summary>Inserts an event keeping the keys in time order.</summary>
    public void Insert(float time, string name)
    {
        var times = Times;
        var index = 0;
        while (index < times.Length && times[index] <= time)
            index++;
        Times = [.. times[..index], time, .. times[index..]];
        Names = [.. Names[..Math.Min(index, Names.Length)], name, .. Names[Math.Min(index, Names.Length)..]];
    }
}
