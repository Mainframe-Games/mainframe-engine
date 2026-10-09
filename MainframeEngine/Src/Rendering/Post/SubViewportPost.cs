using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The post-processing of one <see cref="SubViewport"/> (ADR 0169; <see cref="SubViewport.PostProcessing"/>): its own
/// <see cref="PostProcessStack"/> with an instance of every built-in effect (so TAA's history and auto exposure's adapted
/// luminance are this view's), a target pool, a depth prepass on the view's HDR target, the post tonemap pass and the
/// <see cref="PostStage.AfterTonemap"/> ping-pong, the last of which writes the view's LDR image (the
/// <see cref="SubViewport.ColorTarget"/> the UI shows) instead of the swapchain. Then the overlay pass draws the debug
/// visuals (<see cref="Grid3D"/>, debug and overlay lines) into that image after the effects, against the scene depth.
/// </summary>
/// <remarks>
/// <para>The main view's equivalent lives in <c>VulkanRenderer.Presentation</c>; this class repeats its tonemap step for a
/// view that is not the swapchain. The built-in list is <see cref="VulkanRenderer"/>'s <c>RegisterPostEffects</c>
/// (a render test checks they match).</para>
/// <para>The size is fixed: a resized view gets a new instance and this one is retired through
/// <see cref="DeletionQueue.EnqueueDispose"/> (its effects destroy their objects directly, which needs the frames that
/// used them finished). Allocation-free per frame once created.</para>
/// </remarks>
internal sealed unsafe class SubViewportPost : IPostOutput, IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly SubViewportTargets _targets;
    private readonly PostProcessStack _stack = new();
    private readonly AutoExposure _autoExposure = new();
    private readonly GlowEffect _glow;
    private readonly LightShafts _lightShafts = new();
    private readonly PostTargetPool<RenderTarget> _pool;
    private readonly PostEffectContext _context;
    private readonly Sampler _pointSampler;
    private readonly Sampler _linearSampler;
    private readonly RenderTarget?[] _ldr = new RenderTarget?[2];
    private ScenePrepass? _prepass;
    private SceneColorCopy? _colorCopy;
    private PostTonemapPass? _tonemap;
    private int _ldrWrite;
    private ulong _prepassFrame;
    private RenderPass _overlayPass;
    private Framebuffer _overlayFramebuffer;
    private bool _disposed;

    public SubViewportPost(IVulkanContext ctx, SubViewportTargets targets)
    {
        _ctx = ctx;
        _targets = targets;
        Extent = targets.Hdr!.Extent;
        _glow = new GlowEffect(_autoExposure);
        RegisterEffects();
        _pointSampler = GpuTexture.CreateSampler(ctx, TextureSampling.NearestClamp, 1);
        _linearSampler = GpuTexture.CreateSampler(ctx, TextureSampling.LinearClamp, 1);
        _pool = new PostTargetPool<RenderTarget>(CreateTarget, Extent);
        _context = new PostEffectContext(ctx, _pool, this);
        var scene = _context.Scene;
        scene.Extent = Extent;
        scene.Color = Scene.GetColor(0).View;
        scene.Depth = Scene.Depth!.View;
        scene.DepthFormat = Scene.Depth.Format;
        scene.PointSampler = _pointSampler;
        scene.LinearSampler = _linearSampler;
    }

    /// <summary>The view's size: a resized view gets a new instance.</summary>
    public Extent2D Extent { get; }

    /// <summary>The view's effects (tests).</summary>
    public PostProcessStack Effects => _stack;

    /// <summary>What the view's effects decide on this frame (set by the render server before the frame starts).</summary>
    public PostEffectSettings Settings { get; set; }

    /// <summary>What the enabled effects need this frame (from <see cref="Settings"/>).</summary>
    public PostEffectNeeds Needs { get; set; }

    /// <summary>Frames of this view that ran the post tonemap pass (tests).</summary>
    public long PostTonemapFrames { get; private set; }

    private RenderTarget Scene => _targets.Hdr!;
    private RenderTarget Output => _targets.Ldr!;

    // VulkanRenderer.RegisterPostEffects' list, one instance per view.
    private void RegisterEffects()
    {
        var ssao = new SsaoEffect();
        _stack.Add(ssao);
        _stack.Add(_autoExposure);
        _stack.Add(_glow);
        _stack.Add(_lightShafts);
        _stack.Add(new TaaEffect());
        _stack.Add(new TaaSharpenEffect());
        _stack.Add(new ContactShadows());
        _stack.Add(new DepthOfFieldEffect());
        _stack.Add(new FxaaEffect());
        _stack.Add(new ColorGradeEffect());
        _stack.Add(new VelocityDebugView(ssao));
    }

    private RenderTarget CreateTarget(PostTargetDesc desc, Extent2D extent) =>
        new(_ctx, new RenderTargetDesc(desc.Name, [RenderTargetAttachment.Sampled(desc.Format)], null), extent);

    /// <summary>The scene pass after this frame's prepass loads its depth (null: no prepass this frame).</summary>
    public RenderPass? SceneLoadPass => _prepass is not null && _prepassFrame == _ctx.FrameNumber ? _prepass.SceneLoadPass : null;

    /// <summary>
    /// Starts the view's frame (no render pass active, before its frame set is first bound): clears its ambient occlusion
    /// and contact shadows, then every enabled effect's <c>OnBeginFrame</c> (SSAO binds its output for
    /// <paramref name="view"/> here). <paramref name="prepass"/>: this frame draws the prepass.
    /// </summary>
    public void BeginFrame(CommandBuffer cb, int view, bool prepass, ICamera? camera, in LightShaftsSun sun)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (prepass)
            _prepass ??= new ScenePrepass(_ctx, Scene);
        var frame = _ctx.Frame;
        frame.PostView = view;
        try
        {
            frame.ClearAmbientOcclusion();
            frame.ClearContactShadows();
            var context = Prepare(cb, view, camera, sun);
            context.Scene.HasPrepass = prepass; // this frame's (the prepass has not run yet)
            context.Scene.Velocity = prepass ? _prepass!.VelocityView : default;
            _stack.BeginFrame(context);
        }
        finally
        {
            frame.PostView = 0;
        }
    }

    /// <summary>Begins the depth prepass on the view's depth (the view's frame set written).</summary>
    public void BeginPrepass(CommandBuffer cb) => _prepass!.Begin(cb);

    /// <summary>Draws the sky's velocity, ends the prepass and records the <see cref="PostStage.AfterPrepass"/> stage.</summary>
    public void EndPrepass(CommandBuffer cb, int view, ICamera camera, in LightShaftsSun sun)
    {
        _prepass!.End(cb);
        _prepassFrame = _ctx.FrameNumber;
        _stack.Record(PostStage.AfterPrepass, Prepare(cb, view, camera, sun));
    }

    /// <summary>
    /// After the scene pass (no render pass active): the <see cref="PostStage.BeforeTonemap"/> stage, the tonemap into the
    /// view's LDR image (or the stage's first LDR image) and the <see cref="PostStage.AfterTonemap"/> stage, the last
    /// effect writing the view's image. The image is readable by fragment shaders and transfers afterwards.
    /// </summary>
    public void Record(CommandBuffer cb, int view, ICamera? camera, in LightShaftsSun sun, SubViewportCompositor compositor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = Settings;
        var post = settings.World;
        var context = Prepare(cb, view, camera, sun);
        _stack.Record(PostStage.BeforeTonemap, context);

        var usePost = settings.PostTonemap;
        var afterTonemap = _stack.CountEnabled(PostStage.AfterTonemap, settings) > 0;
        if (!usePost && !afterTonemap)
        {
            compositor.Tonemap(cb, _targets, keepAlpha: false); // exactly a view without post-processing
            return;
        }

        var target = afterTonemap ? EnsureLdr(0) : Output;
        target.Begin(cb, default);
        if (usePost)
        {
            _tonemap ??= new PostTonemapPass(_ctx, Output.RenderPass);
            var shafts = _lightShafts.DrawnFrame == _ctx.FrameNumber ? post.LightShaftsIntensity * sun.Fade : 0f;
            _tonemap.Record(cb, Scene, _glow, _autoExposure, _lightShafts, post, context.Exposure, shafts, target.Extent);
            PostTonemapFrames++;
        }
        else
        {
            compositor.DrawTonemap(cb, _targets, target.Extent, keepAlpha: false);
        }

        target.End(cb);
        if (!afterTonemap)
            return;

        context.Scene.Ldr = target.GetColor(0).View;
        _ldrWrite = 1;
        _stack.Record(PostStage.AfterTonemap, context); // the last effect begins the output pass and leaves it open
        Output.End(cb);
    }

    // Fills the context for a stage of this frame (no allocation).
    private PostEffectContext Prepare(CommandBuffer cb, int view, ICamera? camera, in LightShaftsSun sun)
    {
        var context = _context;
        var settings = Settings;
        var frame = _ctx.Frame;
        context.CommandBuffer = cb;
        context.Settings = settings;
        context.FrameNumber = _ctx.FrameNumber;
        context.DeltaTime = _ctx.FrameDeltaTime;
        context.Time = frame.Time;
        context.Exposure = settings.World.ExposureFor(_ctx.Exposure);
        context.JitterIndex = frame.ViewJitterIndex(view);
        context.LightShaftsSun = sun;
        var scene = context.Scene;
        scene.HasPrepass = _prepass is not null && _prepassFrame == _ctx.FrameNumber;
        scene.Velocity = scene.HasPrepass ? _prepass!.VelocityView : default;

        context.HasCamera = camera is not null;
        if (camera is null)
        {
            context.Camera = default;
            return context;
        }

        var viewMatrix = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix;
        ref readonly var history = ref frame.History(view);
        var current = history.Frame == _ctx.FrameNumber;
        var (near, far) = FrameData.ClipPlanes(projection);
        context.Camera = new PostCamera(viewMatrix, projection,
            TemporalJitter.Apply(projection, current ? history.Jitter : default),
            viewMatrix * projection,
            current ? history.PreviousViewProjection : viewMatrix * projection,
            current ? history.Jitter : default,
            current ? history.PreviousJitter : default,
            current && history.HistoryValid,
            camera.Position, near, far);
        return context;
    }

    // ── The overlay pass (debug visuals after post) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The overlay pass's render pass: the view's LDR image (loaded) and the scene depth (loaded, read-only). Build the
    /// debug visuals' after-post pipelines against it.
    /// </summary>
    public RenderPass OverlayRenderPass => _overlayPass.Handle != 0 ? _overlayPass : _overlayPass = CreateOverlayPass();

    /// <summary>Begins the overlay pass (no render pass active; after <see cref="Record"/>).</summary>
    public void BeginOverlay(CommandBuffer cb)
    {
        var pass = OverlayRenderPass;
        if (_overlayFramebuffer.Handle == 0)
        {
            var views = stackalloc ImageView[2];
            views[0] = Output.GetColor(0).View;
            views[1] = Scene.Depth!.View;
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = pass,
                AttachmentCount = 2,
                PAttachments = views,
                Width = Extent.Width,
                Height = Extent.Height,
                Layers = 1,
            };
            _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _overlayFramebuffer).Check("vkCreateFramebuffer (sub-viewport overlay)");
        }

        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = _overlayFramebuffer,
            RenderArea = new Rect2D { Extent = Extent },
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
    }

    /// <summary>Ends the overlay pass with the barrier that makes the image readable (the UI, a capture copy).</summary>
    public void EndOverlay(CommandBuffer cb)
    {
        var vk = _ctx.Vk;
        vk.CmdEndRenderPass(cb);
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Output.GetColor(0).Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &barrier);
    }

    private RenderPass CreateOverlayPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = SceneTextures.LdrFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ShaderReadOnlyOptimal,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = Scene.Depth!.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilReadOnlyOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        // Incoming: after the post passes wrote the image (and sampled the depth); outgoing: the writes are visible to
        // sampling and copies (EndOverlay records the same as a barrier, for MoltenVK).
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out var pass).Check("vkCreateRenderPass (sub-viewport overlay)");
        return pass;
    }

    // ── IPostOutput ──────────────────────────────────────────────────────────────────────────────────────────────

    RenderPass IPostOutput.PresentPass => Output.RenderPass;

    bool IPostOutput.PresentEncodesSrgb => false; // a UNORM image holding display values

    void IPostOutput.BeginPresentPass(CommandBuffer cb) => Output.Begin(cb, default);

    RenderPass IPostOutput.LdrPass => EnsureLdr(0).RenderPass;

    void IPostOutput.BeginLdrPass(CommandBuffer cb) => EnsureLdr(_ldrWrite).Begin(cb, default);

    void IPostOutput.EndLdrPass(CommandBuffer cb)
    {
        var target = _ldr[_ldrWrite]!;
        target.End(cb);
        _context.Scene.Ldr = target.GetColor(0).View;
        _ldrWrite = 1 - _ldrWrite;
    }

    void IPostOutput.CopyToSceneColor(CommandBuffer cb, ImageView source)
    {
        _colorCopy ??= new SceneColorCopy(_ctx, Scene);
        _colorCopy.Record(cb, Scene, source);
    }

    private RenderTarget EnsureLdr(int index) =>
        _ldr[index] ??= new RenderTarget(_ctx,
            new RenderTargetDesc($"{_targets.Name} post ldr {index}", [RenderTargetAttachment.Sampled(SceneTextures.LdrFormat)], null), Extent);

    /// <summary>Destroys everything (the frames that used it have finished: see the remarks).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stack.Dispose();
        _pool.Dispose();
        _prepass?.Dispose();
        _colorCopy?.Dispose();
        _tonemap?.Dispose();
        foreach (var ldr in _ldr)
            ldr?.Dispose();
        var deletions = _ctx.Deletions;
        if (_overlayFramebuffer.Handle != 0)
            deletions.Enqueue(GpuDeletion.Of(_overlayFramebuffer));
        if (_overlayPass.Handle != 0)
            deletions.Enqueue(GpuDeletion.Of(_overlayPass));
        deletions.Enqueue(GpuDeletion.Of(_pointSampler));
        deletions.Enqueue(GpuDeletion.Of(_linearSampler));
    }
}

