using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Temporal anti-aliasing (ADR 0166; <c>Post/Taa.vk.frag</c>): a <see cref="PostStage.BeforeTonemap"/> effect, first in
/// the stage, enabled by <see cref="AntiAliasing.Taa"/>. It needs the prepass's motion vectors and the Halton (2, 3)
/// projection jitter. Each frame one fullscreen pass resolves the jittered HDR scene colour against last frame's result:
/// <list type="bullet">
/// <item>the current image is reconstructed at the pixel centre from the 3 × 3 jittered samples around it, weighted by
/// each sample's distance (<see cref="FilterFalloff"/>: narrow, so the history converges to the pixel's own area; TAAU
/// will reuse the shape at a render scale below 1);</item>
/// <item>the history is reprojected with the velocity of the closest depth in the 3 × 3 (edges move with the
/// foreground) and read with a 5-tap Catmull–Rom filter;</item>
/// <item>it is dropped where no history texel around that spot saw this pixel's last-frame view depth (disocclusion:
/// the background behind a swaying branch), and then the pixel shows a softer reconstruction until it builds up;</item>
/// <item>it is clipped towards the neighbourhood's variance box in YCoCg (Salvi, γ = 1.25, inside its min/max);</item>
/// <item>both are blended in Karis' luminance-weighted space (<c>c / (1 + exposure · luma)</c>, which keeps bright sky
/// through needles from flickering) with feedback <see cref="FeedbackStill"/>, falling to <see cref="FeedbackMoving"/>
/// as the pixel moves faster and to <see cref="FeedbackReactive"/> where water marks the scene alpha.</item>
/// </list>
/// The result (linear HDR; alpha holds its view depth for the next frame's disocclusion test) goes to the ping-pong
/// history's current target and back into the scene colour (<see cref="PostEffectContext.CopyToSceneColor"/>), so auto
/// exposure, glow, light shafts and the tonemap read the resolved image. History resets on the first frame, a resize, a
/// frame without a resolve, a camera cut (<see cref="RenderServer.ResetTemporalHistory"/>, a new main camera) and a view
/// without motion history. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class TaaEffect() : PostEffect("taa", PostStage.BeforeTonemap, PostEffectOrder.Taa)
{
    /// <summary>The history's weight for a still pixel: about 1 / (1 − 0.94) ≈ 17 frames, two jitter cycles.</summary>
    public const float FeedbackStill = 0.94f;

    /// <summary>The history's weight for a pixel moving <see cref="MovingPixels"/> or more per frame.</summary>
    public const float FeedbackMoving = 0.88f;

    /// <summary>
    /// The history's weight on reactive pixels (ADR 0166): water marks the scene alpha (<see cref="BlendMode.AlphaReactive"/>)
    /// because its flow animates without motion vectors of its own; more history would smear the ripples along the flow.
    /// </summary>
    public const float FeedbackReactive = 0.2f;

    /// <summary>Screen motion (pixels per frame) at which the feedback reaches <see cref="FeedbackMoving"/>.</summary>
    public const float MovingPixels = 8f;

    /// <summary>
    /// Relative difference between the history's stored view depth and this pixel's last-frame depth beyond which the
    /// history is dropped as disoccluded.
    /// </summary>
    public const float DepthTolerance = 0.15f;

    /// <summary>
    /// The reconstruction filter's falloff: exp(−2.29 d²) is the Gaussian fit of a Blackman–Harris window Unreal uses;
    /// this is that over a width of 0.75 pixels.
    /// </summary>
    public const float FilterFalloff = -2.29f / (0.75f * 0.75f);

    /// <summary>The history pair in the target pool: linear HDR, alpha = view depth.</summary>
    internal static readonly PostTargetDesc HistoryDesc = new("taa", SceneTextures.ColorFormat);

    // TaaParams flags (Taa.vk.frag).
    internal const uint FlagHistoryValid = 1, FlagDepthRejection = 2;

    private IVulkanContext _ctx = null!;
    private PostHistory<RenderTarget> _history = null!;
    private RenderTarget _first = null!; // the pair's targets: set i reads target i as last frame's
    private Sampler _pointSampler;
    private Sampler _linearSampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[2];
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private ulong _writtenScene;    // the views the sets were written for (rewritten after a resize)
    private ulong _writtenVelocity;
    private bool _setsDirty = true;

    /// <summary>Frames resolved with a valid history since creation (tests).</summary>
    internal long AccumulatedFrames { get; private set; }

    /// <summary>Frames that started a new history (first frame, resize, camera cut; tests).</summary>
    internal long ResetFrames { get; private set; }

    public override PostEffectNeeds Needs => PostEffectNeeds.Velocity | PostEffectNeeds.Jitter;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.AntiAliasing == AntiAliasing.Taa;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        _history = context.Targets.GetHistory(HistoryDesc);
        _first = _history.Previous;
        _pointSampler = CreateSampler(ctx, Filter.Nearest);
        _linearSampler = CreateSampler(ctx, Filter.Linear); // the Catmull–Rom taps are bilinear
        var binding = new DescriptorSetLayoutBinding { DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [binding with { Binding = 0 }, binding with { Binding = 1 }, binding with { Binding = 2 }, binding with { Binding = 3 }], "taa");
        _pool = PipelineBuilder.CreatePool(ctx, 2, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 8 }], "taa");
        for (var i = 0; i < _sets.Length; i++)
            _sets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "taa");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(TaaParams), ShaderStageFlags.FragmentBit, "taa");
        // Both history targets have the same render pass (same format, same attachment): one pipeline serves both.
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _history.Current.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/Taa.vk.frag.spv", [], [], "taa resolve");
    }

    protected override void OnBeginFrame(PostEffectContext context) => _history.Advance(context.FrameNumber);

    /// <summary>After a resize (device idle): the scene's images and the history are new; the sets are rewritten on use.</summary>
    protected override void OnResize(PostEffectContext context) => _setsDirty = true;

    protected override void OnRecord(PostEffectContext context)
    {
        var scene = context.Scene;
        // No motion vectors (a frame without the prepass) or no camera: nothing to resolve; the history lapses.
        if (!scene.HasPrepass || scene.Velocity.Handle == 0 || !context.HasCamera)
            return;

        if (_setsDirty || _writtenScene != scene.Color.Handle || _writtenVelocity != scene.Velocity.Handle)
            WriteSets(scene); // only after creation or a resize: no frame in flight binds these sets

        var cb = context.CommandBuffer;
        var vk = _ctx.Vk;
        var valid = _history.IsValid && context.Camera.HistoryValid;
        if (valid)
        {
            AccumulatedFrames++;
        }
        else
        {
            ResetFrames++;
            // Last frame's target may never have been written (undefined layout): clear it so the set can bind it.
            _history.Previous.Begin(cb, default);
            _history.Previous.End(cb);
        }

        var target = _history.Current;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = _sets[ReferenceEquals(_history.Previous, _first) ? 0 : 1];
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = Params(context.Camera, scene.Extent, context.Exposure, valid);
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(TaaParams), &push);
        PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
        _history.MarkWritten();
        context.CopyToSceneColor(target.GetColor(0).View);
    }

    /// <summary>
    /// The resolve's push block for <paramref name="camera"/> at <paramref name="extent"/>: the jitter in pixels (where
    /// the geometry moved: x right, y down) and NDC, and the two depth rows that give a pixel's view depth now and last
    /// frame from its NDC position and depth.
    /// </summary>
    internal static TaaParams Params(in PostCamera camera, Extent2D extent, float exposure, bool historyValid)
    {
        var inverse = camera.InverseViewProjection;
        var toPrevious = inverse * camera.PreviousViewProjection;
        float width = Math.Max(1u, extent.Width), height = Math.Max(1u, extent.Height);
        return new TaaParams
        {
            DepthCurrent = new Vector4(inverse.M14, inverse.M24, inverse.M34, inverse.M44),
            DepthPrevious = new Vector4(toPrevious.M14, toPrevious.M24, toPrevious.M34, toPrevious.M44),
            JitterPixels = new Vector2(camera.Jitter.X * width * 0.5f, -camera.Jitter.Y * height * 0.5f),
            JitterNdc = camera.Jitter,
            OutputSize = new Vector2(width, height),
            RcpOutputSize = new Vector2(1f / width, 1f / height),
            FeedbackStill = FeedbackStill,
            FeedbackMoving = FeedbackMoving,
            MovingPixels = MovingPixels,
            Exposure = exposure > 0f && float.IsFinite(exposure) ? exposure : IVulkanContext.DefaultExposure,
            DepthTolerance = DepthTolerance,
            Flags = (historyValid ? FlagHistoryValid : 0u) | FlagDepthRejection,
            FilterFalloff = FilterFalloff,
            FeedbackReactive = FeedbackReactive,
        };
    }

    /// <summary>
    /// The view depth of a point from its unjittered NDC position and depth, through <see cref="TaaParams.DepthCurrent"/>
    /// (now) or <see cref="TaaParams.DepthPrevious"/> (last frame): what <c>Taa.vk.frag</c> computes (tests).
    /// </summary>
    internal static (float Now, float Previous) ViewDepths(in TaaParams p, Vector3 ndc)
    {
        var h = new Vector4(ndc, 1f);
        var inverseW = Vector4.Dot(h, p.DepthCurrent);
        return (1f / inverseW, Vector4.Dot(h, p.DepthPrevious) / inverseW);
    }

    private void WriteSets(SceneTextures scene)
    {
        var second = ReferenceEquals(_history.Current, _first) ? _history.Previous : _history.Current;
        WriteSet(_sets[0], scene, _first);
        WriteSet(_sets[1], scene, second);
        _writtenScene = scene.Color.Handle;
        _writtenVelocity = scene.Velocity.Handle;
        _setsDirty = false;
    }

    private void WriteSet(DescriptorSet set, SceneTextures scene, RenderTarget previous)
    {
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo { Sampler = _pointSampler, ImageView = scene.Color, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
        PipelineBuilder.WriteImage(_ctx, set, 1, new DescriptorImageInfo { Sampler = _linearSampler, ImageView = previous.GetColor(0).View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
        PipelineBuilder.WriteImage(_ctx, set, 2, new DescriptorImageInfo { Sampler = _pointSampler, ImageView = scene.Velocity, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
        PipelineBuilder.WriteImage(_ctx, set, 3, new DescriptorImageInfo { Sampler = _pointSampler, ImageView = scene.Depth, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal });
    }

    private static Sampler CreateSampler(IVulkanContext ctx, Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (taa)");
        return sampler;
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle); the pool owns the history.</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _pointSampler, null);
        vk.DestroySampler(_ctx.Device, _linearSampler, null);
    }
}

/// <summary>Taa.vk.frag's push block (96 bytes, scalar offsets as declared).</summary>
internal struct TaaParams
{
    /// <summary>Column 3 of the unjittered inverse view-projection: view depth = 1 / dot((ndc, depth, 1), this).</summary>
    public Vector4 DepthCurrent;

    /// <summary>Column 3 of inverse view-projection × last frame's: last frame's view depth = dot(p, this) / dot(p, DepthCurrent).</summary>
    public Vector4 DepthPrevious;

    /// <summary>This frame's jitter in pixels: where the geometry moved (x right, y down).</summary>
    public Vector2 JitterPixels;

    /// <summary>This frame's jitter in NDC (+Y up).</summary>
    public Vector2 JitterNdc;

    public Vector2 OutputSize;
    public Vector2 RcpOutputSize;
    public float FeedbackStill;
    public float FeedbackMoving;
    public float MovingPixels;
    public float Exposure;
    public float DepthTolerance;
    public uint Flags;

    /// <summary>The reconstruction filter: a sample's weight is exp(FilterFalloff · d²), d its distance in output pixels.</summary>
    public float FilterFalloff;

    /// <summary>The history's weight on fully reactive pixels (the scene alpha 0: water).</summary>
    public float FeedbackReactive;
}
