using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// A view's scene copy for refracting water (ADR 0173, docs/design/water.md#scene-textures): after the opaque half of
/// the scene pass, the HDR colour and the linear view depth go into one <c>R16G16B16A16_SFLOAT</c> mip chain (rgb =
/// colour, a = view depth in metres), built with fragment passes, and the scene pass resumes (colour and depth loaded)
/// for the blended surfaces. <see cref="WaterMaterial3D"/> with <see cref="WaterMaterial3D.RefractionEnabled"/> reads
/// it through set 3 (<see cref="WaterSet"/>): refraction (mip 0 and blurrier mips), the true water column, caustics on
/// the bed and screen-space reflections against mip 1 (half resolution).
/// </summary>
/// <remarks>
/// <para>Level 0 is an exact copy of the colour and the depth linearised through the view's projection (<c>Water/SceneCopy</c>);
/// each further level box-filters the colour and keeps the <b>nearest</b> depth of its 2 × 2 (<c>Water/SceneDownsample</c>),
/// so a ray marched against a coarse level never steps through a thin trunk. The sky is <see cref="SkyDepth"/>.</para>
/// <para>One copy per view, shared by the frames in flight (ordered by the passes' dependencies, like the scene target).
/// Created the first frame a view draws refracting water; recreated (through the deletion queue) when the scene target's
/// size or images change. Recording allocates nothing.</para>
/// </remarks>
internal sealed unsafe class WaterSceneTextures : IDisposable
{
    /// <summary>The chain's format: HDR colour, alpha = linear view depth.</summary>
    public const Format ChainFormat = Format.R16G16B16A16Sfloat;

    /// <summary>Levels at most (full resolution down to 1/32).</summary>
    public const int MaxMips = 6;

    /// <summary>The view depth written where nothing was drawn (the sky), in metres (inside half-float range).</summary>
    public const float SkyDepth = 60000f;

    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _waterSetLayout; // set 3 of the water-scene pipelines (MeshRenderer's)
    private readonly RenderPass _chainPass;
    private readonly DescriptorSetLayout _copyLayout;
    private readonly DescriptorSetLayout _downLayout;
    private readonly PipelineLayout _copyPipelineLayout;
    private readonly PipelineLayout _downPipelineLayout;
    private readonly Pipeline _copyPipeline;
    private readonly Pipeline _downPipeline;
    private readonly Sampler _pointSampler;
    private readonly Sampler _linearSampler;
    private readonly ImageView[] _mipViews = new ImageView[MaxMips];
    private readonly Framebuffer[] _framebuffers = new Framebuffer[MaxMips];
    private readonly Extent2D[] _mipExtents = new Extent2D[MaxMips];
    private readonly DescriptorSet[] _downSets = new DescriptorSet[MaxMips];
    private GpuImage? _chain;
    private DescriptorPool _pool;
    private DescriptorSet _copySet;
    private ulong _colorView;
    private ulong _depthView;
    private bool _disposed;

    public WaterSceneTextures(IVulkanContext ctx, DescriptorSetLayout waterSetLayout, Format depthFormat, string name)
    {
        _ctx = ctx;
        _waterSetLayout = waterSetLayout;
        Name = name;
        _chainPass = CreateChainPass(ctx);
        ResumePass = CreateResumePass(ctx, depthFormat);

        var point = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in point, null, out _pointSampler).Check("vkCreateSampler (water scene copy)");
        var linear = point with
        {
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            MaxLod = MaxMips,
        };
        ctx.Vk.CreateSampler(ctx.Device, in linear, null, out _linearSampler).Check("vkCreateSampler (water scene)");

