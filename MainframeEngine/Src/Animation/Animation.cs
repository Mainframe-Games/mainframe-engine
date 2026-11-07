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

    /// <summary>
    /// Through the neighbouring keys in time (Godot's cubic: <see cref="Mathf.CubicInterpolateInTime(float, float, float, float, float, float, float, float)"/>;
    /// rotations: its spherical form).
    /// </summary>
    Cubic,
}

/// <summary>When a value track writes (Godot's <c>UpdateMode</c>).</summary>
public enum AnimationUpdateMode : byte
{
    /// <summary>Every frame, interpolated (bools, integers and enums are discrete anyway).</summary>
    Continuous,

    /// <summary>Only the key's value, when the playhead reaches it.</summary>
    Discrete,
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

    /// <summary>The editor's snap step in seconds (Godot's <c>step</c>; 1/30 s by default).</summary>
    [Export(Range = "0.001,1,0.001")]
    public float Step { get; set; } = 1f / 30;

    /// <summary>
    /// G1c: the <see cref="AnimationHash"/> of the keys <see cref="PoseBaker"/> wrote (empty for clips made by hand or
    /// imported). When the clip's current hash differs, a person edited it, and a re-bake needs to be told to replace it.
    /// </summary>
    [Export]
    public string BakedHash { get; set; } = "";

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
}

/// <summary>
/// A segment of keys an interpolated track samples at a time: keys <see cref="A"/> → <see cref="B"/> at fraction
/// <see cref="F"/> (after the key's transition), and for cubic interpolation the neighbours and their times relative to
/// <see cref="A"/>. On the loop-wrap segment B is the first key and the times run past the animation's end.
/// </summary>
internal struct KeySpan
{
    public int A, B, Pre, Post;
    public float F, ToT, PreT, PostT;

    /// <summary>No key before A (or after B): the cubic uses A mirrored through B (2A − B), so evenly spaced keys stay even.</summary>
    public bool MirrorPre, MirrorPost;
}

/// <summary>
/// A track whose keys interpolate (transform and value tracks): <see cref="Interpolation"/>, a per-key
/// <see cref="Transitions"/> ease (Godot's key transition) and <see cref="LoopWrap"/>.
/// </summary>
public abstract class InterpolatedTrack : AnimationTrack
{
    [Export]
    public AnimationInterpolation Interpolation { get; set; } = AnimationInterpolation.Linear;

    /// <summary>
    /// The ease of the segment after each key (<see cref="Mathf.Ease(float, float)"/>: 1 linear, below 1 ease out, above
    /// 1 ease in, negative in-out). Empty: every segment linear.
    /// </summary>
    [Export]
    public float[] Transitions { get; set; } = [];

    /// <summary>On a looping animation, the segment after the last key interpolates towards the first key.</summary>
    [Export]
    public bool LoopWrap { get; set; } = true;

