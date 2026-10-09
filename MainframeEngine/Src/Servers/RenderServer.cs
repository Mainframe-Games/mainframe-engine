using DrawingColor = System.Drawing.Color;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;


/// <summary>Something holding GPU objects created through the <see cref="RenderServer"/>.</summary>
internal interface IRenderResourceOwner
{
    /// <summary>Destroys the GPU objects (server shutdown with the owner still alive).</summary>
    void ReleaseRenderResourcesForShutdown();
}

/// <summary>
/// Draws <see cref="World3D"/>s. Visual nodes create their GPU objects through it when they enter a tree; every
/// frame it prepares the draw lists (<see cref="PrepareFrame"/>, before the frame starts), records the shadow maps
/// (<see cref="RenderShadows"/>), offscreen views and object-ID passes (<see cref="RenderOffscreen"/>), and the
/// main view (<see cref="RenderMain"/>: sky, visuals by <see cref="VisualInstance3D.RenderPriority"/>, batched
/// meshes, then the viewport's <see cref="DebugLines"/>). <see cref="GeometryInstance3D"/>s are culled, sorted and
/// drawn in instanced batches by the <see cref="MeshRenderer"/>.
/// </summary>
/// <remarks>
/// <para>Order inside a colour pass: the environment's sky, visuals with a negative render priority (the debug
/// grid), opaque and cutout meshes (sorted pipeline → material → mesh), the remaining visuals (Spine), then
/// blended meshes back to front.</para>
/// <para>The per-frame paths are allocation-free (picking requests allocate when made).</para>
/// </remarks>
public sealed class RenderServer : IServer
{
    private readonly HashSet<IRenderResourceOwner> _owners = [];
    private readonly MeshViewDraws _mainDraws = new();
    private readonly List<SubViewport> _subViewports = [];
    private readonly HashSet<SubViewport> _subViewportsWithTargets = [];
    private readonly List<(TaskCompletionSource<PickResult> Completion, PickResult Result)> _pickCompletions = [];
    private readonly List<(Action<FrameCapture> Callback, FrameCapture Capture)> _captureCompletions = [];
    private bool _warnedTooManyViews;
    private ShadowSystem? _shadows;
    private SubViewport? _shadowView;      // the sub-viewport that owns the shadow maps this frame (see SubViewport.Shadows)
    private ulong _shadowViewRenderedFrame = ulong.MaxValue; // frame number its shadow maps were recorded in
    private DebugLinesRenderer? _debugLines;
    private ScreenGizmosRenderer? _screenGizmosRenderer;
    private MeshRenderer? _meshes;
    private SubViewportCompositor? _compositor;
    private ObjectIdPicker? _rootPicker;
    private WaterSceneTextures? _mainWater; // ADR 0173: the main view's scene copy for refracting water
    private SceneViewport? _root;
    private bool _disposed;

    public RenderServer(IRenderer renderer)
    {
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        ForceDepthPrepass = Environment.GetEnvironmentVariable(DepthPrepassVariable) is "1" or "on" or "true";
        _startDebugView = Environment.GetEnvironmentVariable(DebugViewVariable) switch
        {
            "velocity" => RenderDebugView.Velocity,
            "ao" or "ssao" => RenderDebugView.AmbientOcclusion,
            _ => RenderDebugView.None,
        };
    }

    /// <summary>Set to <c>velocity</c> or <c>ao</c> to start with that <see cref="DebugView"/> (tuning, QA).</summary>
    public const string DebugViewVariable = "MAINFRAME_DEBUG_VIEW";

    private RenderDebugView _startDebugView; // applied once the Vulkan renderer exists

    public IRenderer Renderer { get; }

    /// <summary>Set to <c>1</c> to start with <see cref="ForceDepthPrepass"/> on (benchmarks, QA).</summary>
    public const string DepthPrepassVariable = "MAINFRAME_DEPTH_PREPASS";

    /// <summary>
    /// Runs the main view's depth prepass every frame (ADR 0163), even when no post effect needs it: the scene pass then
    /// tests the prepass depth and skips cutout discards. Off by default (the prepass runs when SSAO, TAA or a debug view
    /// needs it); <c>MAINFRAME_DEPTH_PREPASS=1</c> turns it on at start-up.
    /// </summary>
    public bool ForceDepthPrepass { get; set; }

    /// <summary>Jitters the main view's projection every frame even without TAA (tests: motion vectors must ignore it).</summary>
    internal bool ForceProjectionJitter { get; set; }

    /// <summary>
    /// Replaces the main view's final image with an intermediate buffer (ADR 0163): <see cref="RenderDebugView.Velocity"/>
    /// shows the motion vectors (and runs the depth prepass), <see cref="RenderDebugView.AmbientOcclusion"/> the SSAO
    /// (ADR 0165). <c>MAINFRAME_DEBUG_VIEW</c> (<see cref="DebugViewVariable"/>) sets it at start-up.
    /// </summary>
    public RenderDebugView DebugView
    {
        get => (Vulkan as IPostProcessHost)?.DebugView ?? RenderDebugView.None;
        set
        {
            if (Vulkan is IPostProcessHost host)
                host.DebugView = value;
        }
    }

    /// <summary>GPU time of the SSAO passes (ADR 0165) in a recent frame, in milliseconds (0 while SSAO is off or untimed).</summary>
    public double SsaoGpuMilliseconds => PostEffects?.Find<SsaoEffect>() is { IsCreated: true } ssao ? ssao.LastGpuMilliseconds : 0;

    /// <summary>GPU time of the volumetric fog's passes (ADR 0171) in a recent frame, in milliseconds (0 while it is off or untimed).</summary>
    public double VolumetricFogGpuMilliseconds =>
        PostEffects?.Find<VolumetricFogEffect>() is { IsCreated: true } fog ? fog.LastGpuMilliseconds : 0;

