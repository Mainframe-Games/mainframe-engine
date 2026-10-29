using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// The whole editable state of a <see cref="SpriteFrames"/>: its animations (by reference, in order) and each one's name,
/// speed, loop flag, textures and frames. Restoring a snapshot puts every value back, so any SpriteFrames edit can be
/// recorded as one undo entry (ADR 0224).
/// </summary>
public sealed class SpriteFramesSnapshot
{
    private readonly SpriteAnimation[] _animations;
    private readonly (string Name, float Speed, bool Loop, Texture2D?[] Textures, SpriteFrame[] Frames)[] _values;

    public SpriteFramesSnapshot(SpriteFrames frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _animations = [.. frames.Animations];
        _values = new (string, float, bool, Texture2D?[], SpriteFrame[])[_animations.Length];
        for (var i = 0; i < _animations.Length; i++)
        {
            var a = _animations[i];
            _values[i] = a is null ? ("", 0f, false, [], []) : (a.Name, a.Speed, a.Loop, a.Textures, a.Frames);
        }
    }

    public void Restore(SpriteFrames frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        for (var i = 0; i < _animations.Length; i++)
        {
            if (_animations[i] is not { } a)
                continue;
            var (name, speed, loop, textures, list) = _values[i];
            a.Name = name;
            a.Speed = speed;
            a.Loop = loop;
            a.Textures = textures;
            a.Frames = list;
        }

        frames.Animations = [.. _animations]; // last: users re-resolve their animation once everything is back
    }

    /// <summary>True when both describe the same state (an edit that changed nothing is not recorded).</summary>
    public bool SameAs(SpriteFramesSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_animations.Length != other._animations.Length)
            return false;
        for (var i = 0; i < _animations.Length; i++)
        {
            if (!ReferenceEquals(_animations[i], other._animations[i]))
                return false;
            var (a, b) = (_values[i], other._values[i]);
            if (a.Name != b.Name || a.Speed != b.Speed || a.Loop != b.Loop || !a.Textures.SequenceEqual(b.Textures) ||
                !a.Frames.AsSpan().SequenceEqual(b.Frames))
                return false;
        }

        return true;
    }
}

/// <summary>One SpriteFrames edit (any number of animation and frame changes) as one history entry.</summary>
public sealed class SpriteFramesEditAction(string name, SpriteFrames frames, SpriteFramesSnapshot before, SpriteFramesSnapshot after) : IEditorAction
{
    public string Name { get; } = name;

    public SpriteFrames Frames { get; } = frames;

    public void Do() => after.Restore(Frames);

    public void Undo() => before.Restore(Frames);
}

