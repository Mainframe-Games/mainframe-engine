using System.Drawing;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>When a <see cref="SubViewport"/> renders.</summary>
public enum SubViewportUpdateMode : byte
{
    /// <summary>Every frame.</summary>
    Always,

    /// <summary>Never (the last image stays).</summary>
    Disabled,

    /// <summary>On the next frame, then switches to <see cref="Disabled"/>.</summary>
    Once,
}

/// <summary>
/// An offscreen view (Godot's <c>SubViewport</c>): its children live in their own <see cref="SceneViewport.World3D"/>
/// and are rendered with the sub-viewport's camera into its own targets before the main pass — HDR colour + depth
/// (<see cref="SceneImage"/>, <see cref="DepthImage"/>), the tonemapped sRGB-encoded result (<see cref="ColorImage"/>,
/// usable as a texture, e.g. in the UI) and, with <see cref="ObjectIds"/>, an
/// object-ID target for picking. The editor viewport is one of these.
/// </summary>
/// <remarks>
/// Limits: there is one set of shadow maps, used by the main world; a sub-viewport world is lit without shadows unless
/// it sets <see cref="Shadows"/> and the main world has nothing to shadow (the editor). At most
/// <see cref="FrameContext.MaxViews"/> - 1 sub-viewports render per frame.
/// </remarks>
[EditorIcon("device-desktop")]
public class SubViewport : SceneViewport
{
    private RenderServer? _server;

    /// <summary>Size of the targets in pixels.</summary>
    [Export(Range = "1,8192,1")]
    public int Width { get; set; } = 512;

    [Export(Range = "1,8192,1")]
    public int Height { get; set; } = 512;

    /// <summary>Background where nothing is drawn (sRGB); the world's sky draws over it.</summary>
    [Export]
    public Color ClearColor { get; set; } = Color.FromArgb(255, 46, 46, 51);

    [Export]
    public SubViewportUpdateMode UpdateMode { get; set; }

    /// <summary>
    /// Render shadow maps for this view's world. The engine has one set of shadow maps: the main world uses it whenever it
    /// has visuals; otherwise the first rendering sub-viewport with this set gets it (the editor's view of the edited
    /// scene). Other sub-viewports are lit without shadows.
    /// </summary>
    [Export]
    public bool Shadows { get; set; }

    /// <summary>Also render the object-ID target every frame (hover/picking in tools). Picks work without it.</summary>
    [Export]
    public bool ObjectIds { get; set; }

    /// <summary>The render server's state for this view (targets, draw lists).</summary>
    internal SubViewportTargets? Targets { get; set; }

    /// <summary>The server that created <see cref="Targets"/> (told when the view is freed).</summary>
    internal RenderServer? TargetsOwner { get; set; }

    /// <summary>Tonemapped, sRGB-encoded colour (<c>R8G8B8A8_UNORM</c>, sampled); null until first rendered.</summary>
    public GpuImage? ColorImage => Targets?.Ldr?.GetColor(0);

    /// <summary>
    /// The render target holding <see cref="ColorImage"/> (null until first rendered). It is resized in place, so it
    /// can be published once to the game UI (<see cref="UiServer.RegisterTexture(string, RenderTarget, int, UiTextureConversion)"/>,
    /// <c>engine://name</c>) and followed across resizes.
    /// </summary>
    public RenderTarget? ColorTarget => Targets?.Ldr;

    /// <summary>Linear HDR colour (<c>R16G16B16A16_SFLOAT</c>, sampled); null until first rendered.</summary>
    public GpuImage? SceneImage => Targets?.Hdr?.GetColor(0);

    /// <summary>Depth of the colour pass (sampled); null until first rendered.</summary>
    public GpuImage? DepthImage => Targets?.Hdr?.Depth;

    /// <summary>The object-ID target (<c>R32_UINT</c>, transfer source); null until an ID pass ran.</summary>
    public GpuImage? ObjectIdImage => Targets?.Picker?.Target?.GetColor(0);

    /// <summary>Times the view has been rendered.</summary>
    public long RenderCount => Targets?.RenderCount ?? 0;

    /// <summary>
    /// What is under pixel (<paramref name="x"/>, <paramref name="y"/>) of the view (origin top-left), read back
    /// from the GPU a couple of frames later without stalling. Completes on the render thread.
    /// </summary>
    public Task<PickResult> PickAsync(int x, int y) =>
        _server?.PickAsync(this, x, y) ?? Task.FromResult(PickResult.Miss);

    /// <summary>Queues a pick and returns a handle to poll with <see cref="TryGetPickResult"/>.</summary>
    public PickHandle RequestPick(int x, int y) => _server?.RequestPick(this, x, y) ?? default;

    /// <summary>The result of <see cref="RequestPick"/> once it is ready (taken: true only once).</summary>
    public bool TryGetPickResult(PickHandle handle, out PickResult result)
    {
        result = default;
        return _server is not null && RenderServer.TryGetPickResult(this, handle, out result);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _server = Tree?.Servers.Render;
        _server?.AddSubViewport(this);
    }

    protected override void OnExitTree()
    {
        _server?.RemoveSubViewport(this);
        _server = null;
        base.OnExitTree();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Targets?.Dispose();
            Targets = null;
            TargetsOwner?.ForgetSubViewport(this);
            TargetsOwner = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// GPU state of a <see cref="SubViewport"/>: HDR scene target (compatible with the main scene pass, so every scene
/// pipeline draws into it), the LDR tonemapped target, an optional object-ID picker and the tonemap descriptor set.
/// </summary>
internal sealed class SubViewportTargets : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly SubViewportCompositor _compositor;
    private DescriptorSet _tonemapSet;
    private bool _disposed;

    public SubViewportTargets(IVulkanContext ctx, SubViewportCompositor compositor, string name)
    {
        _ctx = ctx;
        _compositor = compositor;
        Name = name;
    }

    public string Name { get; }
    public RenderTarget? Hdr { get; private set; }
    public RenderTarget? Ldr { get; private set; }
    public ObjectIdPicker? Picker { get; set; }
    public MeshViewDraws Draws { get; } = new();
    public long RenderCount { get; set; }
    public DescriptorSet TonemapSet => _tonemapSet;

    /// <summary>Creates or resizes the targets; rewrites the tonemap set when images change.</summary>
    public void Ensure(Extent2D extent)
    {
        var changed = false;
        if (Hdr is null)
        {
            Hdr = new RenderTarget(_ctx, new RenderTargetDesc($"{Name} (HDR)",
                [RenderTargetAttachment.Sampled(VulkanRenderer.SceneColorFormat)], MeshRenderer.FindDepthFormat(_ctx), SampleDepth: true), extent);
            Ldr = new RenderTarget(_ctx, SubViewportCompositor.LdrTargetDesc(Name), extent);
            changed = true;
        }
        else
        {
            changed |= Hdr.Resize(extent);
            changed |= Ldr!.Resize(extent);
        }

        if (!changed)
            return;

        _compositor.FreeSet(_tonemapSet);
        _tonemapSet = _compositor.AllocateSet(Hdr.GetColor(0).View);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _compositor.FreeSet(_tonemapSet);
        _tonemapSet = default;
        Picker?.Dispose();
        Picker = null;
        Hdr?.Dispose();
        Ldr?.Dispose();
        Hdr = Ldr = null;
    }
}