    /// <summary>
    /// The segment at <paramref name="time"/> on an animation of <paramref name="length"/> seconds (wrapping past the
    /// last key when <paramref name="loop"/> and <see cref="LoopWrap"/>); false when the track has no keys.
    /// </summary>
    internal bool Locate(double time, ref int cursor, float length, bool loop, out KeySpan span)
    {
        span = default;
        var times = Times;
        var n = times.Length;
        if (n == 0)
            return false;
        var key = FindKey(times, time, ref cursor);
        var wrap = loop && LoopWrap && n > 1 && length > 0f;
        float toT, f;
        int a, b;
        if (key < 0 || key >= n - 1)
        {
            if (!wrap)
            {
                a = b = key < 0 ? 0 : n - 1;
                span = new KeySpan { A = a, B = b, Pre = a, Post = b };
                return true;
            }

            a = n - 1;
            b = 0;
            toT = length - times[n - 1] + times[0];
            var into = key < 0 ? time + length - times[n - 1] : time - times[n - 1];
            f = toT > 0f ? (float)Math.Clamp(into / toT, 0.0, 1.0) : 0f;
        }
        else
        {
            a = key;
            b = key + 1;
            toT = times[b] - times[a];
            f = toT > 0f ? (float)Math.Clamp((time - times[a]) / toT, 0.0, 1.0) : 0f;
        }

        if (Interpolation == AnimationInterpolation.Nearest)
        {
            span = new KeySpan { A = a, B = a, Pre = a, Post = a };
            return true;
        }

        var transitions = Transitions;
        if (a < transitions.Length && transitions[a] != 1f)
            f = Mathf.Ease(f, transitions[a]);
        span = new KeySpan { A = a, B = b, F = f, ToT = toT, Pre = a, Post = b, PreT = -toT, PostT = 2f * toT, MirrorPre = true, MirrorPost = true };
        if (Interpolation != AnimationInterpolation.Cubic)
            return true;

        // Neighbours for the cubic, through the loop when wrapping.
        if (a > 0 && !(wrap && b == 0 && a == n - 1 && n == 2))
        {
            span.Pre = a - 1;
            span.PreT = times[a - 1] - times[a];
            span.MirrorPre = false;
        }
        else if (wrap && n > 2)
        {
            span.Pre = n - 1;
            span.PreT = times[n - 1] - length - times[a];
            span.MirrorPre = false;
        }

        if (b + 1 < n && b != 0)
        {
            span.Post = b + 1;
            span.PostT = times[b + 1] - times[a];
            span.MirrorPost = false;
        }
        else if (wrap && n > 2)
        {
            var post = (b + 1) % n;
            span.Post = post;
            var postTime = times[post] + (post <= a ? length : 0f);
            span.PostT = postTime - times[a];
            span.MirrorPost = false;
        }

        return true;
    }

    internal static Vector3 Interpolate(Vector3[] values, in KeySpan span, AnimationInterpolation interpolation)
    {
        var a = values[Math.Min(span.A, values.Length - 1)];
        if (span.A == span.B || span.F <= 0f)
            return a;
        var b = values[Math.Min(span.B, values.Length - 1)];
        if (interpolation != AnimationInterpolation.Cubic)
            return Vector3.Lerp(a, b, span.F);
        var pre = span.MirrorPre ? 2f * a - b : values[Math.Min(span.Pre, values.Length - 1)];
        var post = span.MirrorPost ? 2f * b - a : values[Math.Min(span.Post, values.Length - 1)];
        return new Vector3(
            Mathf.CubicInterpolateInTime(a.X, b.X, pre.X, post.X, span.F, span.ToT, span.PreT, span.PostT),
            Mathf.CubicInterpolateInTime(a.Y, b.Y, pre.Y, post.Y, span.F, span.ToT, span.PreT, span.PostT),
            Mathf.CubicInterpolateInTime(a.Z, b.Z, pre.Z, post.Z, span.F, span.ToT, span.PreT, span.PostT));
    }
}

/// <summary>
/// The position of a <see cref="Node3D"/> or of a bone (Godot's <c>TYPE_POSITION_3D</c>): <see cref="AnimationTrack.Path"/>
/// names the node or the <see cref="Skeleton3D"/>, <see cref="Bone"/> the bone (empty: the node itself).
/// </summary>
public sealed class Position3DTrack : InterpolatedTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Vector3[] Values { get; set; } = [];

    /// <summary>The value at <paramref name="time"/> (no loop wrap).</summary>
    public Vector3 Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Vector3 Sample(double time, ref int cursor) => Sample(time, ref cursor, 0f, loop: false);

    internal Vector3 Sample(double time, ref int cursor, float length, bool loop) =>
        Values.Length > 0 && Locate(time, ref cursor, length, loop, out var span) ? Interpolate(Values, span, Interpolation) : Vector3.Zero;
}

