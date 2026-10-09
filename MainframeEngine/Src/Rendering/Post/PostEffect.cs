namespace MainframeEngine;

/// <summary>
/// Where in the frame a <see cref="PostEffect"/> runs (ADR 0163, docs/design/post-processing.md). Stages run in this
/// order; inside a stage, effects run by <see cref="PostEffect.Order"/>.
/// </summary>
internal enum PostStage
{
    /// <summary>
    /// After the depth prepass, before the lit scene pass: <see cref="SceneTextures.Depth"/> and
    /// <see cref="SceneTextures.Velocity"/> hold the opaque geometry, nothing is lit yet. SSAO runs here; its output is
    /// bound for the lit shaders at set 0, binding 5 (<see cref="FrameContext.AmbientOcclusionBinding"/>).
    /// </summary>
    AfterPrepass,

    /// <summary>
    /// After the scene pass, on the linear HDR colour: TAA, auto exposure, glow, light shafts (and later depth of field).
    /// An effect that produces a new HDR image writes it back with <see cref="PostEffectContext.CopyToSceneColor"/>, so the
    /// effects after it and the tonemap read it.
    /// </summary>
    BeforeTonemap,

    /// <summary>
    /// After the tonemap, on the display-encoded LDR image, before the 2D canvas, gizmos and UI: FXAA (and later
    /// sharpening). Each effect draws a fullscreen pass from <see cref="SceneTextures.Ldr"/> into
    /// <see cref="PostEffectContext.BeginOutput"/>'s target; the last one draws into the swapchain.
    /// </summary>
    AfterTonemap,
}

/// <summary>What a <see cref="PostEffect"/> needs from the frame while it is enabled (the union of every enabled effect's).</summary>
[Flags]
internal enum PostEffectNeeds
{
    None = 0,

    /// <summary>The depth prepass: opaque and cutout depth before lighting (<see cref="PostStage.AfterPrepass"/>).</summary>
    DepthPrepass = 1,

    /// <summary>The prepass's screen-space motion vectors (<see cref="SceneTextures.Velocity"/>); implies the prepass.</summary>
    Velocity = 2 | DepthPrepass,

    /// <summary>A sub-pixel Halton (2, 3) projection jitter every frame (<see cref="TemporalJitter"/>): TAA.</summary>
    Jitter = 4,
}

/// <summary>
/// The engine's effect orders inside each <see cref="PostStage"/> (lower first). Leave gaps: game-defined effects will be
/// placed between them.
/// </summary>
internal static class PostEffectOrder
{
    // AfterPrepass
    public const int Ssao = 100;
    public const int ContactShadows = 200;

    // BeforeTonemap: TAA first (everything after it reads the resolved, jitter-free image), then exposure, which glow reads.
    public const int Taa = 100;
    public const int DepthOfField = 150;
    public const int AutoExposure = 200;
    public const int Glow = 300;
    public const int LightShafts = 400;

    // AfterTonemap
    public const int Sharpen = 50;
    public const int Fxaa = 100;

    /// <summary>Debug views replace the final image, so they run last.</summary>
    public const int DebugView = 1000;
}

/// <summary>What the frame's post effects decide on: the root world's settings and the renderer's.</summary>
/// <param name="World">The tree's root <see cref="WorldEnvironment"/>'s settings (<see cref="IVulkanContext.PostProcess"/>).</param>
/// <param name="AntiAliasing">The project's anti-aliasing (<see cref="IVulkanContext.AntiAliasing"/>).</param>
/// <param name="DebugView">A renderer debug view (<see cref="RenderServer.DebugView"/>).</param>
internal readonly record struct PostEffectSettings(PostProcessSettings World, AntiAliasing AntiAliasing, RenderDebugView DebugView)
{
    /// <summary>The engine's defaults: its own tonemap, no anti-aliasing, no debug view (no post effect runs).</summary>
    public static PostEffectSettings Default => new(PostProcessSettings.Default, AntiAliasing.None, RenderDebugView.None);

    /// <summary>The world asks for something beyond the engine's own tonemap (ADR 0124): the post tonemap pass runs.</summary>
    public bool PostTonemap => World != PostProcessSettings.Default;
}

/// <summary>
/// A post-processing effect (ADR 0163): a stage, an order inside it, an enable rule read from the frame's settings,
/// lazily created GPU resources resized with the scene, and <see cref="OnRecord"/>, which records its passes. Effects are
/// registered on the renderer's <see cref="PostProcessStack"/> (one per view; today only the main view has one), so an
/// effect instance is that view's state: TAA's history, auto exposure's adapted luminance.
/// </summary>
/// <remarks>
/// <para>Internal for now with a public-ready shape: a game-defined effect (Godot's <c>CompositorEffect</c>) would
/// subclass it and be added to a world's compositor; see docs/design/post-processing.md.</para>
/// <para>Every callback runs on the render thread with the frame's command buffer open and no render pass active (an
/// <see cref="PostStage.AfterTonemap"/> effect begins its output pass through the context). Per-frame code must not
/// allocate.</para>
/// </remarks>
internal abstract class PostEffect : IDisposable
{
    private bool _disposed;

    protected PostEffect(string name, PostStage stage, int order)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown post stage.");
        Name = name;
        Stage = stage;
        Order = order;
    }

    public string Name { get; }
    public PostStage Stage { get; }
    public int Order { get; }

    /// <summary>What the effect needs while enabled (the stack turns on the prepass, velocity and jitter for it).</summary>
    public virtual PostEffectNeeds Needs => PostEffectNeeds.None;

    /// <summary>Whether the effect runs this frame (read from the settings: cheap, no side effects).</summary>
    public abstract bool IsEnabled(in PostEffectSettings settings);

    /// <summary>True once <see cref="OnCreate"/> has run (the first frame the effect was enabled).</summary>
    public bool IsCreated { get; private set; }

    /// <summary>Creates GPU resources, the first frame the effect is enabled (the scene's size is <see cref="PostEffectContext.Scene"/>).</summary>
    protected virtual void OnCreate(PostEffectContext context)
    {
    }

    /// <summary>
    /// The start of every frame the effect is enabled, before any pass of the view (the frame set is not bound yet):
    /// declare per-frame bindings — an SSAO effect binds its output here with <see cref="FrameContext.SetAmbientOcclusion"/>
    /// — and advance histories. The camera is not filled in yet.
    /// </summary>
    protected virtual void OnBeginFrame(PostEffectContext context)
    {
    }

    /// <summary>After a resize (device idle): resize targets, rewrite the descriptor sets that sample the scene's images.</summary>
    protected virtual void OnResize(PostEffectContext context)
    {
    }

    /// <summary>Records the effect's passes for this frame.</summary>
    protected abstract void OnRecord(PostEffectContext context);

    /// <summary>Destroys GPU resources (the device is idle).</summary>
    protected virtual void OnDispose()
    {
    }

    internal void Create(PostEffectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCreated)
            return;
        OnCreate(context);
        IsCreated = true;
    }

    internal void BeginFrame(PostEffectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Create(context);
        OnBeginFrame(context);
    }

    internal void Resize(PostEffectContext context)
    {
        if (IsCreated && !_disposed)
            OnResize(context);
    }

    internal void Record(PostEffectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Create(context);
        OnRecord(context);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (IsCreated)
            OnDispose();
    }

    public override string ToString() => $"{Name} ({Stage}, {Order})";
}
