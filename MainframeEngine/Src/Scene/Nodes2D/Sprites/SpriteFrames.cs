namespace MainframeEngine;

/// <summary>
/// One flipbook frame (ADR 0223): <see cref="Texture"/> indexes <see cref="SpriteAnimation.Textures"/>,
/// <see cref="Region"/> is the part of that texture drawn in texture pixels (a zero size draws the whole texture), and
/// <see cref="Duration"/> is relative (Godot's frame duration: 2 lasts twice as long as 1 at the animation's speed).
/// Stored as six numbers <c>[texture, x, y, w, h, duration]</c>, one line per frame.
/// </summary>
[SerializableValue]
public readonly record struct SpriteFrame(int Texture, Rect2 Region, float Duration = 1f)
{
    /// <summary>A frame showing the whole texture at <paramref name="texture"/>.</summary>
    public SpriteFrame(int texture)
        : this(texture, default)
    {
    }

    /// <summary>The duration the timing uses: a non-positive or non-finite one counts as 1 (Godot rejects them).</summary>
    public float EffectiveDuration => Duration > 0f && float.IsFinite(Duration) ? Duration : 1f;
}

/// <summary>
/// One named animation of a <see cref="SpriteFrames"/> (Godot keeps these as dictionaries inside the resource): its
/// speed in frames per second, whether it loops, the textures its frames use and the frames. An inline sub-resource.
/// </summary>
[EditorIcon("movie")]
public sealed class SpriteAnimation : Resource
{
    private string _name = SpriteFrames.DefaultAnimation;
    private float _speed = 5f;
    private bool _loop = true;
    private Texture2D?[] _textures = [];
    private SpriteFrame[] _frames = [];

    /// <summary>The animation's name (<see cref="AnimatedSprite2D.Animation"/>, <see cref="AnimatedSprite2D.Play"/>).</summary>
    [Export]
    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? "");
    }

    /// <summary>Frames per second at speed scale 1 (Godot's default is 5).</summary>
    [Export(Range = "0,120,0.01")]
    public float Speed
    {
        get => _speed;
        set => Set(ref _speed, value);
    }

    /// <summary>Starts again from the first frame after the last (otherwise playback stops on the last frame).</summary>
    [Export]
    public bool Loop
    {
        get => _loop;
        set => Set(ref _loop, value);
    }

    /// <summary>The sheets or images the frames draw from (<see cref="SpriteFrame.Texture"/> indexes this).</summary>
    [Export]
    public Texture2D?[] Textures
    {
        get => _textures;
        set
        {
            _textures = value ?? [];
            EmitChanged();
        }
    }

    /// <summary>The frames, in playback order.</summary>
    [Export]
    public SpriteFrame[] Frames
    {
        get => _frames;
        set
        {
            _frames = value ?? [];
            EmitChanged();
        }
    }

    public int FrameCount => _frames.Length;

    /// <summary>The texture and texture-pixel region <paramref name="index"/> draws; false when it has no texture.</summary>
    public bool TryGetFrame(int index, out Texture2D texture, out Rect2 region)
    {
        texture = null!;
        region = default;
        if ((uint)index >= (uint)_frames.Length)
            return false;
        var frame = _frames[index];
        if ((uint)frame.Texture >= (uint)_textures.Length || _textures[frame.Texture] is not { } t)
            return false;
        texture = t;
        region = frame.Region.Size is { X: 0, Y: 0 } ? new Rect2(0, 0, t.Width, t.Height) : frame.Region;
        return true;
    }

    /// <summary>The relative duration of frame <paramref name="index"/> (1 out of range).</summary>
    public float GetFrameDuration(int index) => (uint)index < (uint)_frames.Length ? _frames[index].EffectiveDuration : 1f;

    /// <summary>The index of <paramref name="texture"/> in <see cref="Textures"/>, adding it when missing.</summary>
    public int GetOrAddTexture(Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        for (var i = 0; i < _textures.Length; i++)
            if (ReferenceEquals(_textures[i], texture))
                return i;
        Textures = [.. _textures, texture];
        return _textures.Length - 1;
    }

    /// <summary>
    /// Adds a frame drawing <paramref name="region"/> of <paramref name="texture"/> (default: the whole texture) at
    /// <paramref name="atPosition"/> (-1: the end), as Godot's <c>add_frame</c>.
    /// </summary>
    public void AddFrame(Texture2D texture, Rect2 region = default, float duration = 1f, int atPosition = -1)
    {
        var frame = new SpriteFrame(GetOrAddTexture(texture), region, duration);
        var frames = new List<SpriteFrame>(_frames);
        if (atPosition < 0 || atPosition > frames.Count)
            frames.Add(frame);
        else
            frames.Insert(atPosition, frame);
        Frames = [.. frames];
    }

    /// <summary>Adds one frame per cell of <paramref name="grid"/>, in the given order (the sheet slicer's selection).</summary>
    public void AddFrames(Texture2D texture, SpriteSheetGrid grid, ReadOnlySpan<Vector2I> cells, float duration = 1f)
    {
        var index = GetOrAddTexture(texture);
        var frames = new SpriteFrame[_frames.Length + cells.Length];
        _frames.CopyTo(frames, 0);
        for (var i = 0; i < cells.Length; i++)
            frames[_frames.Length + i] = new SpriteFrame(index, grid.CellRegion(cells[i]), duration);
        Frames = frames;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        EmitChanged();
    }
}