/// <summary>The rotation of a <see cref="Node3D"/> or a bone (Godot's <c>TYPE_ROTATION_3D</c>); slerped (shortest arc).</summary>
public sealed class Rotation3DTrack : InterpolatedTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Quaternion[] Values { get; set; } = [];

    /// <summary>The value at <paramref name="time"/> (no loop wrap).</summary>
    public Quaternion Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Quaternion Sample(double time, ref int cursor) => Sample(time, ref cursor, 0f, loop: false);

    internal Quaternion Sample(double time, ref int cursor, float length, bool loop)
    {
        var values = Values;
        if (values.Length == 0 || !Locate(time, ref cursor, length, loop, out var span))
            return Quaternion.Identity;
        var a = values[Math.Min(span.A, values.Length - 1)];
        if (span.A == span.B || span.F <= 0f)
            return a;
        var b = values[Math.Min(span.B, values.Length - 1)];
        if (Interpolation != AnimationInterpolation.Cubic)
            return Quaternion.Slerp(a, b, span.F);
        // A missing neighbour mirrors: the turn from B to A applied again before A (after B likewise).
        var pre = span.MirrorPre ? a * Quaternion.Inverse(b) * a : values[Math.Min(span.Pre, values.Length - 1)];
        var post = span.MirrorPost ? b * Quaternion.Inverse(a) * b : values[Math.Min(span.Post, values.Length - 1)];
        return QuaternionCubic.InTime(a, b, pre, post, span.F, span.ToT, span.PreT, span.PostT);
    }
}

/// <summary>The scale of a <see cref="Node3D"/> or a bone (Godot's <c>TYPE_SCALE_3D</c>).</summary>
public sealed class Scale3DTrack : InterpolatedTrack
{
    [Export]
    public string Bone { get; set; } = "";

    [Export]
    public Vector3[] Values { get; set; } = [];

    /// <summary>The value at <paramref name="time"/> (no loop wrap).</summary>
    public Vector3 Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal Vector3 Sample(double time, ref int cursor) => Sample(time, ref cursor, 0f, loop: false);

    internal Vector3 Sample(double time, ref int cursor, float length, bool loop) =>
        Values.Length > 0 && Locate(time, ref cursor, length, loop, out var span) ? Interpolate(Values, span, Interpolation) : Vector3.One;
}

/// <summary>
/// Any animatable exported property of a node (G1.2): <see cref="Property"/> is its C# name, optionally with a component
/// (<c>Energy</c>, <c>Position</c>, <c>Position:y</c>, <c>Modulate:a</c>). Each key is <see cref="Components"/> floats in
/// <see cref="Values"/> (the property's value as its codec stores it; see <see cref="IAnimationValueOps{T}"/>).
/// Bools, integers and enums change only at keys.
/// </summary>
public sealed class ValueTrack : InterpolatedTrack
{
    [Export]
    public string Property { get; set; } = "";

    /// <summary>Floats per key.</summary>
    [Export(Range = "1,16,1")]
    public int Components { get; set; } = 1;

    /// <summary>The keys' values, <see cref="AnimationTrack.Times"/>.Length × <see cref="Components"/> floats.</summary>
    [Export]
    public float[] Values { get; set; } = [];

    [Export]
    public AnimationUpdateMode Update { get; set; }

    /// <summary>
    /// The value at <paramref name="time"/> into <paramref name="destination"/> (<see cref="Components"/> floats);
    /// <paramref name="discrete"/> holds each key's value until the next. False without keys.
    /// </summary>
    internal bool Sample(double time, ref int cursor, float length, bool loop, bool discrete, Span<float> destination)
    {
        var n = Components;
        var values = Values;
        if (n <= 0 || values.Length < n || !Locate(time, ref cursor, length, loop, out var span))
            return false;
        var keys = values.Length / n;
        var a = Math.Min(span.A, keys - 1) * n;
        if (discrete || Update == AnimationUpdateMode.Discrete || span.A == span.B || span.F <= 0f)
        {
            values.AsSpan(a, n).CopyTo(destination);
            return true;
        }

        var b = Math.Min(span.B, keys - 1) * n;
        if (Interpolation != AnimationInterpolation.Cubic)
        {
            for (var i = 0; i < n; i++)
                destination[i] = values[a + i] + (values[b + i] - values[a + i]) * span.F;
            return true;
        }

        var pre = Math.Min(span.Pre, keys - 1) * n;
        var post = Math.Min(span.Post, keys - 1) * n;
        for (var i = 0; i < n; i++)
        {
            var va = values[a + i];
            var vb = values[b + i];
            var vPre = span.MirrorPre ? 2f * va - vb : values[pre + i];
            var vPost = span.MirrorPost ? 2f * vb - va : values[post + i];
            destination[i] = Mathf.CubicInterpolateInTime(va, vb, vPre, vPost, span.F, span.ToT, span.PreT, span.PostT);
        }
        return true;
    }

