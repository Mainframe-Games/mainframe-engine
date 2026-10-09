using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>A render target the <see cref="PostTargetPool{TTarget}"/> can resize and destroy (<see cref="RenderTarget"/>).</summary>
internal interface IPostTarget : IDisposable
{
    Extent2D Extent { get; }

    /// <summary>Recreates the images at <paramref name="extent"/>; false when the size did not change.</summary>
    bool Resize(Extent2D extent);
}

/// <summary>A post target's size relative to the scene.</summary>
internal enum PostTargetScale : byte
{
    Full,
    Half,
    Quarter,
}

/// <summary>A pooled post target: one colour attachment of <paramref name="Format"/>, sampled after its pass.</summary>
/// <param name="Name">Its key in the pool (and debug name): one target per name.</param>
internal readonly record struct PostTargetDesc(string Name, Format Format, PostTargetScale Scale = PostTargetScale.Full)
{
    /// <summary>The target's size for a scene of <paramref name="scene"/> pixels (at least one pixel).</summary>
    public Extent2D ExtentFor(Extent2D scene)
    {
        var shift = Scale switch
        {
            PostTargetScale.Half => 1,
            PostTargetScale.Quarter => 2,
            _ => 0,
        };
        return new Extent2D(Math.Max(1u, scene.Width >> shift), Math.Max(1u, scene.Height >> shift));
    }
}

/// <summary>
/// Post-effect render targets owned by the stack (ADR 0163): named targets created on first request and resized with the
/// scene (<see cref="Get"/>), and ping-pong pairs for temporal effects (<see cref="GetHistory"/>: TAA's history). Effects
/// do not create or resize these themselves; they rewrite the descriptor sets that sample them when
/// <see cref="Generation"/> moves. Allocation-free once every target exists.
/// </summary>
internal sealed class PostTargetPool<TTarget> : IDisposable where TTarget : class, IPostTarget
{
    private readonly Func<PostTargetDesc, Extent2D, TTarget> _create;
    private readonly Dictionary<string, (PostTargetDesc Desc, TTarget Target)> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PostHistory<TTarget>> _histories = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="create">Creates a target for a description at a size.</param>
    /// <param name="sceneExtent">The scene's size in pixels.</param>
    public PostTargetPool(Func<PostTargetDesc, Extent2D, TTarget> create, Extent2D sceneExtent)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        SceneExtent = sceneExtent;
    }

    /// <summary>The scene's size the targets follow.</summary>
    public Extent2D SceneExtent { get; private set; }

    /// <summary>Bumped whenever targets were resized: rewrite the descriptor sets that sample them.</summary>
    public int Generation { get; private set; }

    /// <summary>Targets and history pairs alive (each pair counts twice).</summary>
    public int Count => _targets.Count + 2 * _histories.Count;

    /// <summary>The target named <paramref name="desc"/>.Name, created on the first request; a later request must describe it the same way.</summary>
    public TTarget Get(in PostTargetDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_targets.TryGetValue(desc.Name, out var entry))
        {
            if (entry.Desc != desc)
                throw new InvalidOperationException($"Post target '{desc.Name}' was created as {entry.Desc}, now requested as {desc}.");
            return entry.Target;
        }

        if (_histories.ContainsKey(desc.Name))
            throw new InvalidOperationException($"'{desc.Name}' is a history pair.");
        var target = _create(desc, desc.ExtentFor(SceneExtent));
        _targets.Add(desc.Name, (desc, target));
        return target;
    }

    /// <summary>
    /// The ping-pong pair named <paramref name="desc"/>.Name (two targets, <c>Name/0</c> and <c>Name/1</c>), created on the
    /// first request. Call <see cref="PostHistory{TTarget}.Advance"/> once per frame before reading it.
    /// </summary>
    public PostHistory<TTarget> GetHistory(in PostTargetDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_histories.TryGetValue(desc.Name, out var history))
        {
            if (history.Desc != desc)
                throw new InvalidOperationException($"Post history '{desc.Name}' was created as {history.Desc}, now requested as {desc}.");
            return history;
        }

        if (_targets.ContainsKey(desc.Name))
            throw new InvalidOperationException($"'{desc.Name}' is a single target.");
        var extent = desc.ExtentFor(SceneExtent);
        history = new PostHistory<TTarget>(desc,
            _create(desc with { Name = desc.Name + "/0" }, extent),
            _create(desc with { Name = desc.Name + "/1" }, extent));
        _histories.Add(desc.Name, history);
        return history;
    }

    /// <summary>Follows the scene's new size (device idle): resizes every target; histories become invalid.</summary>
    public void Resize(Extent2D sceneExtent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sceneExtent.Width == SceneExtent.Width && sceneExtent.Height == SceneExtent.Height)
            return;
        SceneExtent = sceneExtent;
        foreach (var (desc, target) in _targets.Values)
            target.Resize(desc.ExtentFor(sceneExtent));
        foreach (var history in _histories.Values)
            history.Resize(history.Desc.ExtentFor(sceneExtent));
        Generation++;
    }

    /// <summary>Destroys the target or history pair named <paramref name="name"/> (device idle, or deferred by the target).</summary>
    public bool Release(string name)
    {
        if (_targets.Remove(name, out var entry))
        {
            entry.Target.Dispose();
            return true;
        }

        if (_histories.Remove(name, out var history))
        {
            history.Dispose();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var (_, target) in _targets.Values)
            target.Dispose();
        foreach (var history in _histories.Values)
            history.Dispose();
        _targets.Clear();
        _histories.Clear();
    }
}

/// <summary>
/// Two targets that swap every frame (TAA's history): <see cref="Current"/> is written this frame, <see cref="Previous"/>
/// holds what was written last frame. <see cref="IsValid"/> says whether <see cref="Previous"/> really is last frame's:
/// false on the first frame, after a resize, a <see cref="Reset"/>, or a frame in which nothing was written
/// (<see cref="MarkWritten"/>).
/// </summary>
internal sealed class PostHistory<TTarget> : IDisposable where TTarget : class, IPostTarget
{
    private readonly TTarget[] _targets;
    private int _current;
    private ulong _frame;
    private ulong _writtenFrame;

    internal PostHistory(PostTargetDesc desc, TTarget first, TTarget second)
    {
        Desc = desc;
        _targets = [first, second];
    }

    public PostTargetDesc Desc { get; }

    /// <summary>The target to write this frame.</summary>
    public TTarget Current => _targets[_current];

    /// <summary>Last frame's target (read it only when <see cref="IsValid"/>).</summary>
    public TTarget Previous => _targets[1 - _current];

    /// <summary>True when <see cref="Previous"/> was written in the frame before this one.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Starts frame <paramref name="frame"/>: swaps the targets (once per frame; repeated calls do nothing).</summary>
    public void Advance(ulong frame)
    {
        if (frame == _frame)
            return;
        _current = 1 - _current;
        IsValid = _writtenFrame != 0 && _writtenFrame == _frame && _frame + 1 == frame;
        _frame = frame;
    }

    /// <summary>Records that <see cref="Current"/> was written this frame (it is next frame's valid history).</summary>
    public void MarkWritten() => _writtenFrame = _frame;

    /// <summary>Forgets the history: next frame's <see cref="IsValid"/> is false (camera cuts).</summary>
    public void Reset()
    {
        _writtenFrame = 0;
        IsValid = false;
    }

    internal void Resize(Extent2D extent)
    {
        _targets[0].Resize(extent);
        _targets[1].Resize(extent);
        Reset();
    }

    public void Dispose()
    {
        _targets[0].Dispose();
        _targets[1].Dispose();
    }
}
