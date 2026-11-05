using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>A key of the edited animation: its track index and key index.</summary>
public readonly record struct AnimationKeyRef(int Track, int Key);

/// <summary>
/// The Animation panel's model (G1.6, docs/design/animation.md#editor): the edited <see cref="AnimationPlayer"/> (the
/// selection's, or pinned), its animation, the playhead and selected keys, and every edit as an undo step on the scene
/// tab (<see cref="AnimationEditAction"/>, <see cref="AnimationLibraryAction"/>). Preview poses the scene through the
/// player (<see cref="AnimationPlayer.Seek"/>; method and audio keys do not fire) after capturing what the animation
/// changes, and puts it back when the preview ends or the scene is saved, so a saved scene never holds a preview frame.
/// </summary>
public sealed class AnimationEditor
{
    public const float RulerHeight = 24f;
    public const float RowHeight = 22f;

    private readonly EditorWorkspace? _workspace;
    private AnimationPlayer? _testPlayer;
    private UndoRedo? _testHistory;
    private AnimationPlayer? _pinned;
    private AnimationPlayer? _previewPlayer;
    private string _animation = "";
    private readonly List<Restore> _captured = [];
    private AnimationSnapshot? _gestureBefore;
    private Animation? _gestureAnimation;
    private string _gestureName = "";

    public AnimationEditor(EditorWorkspace? workspace)
    {
        _workspace = workspace;
    }

    /// <summary>Changes whenever the panel's state changes (the UI rebuilds on it).</summary>
    public int Version { get; private set; }

    /// <summary>The timeline's horizontal view (seconds).</summary>
    public TimelineZoom View { get; } = new() { PixelsPerUnit = 160, MinPixelsPerUnit = 4, MaxPixelsPerUnit = 4000, Inset = 10 };

    /// <summary>The playhead in seconds.</summary>
    public double Playhead { get; private set; }

    /// <summary>True while the preview plays.</summary>
    public bool Playing { get; private set; }

    /// <summary>Key moves and the playhead snap to the animation's <see cref="Animation.Step"/>.</summary>
    public bool Snap { get; set; } = true;

    /// <summary>The ruler counts frames (1 / step) instead of seconds.</summary>
    public bool ShowFrames { get; set; }

    /// <summary>Keep editing this player while other nodes are selected (Godot's pin).</summary>
    public bool Pin
    {
        get => _pinned is not null;
        set
        {
            _pinned = value ? Player : null;
            Touch();
        }
    }

    /// <summary>The selected keys.</summary>
    public List<AnimationKeyRef> Selected { get; } = [];

    /// <summary>Tests: edit <paramref name="player"/> with <paramref name="history"/> and no workspace.</summary>
    internal void EditForTests(AnimationPlayer? player, UndoRedo? history)
    {
        _testPlayer = player;
        _testHistory = history;
    }

    /// <summary>The edited player: the pinned one, else the active scene's selected <see cref="AnimationPlayer"/>.</summary>
    public AnimationPlayer? Player
    {
        get
        {
            if (_workspace is null)
                return _testPlayer;
            if (_pinned is { IsInsideTree: true } pinned)
                return pinned;
            var scene = _workspace.Session.Active;
            if (scene is null || _workspace.Mode != EditorMode.Game)
                return null;
            return scene.Selection.Primary as AnimationPlayer;
        }
    }

    /// <summary>True while the Animation tab is available.</summary>
    public bool IsActive => Player is not null;

    /// <summary>The inspector shows key buttons: an animation is being edited.</summary>
    public bool KeysShown => IsActive && Animation is not null;

    /// <summary>
    /// True when the inspector row of <paramref name="property"/> on <paramref name="target"/> gets a key button: a node under
    /// the player's root, and a value an animation can key (a Node3D's transform, or a bindable property).
    /// </summary>
    public bool CanKey(object target, string property)
    {
        if (!KeysShown || target is not Node node || node is AnimationPlayer || PathTo(node) is null)
            return false;
        if (node is Node3D && property is "Position" or "Rotation" or "RotationDegrees" or "Scale")
            return true;
        return PropertyBinding.Create(node, property, out _) is { Components: <= 16 };
    }

    /// <summary>Drops every reference into the edited scenes (tab closed, code reload).</summary>
    public void ReleaseReferences()
    {
        EndPreview();
        _pinned = null;
        Selected.Clear();
        _animation = "";
    }

    private UndoRedo? History => _workspace?.Session.Active?.History ?? _testHistory;

    /// <summary>The edited animation's name (library-prefixed outside the default library).</summary>
    public string AnimationName
    {
        get
        {
            if (Player is { } player && (_animation.Length == 0 || !player.HasAnimation(_animation)))
            {
                var names = new List<string>();
                player.GetAnimationNames(names);
                _animation = names.Count > 0 ? names[0] : "";
            }

            return _animation;
        }
    }

