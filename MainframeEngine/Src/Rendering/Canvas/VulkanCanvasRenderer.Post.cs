using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

// The 2D canvas's post-processing (ADR 0185): the main pass's post-processed batches draw into the canvas layer as
// before; glow (a 13-tap down / tent up chain at half size and below) and CanvasPost.vk.frag (grade, vignette, chromatic
// aberration, grain) then write the post target, the batches of the layers above MaxLayer draw on top of it with a load
// pass, and the composite shows the post target instead of the layer. Targets live until the size changes; nothing is
// allocated per frame.
public sealed unsafe partial class VulkanCanvasRenderer
{
    private const Format GlowFormat = Format.R16G16B16A16Sfloat;
    private const int MaxGlowLevels = 5;
    private const string FullscreenVertex = "Shaders/Post/Fullscreen.vk.vert.spv";

    private RenderPass _loadPass;
    private RenderPass _glowPass;
    private PipelineLayout _postLayout;
    private Pipeline _glowDownPipeline;
    private Pipeline _glowUpPipeline;
    private Pipeline _postPipeline;
    private GpuImage? _postTarget;
    private Framebuffer _postFramebuffer;
    private DescriptorSet _postSet;
    private DescriptorSet _compositeSet;
    private readonly PostLevel[] _glowDown = new PostLevel[MaxGlowLevels];
    private readonly PostLevel[] _glowUp = new PostLevel[MaxGlowLevels];
    private int _glowLevels;

    private struct PostLevel
    {
        public GpuImage? Image;
        public Framebuffer Framebuffer;
        public DescriptorSet Set;
        public Extent2D Extent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlowDownPush
    {
        public Vector2 DstSize;
        public uint Flags;
        public float Threshold;
        public float Knee;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlowUpPush
    {
        public Vector2 DstSize;
        public float Spread;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PostPush
    {
        public Vector4 Lift;
        public Vector4 Gamma;
        public Vector4 Gain;
        public Vector4 Tint;
        public Vector4 Vignette;
        public Vector4 Shape;
        public Vector4 Misc;
        public Vector2 DstSize;
    }

    /// <summary>Whether the last frame ran the 2D post-processing (tests, diagnostics).</summary>
    public bool PostProcessed { get; private set; }

    private void CreatePostResources()
    {
        _loadPass = CreateColorPass(LayerFormat, AttachmentLoadOp.Load, ImageLayout.ShaderReadOnlyOptimal, "canvas load");
        _glowPass = CreateColorPass(GlowFormat, AttachmentLoadOp.DontCare, ImageLayout.Undefined, "canvas glow");
        _postLayout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout, _textureSetLayout], 128, ShaderStageFlags.FragmentBit, "canvas post");
        _glowDownPipeline = BuildPipeline(_glowPass, _postLayout, FullscreenVertex, "Shaders/Canvas/CanvasBloomDown.vk.frag.spv", geometry: false,
            CanvasBlendMode.Disabled, PrimitiveTopology.TriangleList, "canvas glow down");
        _glowUpPipeline = BuildPipeline(_glowPass, _postLayout, FullscreenVertex, "Shaders/Canvas/CanvasBloomUp.vk.frag.spv", geometry: false,
            CanvasBlendMode.Disabled, PrimitiveTopology.TriangleList, "canvas glow up");
        _postPipeline = BuildPipeline(_pass, _postLayout, FullscreenVertex, "Shaders/Canvas/CanvasPost.vk.frag.spv", geometry: false,
            CanvasBlendMode.Disabled, PrimitiveTopology.TriangleList, "canvas post");
    }

    private RenderPass CreateColorPass(Format format, AttachmentLoadOp load, ImageLayout initialLayout, string what)
    {
        var attachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = initialLayout,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit;
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = stages,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
            DstStageMask = stages,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = stages,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = stages,
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
            PDependencies = deps,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out var pass).Check($"vkCreateRenderPass ({what})");
        return pass;
    }

    // The main pass with post-processing: the post-processed batches into the layer, glow and the grade into the post
    // target, then the rest of the batches on top of it. The composite then shows the post target.
    private void RecordMainWithPost(CommandBuffer cb, CanvasFrame frame, in CanvasPass pass, Extent2D extent, Vector4 clear,
        GpuBuffer? vertexBuffer, GpuBuffer? indexBuffer)
    {
        var end = pass.FirstBatch + pass.BatchCount;
        var split = pass.PostSplit < 0 ? end : pass.PostSplit;
        var below = pass;
        below.BatchCount = split - pass.FirstBatch;
        RecordPass(cb, frame, below, _pass, _framebuffer, extent, clear, vertexBuffer, indexBuffer);
        Barrier(cb);

        EnsurePostTargets(extent);
        var settings = frame.Post;
        var glow = settings.GlowIntensity > 0f && _glowLevels > 0;
        if (glow)
            RecordGlow(cb, settings);
        RecordPostComposite(cb, settings, extent, glow);

        if (end > split)
        {
            var above = pass;
            above.FirstBatch = split;
            above.BatchCount = end - split;
            RecordPass(cb, frame, above, _loadPass, _postFramebuffer, extent, default, vertexBuffer, indexBuffer);
            Barrier(cb);
        }

        _compositeSet = _postSet;
        PostProcessed = true;
    }

