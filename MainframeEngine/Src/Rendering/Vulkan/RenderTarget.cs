using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>One colour attachment of a <see cref="RenderTarget"/>.</summary>
/// <param name="Format">Image format (<c>R16G16B16A16_SFLOAT</c> HDR colour, <c>R32_UINT</c> object IDs, ...).</param>
/// <param name="FinalLayout">Layout after the pass: <c>SHADER_READ_ONLY_OPTIMAL</c> to sample it, <c>TRANSFER_SRC_OPTIMAL</c> to read it back.</param>
/// <param name="Usage">Usage besides colour attachment (sampled, transfer source, ...).</param>
public readonly record struct RenderTargetAttachment(Format Format, ImageLayout FinalLayout, ImageUsageFlags Usage)
{
    /// <summary>A colour attachment sampled by a later pass (post-processing, editor viewport).</summary>
    public static RenderTargetAttachment Sampled(Format format) =>
        new(format, ImageLayout.ShaderReadOnlyOptimal, ImageUsageFlags.SampledBit);

    /// <summary>A colour attachment copied to a buffer after the pass (object-ID picking).</summary>
    public static RenderTargetAttachment Readback(Format format) =>
        new(format, ImageLayout.TransferSrcOptimal, ImageUsageFlags.TransferSrcBit);
}

/// <summary>Creation parameters for a <see cref="RenderTarget"/>.</summary>
/// <param name="Name">For debug output and error messages.</param>
/// <param name="ColorAttachments">Colour attachments in location order (fragment output <c>location = i</c>).</param>
/// <param name="DepthFormat">Depth attachment format, or null for none.</param>
/// <param name="SampleDepth">Keep depth after the pass and make it sampleable (otherwise it is discarded).</param>
public sealed record RenderTargetDesc(
    string Name,
    IReadOnlyList<RenderTargetAttachment> ColorAttachments,
    Format? DepthFormat,
    bool SampleDepth = false);

/// <summary>
/// An offscreen render target: colour attachments (any formats, e.g. HDR colour + an <c>R32_UINT</c> object-ID
/// buffer for editor picking) plus an optional depth attachment, with its render pass and framebuffer. Every
/// attachment is cleared at the start of the pass. <see cref="Resize"/> recreates the images and framebuffer but
/// keeps the render pass, so pipelines built against <see cref="RenderPass"/> stay valid.
/// </summary>
/// <remarks>
/// Synchronisation is in the render pass: the incoming dependency orders this frame's attachment writes after the
/// previous frame's reads (sampling, copies) of the same images; the outgoing one makes the writes visible to
/// fragment-shader sampling and transfers. End the pass with <see cref="End"/>, which also records explicit
/// barriers for those reads (MoltenVK does not honour the outgoing dependency between encoders), so a following pass
/// or copy can read <see cref="GetColor"/> directly. The renderer's HDR scene target is one of these
/// (<see cref="IVulkanContext.SceneTarget"/>).
/// </remarks>
public sealed unsafe class RenderTarget : IDisposable, IPostTarget
{
    private readonly IVulkanContext _ctx;
    private readonly GpuImage[] _colors;
    private GpuImage? _depth;
    private Framebuffer _framebuffer;
    private bool _disposed;

    public RenderTarget(IVulkanContext ctx, RenderTargetDesc desc, Extent2D extent)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(desc);
        if (desc.ColorAttachments.Count == 0 && desc.DepthFormat is null)
            throw new ArgumentException("A render target needs at least one attachment.", nameof(desc));

