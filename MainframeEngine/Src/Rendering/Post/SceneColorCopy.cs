using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// <see cref="PostEffectContext.CopyToSceneColor"/> (ADR 0163): a fullscreen pass that overwrites the HDR scene colour
/// with another scene-sized image (TAA's resolved history), so the effects after it and the tonemap — whose descriptor
/// sets bind the scene colour — read the new image. One descriptor set per source image (a ping-pong history has two),
/// written once; created on first use, allocation-free afterwards.
/// </summary>
internal sealed unsafe class SceneColorCopy : IDisposable
{
    private const int MaxSources = 4;

    private readonly IVulkanContext _ctx;
    private readonly RenderPass _renderPass;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorSet[] _sets = new DescriptorSet[MaxSources];
    private readonly ulong[] _sources = new ulong[MaxSources];
    private Framebuffer _framebuffer;
    private Extent2D _extent;
    private bool _disposed;

    public SceneColorCopy(IVulkanContext ctx, RenderTarget scene)
    {
        _ctx = ctx;
        _renderPass = CreateRenderPass(ctx);
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (scene colour copy)");
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "scene colour copy");
        _pool = PipelineBuilder.CreatePool(ctx, MaxSources,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = MaxSources }], "scene colour copy");
        for (var i = 0; i < MaxSources; i++)
            _sets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "scene colour copy");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], 0, 0, "scene colour copy");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _renderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/CopyColor.vk.frag.spv", [], [], "scene colour copy");
        Resize(scene);
    }

    /// <summary>After the scene target was resized (device idle): a framebuffer over the new image; sources are forgotten.</summary>
    public void Resize(RenderTarget scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_framebuffer.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        Array.Clear(_sources);
        _extent = scene.Extent;
        var view = scene.GetColor(0).View;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = _extent.Width,
            Height = _extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _framebuffer).Check("vkCreateFramebuffer (scene colour copy)");
    }

    /// <summary>
    /// Copies <paramref name="source"/> (scene-sized, <c>SHADER_READ_ONLY_OPTIMAL</c>) into <paramref name="scene"/>'s
    /// colour. No render pass may be active; the scene colour is readable by fragment shaders afterwards.
    /// </summary>
    public void Record(CommandBuffer cb, RenderTarget scene, ImageView source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vk = _ctx.Vk;
        var image = scene.GetColor(0).Handle;

        // Earlier passes this frame sampled the scene colour (the effect producing the source did): writes wait for them.
        Barrier(vk, cb, image, AccessFlags.ShaderReadBit, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ColorAttachmentOutputBit);
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D { Extent = _extent },
        };
        vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = GetSet(source);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        PipelineBuilder.SetViewport(vk, cb, _extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        vk.CmdEndRenderPass(cb);
        // MoltenVK does not wait on the outgoing dependency between encoders (RenderTarget.End): an explicit barrier.
        Barrier(vk, cb, image, AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit);
    }

    private DescriptorSet GetSet(ImageView source)
    {
        for (var i = 0; i < MaxSources; i++)
            if (_sources[i] == source.Handle)
                return _sets[i];
        for (var i = 0; i < MaxSources; i++)
        {
            if (_sources[i] != 0)
                continue;
            _sources[i] = source.Handle;
            PipelineBuilder.WriteImage(_ctx, _sets[i], 0, new DescriptorImageInfo
            {
                Sampler = _sampler,
                ImageView = source,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            });
            return _sets[i];
        }

        throw new InvalidOperationException($"More than {MaxSources} images were copied into the scene colour.");
    }

    private static void Barrier(Vk vk, CommandBuffer cb, Image image, AccessFlags src, AccessFlags dst,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = src,
            DstAccessMask = dst,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    // The scene colour, overwritten whole (DontCare), from and back to SHADER_READ_ONLY_OPTIMAL.
    private static RenderPass CreateRenderPass(IVulkanContext ctx)
    {
        var attachment = new AttachmentDescription
        {
            Format = VulkanRenderer.SceneColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ShaderReadOnlyOptimal,
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
        ctx.Vk.CreateRenderPass(ctx.Device, in info, null, out var pass).Check("vkCreateRenderPass (scene colour copy)");
        return pass;
    }

    /// <summary>Destroys everything through the deletion queue.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(_setLayout));
        deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        deletions.Enqueue(GpuDeletion.Of(_renderPass));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_sampler));
    }
}