    private void RecordGlow(CommandBuffer cb, in CanvasPostSettings settings)
    {
        var knee = settings.GlowThreshold * settings.GlowSoftKnee;
        for (var k = 0; k < _glowLevels; k++)
        {
            ref var level = ref _glowDown[k];
            var push = new GlowDownPush
            {
                DstSize = new Vector2(level.Extent.Width, level.Extent.Height),
                Flags = k == 0 ? 1u : 0u,
                Threshold = settings.GlowThreshold,
                Knee = knee,
            };
            var source = k == 0 ? _targetSet : _glowDown[k - 1].Set;
            Fullscreen(cb, _glowPass, level.Framebuffer, level.Extent, _glowDownPipeline, source, default, &push, (uint)sizeof(GlowDownPush));
        }

        for (var k = _glowLevels - 2; k >= 0; k--)
        {
            ref var level = ref _glowUp[k];
            var lower = k == _glowLevels - 2 ? _glowDown[k + 1].Set : _glowUp[k + 1].Set;
            var push = new GlowUpPush { DstSize = new Vector2(level.Extent.Width, level.Extent.Height), Spread = settings.GlowSpread };
            Fullscreen(cb, _glowPass, level.Framebuffer, level.Extent, _glowUpPipeline, lower, _glowDown[k].Set, &push, (uint)sizeof(GlowUpPush));
        }
    }

    private void RecordPostComposite(CommandBuffer cb, in CanvasPostSettings s, Extent2D extent, bool glow)
    {
        var glowSet = !glow ? WhiteSet() : _glowLevels > 1 ? _glowUp[0].Set : _glowDown[0].Set;
        var push = new PostPush
        {
            Lift = new Vector4(s.Lift, s.Brightness),
            Gamma = new Vector4(s.Gamma, s.Contrast),
            Gain = new Vector4(s.Gain, s.Saturation),
            Tint = s.Tint,
            Vignette = new Vector4(s.VignetteColor, s.VignetteStrength),
            Shape = new Vector4(s.VignetteRadius, s.VignetteSoftness, s.GrainAmount, s.GrainSize),
            Misc = new Vector4(s.ChromaticAberration, s.GlowIntensity, MathF.Floor(_server.Time * 24f) % 97f * 13.7f, BitConverter.UInt32BitsToSingle(glow ? 1u : 0u)),
            DstSize = new Vector2(extent.Width, extent.Height),
        };
        Fullscreen(cb, _pass, _postFramebuffer, extent, _postPipeline, _targetSet, glowSet, &push, (uint)sizeof(PostPush));
    }

    private DescriptorSet WhiteSet()
    {
        var s = (int)CanvasSampler.LinearClamp;
        if (_whiteSets[s].Handle == 0)
            _whiteSets[s] = AllocateSet(_white.View, _samplers[s]);
        return _whiteSets[s];
    }

    private void Fullscreen(CommandBuffer cb, RenderPass renderPass, Framebuffer framebuffer, Extent2D extent, Pipeline pipeline,
        DescriptorSet set0, DescriptorSet set1, void* push, uint pushSize)
    {
        var vk = _ctx.Vk;
        var clearValue = default(ClearValue);
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = extent },
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        var sets = stackalloc DescriptorSet[2];
        sets[0] = set0;
        sets[1] = set1.Handle != 0 ? set1 : set0;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _postLayout, 0, 2, sets, 0, null);
        vk.CmdPushConstants(cb, _postLayout, ShaderStageFlags.FragmentBit, 0, pushSize, push);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        vk.CmdEndRenderPass(cb);
        Barrier(cb);
    }

    private void EnsurePostTargets(Extent2D extent)
    {
        if (_postTarget is not null && _postTarget.Width == extent.Width && _postTarget.Height == extent.Height)
            return;
        DestroyPostTargets();
        _postTarget = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit));
        _postFramebuffer = CreateFramebuffer(_pass, _postTarget.View, extent, "canvas post");
        _postSet = AllocateSet(_postTarget.View, _samplers[(int)CanvasSampler.LinearClamp]);

        var w = extent.Width;
        var h = extent.Height;
        _glowLevels = 0;
        for (var k = 0; k < MaxGlowLevels; k++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            if (k > 0 && (w < 4 || h < 4))
                break;
            var size = new Extent2D(w, h);
            _glowDown[k] = CreateLevel(size);
            if (k < MaxGlowLevels - 1)
                _glowUp[k] = CreateLevel(size);
            _glowLevels++;
        }
    }

    private PostLevel CreateLevel(Extent2D extent)
    {
        var image = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, GlowFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit));
        return new PostLevel
        {
            Image = image,
            Framebuffer = CreateFramebuffer(_glowPass, image.View, extent, "canvas glow"),
            Set = AllocateSet(image.View, _samplers[(int)CanvasSampler.LinearClamp]),
            Extent = extent,
        };
    }

    private Framebuffer CreateFramebuffer(RenderPass renderPass, ImageView view, Extent2D extent, string what)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out var framebuffer).Check($"vkCreateFramebuffer ({what})");
        return framebuffer;
    }

    private void DestroyPostTargets()
    {
        if (_postTarget is null)
            return;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_postFramebuffer));
        FreeSet(_postSet);
        _postTarget.Dispose();
        _postTarget = null;
        _postFramebuffer = default;
        _postSet = default;
        for (var k = 0; k < MaxGlowLevels; k++)
        {
            DestroyLevel(ref _glowDown[k]);
            DestroyLevel(ref _glowUp[k]);
        }

        _glowLevels = 0;
    }

    private void DestroyLevel(ref PostLevel level)
    {
        if (level.Image is null)
            return;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(level.Framebuffer));
        FreeSet(level.Set);
        level.Image.Dispose();
        level = default;
    }

    private void DisposePostResources()
    {
        DestroyPostTargets();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_glowDownPipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_glowUpPipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_postPipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_postLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_loadPass));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_glowPass));
    }
}