    /// <summary>The edited animation, or null.</summary>
    public Animation? Animation => Player?.GetAnimation(AnimationName);

    /// <summary>The player's animation names.</summary>
    public List<string> AnimationNames()
    {
        var names = new List<string>();
        Player?.GetAnimationNames(names);
        return names;
    }

    /// <summary>Edits <paramref name="name"/>.</summary>
    public void SelectAnimation(string name)
    {
        if (Player?.HasAnimation(name) != true || name == _animation)
            return;
        EndPreview();
        _animation = name;
        Selected.Clear();
        Playhead = 0;
        Touch();
    }

    // ── Animations ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Adds an empty animation (1 s) to the player's default library (created when missing) and edits it.</summary>
    public string NewAnimation(string baseName = "new_animation")
    {
        if (Player is not { } player || History is not { } history)
            return "";
        var library = player.GetAnimationLibrary("");
        var actions = new List<IEditorAction>();
        if (library is null)
        {
            library = new AnimationLibrary();
            var property = TypeRegistry.GetNearest(typeof(AnimationPlayer))!.FindProperty(nameof(AnimationPlayer.Libraries))!;
            actions.Add(SetPropertyAction.Create(player, property, new List<AnimationLibrary>([.. player.Libraries, library])));
        }

        var name = UniqueName(player, baseName);
        var animation = new Animation { ResourceName = name };
        actions.Add(new AnimationLibraryAction("New Animation", library, [.. library.Animations], [.. library.Animations, animation]));
        history.Commit(actions.Count == 1 ? actions[0] : new CompositeAction("New Animation", [.. actions]));
        SelectAnimation(name);
        return name;
    }

    /// <summary>A copy of the edited animation next to it, named "…_copy".</summary>
    public void DuplicateAnimation()
    {
        if (Animation is not { } animation || LibraryOf(animation) is not { } library || History is not { } history)
            return;
        var copy = (Animation)animation.Duplicate(deep: true);
        copy.ResourceName = UniqueName(Player!, animation.ResourceName + "_copy");
        history.Commit(new AnimationLibraryAction("Duplicate Animation", library, [.. library.Animations], [.. library.Animations, copy]));
        SelectAnimation(Prefixed(library, copy.ResourceName));
    }

    /// <summary>Renames the edited animation (unique within its library).</summary>
    public void RenameAnimation(string newName)
    {
        if (Animation is not { } animation || LibraryOf(animation) is not { } library || History is not { } history ||
            string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || library.Get(newName) is not null)
            return;
        var list = library.Animations.ToArray();
        history.Commit(new AnimationLibraryAction("Rename Animation", library, list, list, (animation, animation.ResourceName, newName)));
        _animation = Prefixed(library, newName);
        Touch();
    }

    /// <summary>Removes the edited animation from its library.</summary>
    public void DeleteAnimation()
    {
        if (Animation is not { } animation || LibraryOf(animation) is not { } library || History is not { } history)
            return;
        EndPreview();
        history.Commit(new AnimationLibraryAction("Delete Animation", library, [.. library.Animations], [.. library.Animations.Where(a => !ReferenceEquals(a, animation))]));
        _animation = "";
        Selected.Clear();
        Touch();
    }

    public void SetLength(float seconds) => Edit("Set Animation Length", a => a.Length = Math.Clamp(seconds, 0.001f, 3600f));

    /// <summary>None → Linear → PingPong → None.</summary>
    public void CycleLoop() => Edit("Set Loop Mode", a => a.LoopMode = (AnimationLoopMode)(((int)a.LoopMode + 1) % 3));

    public void SetStep(float seconds) => Edit("Set Snap Step", a => a.Step = Math.Clamp(seconds, 0.001f, 1f));

    // ── Tracks ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The path of <paramref name="node"/> relative to the player's root node (track paths), or null.</summary>
    public NodePath? PathTo(Node node)
    {
        if (Player is not { } player || (player.RootNode.IsEmpty ? player : player.GetNodeOrNull(player.RootNode)) is not { } root)
            return null;
        return root.GetPathTo(node);
    }