/// <summary>
/// The post tonemap pass of a <see cref="SubViewportPost"/> (ADR 0124's <c>TonemapPost</c>, as the main view records it in
/// <c>VulkanRenderer.BeginOverlayPass</c>): the HDR scene with glow, auto exposure and light shafts composited, into an
/// LDR image (always shader-encoded sRGB). Two descriptor sets: one with a placeholder for the shafts, one written when
/// they exist; neither is rewritten afterwards.
/// </summary>
internal sealed unsafe class PostTonemapPass : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private readonly Sampler _sceneSampler;
    private DescriptorSet _set;
    private DescriptorSet _shaftsSet;

    public PostTonemapPass(IVulkanContext ctx, RenderPass ldrPass)
    {
        _ctx = ctx;
        _sceneSampler = GpuTexture.CreateSampler(ctx, TextureSampling.NearestClamp, 1);
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = GlowEffect.LevelCount, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "sub-viewport post tonemap");
        _pool = PipelineBuilder.CreatePool(ctx, 2,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * (3 + GlowEffect.LevelCount) }], "sub-viewport post tonemap");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(PostPush), ShaderStageFlags.FragmentBit, "sub-viewport post tonemap");
        // Every LDR target (the view's image, the AfterTonemap ping-pong) has the same format: one compatible pipeline.
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, ldrPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/TonemapPost.vk.frag.spv", [], [], "sub-viewport post tonemap");
    }

    /// <summary>
    /// Draws the tonemap into the open pass of an <paramref name="extent"/>-sized LDR target. Glow and auto exposure ran
    /// this frame (they run whenever the post tonemap does); <paramref name="shafts"/> &gt; 0 adds the light shafts.
    /// </summary>
    public void Record(CommandBuffer cb, RenderTarget scene, GlowEffect glow, AutoExposure autoExposure, LightShafts lightShafts,
        in PostProcessSettings post, float exposure, float shafts, Extent2D extent)
    {
        if (_set.Handle == 0)
        {
            // Binding 3 needs an image in shader-read layout even when no shafts are drawn: the glow's smallest level.
            _set = PipelineBuilder.AllocateSet(_ctx, _pool, _setLayout, "sub-viewport post tonemap");
            WriteSet(_set, scene, glow, autoExposure, new DescriptorImageInfo
            {
                Sampler = glow.Sampler,
                ImageView = glow.LevelView(GlowEffect.LevelCount - 1),
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            });
        }

        if (lightShafts.IsCreated && _shaftsSet.Handle == 0)
        {
            _shaftsSet = PipelineBuilder.AllocateSet(_ctx, _pool, _setLayout, "sub-viewport post tonemap (light shafts)");
            WriteSet(_shaftsSet, scene, glow, autoExposure, new DescriptorImageInfo
            {
                Sampler = lightShafts.Sampler,
                ImageView = lightShafts.OutputView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            });
        }

        var vk = _ctx.Vk;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = shafts > 0f && _shaftsSet.Handle != 0 ? _shaftsSet : _set;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new PostPush
        {
            Exposure = exposure,
            EncodeSrgb = 1u,
            Tonemapper = (uint)post.Tonemapper,
            GlowMode = (uint)post.GlowBlendMode,
            GlowEnabled = post.GlowMaxLevel >= 0 ? 1u : 0u,
            GlowIntensity = post.GlowBlendMode == GlowBlendMode.Mix ? post.GlowMix : post.GlowIntensity,
            White = post.GlowWhite,
            WhiteTonemapped = post.Tonemapper == Tonemapper.Filmic ? post.FilmicWhiteTonemapped : post.GodotAcesWhiteTonemapped,
            AutoExposure = post.AutoExposureEnabled ? 1u : 0u,
            AutoExposureScale = post.AutoExposureScale,
            Shafts = _shaftsSet.Handle != 0 ? shafts : 0f,
        };
        post.GetGlowWeights(new Span<float>(push.GlowWeights, GlowEffect.LevelCount));
        if (post.GlowQuality == GlowQuality.High)
            GlowEffect.CombinedWeights(new Span<float>(push.GlowWeights, GlowEffect.LevelCount)); // level 0 holds the sum
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PostPush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    private void WriteSet(DescriptorSet set, RenderTarget scene, GlowEffect glow, AutoExposure autoExposure, in DescriptorImageInfo shafts)
    {
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
        {
            Sampler = _sceneSampler,
            ImageView = scene.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        var levels = stackalloc DescriptorImageInfo[GlowEffect.LevelCount];
        for (var k = 0; k < GlowEffect.LevelCount; k++)
            levels[k] = new DescriptorImageInfo { Sampler = glow.Sampler, ImageView = glow.LevelView(k), ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorCount = GlowEffect.LevelCount,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = levels,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
        PipelineBuilder.WriteImage(_ctx, set, 2, autoExposure.AdaptedDescriptor);
        PipelineBuilder.WriteImage(_ctx, set, 3, shafts);
    }

    // TonemapPost.vk.frag's push block (std430): the main view's PostPush in VulkanRenderer.Presentation.
    private struct PostPush
    {
        public float Exposure;
        public uint EncodeSrgb;
        public uint Tonemapper;
        public uint GlowMode;
        public uint GlowEnabled;
        public float GlowIntensity;
        public float White;
        public float WhiteTonemapped;
        public fixed float GlowWeights[GlowEffect.LevelCount];
        public uint AutoExposure;
        public float AutoExposureScale;
        public float Shafts;
    }

    public void Dispose()
    {
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(_setLayout));
        deletions.Enqueue(GpuDeletion.Of(_sceneSampler));
    }
}
