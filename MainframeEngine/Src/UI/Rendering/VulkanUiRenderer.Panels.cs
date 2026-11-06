using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// G11.5 (ADR 0252): world-space panels. The UI server renders each due <see cref="UiPanel3D"/>'s context after
/// <see cref="BeginTarget"/>, then the screen layers after <see cref="BeginScreen"/>, so the frame's command list is split
/// into panel segments and the screen segment. <see cref="IPreSceneRenderer.RecordPreScene"/> (before the scene passes)
/// replays every panel segment into the top-left of the shared layer stack, resolves it into the panel's own RGBA8
/// image and builds its mips; the overlay replays only the screen segment later, as before.
/// </summary>
public sealed unsafe partial class VulkanUiRenderer : IPreSceneRenderer
{
    private struct Segment
    {
        public UiPanelTarget Target;
        public int Start, End;
    }

    private Segment[] _segments = new Segment[8];
    private int _segmentCount;
    private int _screenStart;            // first command of the screen segment
    private int _segmentStart = -1;      // first command of the open panel segment, -1 = none
    private int _listMaxLayerDepth;      // over every segment (the shared stack is sized for all)
    private bool _panelsConsumed = true;
    private readonly List<UiPanelTarget> _panelTargets = [];
    private RenderPass _panelPass;
    private Pipeline _panelResolvePipeline;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ResolvePush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public Vector4 Background;
        public uint Transparent;
    }

    /// <summary>Panel images resolved by the last pre-scene replay (diagnostics).</summary>
    public int PanelsResolvedLastFrame { get; private set; }

    /// <summary>A panel's target takes part in sizing the shared layer stack until <see cref="RemovePanelTarget"/>.</summary>
    internal void AddPanelTarget(UiPanelTarget target)
    {
        if (!_panelTargets.Contains(target))
            _panelTargets.Add(target);
    }

    /// <summary>Releases the panel's image and framebuffer (after the frames in flight).</summary>
    internal void RemovePanelTarget(UiPanelTarget target)
    {
        _panelTargets.Remove(target);
        DestroyPanelImage(target);
    }

    /// <summary>
    /// Starts the panel segment of <paramref name="target"/>: the following context render draws into it at the top-left
    /// of the layer stack, clipped to the panel's size. (Re)creates the panel image when its size changed.
    /// </summary>
    internal void BeginTarget(UiPanelTarget target)
    {
        CloseSegment();
        EnsurePanelImage(target);
        if (_segmentCount == _segments.Length)
            Array.Resize(ref _segments, _segments.Length * 2);
        _segments[_segmentCount] = new Segment { Target = target, Start = _commandCount };
        _segmentStart = _commandCount;
        _listMaxLayerDepth = Math.Max(_listMaxLayerDepth, _maxLayerDepth);
        _maxLayerDepth = 0;
        ResetState();
        SetRegion(new System.Drawing.Rectangle(0, 0, target.Width, target.Height));
        target.Scheduled = true;
        target.NeedsRender = false;
        target.Renders++;
        _panelsConsumed = false;
    }

    /// <summary>Ends the panel segments: the following renders are the screen layers.</summary>
    internal void BeginScreen()
    {
        CloseSegment();
        _listMaxLayerDepth = Math.Max(_listMaxLayerDepth, _maxLayerDepth);
        _maxLayerDepth = 0;
        _screenStart = _commandCount;
        ResetState();
        SetRegion(null);
    }

    private void CloseSegment()
    {
        if (_segmentStart < 0)
            return;
        _segments[_segmentCount].End = _commandCount;
        _segmentCount++;
        _segmentStart = -1;
    }

    // BeginFrame: segments of a list that never reached the GPU (a skipped frame) render again next frame.
    private void ResetSegments()
    {
        if (!_panelsConsumed)
        {
            for (var i = 0; i < _segmentCount; i++)
            {
                _segments[i].Target.Scheduled = false;
                _segments[i].Target.NeedsRender = true;
            }
        }

        for (var i = 0; i < _segmentCount; i++)
            _segments[i] = default;
        _segmentCount = 0;
        _segmentStart = -1;
        _screenStart = 0;
        _listMaxLayerDepth = 0;
        _panelsConsumed = true;
    }

    /// <summary>The layer stack's size: the swapchain, grown to hold the largest registered panel.</summary>
    private Extent2D StackExtent()
    {
        var extent = _ctx.SwapchainExtent;
        uint w = extent.Width, h = extent.Height;
        if (w == 0 || h == 0)
            return default;
        for (var i = 0; i < _panelTargets.Count; i++)
        {
            w = Math.Max(w, (uint)_panelTargets[i].Width);
            h = Math.Max(h, (uint)_panelTargets[i].Height);
        }

        return new Extent2D(w, h);
    }

    void IPreSceneRenderer.RecordPreScene(CommandBuffer commandBuffer)
    {
        PanelsResolvedLastFrame = 0;
        if (_panelsConsumed || _segmentCount == 0 || IsDisposed)
            return;
        var extent = StackExtent();
        if (extent.Width == 0 || extent.Height == 0)
            return; // stays unconsumed: BeginFrame schedules the panels again

        CollectReleases(_ctx.Deletions.CompletedFrame);
        EnsureTargets(extent, Math.Max(_listMaxLayerDepth, _maxLayerDepth) + 1);
        EnsureGradientCapacity(_shaderDraws);
        _cb = commandBuffer;
        _vk = _ctx.Vk;
        _drawCalls = 0;
        _passes = 0;
        for (var s = 0; s < _segmentCount; s++)
        {
            ref readonly var segment = ref _segments[s];
            var target = segment.Target;
            if (target.Image is null || target.Framebuffer.Handle == 0)
                continue;
            _top = 0;
            BeginLayerPass(0, _layerPassClearAll);
            Replay(segment.Start, segment.End);
            EndPass();
            Resolve(target);
            target.Scheduled = false;
            target.Resolves++;
            PanelsResolvedLastFrame++;
        }

        _panelsConsumed = true;
    }

    // Layer 0 (top-left, the panel's size) → the panel image's mip 0 over its background, then the mip chain by blits,
    // then every level to SHADER_READ_ONLY for the scene's fragment shaders.
    private void Resolve(UiPanelTarget target)
    {
        Barrier(); // the layer's writes before it is sampled
        var image = target.Image!;
        var extent = new Extent2D((uint)target.Width, (uint)target.Height);
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _panelPass,
            Framebuffer = target.Framebuffer,
            RenderArea = new Rect2D { Extent = extent },
        };
        _vk.CmdBeginRenderPass(_cb, &info, SubpassContents.Inline);
        _passes++;
        _vk.CmdBindPipeline(_cb, PipelineBindPoint.Graphics, _panelResolvePipeline);
        var viewport = new Viewport { Width = extent.Width, Height = extent.Height, MinDepth = 0, MaxDepth = 1 };
        _vk.CmdSetViewport(_cb, 0, 1, &viewport);
        var scissor = new Rect2D { Extent = extent };
        _vk.CmdSetScissor(_cb, 0, 1, &scissor);
        var set = _layers[0].Set;
        _vk.CmdBindDescriptorSets(_cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new ResolvePush
        {
            UvScale = new Vector2((float)target.Width / _targetExtent.Width, (float)target.Height / _targetExtent.Height),
            Background = target.Background,
            Transparent = target.Transparent ? 1u : 0u,
        };
        _vk.CmdPushConstants(_cb, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ResolvePush), &push);
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
        _vk.CmdEndRenderPass(_cb); // mip 0 ends in TRANSFER_SRC_OPTIMAL
        _boundPipeline = default;
        _boundSet0 = default;
        _boundBuffer = default;
        _pushedTransform = int.MinValue;
        _stencilRefValid = false;
        GenerateMips(image);
    }

    private void GenerateMips(GpuImage image)
    {
        var mips = image.MipLevels;
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        // Mip 0's colour writes before the blits read it (explicit: MoltenVK skips render-pass dependencies).
        var first = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Handle,
            SubresourceRange = range,
        };
        _vk.CmdPipelineBarrier(_cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &first);

        int w = (int)image.Width, h = (int)image.Height;
        for (var level = 1u; level < mips; level++)
        {
            var toDst = first with
            {
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SubresourceRange = range with { BaseMipLevel = level },
            };
            _vk.CmdPipelineBarrier(_cb, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toDst);
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            _vk.CmdBlitImage(_cb, image.Handle, ImageLayout.TransferSrcOptimal, image.Handle, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            var toSrc = toDst with
            {
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
            };
            _vk.CmdPipelineBarrier(_cb, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toSrc);
            (w, h) = (nw, nh);
        }

        var toRead = first with
        {
            SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SubresourceRange = range with { LevelCount = mips },
        };
        _vk.CmdPipelineBarrier(_cb, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            0, 0, null, 0, null, 1, &toRead);
    }

    // ── Panel images ─────────────────────────────────────────────────────────────────────────────────────────

    private void EnsurePanelImage(UiPanelTarget target)
    {
        if (target.Image is { } image && image.Width == (uint)target.Width && image.Height == (uint)target.Height)
            return;
        DestroyPanelImage(target);
        var width = (uint)Math.Max(1, target.Width);
        var height = (uint)Math.Max(1, target.Height);
        target.Image = GpuImage.Create(_ctx, new GpuImageDesc(width, height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit)
        {
            MipLevels = GpuImageDesc.FullMipChain(width, height),
            Flags = ImageCreateFlags.CreateMutableFormatBit, // materials sample it through an sRGB view
        });
        target.Mip0 = target.Image.CreateView(ImageViewType.Type2D, 0, 1, 0, 1);
        target.Framebuffer = CreateFramebuffer(_panelPass, target.Mip0, default, width, height, "UI panel");
        target.Resolves = 0;
    }

    private void DestroyPanelImage(UiPanelTarget target)
    {
        if (target.Framebuffer.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(target.Framebuffer));
        target.Framebuffer = default;
        target.Mip0 = default; // owned by the image
        target.Image?.Dispose();
        target.Image = null;
        target.Scheduled = false;
    }

    private void CreatePanelResources()
    {
        var attachment = new AttachmentDescription
        {
            Format = LayerFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal, // the mip blits read it next
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var deps = stackalloc SubpassDependency[2];
        Dependencies(deps);
        deps[1].DstStageMask |= PipelineStageFlags.TransferBit;
        deps[1].DstAccessMask |= AccessFlags.TransferReadBit;
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out _panelPass).Check("vkCreateRenderPass (UI panel)");
        _panelResolvePipeline = Build(_panelPass, "Shaders/UI/UiFullscreen.vk.vert.spv", "Shaders/UI/UiPanelResolve.vk.frag.spv", false,
            BlendKind.Opaque, StencilMode.Off, true, "UI panel resolve");
    }

    private void DestroyPanelResources()
    {
        foreach (var target in _panelTargets)
            DestroyPanelImage(target);
        _panelTargets.Clear();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_panelResolvePipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_panelPass));
    }
}
