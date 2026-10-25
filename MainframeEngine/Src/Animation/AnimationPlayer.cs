using System.Numerics;

namespace MainframeEngine;

/// <summary>When an <see cref="AnimationPlayer"/> advances (Godot's <c>callback_mode_process</c>).</summary>
public enum AnimationCallbackMode : byte
{
    /// <summary>In <see cref="Node.OnPhysicsProcess"/>, on the fixed step (animated physics bodies).</summary>
    Physics,

    /// <summary>In <see cref="Node.OnProcess"/>, every frame.</summary>
    Idle,

    /// <summary>Only through <see cref="AnimationPlayer.Advance"/>.</summary>
    Manual,
}

/// <summary>
/// Plays <see cref="Animation"/>s from its <see cref="Libraries"/> on the nodes under <see cref="RootNode"/> (Godot's
/// <c>AnimationPlayer</c>, ADR 0207): <see cref="Play"/>, <see cref="Queue"/>, <see cref="Stop"/>, <see cref="Seek"/>,
/// per-clip loop modes, <see cref="SpeedScale"/>, a crossfade when the animation changes, and signals for start, finish,
/// change and named events (<see cref="EventTrack"/>).
/// </summary>
/// <remarks>
/// <para>Tracks bind once per animation (node and bone lookups, by path and name) when it first plays; each frame then finds
/// the key segment from a cursor, interpolates and writes <see cref="Node3D"/> transforms or <see cref="Skeleton3D"/> bone
/// poses through typed calls. Steady playback, crossfades and events allocate nothing.</para>
/// <para>Playback is deterministic: the pose and the events depend only on the clip and the time, so peers that start the
/// same clips from replicated state agree without replicating the player. It runs on dedicated servers too
/// (<see cref="HeadlessHost"/>): nothing here needs a renderer.</para>
/// <para>Crossfades: during a blend both animations play; values both animate mix (vectors lerp, rotations slerp); a channel
/// only the new one animates is set as is; one only the old one animates blends toward the bone's rest (or, for a node,
/// the value it had when the blend started). A <see cref="Play"/> during a crossfade blends from the pose as it is at that
/// moment. Only the incoming animation raises events.</para>
/// </remarks>
[EditorIcon("player-play")]
public class AnimationPlayer : Node
{
    private const byte HasFrom = 1, HasTo = 2;
    private const byte KindPosition = 0, KindRotation = 1, KindScale = 2;

    private AnimationCallbackMode _callbackMode = AnimationCallbackMode.Idle;
    private Dictionary<Animation, BoundAnimation>? _bound;
    private Slot[] _slots = [];
    private int _slotCount;
    private readonly Playback _first = new();
    private readonly Playback _second = new();
    private Playback? _current;
    private Playback? _from;
    private bool _fromSnapshot; // the outgoing pose is a captured mix (a Play during a crossfade), not an animation
    private double _blendTime;
    private double _blendElapsed;
    private bool _playing;
    private string _assigned = "";
    private Queue<string>? _queue;
    private (string Animation, string Event)[] _pending = [];
    private int _pendingCount;

    public AnimationPlayer()
    {
        UpdateProcessing();
    }

    /// <summary>The animations, by library (a library's <see cref="Resource.ResourceName"/> prefixes its animations' names).</summary>
    [Export]
    public List<AnimationLibrary> Libraries { get; set; } = [];

    /// <summary>The node track paths are relative to (Godot's <c>root_node</c>; default the parent).</summary>
    [Export(NodeType = typeof(Node))]
    public NodePath RootNode
    {
        get;
        set
        {
            field = value;
            InvalidateBindings();
        }
    } = "..";

    /// <summary>Played when the node is ready (outside the editor).</summary>
    [Export]
    public string Autoplay { get; set; } = "";

    [Export]
    public AnimationCallbackMode CallbackModeProcess
    {
        get => _callbackMode;
        set
        {
            _callbackMode = value;
            UpdateProcessing();
        }
    }

