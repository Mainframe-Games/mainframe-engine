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
/// Draws <see cref="World3D"/>s: a facade over the existing renderer and <see cref="ShadowSystem"/>. Visual
/// nodes create their GPU objects through it when they enter a tree, and it renders a viewport's world each
/// frame: the shadow pass (every shadow-casting visual for every light) and the main pass (sky, then visuals by
/// <see cref="VisualInstance3D.RenderPriority"/> and tree-entry order) with the viewport's active camera.
/// </summary>
/// <remarks>
/// The draw loops are allocation-free. Opaque/transparent buckets, shared pipelines and multiple render targets
/// arrive with materials and meshes (M3).
/// </remarks>
public sealed class RenderServer : IServer
{
    private readonly HashSet<IRenderResourceOwner> _owners = [];
    private ShadowSystem? _shadows;
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
            if (_owners.Count > 0)
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

    internal void Track(IRenderResourceOwner owner) => _owners.Add(owner);

    internal void Untrack(IRenderResourceOwner owner) => _owners.Remove(owner);

    /// <summary>
    /// Records the shadow maps for <paramref name="viewport"/>'s world. Call with the frame's command buffer open
    /// and no render pass active (<see cref="Engine"/> does, after <c>OnShadowPass</c>).
    /// </summary>
    public void RenderShadows(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var world = viewport.World3D;
        if (world.VisualList.Count == 0 || Vulkan is not { FrameStarted: true } || Shadows is not { } shadows)
            return;

        EnsureResources(world);
        shadows.RenderShadows(world.Lights, world.VisualList,
            static (visuals, cb, _, _, _) =>
            {
                foreach (var visual in visuals)
                    if (visual.CastShadows && visual.IsVisibleInTree())
                        visual.DrawShadow2D(cb);
            },
            static (visuals, cb, _, _, _, lightPosition, lightRange) =>
            {
                foreach (var visual in visuals)
                    if (visual.CastShadows && visual.IsVisibleInTree())
                        visual.DrawShadowPoint(cb, lightPosition, lightRange);
            });
    }

    /// <summary>
    /// Draws <paramref name="viewport"/>'s world inside the main (HDR scene) render pass: writes the frame's shared
    /// set 0 (camera + the world's lights, <see cref="FrameContext.Begin"/>), then the sky of its
    /// <see cref="WorldEnvironment"/>, then every visible visual. Nothing is drawn without an active camera.
    /// </summary>
    public void RenderMain(SceneViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (Vulkan is not { FrameStarted: true } vk)
            return;

        var extent = vk.SwapchainExtent;
        var camera = GetRenderCamera(viewport, extent);
        if (camera is null)
            return;

        var world = viewport.World3D;
        EnsureResources(world);
        vk.Frame.Begin(camera, world.Lights); // shared set 0: camera + lights, once per frame
        world.Environment?.DrawSky(this, camera);
        foreach (var visual in world.VisualList)
            if (visual.IsVisibleInTree())
                visual.Draw(camera, world.Lights);
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

    /// <summary>Releases what nodes still hold (nodes not freed before shutdown), then the shadow system.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var owner in _owners.ToArray())
            owner.ReleaseRenderResourcesForShutdown();
        _owners.Clear();
        _shadows?.Dispose();
        _shadows = null;
        _disposed = true;
    }
}
