using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Draws the <see cref="CanvasServer"/>'s <see cref="CanvasFrame"/> on the engine's Vulkan device (ADR 0111): into an
/// offscreen <c>R8G8B8A8_UNORM</c> layer sized to the swapchain, blending in gamma space with Godot's blend states, then
/// composited (premultiplied) onto the swapchain right after the tonemap, below the game UI and ImGui. Textures are
/// uploaded in the frame step (<see cref="PrepareTextures"/>) so their uploads are recorded at the next frame's start,
/// before the pass that samples them.
/// </summary>
public sealed unsafe class VulkanCanvasRenderer : IOverlayRenderer, IDisposable
{
    internal const Format LayerFormat = Format.R8G8B8A8Unorm;
    private const int PushSize = 96;
    private const int SetsPerPool = 256;
    private const int EvictAfterFrames = 600;

    private static readonly nint EntryPoint = SilkMarshal.StringToPtr("main");

    private readonly IVulkanContext _ctx;
    private readonly CanvasServer _server;
    private readonly Sampler[] _samplers = new Sampler[6];
    private readonly Dictionary<Texture2D, TextureEntry> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly List<Texture2D> _evict = [];
    private readonly Dictionary<PipelineKey, Pipeline> _pipelines = [];
    private readonly List<DescriptorPool> _pools = [];
    private readonly FrameBuffers[] _frames = new FrameBuffers[IVulkanContext.MaxFramesInFlight];
    private RenderPass _pass;
    private DescriptorSetLayout _textureSetLayout;
    private PipelineLayout _layout;
    private PipelineLayout _compositeLayout;
    private Pipeline _compositePipeline;
    private GpuTexture _white = null!;
    private DescriptorSet[] _whiteSets = new DescriptorSet[6];
    private GpuImage? _target;
    private Framebuffer _framebuffer;
    private DescriptorSet _targetSet;
    private Extent2D _targetExtent;
    private bool _hasContent;
    private bool _disposed;

    private sealed class TextureEntry
    {
        public GpuTexture Texture = null!;
        public int Version;
        public readonly DescriptorSet[] Sets = new DescriptorSet[6];
        public ulong LastUsed;
    }

    private struct FrameBuffers
    {
        public GpuBuffer? Vertices;
        public GpuBuffer? Indices;
    }

    private readonly record struct PipelineKey(CanvasBlendMode Blend, CanvasPrimitive Primitive);

    [StructLayout(LayoutKind.Sequential)]
    private struct CanvasPush
    {
        public Vector4 ModelAxes;
        public Vector4 ModelOrigin;      // origin, texture pixel size
        public Vector4 CanvasAxes;
        public Vector4 CanvasOrigin;     // origin, time, flags (bits)
        public Vector4 Screen;
        public Vector4 CanvasModulation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositePush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public float Factor;
        public uint Linearize;
    }