    /// <summary>The crossfade, in seconds, when <see cref="Play"/> switches animation without a custom blend.</summary>
    [Export(Range = "0,10,0.01")]
    public float PlaybackDefaultBlendTime { get; set; }

    /// <summary>Speed multiplier of every animation (negative plays backwards).</summary>
    [Export(Range = "-64,64,0.01")]
    public float SpeedScale { get; set; } = 1f;

    /// <summary>An inactive player does not advance or apply anything.</summary>
    [Export]
    public bool Active { get; set; } = true;

    /// <summary>The playing animation's name, or <c>""</c> when stopped, paused or finished (Godot's <c>current_animation</c>).</summary>
    public string CurrentAnimation => _playing && _current?.Bound is { } bound ? bound.Name : "";

    /// <summary>The animation last played or seeked, playing or not (Godot's <c>assigned_animation</c>).</summary>
    public string AssignedAnimation => _assigned;

    /// <summary>The playhead of the assigned animation in seconds.</summary>
    public double CurrentAnimationPosition => _current?.Position ?? 0;

    /// <summary>The assigned animation's length in seconds (fit a clip to a duration with <c>customSpeed</c> or <see cref="SpeedScale"/>).</summary>
    public double CurrentAnimationLength => _current?.Bound?.Animation.Length ?? 0;

    /// <summary>True while a crossfade from the previous animation runs.</summary>
    public bool IsBlending => _from is not null;

    /// <summary>The current speed: <see cref="SpeedScale"/> × the custom speed of <see cref="Play"/> (0 when not playing).</summary>
    public float GetPlayingSpeed() => _playing && _current is { } current ? SpeedScale * current.Speed : 0f;

    public bool IsPlaying() => _playing;

    /// <summary>Raised when an animation starts (from <see cref="Play"/> or the queue).</summary>
    [Signal]
    public event Action<string>? AnimationStarted;

    /// <summary>Raised when a non-looping animation reaches its end (Godot's; looping animations never finish).</summary>
    [Signal]
    public event Action<string>? AnimationFinished;

    /// <summary>Raised when a queued animation takes over from the one that finished: (old, new).</summary>
    [Signal]
    public event Action<string, string>? AnimationChanged;

    /// <summary>
    /// Raised when playback passes an <see cref="EventTrack"/> key: (animation, event name), at the key's time, in play
    /// direction, across loops. <see cref="Seek"/> does not raise them.
    /// </summary>
    [Signal]
    public event Action<string, string>? AnimationEvent;

    /// <summary>
    /// Plays <paramref name="name"/> (empty: the assigned animation, else <see cref="Autoplay"/>) at
    /// <paramref name="customSpeed"/>, crossfading from the current one over <paramref name="customBlend"/> seconds (negative:
    /// <see cref="PlaybackDefaultBlendTime"/>). Playing the animation that is already playing (or paused) continues it.
    /// </summary>
    public void Play(string name = "", double customBlend = -1, float customSpeed = 1f, bool fromEnd = false)
    {
        if (string.IsNullOrEmpty(name))
            name = _assigned.Length > 0 ? _assigned : Autoplay;
        if (string.IsNullOrEmpty(name))
        {
            Log.Warning($"[AnimationPlayer] '{Name}': Play() without a name, an assigned animation or an autoplay.");
            return;
        }

        if (FindAnimation(name) is not { } animation)
        {
            Log.Error($"[AnimationPlayer] '{Name}': no animation '{name}'.");
            return;
        }

        var bound = Bind(animation, name);
        if (_current is { } current && ReferenceEquals(current.Bound, bound) && !current.Finished)
        {
            // Godot: the same animation continues (resumes when paused).
            current.Speed = fromEnd ? -MathF.Abs(customSpeed) : customSpeed;
            _playing = true;
            return;
        }

        var blend = customBlend >= 0 ? customBlend : PlaybackDefaultBlendTime;
        if (blend > 0 && _current?.Bound is not null)
        {
            // A Play during a crossfade blends from the current mix: the pose as it is now, held still.
            _fromSnapshot = _from is not null;
            if (_fromSnapshot)
                CaptureSnapshot();
            _from = _current;
            _current = ReferenceEquals(_current, _first) ? _second : _first;
            _blendTime = blend;
            _blendElapsed = 0;
            CaptureBases();
        }
        else
        {
            _from = null;
            _fromSnapshot = false;
            _current ??= _first;
        }

        Start(_current, bound, customSpeed, fromEnd);
        if (_from is null)
            Evaluate(); // the first frame already shows the pose (a crossfade starts from the old one)
        AnimationStarted?.Invoke(name);
    }