    /// <summary>The value at <paramref name="time"/> (tests and tools; allocates the result).</summary>
    public float[] Sample(double time)
    {
        var cursor = -1;
        var result = new float[Math.Max(1, Components)];
        Sample(time, ref cursor, 0f, loop: false, discrete: false, result);
        return result;
    }

    /// <summary>Inserts (or replaces at the same time) a key of <see cref="Components"/> floats, keeping times ascending.</summary>
    public int SetKey(float time, ReadOnlySpan<float> value)
    {
        var n = Components;
        var times = Times;
        var index = 0;
        while (index < times.Length && times[index] < time)
            index++;
        if (index < times.Length && MathF.Abs(times[index] - time) < 1e-5f)
        {
            var copy = (float[])Values.Clone();
            value[..n].CopyTo(copy.AsSpan(index * n, n));
            Values = copy;
            return index;
        }

        Times = [.. times[..index], time, .. times[index..]];
        Values = [.. Values.AsSpan(0, index * n), .. value[..n], .. Values.AsSpan(index * n)];
        if (Transitions.Length > 0)
            Transitions = [.. Transitions[..Math.Min(index, Transitions.Length)], 1f, .. Transitions[Math.Min(index, Transitions.Length)..]];
        return index;
    }
}

/// <summary>
/// One float with per-key handles (Godot's <c>TYPE_BEZIER</c>): each key has an in and an out handle as (time offset,
/// value offset), and the curve between two keys is the cubic Bézier through them, solved for time. For hand-shaped
/// easing of one property (<c>Energy</c>, <c>Position:y</c>).
/// </summary>
public sealed class BezierTrack : AnimationTrack
{
    [Export]
    public string Property { get; set; } = "";

    [Export]
    public float[] Values { get; set; } = [];

    [Export]
    public Vector2[] InHandles { get; set; } = [];

    [Export]
    public Vector2[] OutHandles { get; set; } = [];

    /// <summary>The value at <paramref name="time"/>.</summary>
    public float Sample(double time)
    {
        var cursor = -1;
        return Sample(time, ref cursor);
    }

    internal float Sample(double time, ref int cursor)
    {
        var times = Times;
        var values = Values;
        var n = Math.Min(times.Length, values.Length);
        if (n == 0)
            return 0f;
        var key = FindKey(times, time, ref cursor);
        if (key < 0)
            return values[0];
        if (key >= n - 1)
            return values[n - 1];
        var duration = times[key + 1] - times[key];
        if (duration <= 0f)
            return values[key];
        var outHandle = key < OutHandles.Length ? OutHandles[key] : Vector2.Zero;
        var inHandle = key + 1 < InHandles.Length ? InHandles[key + 1] : Vector2.Zero;
        var target = (float)(time - times[key]);
        // Solve the time curve for t by bisection (it is monotonic for handles inside the segment), then the value.
        float lo = 0f, hi = 1f, t = 0.5f;
        for (var i = 0; i < 20; i++)
        {
            t = (lo + hi) * 0.5f;
            var at = Mathf.BezierInterpolate(0f, outHandle.X, duration + inHandle.X, duration, t);
            if (at < target)
                lo = t;
            else
                hi = t;
        }

        var v0 = values[key];
        var v1 = values[key + 1];
        return Mathf.BezierInterpolate(v0, v0 + outHandle.Y, v1 + inHandle.Y, v1, t);
    }
}