    public VulkanCanvasRenderer(IVulkanContext ctx, CanvasServer server)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(server);
        _ctx = ctx;
        _server = server;
        CreateResources();
        ctx.AddOverlayRenderer(this);
    }

    /// <summary>Draw calls of the last frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Textures currently uploaded for the canvas.</summary>
    public int TextureCount => _textures.Count;

    // ── Frame step ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads (or re-uploads after a change) every texture the frame uses and evicts textures unused for a while. Called
    /// by the canvas server after building the frame, outside any command buffer.
    /// </summary>
    public void PrepareTextures(CanvasFrame frame)
    {
        if (_disposed)
            return;
        var now = _ctx.FrameNumber;
        var batches = frame.Batches;
        for (var i = 0; i < batches.Count; i++)
        {
            if (batches[i].Texture is not { } texture)
                continue;
            if (!_textures.TryGetValue(texture, out var entry) || entry.Version != texture.Version)
            {
                if (entry is not null)
                    Release(entry);
                entry = Upload(texture);
                _textures[texture] = entry;
            }

            entry.LastUsed = now;
        }

        if (now % 120 == 0)
        {
            foreach (var (texture, entry) in _textures)
                if (now - entry.LastUsed > EvictAfterFrames)
                    _evict.Add(texture);
            foreach (var texture in _evict)
            {
                Release(_textures[texture]);
                _textures.Remove(texture);
            }

            _evict.Clear();
        }
    }

    private TextureEntry Upload(Texture2D texture)
    {
        var (rgba, width, height) = texture.DecodePixels();
        var gpu = GpuTexture.Create2D(_ctx, (uint)width, (uint)height, rgba, TextureColorSpace.Linear, TextureSampling.LinearClamp);
        return new TextureEntry { Texture = gpu, Version = texture.Version };
    }

    private void Release(TextureEntry entry)
    {
        foreach (var set in entry.Sets)
            if (set.Handle != 0)
                FreeSet(set);
        entry.Texture.Dispose(); // deletion queue
    }

    // ── IOverlayRenderer ─────────────────────────────────────────────────────────────────────────────────────

    void IOverlayRenderer.RecordOffscreen(CommandBuffer cb)
    {
        _hasContent = false;
        DrawCalls = 0;
        if (_disposed)
            return;
        var frame = _server.Frame;
        var extent = _ctx.SwapchainExtent;
        if (extent.Width == 0 || extent.Height == 0)
            return;
        if (frame.Batches.Count == 0 && frame.ClearColor is null)
            return;

        EnsureTarget(extent);
        var slot = _ctx.FrameSlot;
        var (vertexBuffer, indexBuffer) = Upload(slot, frame);

        var vk = _ctx.Vk;
        Barrier(cb);
        var clear = frame.ClearColor ?? Vector4.Zero;
        var clearValue = new ClearValue { Color = new ClearColorValue(clear.X, clear.Y, clear.Z, clear.W) };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _pass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D { Extent = extent },
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);

        if (frame.Batches.Count > 0 && vertexBuffer is not null && indexBuffer is not null)
        {
            var vb = vertexBuffer.Handle;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
            vk.CmdBindIndexBuffer(cb, indexBuffer.Handle, 0, IndexType.Uint32);

            var screen = new Vector4(2f / extent.Width, 2f / extent.Height, -1f, -1f);
            var time = _server.Time;
            var boundPipeline = default(Pipeline);
            var boundSet = default(DescriptorSet);
            var batches = frame.Batches;
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var pipeline = GetPipeline(new PipelineKey(batch.Blend, batch.Primitive));
                if (pipeline.Handle != boundPipeline.Handle)
                {
                    vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                    boundPipeline = pipeline;
                }

                var set = TextureSet(batch.Texture, batch.Sampler, out var pixelSize);
                if (set.Handle != boundSet.Handle)
                {
                    vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
                    boundSet = set;
                }

                var flags = batch.Material is CanvasItemMaterial { LightMode: CanvasLightMode.Unshaded } ? 1u : 0u;
                var push = new CanvasPush
                {
                    ModelAxes = new Vector4(batch.Model.X, batch.Model.Y.X, batch.Model.Y.Y),
                    ModelOrigin = new Vector4(batch.Model.Origin, pixelSize.X, pixelSize.Y),
                    CanvasAxes = new Vector4(batch.CanvasTransform.X, batch.CanvasTransform.Y.X, batch.CanvasTransform.Y.Y),
                    CanvasOrigin = new Vector4(batch.CanvasTransform.Origin, time, BitConverter.UInt32BitsToSingle(flags)),
                    Screen = screen,
                    CanvasModulation = batch.CanvasModulate,
                };
                vk.CmdPushConstants(cb, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushSize, &push);
                vk.CmdDrawIndexed(cb, (uint)batch.IndexCount, 1, (uint)batch.FirstIndex, 0, 0);
                DrawCalls++;
            }
        }

        vk.CmdEndRenderPass(cb);
        Barrier(cb); // the overlay pass samples the layer
        _hasContent = true;
    }

    void IOverlayRenderer.RecordOverlay(CommandBuffer cb)
    {
        if (!_hasContent)
            return;
        var vk = _ctx.Vk;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _compositePipeline);
        PipelineBuilder.SetViewport(vk, cb, _ctx.SwapchainExtent, flipY: false);
        var set = _targetSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _compositeLayout, 0, 1, &set, 0, null);
        var push = new CompositePush { UvScale = Vector2.One, Factor = 1f, Linearize = _ctx.OverlayEncodesSrgb ? 1u : 0u };
        vk.CmdPushConstants(cb, _compositeLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CompositePush), &push);
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    // ── Per-frame geometry ───────────────────────────────────────────────────────────────────────────────────

    private (GpuBuffer? Vertices, GpuBuffer? Indices) Upload(int slot, CanvasFrame frame)
    {
        ref var buffers = ref _frames[slot];
        var vertexBytes = (ulong)(frame.VertexCount * CanvasVertex.SizeInBytes);
        var indexBytes = (ulong)(frame.IndexCount * sizeof(uint));
        if (vertexBytes == 0 || indexBytes == 0)
            return (null, null);
        if (buffers.Vertices is null || buffers.Vertices.Size < vertexBytes)
        {
            buffers.Vertices?.Dispose();
            buffers.Vertices = GpuBuffer.Create(_ctx, Grow(vertexBytes), BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        }

        if (buffers.Indices is null || buffers.Indices.Size < indexBytes)
        {
            buffers.Indices?.Dispose();
            buffers.Indices = GpuBuffer.Create(_ctx, Grow(indexBytes), BufferUsageFlags.IndexBufferBit, GpuMemoryUsage.Dynamic);
        }

        buffers.Vertices.Write(frame.Vertices);
        buffers.Indices.Write(frame.Indices);
        return (buffers.Vertices, buffers.Indices);
    }

    private static ulong Grow(ulong bytes) => Math.Max(64 * 1024, (ulong)BitOperations.RoundUpToPowerOf2(bytes));

    // ── Textures and descriptor sets ─────────────────────────────────────────────────────────────────────────

    private DescriptorSet TextureSet(Texture2D? texture, CanvasSampler sampler, out Vector2 pixelSize)
    {
        var s = (int)sampler;
        if (texture is not null && _textures.TryGetValue(texture, out var entry))
        {
            pixelSize = new Vector2(1f / entry.Texture.Width, 1f / entry.Texture.Height);
            if (entry.Sets[s].Handle == 0)
                entry.Sets[s] = AllocateSet(entry.Texture.View, _samplers[s]);
            return entry.Sets[s];
        }

        pixelSize = Vector2.One;
        if (_whiteSets[s].Handle == 0)
            _whiteSets[s] = AllocateSet(_white.View, _samplers[s]);
        return _whiteSets[s];
    }

    private DescriptorSet AllocateSet(ImageView view, Sampler sampler)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_pools.Count > 0)
            {
                var layout = _textureSetLayout;
                var info = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _pools[^1],
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                var result = _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in info, out var set);
                if (result == Result.Success)
                {
                    PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
                    {
                        Sampler = sampler,
                        ImageView = view,
                        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    });
                    _setPools[set.Handle] = _pools[^1];
                    return set;
                }

                if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
                    result.Check("vkAllocateDescriptorSets (canvas)");
            }

            var size = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = SetsPerPool };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = SetsPerPool,
                PoolSizeCount = 1,
                PPoolSizes = &size,
            };
            _ctx.Vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out var pool).Check("vkCreateDescriptorPool (canvas)");
            _pools.Add(pool);
        }

        throw new VulkanException("[Canvas] Could not allocate a texture descriptor set.");
    }

    private readonly Dictionary<ulong, DescriptorPool> _setPools = [];

    private void FreeSet(DescriptorSet set)
    {
        if (_setPools.Remove(set.Handle, out var pool))
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pool, set));
    }

    // ── Target ───────────────────────────────────────────────────────────────────────────────────────────────

    private void EnsureTarget(Extent2D extent)
    {
        if (_target is not null && extent.Width == _targetExtent.Width && extent.Height == _targetExtent.Height)
            return;
        DestroyTarget();
        _targetExtent = extent;
        _target = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit));
        var view = _target.View;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _pass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _framebuffer).Check("vkCreateFramebuffer (canvas)");
        _targetSet = AllocateSet(_target.View, _samplers[(int)CanvasSampler.LinearClamp]);
    }

    private void DestroyTarget()
    {
        if (_target is null)
            return;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        FreeSet(_targetSet);
        _target.Dispose();
        _target = null;
        _framebuffer = default;
        _targetSet = default;
    }

    // ── Resources ────────────────────────────────────────────────────────────────────────────────────────────

    private void CreateResources()
    {
        var attachment = new AttachmentDescription
        {
            Format = LayerFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
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
        var passInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in passInfo, null, out _pass).Check("vkCreateRenderPass (canvas)");

        Span<(Filter Filter, SamplerAddressMode Mode)> samplers =
        [
            (Filter.Linear, SamplerAddressMode.ClampToEdge),
            (Filter.Nearest, SamplerAddressMode.ClampToEdge),
            (Filter.Linear, SamplerAddressMode.Repeat),
            (Filter.Nearest, SamplerAddressMode.Repeat),
            (Filter.Linear, SamplerAddressMode.MirroredRepeat),
            (Filter.Nearest, SamplerAddressMode.MirroredRepeat),
        ];
        for (var i = 0; i < samplers.Length; i++)
        {
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = samplers[i].Filter,
                MinFilter = samplers[i].Filter,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = samplers[i].Mode,
                AddressModeV = samplers[i].Mode,
                AddressModeW = samplers[i].Mode,
                MaxLod = 0f,
            };
            _ctx.Vk.CreateSampler(_ctx.Device, in info, null, out _samplers[i]).Check("vkCreateSampler (canvas)");
        }

        _textureSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "canvas texture");
        _layout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout], PushSize, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "canvas");
        _compositeLayout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout], (uint)sizeof(CompositePush),
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "canvas composite");
        _white = GpuTexture.Create2D(_ctx, 1, 1, [255, 255, 255, 255], TextureColorSpace.Linear, TextureSampling.NearestClamp);
        _compositePipeline = BuildPipeline(_ctx.OverlayRenderPass, _compositeLayout, "Shaders/UI/UiFullscreen.vk.vert.spv",
            "Shaders/UI/UiPassthrough.vk.frag.spv", geometry: false, CanvasBlendMode.PremultAlpha, PrimitiveTopology.TriangleList, "canvas composite");
    }

    private Pipeline GetPipeline(PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var pipeline))
            return pipeline;
        var topology = key.Primitive == CanvasPrimitive.Lines ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList;
        pipeline = BuildPipeline(_pass, _layout, "Shaders/Canvas/Canvas.vk.vert.spv", "Shaders/Canvas/Canvas.vk.frag.spv", geometry: true,
            key.Blend, topology, $"canvas {key.Blend} {key.Primitive}");
        _pipelines[key] = pipeline;
        return pipeline;
    }

    private Pipeline BuildPipeline(RenderPass pass, PipelineLayout layout, string vertexSpv, string fragmentSpv, bool geometry,
        CanvasBlendMode blend, PrimitiveTopology topology, string what)
    {
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _ctx.Shaders.Get(vertexSpv),
            PName = (byte*)EntryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _ctx.Shaders.Get(fragmentSpv),
            PName = (byte*)EntryPoint,
        };

        var binding = new VertexInputBindingDescription(0, CanvasVertex.SizeInBytes, VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 8);
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32A32Sfloat, 16);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = geometry ? 1u : 0u,
            PVertexBindingDescriptions = geometry ? &binding : null,
            VertexAttributeDescriptionCount = geometry ? 3u : 0u,
            PVertexAttributeDescriptions = geometry ? attributes : null,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = topology,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1f,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var blendAttachment = BlendState(blend);
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };
        var info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = layout,
            RenderPass = pass,
            Subpass = 0,
        };
        return _ctx.Pipelines.CreateGraphicsPipeline(info, what);
    }

    /// <summary>Godot's canvas blend states (<c>MaterialStorage::ShaderData::blend_mode_to_blend_attachment</c>).</summary>
    internal static PipelineColorBlendAttachmentState BlendState(CanvasBlendMode mode)
    {
        const ColorComponentFlags rgba = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        var s = new PipelineColorBlendAttachmentState { BlendEnable = true, ColorWriteMask = rgba, ColorBlendOp = BlendOp.Add, AlphaBlendOp = BlendOp.Add };
        switch (mode)
        {
            case CanvasBlendMode.Add:
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.One;
                s.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                s.DstAlphaBlendFactor = BlendFactor.One;
                break;
            case CanvasBlendMode.Sub:
                s.ColorBlendOp = BlendOp.ReverseSubtract;
                s.AlphaBlendOp = BlendOp.ReverseSubtract;
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.One;
                s.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                s.DstAlphaBlendFactor = BlendFactor.One;
                break;
            case CanvasBlendMode.Mul:
                s.SrcColorBlendFactor = BlendFactor.DstColor;
                s.DstColorBlendFactor = BlendFactor.Zero;
                s.SrcAlphaBlendFactor = BlendFactor.DstAlpha;
                s.DstAlphaBlendFactor = BlendFactor.Zero;
                break;
            case CanvasBlendMode.PremultAlpha:
                s.SrcColorBlendFactor = BlendFactor.One;
                s.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                s.SrcAlphaBlendFactor = BlendFactor.One;
                s.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                break;
            case CanvasBlendMode.Disabled:
                s.BlendEnable = false;
                break;
            default: // Mix
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                s.SrcAlphaBlendFactor = BlendFactor.One;
                s.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                break;
        }

        return s;
    }

    private void Barrier(CommandBuffer cb)
    {
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit;
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
        };
        _ctx.Vk.CmdPipelineBarrier(cb, stages, stages, 0, 1, &barrier, 0, null, 0, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ctx.RemoveOverlayRenderer(this);
        foreach (var entry in _textures.Values)
            Release(entry);
        _textures.Clear();
        DestroyTarget();
        foreach (ref var frame in _frames.AsSpan())
        {
            frame.Vertices?.Dispose();
            frame.Indices?.Dispose();
        }

        _white.Dispose();
        foreach (var pipeline in _pipelines.Values)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_compositePipeline));
        foreach (var pool in _pools)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pool));
        foreach (var sampler in _samplers)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(sampler));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pass));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_compositeLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_textureSetLayout));
    }
}