    /// <summary>
    /// Adds a track animating <paramref name="property"/> of <paramref name="node"/>: a transform track for a
    /// <see cref="Node3D"/>'s Position, Rotation / RotationDegrees or Scale, else a value track. Returns its index (an
    /// existing one when the animation already has it), or −1.
    /// </summary>
    public int AddPropertyTrack(Node node, string property)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Animation is not { } animation || PathTo(node) is not { } path)
            return -1;
        var existing = FindTrack(animation, path, property);
        if (existing >= 0)
            return existing;
        AnimationTrack track;
        switch (node, property)
        {
            case (Node3D, "Position"):
                track = new Position3DTrack { Path = path };
                break;
            case (Node3D, "Rotation" or "RotationDegrees"):
                track = new Rotation3DTrack { Path = path };
                break;
            case (Node3D, "Scale"):
                track = new Scale3DTrack { Path = path };
                break;
            default:
                if (PropertyBinding.Create(node, property, out _) is not { } binding)
                    return -1;
                track = new ValueTrack { Path = path, Property = property, Components = binding.Components };
                break;
        }

        Edit("Add Track", a => a.Tracks = [.. a.Tracks, track]);
        return animation.Tracks.Count - 1;
    }

    /// <summary>Adds a method track (calls on <paramref name="node"/>) or an audio track (an audio player).</summary>
    public int AddCallTrack(Node node, bool audio)
    {
        if (Animation is null || PathTo(node) is not { } path)
            return -1;
        AnimationTrack track = audio ? new AudioTrack { Path = path } : new MethodTrack { Path = path };
        Edit(audio ? "Add Audio Track" : "Add Method Track", a => a.Tracks = [.. a.Tracks, track]);
        return Animation!.Tracks.Count - 1;
    }

    public void RemoveTrack(int index)
    {
        if (Animation is not { } animation || (uint)index >= (uint)animation.Tracks.Count)
            return;
        Edit("Remove Track", a => a.Tracks = [.. a.Tracks.Where((_, i) => i != index)]);
        Selected.RemoveAll(k => k.Track == index);
        for (var i = 0; i < Selected.Count; i++)
            if (Selected[i].Track > index)
                Selected[i] = Selected[i] with { Track = Selected[i].Track - 1 };
    }

    /// <summary>Nearest → Linear → Cubic → Nearest on an interpolated track.</summary>
    public void CycleInterpolation(int index) =>
        EditTrack(index, "Set Interpolation", t =>
        {
            if (t is InterpolatedTrack i)
                i.Interpolation = (AnimationInterpolation)(((int)i.Interpolation + 1) % 3);
        });

    public void ToggleLoopWrap(int index) => EditTrack(index, "Set Loop Wrap", t =>
    {
        if (t is InterpolatedTrack i)
            i.LoopWrap = !i.LoopWrap;
    });

    public void ToggleEnabled(int index) => EditTrack(index, "Enable Track", t => t.Enabled = !t.Enabled);

    private static int FindTrack(Animation animation, NodePath path, string property)
    {
        var tracks = animation.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Path != path)
                continue;
            var match = (t, property) switch
            {
                (Position3DTrack { Bone.Length: 0 }, "Position") => true,
                (Rotation3DTrack { Bone.Length: 0 }, "Rotation" or "RotationDegrees") => true,
                (Scale3DTrack { Bone.Length: 0 }, "Scale") => true,
                (ValueTrack v, _) => v.Property == property,
                _ => false,
            };
            if (match)
                return i;
        }

        return -1;
    }

    // ── Keys ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The inspector's key button and K (G1.6): a key of <paramref name="property"/>'s current value at the playhead,
    /// creating the track when needed. One undo step.
    /// </summary>
    public bool InsertPropertyKey(Node node, string property)
    {
        if (Animation is not { } animation || PathTo(node) is null)
            return false;
        var before = new AnimationSnapshot(animation);
        var index = FindTrack(animation, PathTo(node)!.Value, property);
        if (index < 0)
        {
            index = AddPropertyTrackUnrecorded(animation, node, property);
            if (index < 0)
                return false;
        }

        if (!InsertKeyUnrecorded(animation, index, (float)Playhead))
            return false;
        Commit("Insert Key", animation, before);
        return true;
    }

    /// <summary>K in the 3D view: position, rotation and scale keys for each node, one undo step.</summary>
    public void KeyTransforms(IEnumerable<Node3D> nodes)
    {
        if (Animation is not { } animation)
            return;
        var before = new AnimationSnapshot(animation);
        var any = false;
        foreach (var node in nodes)
            foreach (var property in (ReadOnlySpan<string>)["Position", "Rotation", "Scale"])
            {
                var index = FindTrack(animation, PathTo(node)!.Value, property);
                if (index < 0)
                    index = AddPropertyTrackUnrecorded(animation, node, property);
                any |= index >= 0 && InsertKeyUnrecorded(animation, index, (float)Playhead);
            }

        if (any)
            Commit("Insert Keys", animation, before);
    }

    /// <summary>A key on track <paramref name="track"/> at <paramref name="time"/> with the target's current value.</summary>
    public bool InsertKey(int track, double time)
    {
        if (Animation is not { } animation)
            return false;
        var before = new AnimationSnapshot(animation);
        if (!InsertKeyUnrecorded(animation, track, (float)SnapTime(time)))
            return false;
        Commit("Insert Key", animation, before);
        return true;
    }

    /// <summary>A method key: <paramref name="method"/> with JSON <paramref name="arguments"/> at <paramref name="time"/>.</summary>
    public void InsertMethodKey(int track, double time, string method, string arguments = "[]")
    {
        if (Animation?.Tracks.ElementAtOrDefault(track) is not MethodTrack)
            return;
        var t = (float)SnapTime(time);
        EditTrack(track, "Insert Method Key", x =>
        {
            var m = (MethodTrack)x;
            var i = InsertIndex(m.Times, t);
            m.Times = [.. m.Times[..i], t, .. m.Times[i..]];
            m.Methods = [.. m.Methods[..Math.Min(i, m.Methods.Length)], method, .. m.Methods[Math.Min(i, m.Methods.Length)..]];
            m.Arguments = [.. m.Arguments[..Math.Min(i, m.Arguments.Length)], arguments, .. m.Arguments[Math.Min(i, m.Arguments.Length)..]];
        });
    }

    /// <summary>The node <paramref name="track"/> animates, or null when its path names none.</summary>
    public Node? TrackTarget(int track)
    {
        if (Animation?.Tracks.ElementAtOrDefault(track) is not { } t || Player is not { } player)
            return null;
        var root = player.RootNode.IsEmpty ? player : player.GetNodeOrNull(player.RootNode);
        return root?.GetNodeOrNull(t.Path);
    }

    /// <summary>The <see cref="AnimationMethodAttribute"/> methods a method track's node offers (the key picker), in declaration order.</summary>
    public IReadOnlyList<AnimationMethodInfo> MethodsFor(int track) =>
        TrackTarget(track) is { } node && TypeRegistry.GetNearest(node.GetType()) is { } info ? [.. info.AnimationMethods] : [];

    /// <summary>An audio key at <paramref name="time"/>: the track's audio player's own stream, from its start. False without one.</summary>
    public bool InsertAudioKey(int track, double time)
    {
        if (Animation?.Tracks.ElementAtOrDefault(track) is not AudioTrack)
            return false;
        var stream = TrackTarget(track) switch
        {
            AudioPlayer p => p.Stream,
            AudioPlayer2D p => p.Stream,
            AudioPlayer3D p => p.Stream,
            _ => null,
        };
        if (stream is null)
            return false;
        var t = (float)SnapTime(time);
        EditTrack(track, "Insert Audio Key", x =>
        {
            var a = (AudioTrack)x;
            var i = InsertIndex(a.Times, t);
            var n = Math.Min(i, a.Streams.Length);
            var o = Math.Min(i, a.StartOffsets.Length);
            a.Times = [.. a.Times[..i], t, .. a.Times[i..]];
            a.Streams = [.. a.Streams[..n], stream, .. a.Streams[n..]];
            a.StartOffsets = [.. a.StartOffsets[..o], 0f, .. a.StartOffsets[o..]];
        });
        return true;
    }

    /// <summary>Removes the selected keys (one undo step).</summary>
    public void DeleteSelectedKeys()
    {
        if (Animation is not { } animation || Selected.Count == 0)
            return;
        var before = new AnimationSnapshot(animation);
        foreach (var group in Selected.GroupBy(k => k.Track))
            if (group.Key < animation.Tracks.Count)
                RemoveKeys(animation.Tracks[group.Key], [.. group.Select(k => k.Key).Distinct().OrderDescending()]);
        Selected.Clear();
        animation.NotifyChanged();
        Commit("Delete Keys", animation, before);
    }

    /// <summary>Starts moving the selected keys (a drag): <see cref="MoveSelectedKeys"/> then <see cref="EndGesture"/>.</summary>
    public void BeginKeyDrag()
    {
        if (Animation is not { } animation)
            return;
        _gestureBefore = new AnimationSnapshot(animation);
        _gestureAnimation = animation;
        _gestureName = "Move Keys";
    }

    /// <summary>
    /// Moves the selected keys to <paramref name="delta"/> seconds from where the drag began (snapped), keeping each
    /// track's keys in order. Live; one undo step at <see cref="EndGesture"/>.
    /// </summary>
    public void MoveSelectedKeys(double delta)
    {
        if (_gestureBefore is null || _gestureAnimation is not { } animation)
            return;
        var step = Snap ? animation.Step : 0;
        delta = TimelineSnap.Round(delta, step);
        _gestureBefore.Apply(animation); // from the drag's start each time: no drift
        foreach (var group in Selected.GroupBy(k => k.Track).ToList())
        {
            if (group.Key >= animation.Tracks.Count)
                continue;
            var track = animation.Tracks[group.Key];
            var keys = group.Select(k => k.Key).ToHashSet();
            var times = (float[])track.Times.Clone();
            foreach (var k in keys)
                if (k < times.Length)
                    times[k] = Math.Max(0f, times[k] + (float)delta);
            ReorderKeys(track, times, keys, out var moved);
            Selected.RemoveAll(s => s.Track == group.Key);
            Selected.AddRange(moved.Select(k => new AnimationKeyRef(group.Key, k)));
        }

        animation.NotifyChanged();
        PreviewAt(Playhead);
    }

    /// <summary>Commits the drag as one undo step (nothing when nothing moved).</summary>
    public void EndGesture()
    {
        if (_gestureBefore is { } before && _gestureAnimation is { } animation)
            Commit(_gestureName, animation, before);
        _gestureBefore = null;
        _gestureAnimation = null;
    }

    /// <summary>Esc during a drag: the keys go back.</summary>
    public void CancelGesture()
    {
        if (_gestureBefore is { } before && _gestureAnimation is { } animation)
            before.Apply(animation);
        _gestureBefore = null;
        _gestureAnimation = null;
        Touch();
    }

    /// <summary>Sets a value key's floats (the key editor row).</summary>
    public void SetKeyValue(AnimationKeyRef key, ReadOnlySpan<float> value)
    {
        var copy = value.ToArray();
        EditTrack(key.Track, "Set Key Value", t =>
        {
            switch (t)
            {
                case ValueTrack v when key.Key * v.Components + v.Components <= v.Values.Length:
                    var values = (float[])v.Values.Clone();
                    copy.AsSpan(0, Math.Min(copy.Length, v.Components)).CopyTo(values.AsSpan(key.Key * v.Components));
                    v.Values = values;
                    break;
                case Position3DTrack p when key.Key < p.Values.Length && copy.Length >= 3:
                    p.Values = Replace(p.Values, key.Key, new Vector3(copy[0], copy[1], copy[2]));
                    break;
                case Scale3DTrack s when key.Key < s.Values.Length && copy.Length >= 3:
                    s.Values = Replace(s.Values, key.Key, new Vector3(copy[0], copy[1], copy[2]));
                    break;
                case Rotation3DTrack r when key.Key < r.Values.Length && copy.Length >= 3:
                    // Euler degrees, as Node3D.RotationDegrees shows them.
                    r.Values = Replace(r.Values, key.Key, EulerAngles.ToQuaternion(new Vector3(copy[0], copy[1], copy[2])));
                    break;
            }
        });
    }

    /// <summary>Sets a key's ease (<see cref="InterpolatedTrack.Transitions"/>).</summary>
    public void SetKeyTransition(AnimationKeyRef key, float transition) => EditTrack(key.Track, "Set Key Easing", t =>
    {
        if (t is not InterpolatedTrack i || key.Key >= i.Times.Length)
            return;
        var transitions = i.Transitions.Length == i.Times.Length ? (float[])i.Transitions.Clone() : Enumerable.Repeat(1f, i.Times.Length).ToArray();
        transitions[key.Key] = transition;
        i.Transitions = transitions;
    });

    // ── Selection and hit testing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The key under (x, y) dp of the timeline (keys are 10 dp diamonds on their track's row), or null.</summary>
    public AnimationKeyRef? HitKey(float x, float y)
    {
        if (Animation is not { } animation || y < RulerHeight)
            return null;
        var row = (int)((y - RulerHeight) / RowHeight);
        if ((uint)row >= (uint)animation.Tracks.Count)
            return null;
        var times = animation.Tracks[row].Times;
        var best = -1;
        var bestDistance = 7f;
        for (var i = 0; i < times.Length; i++)
        {
            var d = MathF.Abs(View.X(times[i]) - x);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best >= 0 ? new AnimationKeyRef(row, best) : null;
    }

    /// <summary>The track row at <paramref name="y"/> dp, or −1 (the ruler, below the last track).</summary>
    public int RowAt(float y) => Animation is { } a && y >= RulerHeight && (int)((y - RulerHeight) / RowHeight) is var row && row < a.Tracks.Count ? row : -1;

    /// <summary>Selects the keys inside a rubber band (dp), replacing or adding to the selection.</summary>
    public void SelectRect(float x0, float y0, float x1, float y1, bool add)
    {
        if (!add)
            Selected.Clear();
        if (Animation is not { } animation)
            return;
        var (left, right) = (MathF.Min(x0, x1), MathF.Max(x0, x1));
        var (top, bottom) = (MathF.Min(y0, y1), MathF.Max(y0, y1));
        for (var t = 0; t < animation.Tracks.Count; t++)
        {
            var rowMid = RulerHeight + (t + 0.5f) * RowHeight;
            if (rowMid < top || rowMid > bottom)
                continue;
            var times = animation.Tracks[t].Times;
            for (var k = 0; k < times.Length; k++)
                if (View.X(times[k]) is var x && x >= left && x <= right && !Selected.Contains(new AnimationKeyRef(t, k)))
                    Selected.Add(new AnimationKeyRef(t, k));
        }

        Touch();
    }

    public void Select(AnimationKeyRef key, bool add)
    {
        if (!add)
            Selected.Clear();
        if (!Selected.Remove(key) || !add)
            Selected.Add(key);
        Touch();
    }

    // ── Preview ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Moves the playhead (snapped) and poses the scene there.</summary>
    public void Scrub(double seconds)
    {
        Playhead = Math.Clamp(SnapTime(seconds), 0, Animation?.Length ?? 0);
        PreviewAt(Playhead);
        Touch();
    }

    public void Play()
    {
        if (Animation is null)
            return;
        if (Playhead >= Animation.Length)
            Playhead = 0;
        Playing = true;
        Touch();
    }

    public void Pause()
    {
        Playing = false;
        Touch();
    }

    /// <summary>Stops, puts the playhead back at 0 and ends the preview (the scene's own values come back).</summary>
    public void Stop()
    {
        Playing = false;
        Playhead = 0;
        EndPreview();
        Touch();
    }

    /// <summary>Per frame: advances a playing preview (looping by the animation's loop mode).</summary>
    public void Tick(double delta)
    {
        if (!ReferenceEquals(Player, _previewPlayer) && _previewPlayer is not null)
            EndPreview(); // the edited player changed: the previous one's scene goes back
        if (!Playing || Animation is not { } animation)
            return;
        var next = Playhead + delta;
        if (next >= animation.Length)
        {
            if (animation.LoopMode == AnimationLoopMode.None)
            {
                next = animation.Length;
                Playing = false;
            }
            else
            {
                next %= Math.Max(animation.Length, 1e-3);
            }
        }

        Playhead = next;
        PreviewAt(next);
        Touch();
    }

    /// <summary>Puts back every value the preview changed (saving, closing the tab, another player).</summary>
    public void EndPreview()
    {
        Playing = false;
        RestoreCaptured();
    }

    private void RestoreCaptured()
    {
        for (var i = _captured.Count - 1; i >= 0; i--)
            _captured[i].Apply();
        _captured.Clear();
        _previewPlayer = null;
    }

    /// <summary>
    /// Saving a scene: the preview's values go back first (saved scenes hold authored values), and the preview returns
    /// after (<see cref="AfterSave"/>).
    /// </summary>
    public bool BeforeSave(EditedScene scene)
    {
        if (_previewPlayer is not { } player || !ReferenceEquals(player.Tree, scene.Viewport.Tree) && !IsUnder(player, scene.Root))
            return false;
        for (var i = _captured.Count - 1; i >= 0; i--)
            _captured[i].Apply();
        return true;
    }

    public void AfterSave() => PreviewAt(Playhead, recapture: false);

    private static bool IsUnder(Node node, Node root)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (ReferenceEquals(n, root))
                return true;
        return false;
    }

    private void PreviewAt(double seconds, bool recapture = true)
    {
        if (Player is not { } player || Animation is not { } animation)
            return;
        if (recapture && !ReferenceEquals(_previewPlayer, player))
        {
            RestoreCaptured();
            _previewPlayer = player;
        }

        if (recapture)
            Capture(player, animation);
        if (player.AssignedAnimation != AnimationName || !player.IsPlaying() && player.CurrentAnimationLength <= 0)
        {
            player.Play(AnimationName, customBlend: 0);
            player.Pause();
        }

        player.Seek(seconds, update: true);
    }

    // Records the current values of everything the animation animates that is not recorded yet (editor-only boxing).
    private void Capture(AnimationPlayer player, Animation animation)
    {
        var root = player.RootNode.IsEmpty ? player : player.GetNodeOrNull(player.RootNode);
        if (root is null)
            return;
        foreach (var track in animation.Tracks)
        {
            if (root.GetNodeOrNull(track.Path) is not { } node)
                continue;
            switch (track)
            {
                case Position3DTrack { Bone.Length: 0 } or Rotation3DTrack { Bone.Length: 0 } or Scale3DTrack { Bone.Length: 0 } when node is Node3D n3:
                    if (!_captured.Any(c => c.Target == n3 && c.Property is null))
                        _captured.Add(new Restore(n3, null, n3.Position, n3.Rotation, n3.Scale, null));
                    break;
                case Position3DTrack or Rotation3DTrack or Scale3DTrack when node is Skeleton3D skeleton:
                    if (!_captured.Any(c => c.Target == skeleton && c.Property == "#bones"))
                        _captured.Add(new Restore(skeleton, "#bones", default, default, default, Bones(skeleton)));
                    break;
                case ValueTrack value:
                    Capture(node, value.Property);
                    break;
                case BezierTrack bezier:
                    Capture(node, bezier.Property);
                    break;
            }
        }
    }

    private void Capture(Node node, string property)
    {
        var name = property.Split(':')[0];
        if (_captured.Any(c => c.Target == node && c.Property == name) ||
            TypeRegistry.GetNearest(node.GetType())?.FindProperty(name) is not { } info)
            return;
        _captured.Add(new Restore(node, name, default, default, default, info.GetValue(node)));
    }

    private static (Vector3, Quaternion, Vector3)[] Bones(Skeleton3D skeleton)
    {
        var poses = new (Vector3, Quaternion, Vector3)[skeleton.GetBoneCount()];
        for (var i = 0; i < poses.Length; i++)
            poses[i] = (skeleton.GetBonePosePosition(i), skeleton.GetBonePoseRotation(i), skeleton.GetBonePoseScale(i));
        return poses;
    }

    private sealed record Restore(Node Target, string? Property, Vector3 Position, Quaternion Rotation, Vector3 Scale, object? Value)
    {
        public void Apply()
        {
            if (Target.IsFreed)
                return;
            switch (Property)
            {
                case null when Target is Node3D node:
                    node.Position = Position;
                    node.Rotation = Rotation;
                    node.Scale = Scale;
                    break;
                case "#bones" when Target is Skeleton3D skeleton && Value is (Vector3, Quaternion, Vector3)[] poses:
                    for (var i = 0; i < poses.Length && i < skeleton.GetBoneCount(); i++)
                    {
                        skeleton.SetBonePosePosition(i, poses[i].Item1);
                        skeleton.SetBonePoseRotation(i, poses[i].Item2);
                        skeleton.SetBonePoseScale(i, poses[i].Item3);
                    }

                    break;
                case { } name when TypeRegistry.GetNearest(Target.GetType())?.FindProperty(name) is { } info:
                    info.SetValue(Target, Value);
                    break;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private double SnapTime(double seconds) => Snap && Animation is { } a ? TimelineSnap.Round(seconds, a.Step) : seconds;

    private void Edit(string name, Action<Animation> change)
    {
        if (Animation is not { } animation)
            return;
        var before = new AnimationSnapshot(animation);
        change(animation);
        animation.NotifyChanged();
        Commit(name, animation, before);
    }

    private void EditTrack(int index, string name, Action<AnimationTrack> change)
    {
        if (Animation is not { } animation || (uint)index >= (uint)animation.Tracks.Count)
            return;
        Edit(name, a => change(a.Tracks[index]));
    }

    private void Commit(string name, Animation animation, AnimationSnapshot before)
    {
        History?.Commit(new AnimationEditAction(name, animation, before, new AnimationSnapshot(animation)), alreadyApplied: true);
        if (_previewPlayer is not null)
            PreviewAt(Playhead);
        Touch();
    }

    private int AddPropertyTrackUnrecorded(Animation animation, Node node, string property)
    {
        if (PathTo(node) is not { } path)
            return -1;
        AnimationTrack? track = (node, property) switch
        {
            (Node3D, "Position") => new Position3DTrack { Path = path },
            (Node3D, "Rotation" or "RotationDegrees") => new Rotation3DTrack { Path = path },
            (Node3D, "Scale") => new Scale3DTrack { Path = path },
            _ => PropertyBinding.Create(node, property, out _) is { } binding
                ? new ValueTrack { Path = path, Property = property, Components = binding.Components }
                : null,
        };
        if (track is null)
            return -1;
        animation.Tracks = [.. animation.Tracks, track];
        return animation.Tracks.Count - 1;
    }

    // A key at time with the target's current value; replaces a key at the same time.
    private bool InsertKeyUnrecorded(Animation animation, int index, float time)
    {
        if ((uint)index >= (uint)animation.Tracks.Count || Player is not { } player ||
            (player.RootNode.IsEmpty ? player : player.GetNodeOrNull(player.RootNode))?.GetNodeOrNull(animation.Tracks[index].Path) is not { } node)
            return false;
        var track = animation.Tracks[index];
        switch (track)
        {
            case Position3DTrack p when node is Node3D n:
                InsertKey(p, time, n.Position, v => p.Values = v, p.Values);
                break;
            case Rotation3DTrack r when node is Node3D n:
                InsertKey(r, time, n.Rotation, v => r.Values = v, r.Values);
                break;
            case Scale3DTrack s when node is Node3D n:
                InsertKey(s, time, n.Scale, v => s.Values = v, s.Values);
                break;
            case ValueTrack v:
                if (PropertyBinding.Create(node, v.Property, out _) is not { } binding)
                    return false;
                var value = new float[binding.Components];
                binding.Read(value);
                v.Components = binding.Components;
                v.SetKey(time, value);
                break;
            default:
                return false;
        }

        animation.NotifyChanged();
        return true;
    }

    private static void InsertKey<T>(InterpolatedTrack track, float time, T value, Action<T[]> set, T[] values)
    {
        var times = track.Times;
        var i = InsertIndex(times, time);
        if (i < times.Length && MathF.Abs(times[i] - time) < 1e-5f && i < values.Length)
        {
            set(Replace(values, i, value));
            return;
        }

        track.Times = [.. times[..i], time, .. times[i..]];
        set([.. values[..Math.Min(i, values.Length)], value, .. values[Math.Min(i, values.Length)..]]);
        if (track.Transitions.Length > 0)
            track.Transitions = [.. track.Transitions[..Math.Min(i, track.Transitions.Length)], 1f, .. track.Transitions[Math.Min(i, track.Transitions.Length)..]];
    }

    private static int InsertIndex(float[] times, float time)
    {
        var i = 0;
        while (i < times.Length && times[i] < time - 1e-5f)
            i++;
        return i;
    }

    private static T[] Replace<T>(T[] values, int index, T value)
    {
        var copy = (T[])values.Clone();
        copy[index] = value;
        return copy;
    }

    // Removes keys (indices descending) from every parallel array of a track.
    private static void RemoveKeys(AnimationTrack track, int[] descending)
    {
        foreach (var k in descending)
        {
            track.Times = RemoveAt(track.Times, k);
            switch (track)
            {
                case Position3DTrack p:
                    p.Values = RemoveAt(p.Values, k);
                    break;
                case Rotation3DTrack r:
                    r.Values = RemoveAt(r.Values, k);
                    break;
                case Scale3DTrack s:
                    s.Values = RemoveAt(s.Values, k);
                    break;
                case ValueTrack v:
                    v.Values = [.. v.Values.AsSpan(0, Math.Min(k * v.Components, v.Values.Length)), .. v.Values.AsSpan(Math.Min((k + 1) * v.Components, v.Values.Length))];
                    break;
                case BezierTrack b:
                    b.Values = RemoveAt(b.Values, k);
                    b.InHandles = RemoveAt(b.InHandles, k);
                    b.OutHandles = RemoveAt(b.OutHandles, k);
                    break;
                case MethodTrack m:
                    m.Methods = RemoveAt(m.Methods, k);
                    m.Arguments = RemoveAt(m.Arguments, k);
                    break;
                case AudioTrack a:
                    a.Streams = RemoveAt(a.Streams, k);
                    a.StartOffsets = RemoveAt(a.StartOffsets, k);
                    break;
                case EventTrack e:
                    e.Names = RemoveAt(e.Names, k);
                    break;
            }

            if (track is InterpolatedTrack i && i.Transitions.Length > k)
                i.Transitions = RemoveAt(i.Transitions, k);
        }
    }

    private static T[] RemoveAt<T>(T[] array, int index) =>
        (uint)index < (uint)array.Length ? [.. array[..index], .. array[(index + 1)..]] : array;

    // Sorts a track's keys by the new times, carrying every parallel array; reports where the moved keys ended up.
    private static void ReorderKeys(AnimationTrack track, float[] times, HashSet<int> moved, out List<int> newIndices)
    {
        var order = Enumerable.Range(0, times.Length).OrderBy(i => times[i]).ThenBy(i => moved.Contains(i) ? 1 : 0).ToArray();
        newIndices = [.. order.Select((old, newIndex) => (old, newIndex)).Where(p => moved.Contains(p.old)).Select(p => p.newIndex)];
        track.Times = [.. order.Select(i => times[i])];
        switch (track)
        {
            case Position3DTrack p:
                p.Values = Permute(p.Values, order);
                break;
            case Rotation3DTrack r:
                r.Values = Permute(r.Values, order);
                break;
            case Scale3DTrack s:
                s.Values = Permute(s.Values, order);
                break;
            case ValueTrack v:
                var n = v.Components;
                var values = new float[v.Values.Length];
                for (var i = 0; i < order.Length && (order[i] + 1) * n <= v.Values.Length; i++)
                    v.Values.AsSpan(order[i] * n, n).CopyTo(values.AsSpan(i * n));
                v.Values = values;
                break;
            case BezierTrack b:
                b.Values = Permute(b.Values, order);
                b.InHandles = Permute(b.InHandles, order);
                b.OutHandles = Permute(b.OutHandles, order);
                break;
            case MethodTrack m:
                m.Methods = Permute(m.Methods, order);
                m.Arguments = Permute(m.Arguments, order);
                break;
            case AudioTrack a:
                a.Streams = Permute(a.Streams, order);
                a.StartOffsets = Permute(a.StartOffsets, order);
                break;
            case EventTrack e:
                e.Names = Permute(e.Names, order);
                break;
        }

        if (track is InterpolatedTrack it && it.Transitions.Length == times.Length)
            it.Transitions = Permute(it.Transitions, order);
    }

    private static T[] Permute<T>(T[] array, int[] order) => order.Length == array.Length ? [.. order.Select(i => array[i])] : array;

    private AnimationLibrary? LibraryOf(Animation animation) =>
        Player?.Libraries.FirstOrDefault(l => l is not null && l.Animations.Contains(animation));

    private static string Prefixed(AnimationLibrary library, string name) => library.ResourceName.Length == 0 ? name : $"{library.ResourceName}/{name}";

    private static string UniqueName(AnimationPlayer player, string baseName)
    {
        var name = baseName;
        for (var i = 2; player.HasAnimation(name); i++)
            name = $"{baseName}_{i}";
        return name;
    }

    private void Touch() => Version++;
}