    /// <summary>Plays <paramref name="name"/> backwards from its end.</summary>
    public void PlayBackwards(string name = "", double customBlend = -1) => Play(name, customBlend, -1f, fromEnd: true);

    /// <summary>Stops advancing; <see cref="Play"/> without a name resumes.</summary>
    public void Pause() => _playing = false;

    /// <summary>
    /// Stops: the queue is cleared and the playhead goes back to the start; unless <paramref name="keepState"/>, the pose at
    /// the start is applied.
    /// </summary>
    public void Stop(bool keepState = false)
    {
        _playing = false;
        _from = null;
        _fromSnapshot = false;
        _queue?.Clear();
        if (_current is not { Bound: not null } current)
            return;
        current.Position = 0;
        current.Finished = false;
        current.FireStart = true;
        if (!keepState)
            Evaluate();
    }

    /// <summary>
    /// Moves the playhead of the assigned animation to <paramref name="seconds"/> (clamped to its length); with
    /// <paramref name="update"/> the pose is applied now. Events between are not raised.
    /// </summary>
    public void Seek(double seconds, bool update = false)
    {
        if (_current?.Bound is null)
        {
            if (_assigned.Length == 0 || FindAnimation(_assigned) is not { } animation)
                return;
            _current ??= _first;
            Start(_current, Bind(animation, _assigned), 1f, fromEnd: false);
        }

        var playback = _current!;
        var length = (double)playback.Bound!.Animation.Length;
        playback.Position = Math.Clamp(seconds, 0, Math.Max(length, 0));
        playback.FireStart = false;
        playback.Finished = false;
        if (update)
            Evaluate();
    }

