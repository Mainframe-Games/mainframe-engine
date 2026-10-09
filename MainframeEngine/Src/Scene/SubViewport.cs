using DrawingColor = System.Drawing.Color;
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
    public DrawingColor ClearColor { get; set; } = DrawingColor.FromArgb(255, 46, 46, 51);

    [Export]
    public SubViewportUpdateMode UpdateMode { get; set; }

    /// <summary>
    /// No 3D (Godot's <c>disable_3d</c>): the view is 2D only — the canvas server draws its canvas into its own target
    /// (<see cref="GetTexture"/>) before the main canvas, and the render server skips its 3D passes.
    /// </summary>
    [Export]
    public bool Disable3D { get; set; }

    /// <summary>
    /// The view clears to transparent black (Godot's <c>transparent_bg</c>) instead of its clear colour: a 2D view's
    /// canvas, and a 3D view's scene, whose alpha the tonemap then keeps (ADR 0136) — rendered objects over a
    /// transparent background, e.g. item icons saved with <see cref="CaptureImage"/>.
    /// </summary>
    [Export]
    public bool TransparentBg { get; set; }

    /// <summary>
    /// Copies the view's image (tonemapped, sRGB-encoded RGBA8, the size of the view) to the CPU after it next renders
    /// and passes it to <paramref name="onCaptured"/> on the main thread a frame or two later (Godot's
    /// <c>get_texture().get_image()</c>; ADR 0136). The view must render: <see cref="UpdateMode"/> Always or Once.
    /// Nothing is delivered outside a renderer (headless).
    /// </summary>
    public void CaptureImage(Action<FrameCapture> onCaptured)
    {
        ArgumentNullException.ThrowIfNull(onCaptured);
        (PendingCaptures ??= []).Add(onCaptured);
    }

    /// <summary>Captures requested before the render server picked them up.</summary>
    internal List<Action<FrameCapture>>? PendingCaptures { get; private set; }

    /// <summary>The view's colour as a texture for canvas items (Godot's <c>get_texture()</c>); one per view.</summary>
    public Texture2D GetTexture() => _texture ??= Texture2D.FromViewport(this);

    private Texture2D? _texture;

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

    /// <summary>
    /// Run the post-processing of the view's world (ADR 0169): its <see cref="WorldEnvironment.PostProcess"/> profile and
    /// lens — SSAO, auto exposure, glow, light shafts, depth of field, the tonemap curve, the colour grade, film effects —
    /// and <see cref="AntiAliasing"/>, with the view's own effect instances and histories. Debug visuals (the
    /// <see cref="Grid3D"/>, <see cref="SceneViewport.DebugLines"/>, <see cref="SceneViewport.OverlayLines"/>) are drawn
    /// after the effects, neither graded nor blurred. Off by default: the view gets the engine tonemap alone. Ignored with
    /// <see cref="TransparentBg"/> (the post chain writes opaque images). The editor's scene view turns it on.
    /// </summary>
    [Export]
    public bool PostProcessing { get; set; }

    /// <summary>
    /// The view's anti-aliasing when <see cref="PostProcessing"/> is on (Godot's <c>screen_space_aa</c> / <c>use_taa</c>
    /// per viewport; the main view uses <c>rendering.antiAliasing</c>). TAA jitters only this view.
    /// </summary>
    [Export]
    public AntiAliasing AntiAliasing { get; set; }

    /// <summary>The sharpen after TAA (0–1; 0 = none), like <c>rendering.taaSharpness</c> for the main view.</summary>
    [Export(Range = "0,1,0.01")]
    public float TaaSharpness { get; set; } = IVulkanContext.DefaultTaaSharpness;

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
/// pipeline draws into it), the LDR tonemapped target, an optional object-ID picker, the tonemap descriptor set and, with
/// <see cref="SubViewport.PostProcessing"/>, the view's post-processing (<see cref="Post"/>, ADR 0169).
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
    public SubViewportCapture? Capture { get; set; }
    public MeshViewDraws Draws { get; } = new();
    public long RenderCount { get; set; }

    /// <summary>The view's post-processing at its current size (null while <see cref="SubViewport.PostProcessing"/> is off).</summary>
    public SubViewportPost? Post { get; private set; }

    /// <summary>The view's post-processing for its current size: a new one after a resize (the old one is retired).</summary>
    public SubViewportPost EnsurePost()
    {
        var extent = Hdr!.Extent;
        if (Post is { } post && post.Extent.Width == extent.Width && post.Extent.Height == extent.Height)
            return post;
        RetirePost();
        return Post = new SubViewportPost(_ctx, this);
    }

    /// <summary>Drops the post-processing; it is disposed once the frames that used it have finished.</summary>
    public void RetirePost()
    {
        if (Post is null)
            return;
        _ctx.Deletions.EnqueueDispose(Post);
        Post = null;
    }
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
        RetirePost();
        _compositor.FreeSet(_tonemapSet);
        _tonemapSet = default;
        Picker?.Dispose();
        Picker = null;
        Capture?.Dispose();
        Capture = null;
        Hdr?.Dispose();
        Ldr?.Dispose();
        Hdr = Ldr = null;
    }
}
