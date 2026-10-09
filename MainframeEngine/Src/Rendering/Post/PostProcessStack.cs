namespace MainframeEngine;

/// <summary>
/// The post effects of one view (ADR 0163; today the main view's, owned by the renderer), kept sorted by stage, then
/// <see cref="PostEffect.Order"/>, then registration. Each frame the render server asks it what the enabled effects need
/// (<see cref="GetNeeds"/>: the depth prepass, velocity, jitter) and the renderer records each stage
/// (<see cref="Record"/>): every enabled effect, created on its first enabled frame, in order. Allocation-free per frame.
/// </summary>
internal sealed class PostProcessStack : IDisposable
{
    private readonly List<PostEffect> _effects = [];
    private bool _disposed;

    /// <summary>The effects in run order.</summary>
    public IReadOnlyList<PostEffect> Effects => _effects;

    /// <summary>Adds <paramref name="effect"/> in run order (after effects of the same stage and order added before it).</summary>
    public void Add(PostEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_effects.Contains(effect))
            throw new InvalidOperationException($"{effect} is already in the stack.");
        var key = SortKey(effect.Stage, effect.Order);
        var index = 0;
        while (index < _effects.Count && SortKey(_effects[index].Stage, _effects[index].Order) <= key)
            index++;
        _effects.Insert(index, effect);
    }

    /// <summary>Removes <paramref name="effect"/> (the caller disposes it).</summary>
    public bool Remove(PostEffect effect)
    {
        var index = _effects.IndexOf(effect);
        if (index < 0)
            return false;
        _effects.RemoveAt(index);
        return true;
    }

    /// <summary>The first effect of type <typeparamref name="T"/>, or null.</summary>
    public T? Find<T>() where T : PostEffect
    {
        foreach (var effect in _effects)
            if (effect is T match)
                return match;
        return null;
    }

    private static long SortKey(PostStage stage, int order) => ((long)stage << 32) + order;

    /// <summary>What the effects enabled by <paramref name="settings"/> need.</summary>
    public PostEffectNeeds GetNeeds(in PostEffectSettings settings)
    {
        var needs = PostEffectNeeds.None;
        foreach (var effect in _effects)
            if (effect.IsEnabled(settings))
                needs |= effect.Needs;
        return needs;
    }

    /// <summary>How many effects of <paramref name="stage"/> are enabled by <paramref name="settings"/>.</summary>
    public int CountEnabled(PostStage stage, in PostEffectSettings settings)
    {
        var count = 0;
        foreach (var effect in _effects)
            if (effect.Stage == stage && effect.IsEnabled(settings))
                count++;
        return count;
    }

    /// <summary>
    /// Starts a frame (before any pass of the view): every enabled effect is created if needed and gets
    /// <see cref="PostEffect"/>'s <c>OnBeginFrame</c>, in run order.
    /// </summary>
    public void BeginFrame(PostEffectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = context.Settings;
        foreach (var effect in _effects)
        {
            if (!effect.IsEnabled(settings))
                continue;
            context.Stage = effect.Stage;
            context.Enter(effect);
            effect.BeginFrame(context);
        }
    }

    /// <summary>
    /// Records <paramref name="stage"/>: each enabled effect in order (created first if needed), with
    /// <see cref="PostEffectContext.Stage"/> and <see cref="PostEffectContext.IsLastInStage"/> set. Returns how many ran.
    /// </summary>
    public int Record(PostStage stage, PostEffectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = context.Settings;
        var enabled = CountEnabled(stage, settings);
        if (enabled == 0)
            return 0;
        context.Stage = stage;
        var ran = 0;
        foreach (var effect in _effects)
        {
            if (effect.Stage != stage || !effect.IsEnabled(settings))
                continue;
            context.IsLastInStage = ++ran == enabled;
            context.Enter(effect);
            effect.Record(context);
            if (context.OutputOpen)
                throw new InvalidOperationException($"{effect} began an output pass and did not end it.");
        }

        context.IsLastInStage = false;
        return ran;
    }

    /// <summary>After a resize (device idle): every created effect resizes.</summary>
    public void Resize(PostEffectContext context)
    {
        foreach (var effect in _effects)
        {
            context.Enter(effect);
            effect.Resize(context);
        }
    }

    /// <summary>Disposes every effect (device idle).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var effect in _effects)
            effect.Dispose();
        _effects.Clear();
    }
}
