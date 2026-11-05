namespace MainframeEngine.Editor;

/// <summary>An <see cref="Animation"/>'s editable state (length, loop, step, tracks as private copies) for undo (G1.6).</summary>
public sealed class AnimationSnapshot
{
    private readonly float _length;
    private readonly AnimationLoopMode _loop;
    private readonly float _step;
    private readonly AnimationTrack[] _tracks;

    public AnimationSnapshot(Animation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        _length = animation.Length;
        _loop = animation.LoopMode;
        _step = animation.Step;
        _tracks = [.. animation.Tracks.Select(t => (AnimationTrack)t.Duplicate(deep: true))];
    }

    /// <summary>Puts this state on <paramref name="animation"/> (its tracks become fresh copies: the snapshot stays unshared).</summary>
    public void Apply(Animation animation)
    {
        animation.Length = _length;
        animation.LoopMode = _loop;
        animation.Step = _step;
        animation.Tracks = [.. _tracks.Select(t => (AnimationTrack)t.Duplicate(deep: true))];
    }
}

/// <summary>One edit of an animation (a key drag, a track added, the length): the state before and after (G1.6).</summary>
public sealed class AnimationEditAction(string name, Animation animation, AnimationSnapshot before, AnimationSnapshot after) : IEditorAction
{
    public string Name { get; } = name;

    public Animation Animation { get; } = animation;

    public void Do() => after.Apply(Animation);

    public void Undo() => before.Apply(Animation);

    public bool TryMerge(IEditorAction next) => false;
}

/// <summary>A library's animation list before and after (new, duplicate, delete, rename) (G1.6).</summary>
public sealed class AnimationLibraryAction(string name, AnimationLibrary library, Animation[] before, Animation[] after,
    (Animation Animation, string Before, string After)? rename = null) : IEditorAction
{
    public string Name { get; } = name;

    public AnimationLibrary Library { get; } = library;

    public void Do()
    {
        Library.Animations = [.. after];
        if (rename is { } r)
            r.Animation.ResourceName = r.After;
    }

    public void Undo()
    {
        Library.Animations = [.. before];
        if (rename is { } r)
            r.Animation.ResourceName = r.Before;
    }

    public bool TryMerge(IEditorAction next) => false;
}