/// <summary>
/// Named flipbook animations for <see cref="AnimatedSprite2D"/> (Godot's <c>SpriteFrames</c>, ADR 0223). Frames keep
/// a texture index and a region rather than one texture each (no <c>AtlasTexture</c>), so a sheet stays one
/// <see cref="Texture2D"/> with one <c>.meta</c> and slicing lives here, not in import settings. Name lookups are
/// cached and allocate nothing.
/// </summary>
[EditorIcon("library-photo")]
public sealed class SpriteFrames : Resource
{
    /// <summary>The animation name new resources (and Godot) start with.</summary>
    public const string DefaultAnimation = "default";

    private List<SpriteAnimation> _animations = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private bool _cacheDirty = true;

    /// <summary>The animations, in list order (the first is the one a sprite falls back to).</summary>
    [Export]
    public List<SpriteAnimation> Animations
    {
        get => _animations;
        set
        {
            _animations = value ?? [];
            Touch();
        }
    }

    /// <summary>The animation named <paramref name="name"/>, or null (ordinal; allocates nothing).</summary>
    public SpriteAnimation? GetAnimation(string? name)
    {
        if (name is null)
            return null;
        var list = _animations;
        if (!_cacheDirty && _byName.TryGetValue(name, out var index) && index < list.Count && list[index] is { } cached &&
            string.Equals(cached.Name, name, StringComparison.Ordinal))
            return cached;
        // A miss or a stale entry (an animation was renamed or the list edited in place): scan, then re-index on a hit.
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { } animation && string.Equals(animation.Name, name, StringComparison.Ordinal))
            {
                Reindex();
                return animation;
            }

        return null;
    }

    public bool HasAnimation(string? name) => GetAnimation(name) is not null;

    /// <summary>The number of frames of <paramref name="name"/> (0 when missing; Godot's <c>get_frame_count</c>).</summary>
    public int GetFrameCount(string? name) => GetAnimation(name)?.FrameCount ?? 0;

    /// <summary>Adds an empty animation (Godot's <c>add_animation</c>); the name must be new.</summary>
    public SpriteAnimation AddAnimation(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (HasAnimation(name))
            throw new ArgumentException($"SpriteFrames already has an animation named '{name}'.", nameof(name));
        var animation = new SpriteAnimation { Name = name };
        _animations.Add(animation);
        Touch();
        return animation;
    }

    /// <summary>Removes the animation named <paramref name="name"/>; false when there is none.</summary>
    public bool RemoveAnimation(string name)
    {
        var animation = GetAnimation(name);
        if (animation is null)
            return false;
        _animations.Remove(animation);
        Touch();
        return true;
    }

    /// <summary>Renames an animation (Godot's <c>rename_animation</c>); the new name must be free.</summary>
    public void RenameAnimation(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        var animation = GetAnimation(from) ?? throw new ArgumentException($"SpriteFrames has no animation named '{from}'.", nameof(from));
        if (from == to)
            return;
        if (HasAnimation(to))
            throw new ArgumentException($"SpriteFrames already has an animation named '{to}'.", nameof(to));
        animation.Name = to;
        Touch();
    }

    /// <summary>Raises <see cref="Resource.Changed"/> after the list was edited in place (users re-resolve their animation).</summary>
    public void NotifyChanged() => Touch();

    private void Touch()
    {
        _cacheDirty = true;
        EmitChanged();
    }

    private void Reindex()
    {
        _byName.Clear();
        var list = _animations;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is { } animation)
                _byName.TryAdd(animation.Name, i); // the first of duplicate names wins, as the scan does
        _cacheDirty = false;
    }
}