        _ctx = ctx;
        Description = desc;
        _colors = new GpuImage[desc.ColorAttachments.Count];
        RenderPass = CreateRenderPass(ctx, desc);
        CreateImages(extent);
    }

    public RenderTargetDesc Description { get; }

    /// <summary>The render pass (stable across <see cref="Resize"/>); build this target's pipelines against it.</summary>
    public RenderPass RenderPass { get; }

    public Framebuffer Framebuffer => _framebuffer;
    public Extent2D Extent { get; private set; }
    public int ColorCount => _colors.Length;

    /// <summary>Colour attachment <paramref name="index"/> (its default view covers the image).</summary>
    public GpuImage GetColor(int index) => _colors[index];

    /// <summary>The depth attachment, or null.</summary>
    public GpuImage? Depth => _depth;

    /// <summary>
    /// Recreates the attachments at <paramref name="extent"/> (no-op when unchanged). The old images go through
    /// the deletion queue; descriptor sets that sample them must be rewritten by the caller.
    /// </summary>
    public bool Resize(Extent2D extent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (extent.Width == Extent.Width && extent.Height == Extent.Height)
            return false;
        DestroyImages();
        CreateImages(extent);
        return true;
    }

    /// <summary>
    /// Begins the render pass. <paramref name="clearValues"/> holds one value per colour attachment then one for
    /// depth (missing entries clear to zero / depth 1).
    /// </summary>
    public void Begin(CommandBuffer cb, ReadOnlySpan<ClearValue> clearValues) => Begin(cb, clearValues, RenderPass);

    /// <summary>
    /// <see cref="Begin(CommandBuffer, ReadOnlySpan{ClearValue})"/> with <paramref name="compatiblePass"/>, a render pass
    /// compatible with <see cref="RenderPass"/> (same attachments; other load ops or layouts): the scene pass after a depth
    /// prepass loads the depth instead of clearing it (ADR 0163).
    /// </summary>
    internal void Begin(CommandBuffer cb, ReadOnlySpan<ClearValue> clearValues, RenderPass compatiblePass)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _colors.Length + (_depth is null ? 0 : 1);
        var clears = stackalloc ClearValue[count];
        for (var i = 0; i < count; i++)
        {
            clears[i] = i < clearValues.Length
                ? clearValues[i]
                : i < _colors.Length ? default : new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        }

        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = compatiblePass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D { Extent = Extent },
            ClearValueCount = (uint)count,
            PClearValues = clears,
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &info, SubpassContents.Inline);
    }

    /// <summary>
    /// Ends the render pass begun by <see cref="Begin"/> and records an explicit barrier per kept attachment, from its
    /// writes to the reads its final layout is for: fragment-shader sampling (<c>SHADER_READ_ONLY_OPTIMAL</c>, a sampled
    /// depth) or transfers (<c>TRANSFER_SRC_OPTIMAL</c>). The outgoing subpass dependency says the same, but MoltenVK
    /// does not wait on it between encoders for these sub-allocated (heap-placed) images: a following pass or copy can
    /// read tiles that are not stored yet (black tiles in the tonemapped scene, stale object ids). A barrier is honoured
    /// by every driver and costs nothing measurable.
    /// </summary>
    public void End(CommandBuffer cb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vk = _ctx.Vk;
        vk.CmdEndRenderPass(cb);

        var barriers = stackalloc ImageMemoryBarrier[_colors.Length + 1];
        var count = 0;
        PipelineStageFlags srcStages = 0, dstStages = 0;
        for (var i = 0; i < _colors.Length; i++)
        {
            var layout = Description.ColorAttachments[i].FinalLayout;
            if (!ReadAfterPass(layout, out var dstAccess, out var dstStage))
                continue;
            barriers[count++] = AfterPassBarrier(_colors[i].Handle, ImageAspectFlags.ColorBit, layout,
                AccessFlags.ColorAttachmentWriteBit, dstAccess);
            srcStages |= PipelineStageFlags.ColorAttachmentOutputBit;
            dstStages |= dstStage;
        }

        if (_depth is not null && Description.SampleDepth)
        {
            barriers[count++] = AfterPassBarrier(_depth.Handle, VkHelpers.DepthBarrierAspects(_depth.Format),
                ImageLayout.DepthStencilReadOnlyOptimal, AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit);
            srcStages |= PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            dstStages |= PipelineStageFlags.FragmentShaderBit;
        }

        if (count > 0)
            vk.CmdPipelineBarrier(cb, srcStages, dstStages, 0, 0, null, 0, null, (uint)count, barriers);
    }

    private static bool ReadAfterPass(ImageLayout finalLayout, out AccessFlags access, out PipelineStageFlags stage)
    {
        switch (finalLayout)
        {
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.FragmentShaderBit;
                return true;
            case ImageLayout.TransferSrcOptimal:
                access = AccessFlags.TransferReadBit;
                stage = PipelineStageFlags.TransferBit;
                return true;
            default:
                access = 0;
                stage = 0;
                return false;
        }
    }

    private static ImageMemoryBarrier AfterPassBarrier(Image image, ImageAspectFlags aspects, ImageLayout layout,
        AccessFlags srcAccess, AccessFlags dstAccess) => new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = layout, // the render pass already transitioned it
            NewLayout = layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspects, 0, 1, 0, 1),
        };

    private void CreateImages(Extent2D extent)
    {
        ArgumentOutOfRangeException.ThrowIfZero(extent.Width);
        ArgumentOutOfRangeException.ThrowIfZero(extent.Height);
        Extent = extent;

        var views = stackalloc ImageView[_colors.Length + 1];
        var count = 0;
        for (var i = 0; i < _colors.Length; i++)
        {
            var attachment = Description.ColorAttachments[i];
            _colors[i] = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, attachment.Format,
                ImageUsageFlags.ColorAttachmentBit | attachment.Usage), dedicated: false);
            views[count++] = _colors[i].View;
        }

        if (Description.DepthFormat is { } depthFormat)
        {
            var usage = ImageUsageFlags.DepthStencilAttachmentBit | (Description.SampleDepth ? ImageUsageFlags.SampledBit : 0);
            _depth = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, depthFormat, usage));
            views[count++] = _depth.View;
        }

        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = RenderPass,
            AttachmentCount = (uint)count,
            PAttachments = views,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _framebuffer).Check($"vkCreateFramebuffer ({Description.Name})");
    }

    private void DestroyImages()
    {
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        _framebuffer = default;
        foreach (var image in _colors)
            image?.Dispose();
        _depth?.Dispose();
        _depth = null;
    }

    internal static RenderPass CreateRenderPass(IVulkanContext ctx, RenderTargetDesc desc)
    {
        var count = desc.ColorAttachments.Count + (desc.DepthFormat is null ? 0 : 1);
        var attachments = stackalloc AttachmentDescription[count];
        var colorRefs = stackalloc AttachmentReference[Math.Max(1, desc.ColorAttachments.Count)];
        for (var i = 0; i < desc.ColorAttachments.Count; i++)
        {
            var a = desc.ColorAttachments[i];
            attachments[i] = new AttachmentDescription
            {
                Format = a.Format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = a.FinalLayout,
            };
            colorRefs[i] = new AttachmentReference((uint)i, ImageLayout.ColorAttachmentOptimal);
        }

        var depthRef = new AttachmentReference((uint)desc.ColorAttachments.Count, ImageLayout.DepthStencilAttachmentOptimal);
        if (desc.DepthFormat is { } depthFormat)
        {
            attachments[desc.ColorAttachments.Count] = new AttachmentDescription
            {
                Format = depthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = desc.SampleDepth ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = desc.SampleDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.DepthStencilAttachmentOptimal,
            };
        }

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = (uint)desc.ColorAttachments.Count,
            PColorAttachments = desc.ColorAttachments.Count > 0 ? colorRefs : null,
            PDepthStencilAttachment = desc.DepthFormat is null ? null : &depthRef,
        };

        // Incoming: the attachments are shared by both frames in flight, so this frame's clears/writes wait for the
        // previous frame's writes (WAW) and reads — sampling, copies — (WAR) of the same images.
        // Outgoing: writes become visible to later sampling and transfers (tonemap, ID readback, capture).
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
            AttachmentCount = (uint)count,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        ctx.Vk.CreateRenderPass(ctx.Device, in info, null, out var renderPass).Check($"vkCreateRenderPass ({desc.Name})");
        return renderPass;
    }

    /// <summary>Releases the images, framebuffer and render pass through the deletion queue.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyImages();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(RenderPass));
    }
}