/// <summary>
/// Calls <see cref="AnimationMethodAttribute"/> methods of the node at <see cref="AnimationTrack.Path"/> when playback
/// passes a key (G1.4): <see cref="Methods"/> names the method per key, <see cref="Arguments"/> its arguments as a JSON
/// array (<c>[]</c>, <c>[2, "open"]</c>), parsed once when the player binds the animation.
/// </summary>
public sealed class MethodTrack : AnimationTrack
{
    [Export]
    public string[] Methods { get; set; } = [];

    [Export]
    public string[] Arguments { get; set; } = [];
}

/// <summary>
/// Plays sounds on the <see cref="AudioPlayer"/>, <see cref="AudioPlayer2D"/> or <see cref="AudioPlayer3D"/> at
/// <see cref="AnimationTrack.Path"/> (G1.4): at each key it sets the stream and plays from the key's start offset.
/// </summary>
public sealed class AudioTrack : AnimationTrack
{
    [Export]
    public AudioStream?[] Streams { get; set; } = [];

    /// <summary>Seconds into each key's stream to start from.</summary>
    [Export]
    public float[] StartOffsets { get; set; } = [];
}

/// <summary>Godot's <c>Quaternion.spherical_cubic_interpolate_in_time</c>: cubic through rotations in log space.</summary>
internal static class QuaternionCubic
{
    public static Quaternion InTime(Quaternion from, Quaternion to, Quaternion pre, Quaternion post, float weight, float toT, float preT, float postT)
    {
        from = Quaternion.Normalize(from);
        to = Quaternion.Normalize(to);
        pre = Quaternion.Normalize(pre);
        post = Quaternion.Normalize(post);
        // Flip to the shortest paths.
        if (Quaternion.Dot(from, pre) < 0f)
            pre = Quaternion.Negate(pre);
        var flip2 = Quaternion.Dot(from, to) < 0f;
        if (flip2)
            to = Quaternion.Negate(to);
        if (flip2 ? Quaternion.Dot(to, post) <= 0f : Quaternion.Dot(to, post) < 0f)
            post = Quaternion.Negate(post);

        var q1 = from * Exp(Cubic(Vector3.Zero, Log(Quaternion.Inverse(from) * to), Log(Quaternion.Inverse(from) * pre),
            Log(Quaternion.Inverse(from) * post), weight, toT, preT, postT));
        var q2 = to * Exp(Cubic(Log(Quaternion.Inverse(to) * from), Vector3.Zero, Log(Quaternion.Inverse(to) * pre),
            Log(Quaternion.Inverse(to) * post), weight, toT, preT, postT));
        return Quaternion.Slerp(q1, q2, weight);
    }

    private static Vector3 Cubic(Vector3 from, Vector3 to, Vector3 pre, Vector3 post, float w, float toT, float preT, float postT) => new(
        Mathf.CubicInterpolateInTime(from.X, to.X, pre.X, post.X, w, toT, preT, postT),
        Mathf.CubicInterpolateInTime(from.Y, to.Y, pre.Y, post.Y, w, toT, preT, postT),
        Mathf.CubicInterpolateInTime(from.Z, to.Z, pre.Z, post.Z, w, toT, preT, postT));

    // Axis × angle of a unit quaternion.
    private static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var w = Math.Clamp(q.W, -1f, 1f);
        var angle = 2f * MathF.Acos(w);
        var sin = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
        var axis = sin > 1e-6f ? new Vector3(q.X, q.Y, q.Z) / sin : new Vector3(q.X, q.Y, q.Z);
        return axis * angle;
    }

    private static Quaternion Exp(Vector3 v)
    {
        var theta = v.Length();
        return theta < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(v / theta, theta);
    }
}

/// <summary>
/// Named moments of an animation (ADR 0207): when playback passes a key, the player raises
/// <see cref="AnimationPlayer.AnimationEvent"/> with its name (a Spine-style event: a footstep, the frame a swing lands).
/// <see cref="AnimationTrack.Path"/> is unused. Imported from glTF animation extras or the model's <c>.mfmeta</c> clips.
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