    /// <summary>Plays <paramref name="name"/> after the current (non-looping) animation finishes.</summary>
    public void Queue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        (_queue ??= new Queue<string>()).Enqueue(name);
    }

    public void ClearQueue() => _queue?.Clear();

    /// <summary>The queued animation names.</summary>
    public IReadOnlyCollection<string> GetQueue() => _queue is null ? [] : _queue.ToArray();

    /// <summary>Advances by <paramref name="delta"/> seconds now (<see cref="AnimationCallbackMode.Manual"/>, tools, tests).</summary>
    public void Advance(double delta) => Process(delta);

    /// <summary>The animation named <paramref name="name"/> (<c>"library/animation"</c> outside the default library), or null.</summary>
    public Animation? GetAnimation(string name) => FindAnimation(name);

    public bool HasAnimation(string name) => FindAnimation(name) is not null;

    /// <summary>Appends every animation's name (library-prefixed) to <paramref name="into"/>.</summary>
    public void GetAnimationNames(List<string> into)
    {
        foreach (var library in Libraries)
        {
            if (library is null)
                continue;
            foreach (var animation in library.Animations)
                if (animation is not null)
                    into.Add(library.ResourceName.Length == 0 ? animation.ResourceName : $"{library.ResourceName}/{animation.ResourceName}");
        }
    }

    /// <summary>Adds (or replaces) a library under <paramref name="name"/> (<c>""</c>: the default library).</summary>
    public void AddAnimationLibrary(string name, AnimationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        library.ResourceName = name ?? "";
        var index = Libraries.FindIndex(l => l?.ResourceName == library.ResourceName);
        if (index >= 0)
            Libraries[index] = library;
        else
            Libraries.Add(library);
    }

    /// <summary>The library named <paramref name="name"/> (<c>""</c>: the default), or null.</summary>
    public AnimationLibrary? GetAnimationLibrary(string name) => Libraries.Find(l => l?.ResourceName == (name ?? ""));

    protected override void OnReady()
    {
        base.OnReady();
        if (Autoplay.Length > 0 && Tree is { EditMode: false })
            Play(Autoplay);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_callbackMode == AnimationCallbackMode.Idle)
            Process(gameTime.DeltaTime);
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_callbackMode == AnimationCallbackMode.Physics)
            Process(delta);
    }

    private void UpdateProcessing()
    {
        SetProcess(_callbackMode == AnimationCallbackMode.Idle);
        SetPhysicsProcess(_callbackMode == AnimationCallbackMode.Physics);
    }

    // ── Playback ───────────────────────────────────────────────────────────────

    private void Start(Playback playback, BoundAnimation bound, float speed, bool fromEnd)
    {
        playback.Bound = bound;
        playback.Speed = fromEnd ? -MathF.Abs(speed) : speed;
        playback.Position = fromEnd ? bound.Animation.Length : 0;
        playback.FireStart = true;
        playback.Finished = false;
        _assigned = bound.Name;
        _playing = true;
    }

    private void Process(double delta)
    {
        if (!_playing || !Active || _current?.Bound is null)
            return;

        var blendDone = false;
        if (_from is { } from)
        {
            _blendElapsed += Math.Abs(delta * SpeedScale);
            blendDone = _blendElapsed >= _blendTime || from.Bound is null;
            if (!blendDone && !_fromSnapshot)
                AdvancePlayback(from, delta, events: false);
        }

        var current = _current;
        var finished = AdvancePlayback(current, delta, events: true);
        Evaluate(); // a finished blend applies once more at full weight, then the old animation is dropped
        if (blendDone)
        {
            _from = null;
            _fromSnapshot = false;
        }

        string? finishedName = null, nextName = null;
        if (finished)
        {
            current.Finished = true;
            _playing = false;
            finishedName = current.Bound!.Name;
            if (_queue is { Count: > 0 } queue)
            {
                nextName = queue.Dequeue();
                Play(nextName);
            }
        }

        RaisePendingEvents();
        if (finishedName is not null)
        {
            AnimationFinished?.Invoke(finishedName);
            if (nextName is not null && _playing)
                AnimationChanged?.Invoke(finishedName, nextName);
        }
    }

    // Moves the playhead; collects the events it passes. True when a non-looping animation reached its end.
    private bool AdvancePlayback(Playback playback, double delta, bool events)
    {
        var animation = playback.Bound!.Animation;
        var length = Math.Max((double)animation.Length, 0);
        var step = delta * SpeedScale * playback.Speed;
        var previous = playback.Position;
        var next = previous + step;
        var inclusive = playback.FireStart;
        playback.FireStart = false;
        var bound = events ? playback.Bound : null;
        var finished = false;
        switch (animation.LoopMode)
        {
            case AnimationLoopMode.None:
                if (step > 0 && next >= length)
                {
                    next = length;
                    finished = true;
                }
                else if (step < 0 && next <= 0)
                {
                    next = 0;
                    finished = true;
                }

                Collect(bound, previous, next, inclusive, true);
                break;
            case AnimationLoopMode.Linear when length > 0:
                if (step >= 0)
                {
                    while (next >= length)
                    {
                        Collect(bound, previous, length, inclusive, false);
                        previous = 0;
                        inclusive = true;
                        next -= length;
                    }
                }
                else
                {
                    while (next < 0)
                    {
                        Collect(bound, previous, 0, inclusive, true);
                        previous = length;
                        inclusive = false;
                        next += length;
                    }
                }

                Collect(bound, previous, next, inclusive, true);
                break;
            case AnimationLoopMode.PingPong when length > 0:
                for (var bounces = 0; (next > length || next < 0) && bounces < 64; bounces++)
                {
                    var edge = next > length ? length : 0;
                    Collect(bound, previous, edge, inclusive, true);
                    next = 2 * edge - next;
                    previous = edge;
                    inclusive = false;
                    playback.Speed = -playback.Speed;
                }

                Collect(bound, previous, next, inclusive, true);
                break;
            default:
                next = 0;
                break;
        }

        playback.Position = next;
        return finished;
    }

    // Queues the event keys between from and to (in play direction) for raising after the pose is applied.
    private void Collect(BoundAnimation? bound, double from, double to, bool includeFrom, bool includeTo)
    {
        if (bound is null || bound.Events.Length == 0 || (from == to && !includeFrom))
            return;
        var forward = to >= from;
        foreach (var track in bound.Events)
        {
            var times = track.Times;
            var names = track.Names;
            var count = Math.Min(times.Length, names.Length);
            if (forward)
            {
                for (var i = 0; i < count; i++)
                {
                    var t = times[i];
                    if ((includeFrom ? t >= from : t > from) && (includeTo ? t <= to : t < to))
                        AddPending(bound.Name, names[i]);
                }
            }
            else
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    var t = times[i];
                    if ((includeFrom ? t <= from : t < from) && (includeTo ? t >= to : t > to))
                        AddPending(bound.Name, names[i]);
                }
            }
        }
    }

    private void AddPending(string animation, string name)
    {
        if (_pendingCount == _pending.Length)
            Array.Resize(ref _pending, Math.Max(8, _pending.Length * 2));
        _pending[_pendingCount++] = (animation, name);
    }

    private void RaisePendingEvents()
    {
        var count = _pendingCount;
        _pendingCount = 0;
        for (var i = 0; i < count; i++)
        {
            var (animation, name) = _pending[i];
            _pending[i] = default;
            AnimationEvent?.Invoke(animation, name);
        }
    }

    // ── Evaluation ─────────────────────────────────────────────────────────────

    // Samples the playing animation(s) into the slots and writes the mixed values to the targets.
    private void Evaluate()
    {
        if (_current?.Bound is not { } current)
            return;
        var slots = _slots.AsSpan(0, _slotCount);
        for (var i = 0; i < slots.Length; i++)
            slots[i].Flags = 0;

        if (_fromSnapshot && _from is not null)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                ref var slot = ref slots[i];
                if (!slot.HasSnapshot)
                    continue;
                slot.FromV = slot.SnapshotV;
                slot.FromQ = slot.SnapshotQ;
                slot.Flags |= HasFrom;
            }
        }
        else if (_from is { Bound: { } fromBound } from)
        {
            Sample(fromBound, from.Position, to: false);
        }
        Sample(current, _current.Position, to: true);

        var weight = _from is null ? 1f : (float)Math.Clamp(_blendElapsed / _blendTime, 0, 1);
        var stale = false;
        for (var i = 0; i < slots.Length; i++)
        {
            ref var slot = ref slots[i];
            if (slot.Flags == 0)
                continue;
            if (_from is null && (slot.Flags & HasTo) == 0)
                continue;
            if (slot.Target is not { } target || target.IsFreed)
            {
                stale = true;
                continue;
            }

            var hasTo = (slot.Flags & HasTo) != 0;
            var hasFrom = (slot.Flags & HasFrom) != 0;
            if (slot.Kind == KindRotation)
            {
                var q = hasTo ? hasFrom ? Quaternion.Slerp(slot.FromQ, slot.ToQ, weight) : slot.ToQ : Quaternion.Slerp(slot.FromQ, slot.BaseQ, weight);
                if (slot.Bone >= 0)
                {
                    if (slot.Bone < ((Skeleton3D)target).GetBoneCount())
                        ((Skeleton3D)target).SetBonePoseRotation(slot.Bone, q);
                }
                else
                {
                    target.Rotation = q;
                }
            }
            else
            {
                var v = hasTo ? hasFrom ? Vector3.Lerp(slot.FromV, slot.ToV, weight) : slot.ToV : Vector3.Lerp(slot.FromV, slot.BaseV, weight);
                if (slot.Bone >= 0)
                {
                    var skeleton = (Skeleton3D)target;
                    if (slot.Bone >= skeleton.GetBoneCount())
                        continue;
                    if (slot.Kind == KindPosition)
                        skeleton.SetBonePosePosition(slot.Bone, v);
                    else
                        skeleton.SetBonePoseScale(slot.Bone, v);
                }
                else if (slot.Kind == KindPosition)
                {
                    target.Position = v;
                }
                else
                {
                    target.Scale = v;
                }
            }
        }

        if (stale)
            InvalidateBindings(); // a target was freed: bind again on the next Play
    }

    private void Sample(BoundAnimation bound, double time, bool to)
    {
        var tracks = bound.Tracks;
        var flag = to ? HasTo : HasFrom;
        for (var i = 0; i < tracks.Length; i++)
        {
            ref var track = ref tracks[i];
            if (!track.Track.Enabled)
                continue;
            ref var slot = ref _slots[track.Slot];
            switch (track.Track)
            {
                case Position3DTrack position:
                    if (to)
                        slot.ToV = position.Sample(time, ref track.Cursor);
                    else
                        slot.FromV = position.Sample(time, ref track.Cursor);
                    break;
                case Rotation3DTrack rotation:
                    if (to)
                        slot.ToQ = rotation.Sample(time, ref track.Cursor);
                    else
                        slot.FromQ = rotation.Sample(time, ref track.Cursor);
                    break;
                case Scale3DTrack scale:
                    if (to)
                        slot.ToV = scale.Sample(time, ref track.Cursor);
                    else
                        slot.FromV = scale.Sample(time, ref track.Cursor);
                    break;
            }

            slot.Flags |= flag;
        }
    }

    // The values channels blend toward when only the outgoing animation animates them: a bone's rest, a node's current value.
    private void CaptureBases()
    {
        for (var i = 0; i < _slotCount; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.Target is not { IsFreed: false } target)
                continue;
            if (slot.Bone >= 0)
            {
                var skeleton = (Skeleton3D)target;
                if (slot.Bone >= skeleton.GetBoneCount())
                    continue;
                skeleton.GetBoneRest(slot.Bone).Decompose(out var position, out var rotation, out var scale);
                slot.BaseV = slot.Kind == KindScale ? scale : position;
                slot.BaseQ = rotation;
            }
            else
            {
                slot.BaseV = slot.Kind == KindScale ? target.Scale : target.Position;
                slot.BaseQ = target.Rotation;
            }
        }
    }

    // The pose every channel shows now (read back from the targets), the outgoing side of a crossfade started during one.
    private void CaptureSnapshot()
    {
        for (var i = 0; i < _slotCount; i++)
        {
            ref var slot = ref _slots[i];
            slot.HasSnapshot = false;
            if (slot.Target is not { IsFreed: false } target)
                continue;
            if (slot.Bone >= 0)
            {
                var skeleton = (Skeleton3D)target;
                if (slot.Bone >= skeleton.GetBoneCount())
                    continue;
                slot.SnapshotV = slot.Kind switch
                {
                    KindPosition => skeleton.GetBonePosePosition(slot.Bone),
                    KindScale => skeleton.GetBonePoseScale(slot.Bone),
                    _ => default,
                };
                slot.SnapshotQ = skeleton.GetBonePoseRotation(slot.Bone);
            }
            else
            {
                slot.SnapshotV = slot.Kind == KindScale ? target.Scale : target.Position;
                slot.SnapshotQ = target.Rotation;
            }

            slot.HasSnapshot = true;
        }
    }

    // ── Binding ────────────────────────────────────────────────────────────────

    private Animation? FindAnimation(ReadOnlySpan<char> name)
    {
        var libraries = Libraries;
        for (var i = 0; i < libraries.Count; i++)
        {
            if (libraries[i] is not { } library)
                continue;
            var prefix = library.ResourceName;
            if (prefix.Length == 0)
            {
                if (library.Get(name) is { } animation)
                    return animation;
            }
            else if (name.Length > prefix.Length + 1 && name.StartsWith(prefix) && name[prefix.Length] == '/' &&
                     library.Get(name[(prefix.Length + 1)..]) is { } prefixed)
            {
                return prefixed;
            }
        }

        return null;
    }

    private void InvalidateBindings()
    {
        if (_bound is null)
            return;
        foreach (var bound in _bound.Values)
            bound.Version = -1;
    }

    // The animation's tracks resolved against the nodes under RootNode (cached; rebuilt when the animation changed or a
    // target was freed).
    private BoundAnimation Bind(Animation animation, string name)
    {
        _bound ??= new Dictionary<Animation, BoundAnimation>(ReferenceEqualityComparer.Instance);
        if (!_bound.TryGetValue(animation, out var bound))
            _bound[animation] = bound = new BoundAnimation(animation);
        if (bound.Version == animation.Version)
            return bound;

        bound.Version = animation.Version;
        bound.Name = name;
        var root = RootNode.IsEmpty ? this : GetNodeOrNull(RootNode);
        var tracks = new List<BoundTrack>(animation.Tracks.Count);
        var events = new List<EventTrack>();
        var missing = 0;
        foreach (var track in animation.Tracks)
        {
            if (track is EventTrack eventTrack)
            {
                events.Add(eventTrack);
                continue;
            }

            var (kind, bone) = track switch
            {
                Position3DTrack p => (KindPosition, p.Bone),
                Rotation3DTrack r => (KindRotation, r.Bone),
                Scale3DTrack s => (KindScale, s.Bone),
                _ => ((byte)255, ""),
            };
            if (kind == 255)
                continue;
            var target = root?.GetNodeOrNull(track.Path) as Node3D;
            var boneIndex = -1;
            if (target is not null && bone.Length > 0)
                boneIndex = target is Skeleton3D skeleton ? skeleton.FindBone(bone) : -1;
            if (target is null || (bone.Length > 0 && boneIndex < 0))
            {
                missing++;
                continue;
            }

            tracks.Add(new BoundTrack { Track = track, Slot = SlotFor(target, boneIndex, kind), Cursor = -1 });
        }

        if (missing > 0)
            Log.Warning($"[AnimationPlayer] '{Name}': {missing} track(s) of '{name}' have no target under '{RootNode}'; they are skipped.");
        bound.Tracks = [.. tracks];
        bound.Events = [.. events];
        return bound;
    }

    private int SlotFor(Node3D target, int bone, byte kind)
    {
        for (var i = 0; i < _slotCount; i++)
            if (ReferenceEquals(_slots[i].Target, target) && _slots[i].Bone == bone && _slots[i].Kind == kind)
                return i;
        if (_slotCount == _slots.Length)
            Array.Resize(ref _slots, Math.Max(16, _slots.Length * 2));
        _slots[_slotCount] = new Slot { Target = target, Bone = bone, Kind = kind, BaseQ = Quaternion.Identity, BaseV = kind == KindScale ? Vector3.One : Vector3.Zero };
        return _slotCount++;
    }

    private sealed class Playback
    {
        public BoundAnimation? Bound;
        public double Position;
        public float Speed = 1f;

        /// <summary>The next advance raises events at the start position too (the first frame of a play).</summary>
        public bool FireStart;

        /// <summary>A non-looping animation reached its end (playing it again restarts it).</summary>
        public bool Finished;
    }

    private sealed class BoundAnimation(Animation animation)
    {
        public readonly Animation Animation = animation;
        public string Name = animation.ResourceName;
        public int Version = -1;
        public BoundTrack[] Tracks = [];
        public EventTrack[] Events = [];
    }

    private struct BoundTrack
    {
        public AnimationTrack Track;
        public int Slot;
        public int Cursor;
    }

    // One animated channel (a node's or a bone's position, rotation or scale) shared by every animation that animates it.
    private struct Slot
    {
        public Node3D? Target;
        public int Bone;
        public byte Kind;
        public byte Flags;
        public Vector3 FromV, ToV, BaseV, SnapshotV;
        public Quaternion FromQ, ToQ, BaseQ, SnapshotQ;
        public bool HasSnapshot;
    }
}
