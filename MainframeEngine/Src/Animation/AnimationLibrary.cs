namespace MainframeEngine;

/// <summary>
/// A named set of <see cref="Animation"/>s (Godot's <c>AnimationLibrary</c>, ADR 0207). An animation's name is its
/// <see cref="Resource.ResourceName"/>; a library's own <see cref="Resource.ResourceName"/> is its prefix in an
/// <see cref="AnimationPlayer"/> (<c>""</c> for the default library, <c>"moves/roll"</c> for <c>roll</c> in the library
/// named <c>moves</c>). There is no dictionary codec, so it is a list.
/// </summary>
[EditorIcon("list")]
public sealed class AnimationLibrary : Resource
{
    [Export]
    public List<Animation> Animations { get; set; } = [];

    /// <summary>The animation named <paramref name="name"/>, or null.</summary>
    public Animation? Get(ReadOnlySpan<char> name)
    {
        var animations = Animations;
        for (var i = 0; i < animations.Count; i++)
            if (animations[i] is { } animation && name.SequenceEqual(animation.ResourceName))
                return animation;
        return null;
    }

    public bool Has(ReadOnlySpan<char> name) => Get(name) is not null;

    /// <summary>Adds <paramref name="animation"/> under <paramref name="name"/> (its <see cref="Resource.ResourceName"/>), replacing one of that name.</summary>
    public void Add(string name, Animation animation)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(animation);
        animation.ResourceName = name;
        var index = Animations.FindIndex(a => a?.ResourceName == name);
        if (index >= 0)
            Animations[index] = animation;
        else
            Animations.Add(animation);
    }

    public bool Remove(string name) => Animations.RemoveAll(a => a?.ResourceName == name) > 0;
}