    /// <summary>The main view's post effects (ADR 0163), or null without a Vulkan renderer.</summary>
    internal PostProcessStack? PostEffects => (Vulkan as IPostProcessHost)?.PostEffects;

    private ulong _prepassFrame; // the frame RenderPrepass drew the main view's prepass in
    private int _mainCameraId;   // the main view's camera last frame (an identity hash: no reference is kept)

    /// <summary>
    /// A camera cut (ADR 0166): forgets every view's motion history, so the next frame has no camera motion vectors and
    /// TAA starts a new history instead of blending in what the camera saw before. Call it after teleporting the camera,
    /// before the frame renders (e.g. from <c>OnProcess</c>). Switching the tree's current camera does this by itself.
    /// </summary>
    public void ResetTemporalHistory() => Vulkan?.Frame.ResetHistory();

    /// <summary>
    /// Screen-space gizmos (framebuffer pixels, sRGB), drawn after the tonemap between the 2D canvas and the UI, then
    /// cleared. Fill it any time before the frame's overlay pass; <see cref="Engine"/> clears it for frames that are
    /// not drawn.
    /// </summary>
    public ScreenGizmoBatch ScreenGizmos { get; } = new();

    /// <summary>Draws light icons/ranges for the root viewport's lights (dev overlay toggle).</summary>
    public bool ShowLightGizmos { get; set; }

    /// <summary>Draws the corner XYZ axes for the root viewport's camera (dev overlay toggle).</summary>
    public bool ShowAxisGizmo { get; set; }

    /// <summary>Pixels per layout point for the light/axis gizmos. <see cref="Engine"/> sets it each frame from the UI content scale.</summary>
    public float GizmoScale { get; set; } = 1f;

    /// <summary>The Vulkan context, when the renderer is the Vulkan backend.</summary>
    public IVulkanContext? Vulkan => Renderer as IVulkanContext;

    /// <summary>
    /// Whether worlds get shadow maps (default true). When false there is no <see cref="ShadowSystem"/>: lit
    /// pipelines bind the renderer's "no shadows" fallback set. Set it before visuals create their GPU objects.
    /// </summary>
    public bool ShadowsEnabled
    {
        get;
        set
        {
            if (value == field)
                return;
            if (_owners.Count > 0 || _meshes is not null)
                throw new InvalidOperationException("ShadowsEnabled must be set before visuals create GPU resources.");
            field = value;
        }
    } = true;