/// <summary>
/// SpriteFrames edits shared by the SpriteFrames inspector, the Select Frames dialog and "New SpriteFrames from
/// Sheet": each runs as one undoable entry in the scene's or resource file's history.
/// </summary>
public static class SpriteFramesEditing
{
    /// <summary>
    /// Runs <paramref name="change"/> on <paramref name="frames"/> and records it as one entry named
    /// <paramref name="name"/> (nothing when it changed nothing). A <c>.mres</c> edited through a scene is written when
    /// the scene is saved.
    /// </summary>
    public static bool Edit(IInspectorContext context, SpriteFrames frames, string name, Action<SpriteFrames> change)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(change);
        var before = new SpriteFramesSnapshot(frames);
        change(frames);
        var after = new SpriteFramesSnapshot(frames);
        if (before.SameAs(after))
            return false;
        frames.NotifyChanged();
        if (context is EditedScene scene && frames.IsExternal)
            scene.MarkResourceFileEdited(frames);
        context.History.Commit(new SpriteFramesEditAction(name, frames, before, after), alreadyApplied: true);
        return true;
    }

    /// <summary>A name not used by any animation: <paramref name="stem"/>, else <c>stem 2</c>, <c>stem 3</c>…</summary>
    public static string UniqueName(SpriteFrames frames, string stem)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (!frames.HasAnimation(stem))
            return stem;
        for (var i = 2; ; i++)
        {
            var name = stem + " " + i.ToString(CultureInfo.InvariantCulture);
            if (!frames.HasAnimation(name))
                return name;
        }
    }

    /// <summary>A copy of <paramref name="source"/> named <paramref name="name"/>, inserted after it.</summary>
    public static SpriteAnimation Duplicate(SpriteFrames frames, SpriteAnimation source, string name)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(source);
        var copy = new SpriteAnimation { Name = name, Speed = source.Speed, Loop = source.Loop, Textures = [.. source.Textures], Frames = [.. source.Frames] };
        var index = frames.Animations.IndexOf(source);
        frames.Animations.Insert(index < 0 ? frames.Animations.Count : index + 1, copy);
        return copy;
    }

    /// <summary>Moves frame <paramref name="index"/> by <paramref name="delta"/> places; returns its new index.</summary>
    public static int MoveFrame(SpriteAnimation animation, int index, int delta)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var frames = animation.Frames;
        var to = Math.Clamp(index + delta, 0, frames.Length - 1);
        if ((uint)index >= (uint)frames.Length || to == index)
            return index;
        var list = new List<SpriteFrame>(frames);
        var frame = list[index];
        list.RemoveAt(index);
        list.Insert(to, frame);
        animation.Frames = [.. list];
        return to;
    }

    /// <summary>Removes frame <paramref name="index"/> and textures no frame uses any more (indices are remapped).</summary>
    public static void RemoveFrames(SpriteAnimation animation, int index, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var list = new List<SpriteFrame>(animation.Frames);
        if ((uint)index >= (uint)list.Count)
            return;
        list.RemoveRange(index, Math.Min(count, list.Count - index));
        animation.Frames = [.. list];
        PruneTextures(animation);
    }

    /// <summary>Drops textures no frame uses, remapping the frames' texture indices.</summary>
    public static void PruneTextures(SpriteAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var textures = animation.Textures;
        var used = new bool[textures.Length];
        foreach (var frame in animation.Frames)
            if ((uint)frame.Texture < (uint)used.Length)
                used[frame.Texture] = true;
        if (Array.TrueForAll(used, static u => u))
            return;
        var remap = new int[textures.Length];
        var kept = new List<Texture2D?>();
        for (var i = 0; i < textures.Length; i++)
        {
            remap[i] = used[i] ? kept.Count : -1;
            if (used[i])
                kept.Add(textures[i]);
        }

        var frames = animation.Frames;
        var remapped = new SpriteFrame[frames.Length];
        for (var i = 0; i < frames.Length; i++)
            remapped[i] = (uint)frames[i].Texture < (uint)remap.Length ? frames[i] with { Texture = remap[frames[i].Texture] } : frames[i];
        animation.Textures = [.. kept];
        animation.Frames = remapped;
    }

    /// <summary>
    /// Inserts copies of <paramref name="frames"/> (from <paramref name="source"/>, which may be another animation)
    /// at <paramref name="at"/> (-1: the end), adding their textures to <paramref name="target"/>.
    /// </summary>
    public static void InsertFrames(SpriteAnimation target, SpriteAnimation source, ReadOnlySpan<SpriteFrame> frames, int at = -1)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        var list = new List<SpriteFrame>(target.Frames);
        var insert = at < 0 || at > list.Count ? list.Count : at;
        foreach (var frame in frames)
        {
            var texture = (uint)frame.Texture < (uint)source.Textures.Length ? source.Textures[frame.Texture] : null;
            if (texture is null)
                continue;
            list.Insert(insert++, frame with { Texture = target.GetOrAddTexture(texture) });
        }

        target.Frames = [.. list];
    }

    /// <summary>Sets one frame's relative duration (clamped to 0.01…100, as Godot's field).</summary>
    public static void SetDuration(SpriteAnimation animation, int index, float duration)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if ((uint)index >= (uint)animation.Frames.Length || !float.IsFinite(duration))
            return;
        var frames = (SpriteFrame[])animation.Frames.Clone();
        frames[index] = frames[index] with { Duration = Math.Clamp(duration, 0.01f, 100f) };
        animation.Frames = frames;
    }

    /// <summary>
    /// A new resource with one animation per grid row of <paramref name="cells"/> (in selection order within a row):
    /// "New SpriteFrames from Sheet". A single row is named "default"; several are "row 1", "row 2"…
    /// </summary>
    public static SpriteFrames FromSheet(Texture2D sheet, SpriteSheetGrid grid, IReadOnlyList<Vector2I> cells, float speed = 5f)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(cells);
        var frames = new SpriteFrames();
        var rows = cells.Select(c => c.Y).Distinct().Order().ToArray();
        foreach (var row in rows)
        {
            var name = rows.Length == 1 ? SpriteFrames.DefaultAnimation : "row " + (row + 1).ToString(CultureInfo.InvariantCulture);
            var animation = frames.AddAnimation(name);
            animation.Speed = speed;
            animation.AddFrames(sheet, grid, cells.Where(c => c.Y == row).ToArray());
        }

        if (rows.Length == 0)
            frames.AddAnimation(SpriteFrames.DefaultAnimation);
        return frames;
    }
}
