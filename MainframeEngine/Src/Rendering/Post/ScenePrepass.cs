using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The depth prepass of a view (ADR 0163; the main view, or a post-processed sub-viewport, ADR 0169): opaque and cutout geometry into the <b>scene target's depth</b> and an
/// <c>R16G16_SFLOAT</c> velocity image (screen-UV motion, <see cref="SceneTextures.Velocity"/>), then the sky's velocity
/// (camera rotation only) where nothing was drawn. The scene pass then begins with <see cref="SceneLoadPass"/>, which
/// loads that depth instead of clearing it: the prepassed draws test it (<c>LESS_OR_EQUAL</c>, cutouts <c>EQUAL</c>
/// without <c>discard</c>) and do not write it. Created the first frame a post effect asks for it.
/// </summary>
/// <remarks>
/// Both passes share the scene target's framebuffer layout: <see cref="SceneLoadPass"/> is compatible with
/// <see cref="IVulkanContext.RenderPass"/> (same attachments; only load ops and layouts differ), so every scene pipeline
/// and the scene target's own framebuffer work in it. The depth leaves the prepass in
/// <c>DEPTH_STENCIL_READ_ONLY_OPTIMAL</c>, sampleable by <see cref="PostStage.AfterPrepass"/> effects (SSAO).
/// </remarks>
internal sealed unsafe class ScenePrepass : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly Format _depthFormat;
    private RenderTarget _scene; // whose depth the prepass writes: the main view's, or a post-processed sub-viewport's (ADR 0169)
    private GpuImage _velocity;
    private Framebuffer _framebuffer;
    private readonly PipelineLayout _skyLayout;
    private readonly Pipeline _skyPipeline;
    private bool _disposed;

    public ScenePrepass(IVulkanContext ctx, RenderTarget scene)
    {
        _ctx = ctx;
        _scene = scene;
        _depthFormat = scene.Depth!.Format;
        PrepassRenderPass = CreatePrepassRenderPass();
        SceneLoadPass = CreateSceneLoadPass();
        _velocity = CreateVelocity(scene.Extent);
        _framebuffer = CreateFramebuffer(scene);
        Extent = scene.Extent;

        // The sky's velocity: a fullscreen triangle at the far plane, depth-tested EQUAL against the cleared 1 (only where
        // nothing was drawn), from the frame set's camera.
        _skyLayout = ctx.Frame.CreatePipelineLayout(null, [], "prepass sky");
        _skyPipeline = PipelineBuilder.Create(ctx, new PipelineState { DepthTest = true, DepthWrite = false, DepthCompare = CompareOp.Equal },
            _skyLayout, PrepassRenderPass, "Shaders/Mesh/PrepassSky.vk.vert.spv", "Shaders/Mesh/PrepassSky.vk.frag.spv", [], [], "prepass sky");
    }

    /// <summary>The prepass: velocity (colour 0) + the scene depth. Build prepass pipelines against it.</summary>
    public RenderPass PrepassRenderPass { get; }

    /// <summary>The scene pass after a prepass: colour cleared, depth loaded (compatible with the scene target's pass).</summary>
    public RenderPass SceneLoadPass { get; }

    public Extent2D Extent { get; private set; }

    /// <summary>The velocity image's view (<c>SHADER_READ_ONLY_OPTIMAL</c> after <see cref="End"/>).</summary>
    public ImageView VelocityView => _velocity.View;

    /// <summary>Bumped when the velocity image (and so its view) was recreated.</summary>
    public int Generation { get; private set; }

    /// <summary>After the scene target was resized (device idle): a new velocity image and framebuffer.</summary>
    public void Resize(RenderTarget scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        _velocity.Dispose();
        _scene = scene;
        _velocity = CreateVelocity(scene.Extent);
        _framebuffer = CreateFramebuffer(scene);
        Extent = scene.Extent;
        Generation++;
    }

    /// <summary>Begins the prepass (velocity cleared to 0, depth to 1). No render pass may be active.</summary>
    public void Begin(CommandBuffer cb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clears = stackalloc ClearValue[2];
        clears[0] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = PrepassRenderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D { Extent = Extent },
            ClearValueCount = 2,
            PClearValues = clears,
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &info, SubpassContents.Inline);
    }

    /// <summary>
    /// Draws the sky's velocity (the frame set's view must be current), ends the prepass and records the barriers that
    /// make the velocity and depth readable by fragment shaders and the depth usable by the scene pass. MoltenVK does not
    /// wait on render-pass dependencies between encoders (see <see cref="RenderTarget.End"/>).
    /// </summary>
    public void End(CommandBuffer cb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vk = _ctx.Vk;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _skyPipeline);
        _ctx.Frame.Bind(cb, _skyLayout);
        PipelineBuilder.SetViewport(vk, cb, Extent, flipY: true);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        vk.CmdEndRenderPass(cb);

        var barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _velocity.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        barriers[1] = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            OldLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = SceneDepthImage,
            SubresourceRange = new ImageSubresourceRange(VkHelpers.DepthBarrierAspects(_depthFormat), 0, 1, 0, 1),
        };
        vk.CmdPipelineBarrier(cb,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            0, 0, null, 0, null, 2, barriers);
    }

    private Image SceneDepthImage => _scene.Depth!.Handle;

    private GpuImage CreateVelocity(Extent2D extent) =>
        GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, SceneTextures.VelocityFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit), dedicated: false);

    private Framebuffer CreateFramebuffer(RenderTarget scene)
    {
        var views = stackalloc ImageView[2];
        views[0] = _velocity.View;
        views[1] = scene.Depth!.View;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = PrepassRenderPass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = scene.Extent.Width,
            Height = scene.Extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out var framebuffer).Check("vkCreateFramebuffer (depth prepass)");
        return framebuffer;
    }

    private RenderPass CreatePrepassRenderPass() => CreatePass(
        new AttachmentDescription
        {
            Format = SceneTextures.VelocityFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        },
        AttachmentLoadOp.Clear, ImageLayout.Undefined, "depth prepass");

    // The scene target's pass with the depth loaded: compatible with IVulkanContext.RenderPass (formats, samples and
    // attachment count match), so scene pipelines and the scene framebuffer are used with it unchanged.
    private RenderPass CreateSceneLoadPass() => CreatePass(
        new AttachmentDescription
        {
            Format = VulkanRenderer.SceneColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        },
        AttachmentLoadOp.Load, ImageLayout.DepthStencilReadOnlyOptimal, "scene (prepassed depth)");

    private RenderPass CreatePass(AttachmentDescription color, AttachmentLoadOp depthLoad, ImageLayout depthInitial, string what)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = color;
        attachments[1] = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = depthLoad,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = depthInitial,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal, // sampled afterwards (SSAO, light shafts)
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };

        // Exactly RenderTarget's (render-pass compatibility includes the dependencies): incoming orders the writes after
        // earlier writes and reads (sampling, the previous frame's light shafts and SSAO) of the same images; outgoing
        // makes the writes visible to sampling. The prepass's depth reaches the scene pass's depth test through the
        // incoming one (late fragment tests → early fragment tests) and End's explicit barrier.
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit |
                           PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
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
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out var pass).Check($"vkCreateRenderPass ({what})");
        return pass;
    }

    /// <summary>Destroys everything through the deletion queue.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_skyPipeline));
        deletions.Enqueue(GpuDeletion.Of(_skyLayout));
        deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        deletions.Enqueue(GpuDeletion.Of(PrepassRenderPass));
        deletions.Enqueue(GpuDeletion.Of(SceneLoadPass));
        _velocity.Dispose();
    }
}