    /// <summary>
    /// The shared shadow system, created on first use; null without a Vulkan renderer or when
    /// <see cref="ShadowsEnabled"/> is false.
    /// </summary>
    public ShadowSystem? Shadows
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shadows is null && ShadowsEnabled && Vulkan is { } vk)
            {
                _shadows = new ShadowSystem(vk);
                _shadows.Apply(ShadowQualitySettings.For(ShadowQuality));
            }

            return _shadows;
        }
    }

    /// <summary>
    /// The project-wide shadow budget (default <see cref="MainframeEngine.ShadowQuality.High"/>, the shadow system's
    /// defaults): <see cref="MainframeEngine.ShadowQuality.Off"/> turns <see cref="ShadowsEnabled"/> off (set it before
    /// visuals create GPU resources); the other levels apply <see cref="ShadowQualitySettings.For"/> to the shadow
    /// system — now, or when it is created — and may change at any time. Settings changed on <see cref="Shadows"/>
    /// afterwards are kept until the next change of quality.
    /// </summary>
    public ShadowQuality ShadowQuality
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown shadow quality.");
            ShadowsEnabled = value != ShadowQuality.Off; // throws (unchanged) when toggling after visuals exist
            field = value;
            if (value != ShadowQuality.Off)
                _shadows?.Apply(ShadowQualitySettings.For(value));
        }
    } = ShadowQuality.High;

    /// <summary>
    /// Screen-space reflections of refracting water (ADR 0173, <c>rendering.waterSsr</c>; default
    /// <see cref="WaterSsrQuality.Low"/>): Off reflects only the sky. Applies from the next frame.
    /// </summary>
    public WaterSsrQuality WaterSsr
    {
        get;
        set => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown water SSR quality.");
    } = WaterSsrQuality.Low;

    /// <summary>The main view's scene copy for refracting water (ADR 0173; null until a frame drew some).</summary>
    internal WaterSceneTextures? MainWaterScene => _mainWater;

    /// <summary>The shadow system if it already exists (never creates one).</summary>
    public ShadowSystem? ExistingShadows => _shadows;

    /// <summary>Number of live GPU resource owners (nodes, environments) created through this server.</summary>
    public int ResourceOwnerCount => _owners.Count;

    /// <summary>Mesh draw counters of the current frame (instances, culled, draw calls, binds).</summary>
    public MeshDrawStats MeshStats => _meshes?.Stats ?? default;

    /// <summary>The state-hash pipeline cache of mesh pipelines (null before the first mesh is drawn).</summary>
    public PipelineStateCache? PipelineStates => _meshes?.Pipelines;

    /// <summary>GPU meshes, materials and textures currently resident.</summary>
    public (int Meshes, int Materials, int Textures) ResidentResources =>
        _meshes is null ? default : (_meshes.MeshCount, _meshes.MaterialCount, _meshes.TextureCount);

    /// <summary>Offscreen views registered (inside a tree).</summary>
    public IReadOnlyList<SubViewport> SubViewports => _subViewports;

    internal MeshRenderer? Meshes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_meshes is null && Vulkan is { } vk)
                _meshes = new MeshRenderer(vk, Shadows);
            return _meshes;
        }
    }

    internal void Track(IRenderResourceOwner owner) => _owners.Add(owner);

    internal void Untrack(IRenderResourceOwner owner) => _owners.Remove(owner);

    internal void ReleaseGeometry(GeometryInstance3D node) => _meshes?.ReleaseGeometry(node);

    internal void AddSubViewport(SubViewport viewport)
    {
        if (!_subViewports.Contains(viewport))
            _subViewports.Add(viewport);
    }

    internal void RemoveSubViewport(SubViewport viewport)
    {
        _subViewports.Remove(viewport);
        if (ReferenceEquals(_shadowView, viewport))
            _shadowView = null;
    }

    // ── Frame ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the frame's draw lists before it starts recording (call before <c>BeginFrame</c>; <see cref="Engine"/>
    /// does): creates and updates GPU meshes, materials and textures (their uploads join this frame's upload batch),
    /// culls and sorts the main world and every sub-viewport, and completes finished pick requests.
    /// </summary>
    public void PrepareFrame(SceneViewport root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        if (Vulkan is not { } vk || _disposed)
            return;

        _screenGizmosRenderer ??= new ScreenGizmosRenderer(vk, ScreenGizmos);
        CollectPicks();
        _meshes?.BeginPreparation(); // rebuild even when the last frame was skipped (same predicted frame number)
        UpdatePostState(root, vk);
        var world = root.World3D;
        EnsureResources(world);
        if (world.GeometryList.Count > 0 && GetRenderCamera(root, vk.SwapchainExtent) is { } camera)
            Meshes!.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled);

        _shadowView = FindShadowView(root);
        foreach (var sub in _subViewports)
        {
            if (!ShouldRender(sub) && !NeedsObjectIds(sub))
                continue;
            UpdateSubPostState(sub, vk);
            EnsureResources(sub.World3D);
            if (sub.World3D.GeometryList.Count > 0 && GetRenderCamera(sub, Extent(sub)) is { } subCamera)
                Meshes!.Prepare(EnsureTargets(sub).Draws, sub.World3D, subCamera, collectCasters: ReferenceEquals(sub, _shadowView));
        }
    }

    // The shadow maps are shared (one set, one light-matrix ring region per frame): the main world uses them, unless it
    // has nothing to shadow; then the first rendering sub-viewport that asks for them (SubViewport.Shadows) gets them.
    private SubViewport? FindShadowView(SceneViewport root)
    {
        if (!ShadowsEnabled || root.World3D.VisualList.Count > 0)
            return null;
        foreach (var sub in _subViewports)
            if (sub.Shadows && ShouldRender(sub))
                return sub;
        return null;
    }

    /// <summary>
    /// Records the shadow maps for <paramref name="viewport"/>'s world. Call with the frame's command buffer open
    /// and no render pass active (<see cref="Engine"/> does, after <c>OnShadowPass</c>).
    /// </summary>
    /// <remarks>
    /// When the main world has nothing to shadow, the shadow maps go to the sub-viewport that asked for them
    /// (<see cref="SubViewport.Shadows"/>, e.g. the editor's view) instead.
    /// </remarks>
    public void RenderShadows(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (Vulkan is not { FrameStarted: true } vk)
            return;
        if (viewport.World3D.VisualList.Count == 0 && ReferenceEquals(viewport, _root) && _shadowView is { IsInsideTree: true } sub)
        {
            if (RenderShadows(vk, sub.World3D, GetRenderCamera(sub, Extent(sub)), EnsureTargets(sub).Draws))
                _shadowViewRenderedFrame = vk.FrameNumber;
            return;
        }

        RenderShadows(vk, viewport.World3D, GetRenderCamera(viewport, vk.SwapchainExtent), _mainDraws);
    }

    // Records the shadow maps for world as seen by camera; false when there was nothing to do.
    private bool RenderShadows(IVulkanContext vk, World3D world, ICamera? camera, MeshViewDraws draws)
    {
        if (world.VisualList.Count == 0 || Shadows is not { } shadows)
            return false;

        EnsureResources(world);
        MeshRenderer? meshes = null;
        if (world.GeometryList.Count > 0 && camera is not null)
        {
            meshes = Meshes!;
            meshes.Prepare(draws, world, camera, collectCasters: true); // no-op when PrepareFrame ran
        }

        // Visuals drawn one by one (Spine) have no bounds: they cast into every pass.
        var unbounded = false;
        foreach (var visual in world.VisualList)
        {
            if (!visual.IsBatched && visual.CastShadows && visual.IsVisibleInTree())
            {
                unbounded = true;
                break;
            }
        }

        var casterBounds = meshes is null ? Aabb.Empty : draws.CasterBounds;
        shadows.RenderShadows(world.Lights, camera, casterBounds, new ShadowState(world.VisualList, meshes, draws, unbounded),
            static (ShadowState s, in ShadowPass pass) =>
                (s.Meshes?.CullShadowCasters(s.Draws, pass) ?? false) | s.Unbounded, // always cull: it writes the instances
            static (ShadowState s, CommandBuffer cb, in ShadowPass pass) =>
            {
                s.Meshes?.DrawShadowCasters(s.Draws, cb, pass);
                if (!s.Unbounded)
                    return;
                foreach (var visual in s.Visuals)
                {
                    if (visual.IsBatched || !visual.CastShadows || !visual.IsVisibleInTree())
                        continue;
                    if (pass.IsPoint)
                        visual.DrawShadowPoint(cb, pass.LightPosition, pass.LightRange);
                    else
                        visual.DrawShadow2D(cb);
                }
            });
        return true;
    }

    private readonly record struct ShadowState(List<VisualInstance3D> Visuals, MeshRenderer? Meshes, MeshViewDraws Draws, bool Unbounded);

    /// <summary>
    /// Records the skies' offscreen work (<see cref="SkyEnvironment.Prepare"/>: the physical sky's LUTs), the offscreen
    /// views (every <see cref="SubViewport"/> in the tree) and the root view's object-ID pass when picks are pending.
    /// Call after the shadow pass, with no render pass active (<see cref="Engine"/> does).
    /// </summary>
    public void RenderOffscreen(SceneViewport root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Vulkan is not { FrameStarted: true } vk)
            return;
        var cb = vk.CurrentCommandBuffer;
        root.World3D.Environment?.PrepareSky(this, cb); // ADR 0154: the physical sky's LUTs, before any pass draws a sky

        // Sky lighting first (ADR 0150): every view of a world binds its captured sky.
        root.World3D.Environment?.UpdateSkyLighting(this, cb);
        foreach (var sub in _subViewports)
        {
            if (!ShouldRender(sub)) continue;
            sub.World3D.Environment?.PrepareSky(this, cb); // the physical sky's LUTs before its capture and its view
            sub.World3D.Environment?.UpdateSkyLighting(this, cb);
        }

        var view = 1;
        foreach (var sub in _subViewports)
        {
            var colour = ShouldRender(sub);
            if (colour || NeedsObjectIds(sub))
            {
                if (view < FrameContext.MaxViews)
                {
                    view += RenderSubViewport(vk, cb, sub, view, colour);
                }
                else if (!_warnedTooManyViews)
                {
                    Log.Warning($"[Render] More than {FrameContext.MaxViews - 1} sub-viewports render per frame; the rest are skipped.");
                    _warnedTooManyViews = true;
                }
            }

            // Immediate mode, as in RenderMain: the frame's lines are consumed, or dropped when the view did not render.
            sub.DebugLines.Clear();
            sub.OverlayLines.Clear();
        }

        vk.Frame.SetView(0, default);

        if (_rootPicker is { HasQueued: true } rootPicker && GetRenderCamera(root, vk.SwapchainExtent) is null)
            rootPicker.MissQueued(); // nothing to pick without a camera
        if (_rootPicker is { HasQueued: true } picker && GetRenderCamera(root, vk.SwapchainExtent) is { } camera)
        {
            var world = root.World3D;
            vk.Frame.Environment = world.Environment?.FrameEnvironment ?? default;
            BindSkyLighting(vk.Frame, world);
            vk.Frame.Begin(camera, world.Lights); // the main pass rewrites the same data
            var meshes = Meshes!;
            meshes.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled);
            picker.Render(cb, vk.SwapchainExtent, (meshes, _mainDraws), static (s, c) => s.meshes.DrawObjectIds(s._mainDraws, c));
        }
    }

    // colour: false renders only the object-ID pass (picks queued on a view whose colour updates are disabled). Returns
    // how many views it used: 2 for a post-processed view with debug visuals (their overlay pass has an unjittered camera).
    private int RenderSubViewport(IVulkanContext vk, CommandBuffer cb, SubViewport sub, int view, bool colour)
    {
        var extent = Extent(sub);
        var targets = EnsureTargets(sub);
        targets.Ensure(extent);
        var world = sub.World3D;
        var camera = GetRenderCamera(sub, extent);
        // ADR 0169: the view's own post-processing (its state was decided in PrepareFrame).
        var post = colour && camera is not null ? targets.Post : null;
        var prepass = post is not null && targets.Draws.Prepassed;
        var sun = post is not null ? SunFor(world, camera!, post.Settings) : default;

        var frame = vk.Frame;
        frame.SetView(view, extent);
        frame.ResetView(view);
        if (post is not null && (post.Needs & PostEffectNeeds.Jitter) != 0)
        {
            var index = TemporalJitter.SampleIndex(vk.FrameNumber);
            frame.SetViewJitter(view, TemporalJitter.NdcOffset(index, extent.Width, extent.Height), index);
        }

        post?.BeginFrame(cb, view, prepass, camera, sun); // before the view's set is bound: SSAO binds its output here
        MeshRenderer? meshes = null;
        if (camera is not null)
        {
            EnsureResources(world);
            // The shadow maps belong to the main world, unless this view owns them this frame (SubViewport.Shadows).
            // ADR 0171: while the view runs the volumetric fog, the analytic fog starts where it ends.
            frame.Environment = world.Environment?.GetFrameEnvironment(post is { Settings.VolumetricFog.Active: true }) ?? default;
            BindSkyLighting(frame, world);
            frame.Begin(camera, world.Lights, shadows: ReferenceEquals(sub, _shadowView) && _shadowViewRenderedFrame == vk.FrameNumber);
            meshes = world.GeometryList.Count > 0 ? Meshes : null;
            meshes?.Prepare(targets.Draws, world, camera, collectCasters: false);
        }

        if (prepass)
        {
            post!.BeginPrepass(cb);
            meshes?.DrawPrepass(targets.Draws, cb);
            post.EndPrepass(cb, view, camera!, sun);
        }

        if (colour)
        {
            var clear = sub.TransparentBg ? default : LinearClear(sub.ClearColor); // transparent black (Godot's transparent_bg)
            Span<ClearValue> clears = [new ClearValue { Color = clear }, new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) }];
            targets.Hdr!.Begin(cb, clears, post?.SceneLoadPass ?? targets.Hdr.RenderPass);
            if (camera is not null)
            {
                DrawWorld(world, camera, meshes, targets.Draws, cb, targets.Hdr!, ref targets.WaterScene, afterPost: post is not null);
                if (post is null)
                    DrawLines(vk, sub, camera);
            }

            targets.Hdr.End(cb); // explicit barrier: the compositor's tonemap samples the HDR image
        }

        var picker = targets.Picker;
        var views = 1;
        if (camera is null)
        {
            picker?.MissQueued();
        }
        else if ((sub.ObjectIds && colour) || picker is { HasQueued: true })
        {
            picker = targets.Picker ??= new ObjectIdPicker(vk, sub.Name);
            picker.Render(cb, extent, (meshes, targets.Draws), static (s, c) => s.meshes?.DrawObjectIds(s.Draws, c));
        }

        if (!colour)
            return views;
        _compositor ??= new SubViewportCompositor(vk);
        if (post is not null)
        {
            post.Record(cb, view, camera, sun, _compositor);
            views += DrawAfterPost(vk, cb, sub, post, camera!, view + 1);
        }
        else
        {
            _compositor.Tonemap(cb, targets, keepAlpha: sub.TransparentBg);
        }

        if (sub.PendingCaptures is { Count: > 0 } captures)
        {
            var capture = targets.Capture ??= new SubViewportCapture(vk);
            foreach (var callback in captures)
                capture.Request(callback);
            captures.Clear();
        }

        targets.Capture?.Copy(cb, targets.Ldr!);
        targets.RenderCount++;
        if (sub.UpdateMode == SubViewportUpdateMode.Once)
            sub.UpdateMode = SubViewportUpdateMode.Disabled;
        return views;
    }

    // ADR 0169: a post-processed sub-viewport's settings (its world's profile and lens, the current camera's lens, its own
    // anti-aliasing) and what its effects need, decided before its draws are prepared (the prepass marks them).
    private void UpdateSubPostState(SubViewport sub, IVulkanContext vk)
    {
        var targets = EnsureTargets(sub);
        if (!sub.PostProcessing || sub.TransparentBg || vk is not IPostProcessHost)
        {
            targets.RetirePost();
            targets.Draws.Prepassed = false;
            return;
        }

        if (!ShouldRender(sub))
        {
            targets.Draws.Prepassed = false; // an object-ID pass only: the post state (histories) is kept for later frames
            return;
        }

        targets.Ensure(Extent(sub));
        var post = targets.EnsurePost();
        var world = sub.World3D.Environment?.PostProcessSettings ?? PostProcessSettings.Default;
        if (sub.CameraOverride is null && sub.ActiveCamera3D?.Attributes is { } attributes)
            world = attributes.ApplyTo(world);
        var contact = ContactShadowSettings.For(PrimaryShadowLight(sub.World3D.Lights),
            _shadows is { ContactShadows: true } && ReferenceEquals(sub, _shadowView));
        var settings = new PostEffectSettings(world, sub.AntiAliasing, RenderDebugView.None, Math.Clamp(sub.TaaSharpness, 0f, 1f))
        {
            ContactShadows = contact,
            VolumetricFog = sub.World3D.Environment?.VolumetricFogSettings ?? default,
        };
        post.Settings = settings;
        post.ShadowDescriptors = _shadows;
        post.Needs = post.Effects.GetNeeds(settings);
        targets.Draws.Prepassed = (post.Needs & PostEffectNeeds.DepthPrepass) != 0;
    }

    // ADR 0160: the light shafts stream from the world's first directional light, seen by the view's camera.
    private static LightShaftsSun SunFor(World3D world, ICamera camera, in PostEffectSettings settings) =>
        settings.World.LightShaftsEnabled && world.Lights.DirectionalLights is [var sun, ..]
            ? LightShaftsSun.Compute(camera.ViewMatrix, camera.ProjectionMatrix, -sun.Direction)
            : default;

    // ADR 0169: the debug visuals of a post-processed view (grid, debug and overlay lines) after its effects, into its LDR
    // image against the scene depth, with an unjittered camera in their own view slot (when one is free). Returns the views
    // used (0 or 1).
    private int DrawAfterPost(IVulkanContext vk, CommandBuffer cb, SubViewport sub, SubViewportPost post, ICamera camera, int overlayView)
    {
        var world = sub.World3D;
        var any = sub.DebugLines.LineCount > 0 || sub.OverlayLines.LineCount > 0;
        if (!any)
        {
            foreach (var visual in world.VisualList)
            {
                if (visual.DrawsAfterPost && visual.IsVisibleInTree())
                {
                    any = true;
                    break;
                }
            }
        }

        if (!any)
            return 0;
        var frame = vk.Frame;
        var used = 0;
        if (overlayView < FrameContext.MaxViews)
        {
            frame.SetView(overlayView, Extent(sub));
            frame.ResetView(overlayView);
            frame.EnvironmentMaps = null;
            frame.Begin(camera, null, shadows: false);
            used = 1;
        }

        var pass = post.OverlayRenderPass;
        post.BeginOverlay(cb);
        foreach (var visual in world.VisualList)
            if (visual.DrawsAfterPost && visual.IsVisibleInTree())
                visual.DrawAfterPost(camera, pass);
        if (sub.DebugLines.LineCount > 0 || sub.OverlayLines.LineCount > 0)
        {
            _debugLines ??= new DebugLinesRenderer(vk);
            if (sub.DebugLines.LineCount > 0)
                _debugLines.DrawAfterPost(sub.DebugLines, camera, pass, overlay: false);
            if (sub.OverlayLines.LineCount > 0)
                _debugLines.DrawAfterPost(sub.OverlayLines, camera, pass, overlay: true);
        }

        post.EndOverlay(cb);
        return used;
    }

    // ADR 0163: the root world's post settings, what its enabled post effects need (the depth prepass, jitter) and the
    // projection jitter, decided before anything draws the main view this frame (PrepareFrame, again in RenderPrepass).
    private void UpdatePostState(SceneViewport root, IVulkanContext vk)
    {
        if (!root.IsTreeRoot)
            return;
        vk.PostProcess = RootPostProcess(root); // a struct copy
        var needs = PostEffectNeeds.None;
        if (vk is IPostProcessHost host)
        {
            if (_startDebugView != RenderDebugView.None)
            {
                host.DebugView = _startDebugView;
                _startDebugView = RenderDebugView.None;
            }

            host.ContactShadows = ContactShadowSettings.For(PrimaryShadowLight(root.World3D.Lights), _shadows is { ContactShadows: true });
            host.VolumetricFog = root.World3D.Environment?.VolumetricFogSettings ?? default; // ADR 0171
            host.ShadowDescriptors = _shadows;
            needs = host.PostEffects.GetNeeds(host.PostSettings);
        }
        if (ForceDepthPrepass)
            needs |= PostEffectNeeds.DepthPrepass;
        if (ForceProjectionJitter)
            needs |= PostEffectNeeds.Jitter;
        _mainDraws.Prepassed = vk is IPostProcessHost && (needs & PostEffectNeeds.DepthPrepass) != 0;

        var frame = vk.Frame;
        if ((needs & PostEffectNeeds.Jitter) != 0)
        {
            var extent = vk.SwapchainExtent;
            var index = TemporalJitter.SampleIndex(vk.FrameStarted ? vk.FrameNumber : vk.FrameNumber + 1);
            frame.ProjectionJitter = TemporalJitter.NdcOffset(index, extent.Width, extent.Height);
            frame.JitterIndex = index;
        }
        else
        {
            frame.ProjectionJitter = default;
            frame.JitterIndex = 0;
        }
    }

    // The root world's environment for the main view (ADR 0171): while the main view runs the volumetric fog, the analytic
    // fog starts where the march ends.
    private static FrameEnvironment MainEnvironment(World3D world, IVulkanContext vk) =>
        world.Environment?.GetFrameEnvironment(vk is IPostProcessHost { VolumetricFog.Active: true }) ?? default;

    // The light the shadow planner gives the cascades: the first directional light that casts shadows.
    private static DirectionalLight? PrimaryShadowLight(LightEnvironment lights)
    {
        var dirs = lights.DirectionalLights;
        var count = Math.Min(dirs.Count, ShaderLimits.MaxDirectionalLights);
        for (var i = 0; i < count; i++)
            if (dirs[i].CastsShadows)
                return dirs[i];
        return null;
    }

    // The root world's post settings (ADR 0124): its environment's, with the current camera's lens (ADR 0168:
    // Camera3D.Attributes replaces the environment's CameraAttributes). Struct copies, no allocation.
    private static PostProcessSettings RootPostProcess(SceneViewport root)
    {
        var post = root.World3D.Environment?.PostProcessSettings ?? PostProcessSettings.Default;
        if (root.CameraOverride is null && root.ActiveCamera3D?.Attributes is { } attributes)
            post = attributes.ApplyTo(post);
        return post;
    }

    /// <summary>
    /// The main view's depth prepass and the post effects that run on it (ADR 0163). Call after
    /// <see cref="RenderOffscreen"/> and before the scene pass, with no render pass active (<see cref="Engine"/> does).
    /// When an enabled post effect needs it (SSAO, TAA, the velocity view) or <see cref="ForceDepthPrepass"/> is set,
    /// <paramref name="root"/>'s opaque and cutout geometry is drawn into the scene depth and a velocity buffer, the sky's
    /// velocity is filled in, and the <c>AfterPrepass</c> stage runs (SSAO); the scene pass then loads that depth.
    /// Otherwise it only starts the frame's post effects.
    /// </summary>
    public void RenderPrepass(SceneViewport root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Vulkan is not { FrameStarted: true } vk || vk is not IPostProcessHost host || _disposed || !root.IsTreeRoot)
            return;
        UpdatePostState(root, vk);
        var camera = GetRenderCamera(root, vk.SwapchainExtent);
        var cameraId = camera is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(camera);
        if (cameraId != _mainCameraId)
        {
            if (_mainCameraId != 0 && cameraId != 0)
                vk.Frame.ResetHistory(); // another camera: a cut (no motion vectors from the old one, a new TAA history)
            _mainCameraId = cameraId;
        }

        host.SetMainCamera(camera);
        var prepass = _mainDraws.Prepassed && camera is not null;
        host.BeginPostFrame(prepass);
        if (!prepass)
            return;

        var world = root.World3D;
        EnsureResources(world);
        var frame = vk.Frame;
        frame.SetView(0, default);
        frame.Environment = MainEnvironment(world, vk);
        BindSkyLighting(frame, world);
        frame.Begin(camera!, world.Lights); // the scene pass rewrites the same data
        MeshRenderer? meshes = null;
        if (world.GeometryList.Count > 0)
        {
            meshes = Meshes!;
            meshes.Prepare(_mainDraws, world, camera!, collectCasters: ShadowsEnabled); // no-op when PrepareFrame ran
        }

        host.BeginPrepass();
        meshes?.DrawPrepass(_mainDraws, vk.CurrentCommandBuffer);
        host.EndPrepass();
        _prepassFrame = vk.FrameNumber;
        host.RecordAfterPrepass();
    }

    /// <summary>
    /// Draws <paramref name="viewport"/>'s world inside the main (HDR scene) render pass: writes the frame's shared
    /// set 0 (camera + the world's lights, <see cref="FrameContext.Begin(ICamera, LightEnvironment?)"/>), then the sky
    /// of its <see cref="WorldEnvironment"/>, the visuals and the batched meshes, then its
    /// <see cref="SceneViewport.DebugLines"/> (cleared afterwards, drawn or not). For the tree's root viewport it also
    /// queues the light and axis gizmos (<see cref="ShowLightGizmos"/>, <see cref="ShowAxisGizmo"/>) on
    /// <see cref="ScreenGizmos"/>. Nothing is drawn without an active camera.
    /// </summary>
    public void RenderMain(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        try
        {
            if (Vulkan is not { FrameStarted: true } vk)
                return;

            // ADR 0124: the tonemap and glow of the tree's root world (a struct copy: no allocation).
            if (viewport.IsTreeRoot)
            {
                vk.PostProcess = RootPostProcess(viewport);
                vk.LightShaftsSun = default;
            }

            var camera = GetRenderCamera(viewport, vk.SwapchainExtent);
            if (viewport.IsTreeRoot)
            {
                (vk as IPostProcessHost)?.SetMainCamera(camera);
                // ADR 0163: prepassed pipelines only after this frame's prepass (a caller may skip RenderPrepass).
                if (_prepassFrame != vk.FrameNumber)
                    _mainDraws.Prepassed = false;
            }

            if (camera is null)
                return;

            var world = viewport.World3D;
            // ADR 0160: light shafts stream from the world's first directional light, the same sun as the physical sky's.
            if (viewport.IsTreeRoot && vk.PostProcess.LightShaftsEnabled && world.Lights.DirectionalLights is [var sun, ..])
                vk.LightShaftsSun = LightShaftsSun.Compute(camera.ViewMatrix, camera.ProjectionMatrix, -sun.Direction);
            EnsureResources(world);
            vk.Frame.SetView(0, default);
            vk.Frame.Environment = viewport.IsTreeRoot ? MainEnvironment(world, vk) : world.Environment?.FrameEnvironment ?? default;
            BindSkyLighting(vk.Frame, world);
            vk.Frame.Begin(camera, world.Lights); // shared set 0: camera + lights, once per frame
            MeshRenderer? meshes = null;
            if (world.GeometryList.Count > 0)
            {
                meshes = Meshes!;
                meshes.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled); // no-op when PrepareFrame ran
            }

            DrawWorld(world, camera, meshes, _mainDraws, vk.CurrentCommandBuffer, vk.SceneTarget, ref _mainWater);
            DrawLines(vk, viewport, camera);
            if (viewport.IsTreeRoot && (ShowLightGizmos || ShowAxisGizmo))
            {
                var size = new Vector2(vk.SwapchainExtent.Width, vk.SwapchainExtent.Height);
                if (ShowLightGizmos)
                    LightGizmos.Draw(ScreenGizmos, camera, size, world.Lights, GizmoScale);
                if (ShowAxisGizmo)
                    AxisGizmo.Draw(ScreenGizmos, camera, size, GizmoScale);
            }
        }
        finally
        {
            // Immediate mode: the frame's lines are consumed, or dropped when nothing could be drawn.
            viewport.DebugLines.Clear();
            viewport.OverlayLines.Clear();
        }
    }

    // The world's sky lighting for the next FrameContext.Begin: the captured cubes (set 0) and the lights UBO's ambient
    // energy and environment flags.
    private static void BindSkyLighting(FrameContext frame, World3D world)
    {
        var environment = world.Environment;
        var maps = environment?.SkyLightingMaps;
        frame.EnvironmentMaps = maps;
        world.Lights.AmbientEnergy = environment?.AmbientEnergy ?? 1f;
        world.Lights.EnvironmentFlags = environment?.EnvironmentFlags(maps) ?? 0;
    }

    // The viewport's debug lines (depth-tested), then its overlay lines (always on top).
    private void DrawLines(IVulkanContext vk, SceneViewport viewport, ICamera camera)
    {
        if (viewport.DebugLines.LineCount == 0 && viewport.OverlayLines.LineCount == 0)
            return;
        _debugLines ??= new DebugLinesRenderer(vk);
        if (viewport.DebugLines.LineCount > 0)
            _debugLines.Draw(viewport.DebugLines, camera);
        if (viewport.OverlayLines.LineCount > 0)
            _debugLines.Draw(viewport.OverlayLines, camera, overlay: true);
    }

    // afterPost: a post-processed sub-viewport, whose debug visuals (DrawsAfterPost) draw after its effects instead.
    // target / scene: the view's HDR target, inside its scene pass, and its scene copy for refracting water (ADR 0173).
    private void DrawWorld(World3D world, ICamera camera, MeshRenderer? meshes, MeshViewDraws draws, CommandBuffer cb,
        RenderTarget target, ref WaterSceneTextures? scene, bool afterPost = false)
    {
        world.Environment?.DrawSky(this, camera);
        var opaqueDrawn = meshes is null;
        foreach (var visual in world.VisualList)
        {
            if (visual.IsBatched || !visual.IsVisibleInTree() || (afterPost && visual.DrawsAfterPost))
                continue;
            if (!opaqueDrawn && visual.RenderPriority >= 0)
            {
                meshes!.DrawOpaque(draws, cb);
                opaqueDrawn = true;
            }

            visual.Draw(camera, world.Lights);
        }

        if (meshes is not null)
        {
            if (!opaqueDrawn)
                meshes.DrawOpaque(draws, cb);

            // ADR 0173: refracting water reads what the opaque half drew: split the scene pass around a scene copy.
            if (draws.SceneReaders > 0 && target.Depth is { } depth)
            {
                scene ??= new WaterSceneTextures(Vulkan!, meshes.SceneSetLayout, depth.Format, target.Description.Name);
                scene.SplitScenePass(cb, target);
                draws.Scene = scene;
            }

            meshes.WaterSsr = WaterSsr;
            meshes.DrawTransparent(draws, cb);
            draws.Scene = null;
        }
    }

    // ── Picking ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What is under pixel (<paramref name="x"/>, <paramref name="y"/>) of the main view (framebuffer pixels, origin
    /// top-left): the next frame renders an object-ID pass and copies the pixel; the task completes on the render
    /// thread once that frame has finished on the GPU (a couple of frames later). Never stalls a frame.
    /// </summary>
    public Task<PickResult> PickAsync(int x, int y)
    {
        var completion = new TaskCompletionSource<PickResult>();
        if (RootPicker() is { } picker)
            picker.Request(x, y, completion);
        else
            completion.SetResult(PickResult.Miss);
        return completion.Task;
    }

    /// <summary>Queues a pick of the main view and returns a handle to poll with <see cref="TryGetPickResult(PickHandle, out PickResult)"/>.</summary>
    public PickHandle RequestPick(int x, int y) => RootPicker()?.Request(x, y, null) ?? default;

    /// <summary>The result of <see cref="RequestPick(int, int)"/> once ready (true once per handle).</summary>
    public bool TryGetPickResult(PickHandle handle, out PickResult result)
    {
        result = default;
        return _rootPicker is not null && _rootPicker.TryTake(handle, out result);
    }

    internal Task<PickResult> PickAsync(SubViewport viewport, int x, int y)
    {
        var completion = new TaskCompletionSource<PickResult>();
        if (Vulkan is { } vk && !_disposed)
            (EnsureTargets(viewport).Picker ??= new ObjectIdPicker(vk, viewport.Name)).Request(x, y, completion);
        else
            completion.SetResult(PickResult.Miss);
        return completion.Task;
    }

    internal PickHandle RequestPick(SubViewport viewport, int x, int y) =>
        Vulkan is { } vk && !_disposed ? (EnsureTargets(viewport).Picker ??= new ObjectIdPicker(vk, viewport.Name)).Request(x, y, null) : default;

    internal static bool TryGetPickResult(SubViewport viewport, PickHandle handle, out PickResult result)
    {
        result = default;
        return viewport.Targets?.Picker is { } picker && picker.TryTake(handle, out result);
    }

    private ObjectIdPicker? RootPicker() =>
        _rootPicker ??= Vulkan is { } vk && !_disposed ? new ObjectIdPicker(vk, "main view") : null;

    // Results are gathered first and completed afterwards: awaiting code continues inline (no synchronization
    // context) and may pick again or change the tree.
    private void CollectPicks()
    {
        _rootPicker?.Collect(_root?.Tree, _pickCompletions);
        foreach (var sub in _subViewportsWithTargets)
        {
            sub.Targets?.Picker?.Collect(sub.Tree, _pickCompletions);
            sub.Targets?.Capture?.Collect(_captureCompletions);
        }

        if (_captureCompletions.Count > 0)
        {
            var captures = _captureCompletions.ToArray();
            _captureCompletions.Clear();
            foreach (var (callback, capture) in captures)
                callback(capture);
        }

        if (_pickCompletions.Count == 0)
            return;
        var completions = _pickCompletions.ToArray();
        _pickCompletions.Clear();
        foreach (var (completion, result) in completions)
            completion.TrySetResult(result);
    }

    private static bool NeedsObjectIds(SubViewport sub) => sub.IsInsideTree && sub.Targets?.Picker is { HasQueued: true };

    /// <summary>A freed sub-viewport (its targets are already disposed).</summary>
    internal void ForgetSubViewport(SubViewport viewport)
    {
        _subViewports.Remove(viewport);
        _subViewportsWithTargets.Remove(viewport);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool ShouldRender(SubViewport sub) =>
        !sub.Disable3D && sub.UpdateMode != SubViewportUpdateMode.Disabled && sub.Width > 0 && sub.Height > 0 && sub.IsInsideTree;

    private static Extent2D Extent(SubViewport sub) => new((uint)Math.Max(1, sub.Width), (uint)Math.Max(1, sub.Height));

    private SubViewportTargets EnsureTargets(SubViewport sub)
    {
        if (sub.Targets is null)
        {
            _compositor ??= new SubViewportCompositor(Vulkan!);
            sub.Targets = new SubViewportTargets(Vulkan!, _compositor, sub.Name);
            sub.TargetsOwner = this;
            _subViewportsWithTargets.Add(sub);
        }

        return sub.Targets;
    }

    private static ClearColorValue LinearClear(System.Drawing.Color c)
    {
        var linear = ColorSpace.SrgbToLinear(new Vector3(c.R, c.G, c.B) / 255f);
        return new ClearColorValue(linear.X, linear.Y, linear.Z, c.A / 255f);
    }

    /// <summary>
    /// The camera <see cref="RenderMain"/> uses for <paramref name="viewport"/>: its <see cref="SceneViewport.CameraOverride"/>,
    /// else the 3D camera, then the 2D one.
    /// </summary>
    public static ICamera? GetRenderCamera(SceneViewport viewport, Extent2D extent)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var aspect = extent.Height == 0 ? 1f : (float)extent.Width / extent.Height;
        if (viewport.CameraOverride is { } overrideCamera)
        {
            if (overrideCamera is PerspectiveCamera perspective)
                perspective.AspectRatio = aspect;
            return overrideCamera;
        }

        if (viewport.ActiveCamera3D is { } camera3D)
            return camera3D.SyncRenderCamera(aspect);
        if (viewport.ActiveCamera2D is { } camera2D)
            return camera2D.SyncRenderCamera(new Vector2(extent.Width, extent.Height));
        return null;
    }

    // Visuals that entered the tree before the server existed (or while it was absent) get resources lazily.
    private void EnsureResources(World3D world)
    {
        foreach (var visual in world.VisualList)
            if (!visual.HasRenderResources)
                visual.EnsureRenderResources(this);
    }

    /// <summary>
    /// Releases what nodes still hold (nodes not freed before shutdown), the offscreen views' targets, the mesh
    /// renderer, then the shadow system.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var owner in _owners.ToArray())
            owner.ReleaseRenderResourcesForShutdown();
        _owners.Clear();
        _debugLines?.Dispose();
        _debugLines = null;
        _screenGizmosRenderer?.Dispose();
        _screenGizmosRenderer = null;
        foreach (var sub in _subViewportsWithTargets)
        {
            sub.Targets?.Dispose();
            sub.Targets = null;
        }

        _subViewportsWithTargets.Clear();
        _subViewports.Clear();
        _rootPicker?.Dispose();
        _rootPicker = null;
        _mainWater?.Dispose();
        _mainWater = null;
        _mainDraws.Clear();
        _compositor?.Dispose();
        _compositor = null;
        _meshes?.Dispose();
        _meshes = null;
        _shadows?.Dispose();
        _shadows = null;
        _disposed = true;
    }
}