        _copyLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "water scene copy");
        _downLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "water scene downsample");
        // The copy reads the view's frame set (set 0: its inverse projection) to linearise the depth.
        _copyPipelineLayout = ctx.Frame.CreatePipelineLayout(null, [_copyLayout], "water scene copy");
        _downPipelineLayout = PipelineBuilder.CreateLayout(ctx, [_downLayout], 0, 0, "water scene downsample");
        _copyPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _copyPipelineLayout, _chainPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Water/SceneCopy.vk.frag.spv", [], [], "water scene copy");
        _downPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _downPipelineLayout, _chainPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Water/SceneDownsample.vk.frag.spv", [], [], "water scene downsample");
    }

    /// <summary>The view the copy belongs to (debug names).</summary>
    public string Name { get; }

    /// <summary>
    /// The scene pass resumed after the copy: colour and depth loaded (from and back to their sampled layouts), compatible
    /// with the scene target's pass, so every scene pipeline and the target's framebuffer work in it.
    /// </summary>
    public RenderPass ResumePass { get; }

    /// <summary>Set 3 of the water-scene pipelines: the chain (all levels) with a trilinear clamp sampler.</summary>
    public DescriptorSet WaterSet { get; private set; }

    /// <summary>Levels in the chain (1 … <see cref="MaxMips"/>).</summary>
    public int MipCount { get; private set; }

    /// <summary>Level 0's size (the scene target's).</summary>
    public Extent2D Extent { get; private set; }

    /// <summary>Times the chain was (re)created (tests).</summary>
    public int Generation { get; private set; }

    /// <summary>Levels for a <paramref name="width"/> × <paramref name="height"/> scene (at least 1, at most <see cref="MaxMips"/>).</summary>
    public static int MipsFor(uint width, uint height)
    {
        var smallest = Math.Max(1u, Math.Min(width, height));
        var levels = 1;
        while (levels < MaxMips && (smallest >> levels) >= 1)
            levels++;
        return levels;
    }

    /// <summary>
    /// Ends the scene pass of <paramref name="scene"/>, copies its colour and depth into the chain and resumes the pass
    /// (<see cref="ResumePass"/>). Inside the scene pass, with the view's frame set current.
    /// </summary>
    public void SplitScenePass(CommandBuffer cb, RenderTarget scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        scene.End(cb); // explicit barrier: colour and depth readable by fragment shaders
        Record(cb, scene);
        scene.Begin(cb, default, ResumePass);
    }

    /// <summary>Records the copy and the downsamples (no render pass active; <paramref name="scene"/>'s images readable).</summary>
    public void Record(CommandBuffer cb, RenderTarget scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Ensure(scene);
        var vk = _ctx.Vk;

        Begin(cb, 0);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _copyPipeline);
        _ctx.Frame.Bind(cb, _copyPipelineLayout);
        var copySet = _copySet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _copyPipelineLayout, 1, 1, &copySet, 0, null);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        End(cb, 0);

        if (MipCount > 1)
            vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _downPipeline);
        for (var mip = 1; mip < MipCount; mip++)
        {
            Begin(cb, mip);
            var set = _downSets[mip];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _downPipelineLayout, 0, 1, &set, 0, null);
            vk.CmdDraw(cb, 3, 1, 0, 0);
            End(cb, mip);
        }

        // The resumed scene pass writes the colour and tests the depth the copy just read: wait for the reads (MoltenVK
        // does not wait on render-pass dependencies between encoders).
        var barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = SceneBarrier(scene.GetColor(0).Handle, ImageAspectFlags.ColorBit, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        barriers[1] = SceneBarrier(scene.Depth!.Handle, VkHelpers.DepthBarrierAspects(scene.Depth.Format), ImageLayout.DepthStencilReadOnlyOptimal,
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            0, 0, null, 0, null, 2, barriers);
    }

    private static ImageMemoryBarrier SceneBarrier(Image image, ImageAspectFlags aspects, ImageLayout layout, AccessFlags dstAccess) => new()
    {
        SType = StructureType.ImageMemoryBarrier,
        SrcAccessMask = AccessFlags.ShaderReadBit,
        DstAccessMask = dstAccess,
        OldLayout = layout,
        NewLayout = layout,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = image,
        SubresourceRange = new ImageSubresourceRange(aspects, 0, 1, 0, 1),
    };

    private void Begin(CommandBuffer cb, int mip)
    {
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _chainPass,
            Framebuffer = _framebuffers[mip],
            RenderArea = new Rect2D { Extent = _mipExtents[mip] },
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        PipelineBuilder.SetViewport(_ctx.Vk, cb, _mipExtents[mip], flipY: false);
    }

    // Ends a level's pass; the next level (and the water) samples it: an explicit barrier (MoltenVK, see RenderTarget.End).
    private void End(CommandBuffer cb, int mip)
    {
        var vk = _ctx.Vk;
        vk.CmdEndRenderPass(cb);
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _chain!.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, (uint)mip, 1, 0, 1),
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, &barrier);
    }

    // (Re)creates the chain, its views, framebuffers and sets when the scene's size or images changed.
    private void Ensure(RenderTarget scene)
    {
        var color = scene.GetColor(0).View.Handle;
        var depth = scene.Depth!.View.Handle;
        var extent = scene.Extent;
        if (_chain is not null && extent.Width == Extent.Width && extent.Height == Extent.Height && color == _colorView && depth == _depthView)
            return;

        DestroySized();
        Extent = extent;
        _colorView = color;
        _depthView = depth;
        MipCount = MipsFor(extent.Width, extent.Height);
        _chain = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, ChainFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit)
        { MipLevels = (uint)MipCount }, dedicated: false);
        for (var mip = 0; mip < MipCount; mip++)
        {
            _mipExtents[mip] = new Extent2D(Math.Max(1u, extent.Width >> mip), Math.Max(1u, extent.Height >> mip));
            _mipViews[mip] = _chain.CreateView(ImageViewType.Type2D, 0, 1, (uint)mip, 1);
            var view = _mipViews[mip];
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _chainPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = _mipExtents[mip].Width,
                Height = _mipExtents[mip].Height,
                Layers = 1,
            };
            _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _framebuffers[mip]).Check($"vkCreateFramebuffer (water scene copy, {Name})");
        }

        _pool = PipelineBuilder.CreatePool(_ctx, 2 + MaxMips,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 3 + MaxMips }], $"water scene copy ({Name})");
        _copySet = PipelineBuilder.AllocateSet(_ctx, _pool, _copyLayout, "water scene copy");
        PipelineBuilder.WriteImage(_ctx, _copySet, 0, new DescriptorImageInfo
        {
            Sampler = _pointSampler,
            ImageView = scene.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        PipelineBuilder.WriteImage(_ctx, _copySet, 1, new DescriptorImageInfo
        {
            Sampler = _pointSampler,
            ImageView = scene.Depth.View,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
        });
        for (var mip = 1; mip < MipCount; mip++)
        {
            _downSets[mip] = PipelineBuilder.AllocateSet(_ctx, _pool, _downLayout, "water scene downsample");
            PipelineBuilder.WriteImage(_ctx, _downSets[mip], 0, new DescriptorImageInfo
            {
                Sampler = _pointSampler,
                ImageView = _mipViews[mip - 1],
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            });
        }

        WaterSet = PipelineBuilder.AllocateSet(_ctx, _pool, _waterSetLayout, "water scene");
        PipelineBuilder.WriteImage(_ctx, WaterSet, 0, new DescriptorImageInfo
        {
            Sampler = _linearSampler,
            ImageView = _chain.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        Generation++;
    }

    // The size-dependent objects, through the deletion queue (frames in flight may still read them).
    private void DestroySized()
    {
        if (_chain is null)
            return;
        var deletions = _ctx.Deletions;
        for (var mip = 0; mip < MipCount; mip++)
        {
            deletions.Enqueue(GpuDeletion.Of(_framebuffers[mip]));
            _framebuffers[mip] = default;
            _mipViews[mip] = default; // owned by the image
            _downSets[mip] = default;
        }

        deletions.Enqueue(GpuDeletion.Of(_pool));
        _pool = default;
        _copySet = default;
        WaterSet = default;
        _chain.Dispose();
        _chain = null;
    }

    // One colour attachment, overwritten whole (DontCare), left sampleable.
    private static RenderPass CreateChainPass(IVulkanContext ctx)
    {
        var attachment = new AttachmentDescription
        {
            Format = ChainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        ctx.Vk.CreateRenderPass(ctx.Device, in info, null, out var pass).Check("vkCreateRenderPass (water scene copy)");
        return pass;
    }

    // The scene pass with colour and depth loaded. Compatible with RenderTarget's scene passes (render-pass compatibility
    // includes the dependencies, so they are exactly RenderTarget.CreateRenderPass's; only load ops and layouts differ).
    private static RenderPass CreateResumePass(IVulkanContext ctx, Format depthFormat)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = VulkanRenderer.SceneColorFormat,
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
            Format = depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal,
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
        ctx.Vk.CreateRenderPass(ctx.Device, in info, null, out var pass).Check("vkCreateRenderPass (scene, resumed after the water copy)");
        return pass;
    }

    /// <summary>Destroys everything through the deletion queue.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DestroySized();
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_copyPipeline));
        deletions.Enqueue(GpuDeletion.Of(_downPipeline));
        deletions.Enqueue(GpuDeletion.Of(_copyPipelineLayout));
        deletions.Enqueue(GpuDeletion.Of(_downPipelineLayout));
        deletions.Enqueue(GpuDeletion.Of(_copyLayout));
        deletions.Enqueue(GpuDeletion.Of(_downLayout));
        deletions.Enqueue(GpuDeletion.Of(_chainPass));
        deletions.Enqueue(GpuDeletion.Of(ResumePass));
        deletions.Enqueue(GpuDeletion.Of(_pointSampler));
        deletions.Enqueue(GpuDeletion.Of(_linearSampler));
    }
}
