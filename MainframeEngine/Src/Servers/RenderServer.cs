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
    private bool _warnedTooManyViews;
    private ShadowSystem? _shadows;
    private DebugLinesRenderer? _debugLines;
    private MeshRenderer? _meshes;
    private SubViewportCompositor? _compositor;
    private ObjectIdPicker? _rootPicker;
    private SceneViewport? _root;
    private bool _disposed;

    public RenderServer(IRenderer renderer)
    {
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public IRenderer Renderer { get; }

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
                _shadows = new ShadowSystem(vk);
            return _shadows;
        }
    }

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

    internal void RemoveSubViewport(SubViewport viewport) => _subViewports.Remove(viewport);

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

        CollectPicks();
        _meshes?.BeginPreparation(); // rebuild even when the last frame was skipped (same predicted frame number)
        var world = root.World3D;
        EnsureResources(world);
        if (world.GeometryList.Count > 0 && GetRenderCamera(root, vk.SwapchainExtent) is { } camera)
            Meshes!.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled);

        foreach (var sub in _subViewports)
        {
            if (!ShouldRender(sub) && !NeedsObjectIds(sub))
                continue;
            EnsureResources(sub.World3D);
            if (sub.World3D.GeometryList.Count > 0 && GetRenderCamera(sub, Extent(sub)) is { } subCamera)
                Meshes!.Prepare(EnsureTargets(sub).Draws, sub.World3D, subCamera, collectCasters: false);
        }
    }

    /// <summary>
    /// Records the shadow maps for <paramref name="viewport"/>'s world. Call with the frame's command buffer open
    /// and no render pass active (<see cref="Engine"/> does, after <c>OnShadowPass</c>).
    /// </summary>
    public void RenderShadows(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var world = viewport.World3D;
        if (world.VisualList.Count == 0 || Vulkan is not { FrameStarted: true } vk || Shadows is not { } shadows)
            return;

        EnsureResources(world);
        MeshRenderer? meshes = null;
        var camera = GetRenderCamera(viewport, vk.SwapchainExtent);
        if (world.GeometryList.Count > 0 && camera is not null)
        {
            meshes = Meshes!;
            meshes.Prepare(_mainDraws, world, camera, collectCasters: true); // no-op when PrepareFrame ran
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

        var casterBounds = meshes is null ? Aabb.Empty : _mainDraws.CasterBounds;
        shadows.RenderShadows(world.Lights, camera, casterBounds, new ShadowState(world.VisualList, meshes, _mainDraws, unbounded),
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
    }

    private readonly record struct ShadowState(List<VisualInstance3D> Visuals, MeshRenderer? Meshes, MeshViewDraws Draws, bool Unbounded);

    /// <summary>
    /// Records the offscreen views (every <see cref="SubViewport"/> in the tree) and the root view's object-ID
    /// pass when picks are pending. Call after the shadow pass, with no render pass active (<see cref="Engine"/> does).
    /// </summary>
    public void RenderOffscreen(SceneViewport root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Vulkan is not { FrameStarted: true } vk)
            return;
        var cb = vk.CurrentCommandBuffer;

        var view = 1;
        foreach (var sub in _subViewports)
        {
            var colour = ShouldRender(sub);
            if (colour || NeedsObjectIds(sub))
            {
                if (view < FrameContext.MaxViews)
                {
                    RenderSubViewport(vk, cb, sub, view++, colour);
                }
                else if (!_warnedTooManyViews)
                {
                    Log.Warning($"[Render] More than {FrameContext.MaxViews - 1} sub-viewports render per frame; the rest are skipped.");
                    _warnedTooManyViews = true;
                }
            }

            // Immediate mode, as in RenderMain: the frame's lines are consumed, or dropped when the view did not render.
            sub.DebugLines.Clear();
        }

        vk.Frame.SetView(0, default);

        if (_rootPicker is { HasQueued: true } rootPicker && GetRenderCamera(root, vk.SwapchainExtent) is null)
            rootPicker.MissQueued(); // nothing to pick without a camera
        if (_rootPicker is { HasQueued: true } picker && GetRenderCamera(root, vk.SwapchainExtent) is { } camera)
        {
            var world = root.World3D;
            vk.Frame.Begin(camera, world.Lights); // the main pass rewrites the same data
            var meshes = Meshes!;
            meshes.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled);
            picker.Render(cb, vk.SwapchainExtent, (meshes, _mainDraws), static (s, c) => s.meshes.DrawObjectIds(s._mainDraws, c));
        }
    }

    // colour: false renders only the object-ID pass (picks queued on a view whose colour updates are disabled).
    private void RenderSubViewport(IVulkanContext vk, CommandBuffer cb, SubViewport sub, int view, bool colour)
    {
        var extent = Extent(sub);
        var targets = EnsureTargets(sub);
        targets.Ensure(extent);
        var world = sub.World3D;
        var camera = GetRenderCamera(sub, extent);

        var frame = vk.Frame;
        frame.SetView(view, extent);
        MeshRenderer? meshes = null;
        if (camera is not null)
        {
            EnsureResources(world);
            frame.Begin(camera, world.Lights, shadows: false); // the shadow maps belong to the main world
            meshes = world.GeometryList.Count > 0 ? Meshes : null;
            meshes?.Prepare(targets.Draws, world, camera, collectCasters: false);
        }

        if (colour)
        {
            Span<ClearValue> clears = [new ClearValue { Color = LinearClear(sub.ClearColor) }, new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) }];
            targets.Hdr!.Begin(cb, clears);
            if (camera is not null)
            {
                DrawWorld(world, camera, meshes, targets.Draws, cb);
                if (sub.DebugLines.LineCount > 0)
                {
                    _debugLines ??= new DebugLinesRenderer(vk);
                    _debugLines.Draw(sub.DebugLines, camera);
                }
            }

            vk.Vk.CmdEndRenderPass(cb);
        }

        var picker = targets.Picker;
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
            return;
        _compositor ??= new SubViewportCompositor(vk);
        _compositor.Tonemap(cb, targets);
        targets.RenderCount++;
        if (sub.UpdateMode == SubViewportUpdateMode.Once)
            sub.UpdateMode = SubViewportUpdateMode.Disabled;
    }

    /// <summary>
    /// Draws <paramref name="viewport"/>'s world inside the main (HDR scene) render pass: writes the frame's shared
    /// set 0 (camera + the world's lights, <see cref="FrameContext.Begin(ICamera, LightEnvironment?)"/>), then the sky
    /// of its <see cref="WorldEnvironment"/>, the visuals and the batched meshes, then its
    /// <see cref="SceneViewport.DebugLines"/> (cleared afterwards, drawn or not). Nothing is drawn without an active
    /// camera.
    /// </summary>
    public void RenderMain(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        try
        {
            if (Vulkan is not { FrameStarted: true } vk)
                return;

            var camera = GetRenderCamera(viewport, vk.SwapchainExtent);
            if (camera is null)
                return;

            var world = viewport.World3D;
            EnsureResources(world);
            vk.Frame.SetView(0, default);
            vk.Frame.Begin(camera, world.Lights); // shared set 0: camera + lights, once per frame
            MeshRenderer? meshes = null;
            if (world.GeometryList.Count > 0)
            {
                meshes = Meshes!;
                meshes.Prepare(_mainDraws, world, camera, collectCasters: ShadowsEnabled); // no-op when PrepareFrame ran
            }

            DrawWorld(world, camera, meshes, _mainDraws, vk.CurrentCommandBuffer);

            if (viewport.DebugLines.LineCount > 0)
            {
                _debugLines ??= new DebugLinesRenderer(vk);
                _debugLines.Draw(viewport.DebugLines, camera);
            }
        }
        finally
        {
            // Immediate mode: the frame's lines are consumed, or dropped when nothing could be drawn.
            viewport.DebugLines.Clear();
        }
    }

    private void DrawWorld(World3D world, ICamera camera, MeshRenderer? meshes, MeshViewDraws draws, CommandBuffer cb)
    {
        world.Environment?.DrawSky(this, camera);
        var opaqueDrawn = meshes is null;
        foreach (var visual in world.VisualList)
        {
            if (visual.IsBatched || !visual.IsVisibleInTree())
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
            meshes.DrawTransparent(draws, cb);
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
            sub.Targets?.Picker?.Collect(sub.Tree, _pickCompletions);
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
        sub.UpdateMode != SubViewportUpdateMode.Disabled && sub.Width > 0 && sub.Height > 0 && sub.IsInsideTree;

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

    /// <summary>The camera <see cref="RenderMain"/> uses for <paramref name="viewport"/> (3D camera first, then 2D).</summary>
    public static ICamera? GetRenderCamera(SceneViewport viewport, Extent2D extent)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var aspect = extent.Height == 0 ? 1f : (float)extent.Width / extent.Height;
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
        foreach (var sub in _subViewportsWithTargets)
        {
            sub.Targets?.Dispose();
            sub.Targets = null;
        }

        _subViewportsWithTargets.Clear();
        _subViewports.Clear();
        _rootPicker?.Dispose();
        _rootPicker = null;
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
