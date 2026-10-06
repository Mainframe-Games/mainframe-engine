using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public sealed unsafe partial class VulkanUiRenderer
{
    /// <summary>UI layers: premultiplied, sRGB-encoded 8-bit values (blending in sRGB space, like RmlUi's GL backends).</summary>
    internal const Format LayerFormat = Format.R8G8B8A8Unorm;

    private const uint PushConstantSize = 128;
    private const int TextureSetsPerPool = 128;
    private const int GradientsPerFrameMinimum = 64;

    private static readonly nint EntryPoint = SilkMarshal.StringToPtr("main"); // process lifetime

    private Format _stencilFormat;

    // Render passes. Layer passes: colour + shared stencil; post passes: colour only. All end in SHADER_READ_ONLY.
    private RenderPass _layerPassClearAll;   // frame start: clear colour and stencil
    private RenderPass _layerPassClearColor; // pushed layer: clear colour, keep stencil
    private RenderPass _layerPassLoad;       // resume a layer
    private RenderPass _postPassDiscard;     // filter targets, saved textures: contents replaced
    private RenderPass _postPassLoad;        // filter targets drawn over (drop-shadow)

    private DescriptorSetLayout _textureSetLayout;
    private DescriptorSetLayout _uniformSetLayout;
    private PipelineLayout _layout;
    private Sampler _linearSampler;

    // Pipelines against the layer passes ([stencil test off, on]).
    private readonly Pipeline[] _geometryPipelines = new Pipeline[2];
    private readonly Pipeline[] _gradientPipelines = new Pipeline[2];
    private readonly Pipeline[] _compositeBlendPipelines = new Pipeline[2];
    private readonly Pipeline[] _compositeReplacePipelines = new Pipeline[2];
    private Pipeline _clipReplacePipeline;
    private Pipeline _clipIncrementPipeline;

    // Pipelines against the post passes.
    private Pipeline _postCopyPipeline;
    private Pipeline _postBlendPipeline;
    private Pipeline _colorMatrixPipeline;
    private Pipeline _blendMaskPipeline;
    private Pipeline _blurPipeline;
    private Pipeline _dropShadowPipeline;

    // Overlay: the UI composited onto the swapchain.
    private Pipeline _overlayPipeline;

    private readonly List<DescriptorPool> _texturePools = [];
    private readonly Queue<(ulong Frame, DescriptorPool Pool, DescriptorSet Set)> _pendingSets = new(32);
    private GpuTexture _white = null!;
    private DescriptorSet _whiteSet;

    // Gradient uniforms: one region per frame slot, dynamic offsets.
    private DescriptorPool _uniformPool;
    private DescriptorSet _uniformSet;
    private GpuBuffer? _gradientBuffer;
    private int _gradientCapacity;     // per frame slot
    private ulong _gradientStride;

    private void CreateResources()
    {
        _stencilFormat = _ctx.StencilFormat;
        _layerPassClearAll = CreateLayerPass(AttachmentLoadOp.Clear, AttachmentLoadOp.Clear, "UI layer (clear)");
        _layerPassClearColor = CreateLayerPass(AttachmentLoadOp.Clear, AttachmentLoadOp.Load, "UI layer (push)");
        _layerPassLoad = CreateLayerPass(AttachmentLoadOp.Load, AttachmentLoadOp.Load, "UI layer (resume)");
        _postPassDiscard = CreatePostPass(AttachmentLoadOp.DontCare, "UI filter");
        _postPassLoad = CreatePostPass(AttachmentLoadOp.Load, "UI filter (load)");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        _ctx.Vk.CreateSampler(_ctx.Device, in samplerInfo, null, out _linearSampler).Check("vkCreateSampler (UI)");

        _textureSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "UI texture");
        _uniformSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBufferDynamic, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "UI uniforms");
        _layout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout, _textureSetLayout, _uniformSetLayout], PushConstantSize,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "UI");

        CreatePipelines();

        _white = GpuTexture.Create2D(_ctx, 1, 1, [255, 255, 255, 255], TextureColorSpace.Linear, TextureSampling.NearestClamp);
        (_whiteSet, _) = AllocateTextureSet(_white.View, _white.Sampler, ImageLayout.ShaderReadOnlyOptimal);

        _uniformPool = PipelineBuilder.CreatePool(_ctx, 1,
            [new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = 1 }], "UI uniforms");
        _uniformSet = PipelineBuilder.AllocateSet(_ctx, _uniformPool, _uniformSetLayout, "UI uniforms");
        _gradientStride = UniformRing.AlignUp(GradientUboSize, UniformRing.MaxSpecAlignment);
        EnsureGradientCapacity(GradientsPerFrameMinimum);
    }

    // ── Render passes ────────────────────────────────────────────────────────────────────────────────────────

    // One dependency each way, generous enough for every UI pass: earlier passes' colour/stencil writes and sampling of
    // the same images (previous frame, previous filter step) complete before this pass touches them; this pass's writes
    // are visible to later sampling and later passes.
    private static void Dependencies(SubpassDependency* deps)
    {
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit |
                                          PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        const AccessFlags writes = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit;
        const AccessFlags all = writes | AccessFlags.ColorAttachmentReadBit | AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.ShaderReadBit;
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = stages,
            SrcAccessMask = writes,
            DstStageMask = stages,
            DstAccessMask = all,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = stages,
            SrcAccessMask = writes,
            DstStageMask = stages,
            DstAccessMask = all,
        };
    }

    private RenderPass CreateLayerPass(AttachmentLoadOp colorLoad, AttachmentLoadOp stencilLoad, string what)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = LayerFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = colorLoad,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = colorLoad == AttachmentLoadOp.Load ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = _stencilFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare, // packed depth/stencil formats: depth is unused
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = stencilLoad,
            StencilStoreOp = AttachmentStoreOp.Store,
            InitialLayout = stencilLoad == AttachmentLoadOp.Load ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var stencilRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &stencilRef,
        };
        var deps = stackalloc SubpassDependency[2];
        Dependencies(deps);
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out var pass).Check($"vkCreateRenderPass ({what})");
        return pass;
    }

    private RenderPass CreatePostPass(AttachmentLoadOp load, string what)
    {
        var attachment = new AttachmentDescription
        {
            Format = LayerFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = load == AttachmentLoadOp.Load ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
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

    private Framebuffer CreateFramebuffer(RenderPass pass, ImageView color, ImageView stencil, uint width, uint height, string what)
    {
        var views = stackalloc ImageView[2];
        views[0] = color;
        views[1] = stencil;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = stencil.Handle == 0 ? 1u : 2u,
            PAttachments = views,
            Width = width,
            Height = height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out var framebuffer).Check($"vkCreateFramebuffer ({what})");
        return framebuffer;
    }

    // ── Pipelines ────────────────────────────────────────────────────────────────────────────────────────────

    private enum StencilMode : byte
    {
        Off,
        TestEqual,
        WriteReplace,
        WriteIncrement,
    }

    private enum BlendKind : byte
    {
        Premultiplied,
        Opaque,
    }

    private void CreatePipelines()
    {
        const string geoVert = "Shaders/UI/Ui.vk.vert.spv";
        const string fullVert = "Shaders/UI/UiFullscreen.vk.vert.spv";
        for (var s = 0; s < 2; s++)
        {
            var stencil = s == 0 ? StencilMode.Off : StencilMode.TestEqual;
            _geometryPipelines[s] = Build(_layerPassLoad, geoVert, "Shaders/UI/Ui.vk.frag.spv", true, BlendKind.Premultiplied, stencil, true, "UI geometry");
            _gradientPipelines[s] = Build(_layerPassLoad, geoVert, "Shaders/UI/UiGradient.vk.frag.spv", true, BlendKind.Premultiplied, stencil, true, "UI gradient");
            _compositeBlendPipelines[s] = Build(_layerPassLoad, fullVert, "Shaders/UI/UiPassthrough.vk.frag.spv", false, BlendKind.Premultiplied, stencil, true, "UI composite");
            _compositeReplacePipelines[s] = Build(_layerPassLoad, fullVert, "Shaders/UI/UiPassthrough.vk.frag.spv", false, BlendKind.Opaque, stencil, true, "UI composite (replace)");
        }

        _clipReplacePipeline = Build(_layerPassLoad, geoVert, "Shaders/UI/Ui.vk.frag.spv", true, BlendKind.Opaque, StencilMode.WriteReplace, false, "UI clip mask");
        _clipIncrementPipeline = Build(_layerPassLoad, geoVert, "Shaders/UI/Ui.vk.frag.spv", true, BlendKind.Opaque, StencilMode.WriteIncrement, false, "UI clip mask (intersect)");

        _postCopyPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiPassthrough.vk.frag.spv", false, BlendKind.Opaque, StencilMode.Off, true, "UI copy");
        _postBlendPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiPassthrough.vk.frag.spv", false, BlendKind.Premultiplied, StencilMode.Off, true, "UI blend");
        _colorMatrixPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiColorMatrix.vk.frag.spv", false, BlendKind.Opaque, StencilMode.Off, true, "UI colour matrix");
        _blendMaskPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiBlendMask.vk.frag.spv", false, BlendKind.Opaque, StencilMode.Off, true, "UI mask image");
        _blurPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiBlur.vk.frag.spv", false, BlendKind.Opaque, StencilMode.Off, true, "UI blur");
        _dropShadowPipeline = Build(_postPassDiscard, fullVert, "Shaders/UI/UiDropShadow.vk.frag.spv", false, BlendKind.Opaque, StencilMode.Off, true, "UI drop shadow");

        _overlayPipeline = Build(_ctx.OverlayRenderPass, fullVert, "Shaders/UI/UiPassthrough.vk.frag.spv", false, BlendKind.Premultiplied, StencilMode.Off, true, "UI overlay");
    }

    private Pipeline Build(RenderPass pass, string vertexSpv, string fragmentSpv, bool geometryInput, BlendKind blend, StencilMode stencil,
        bool writeColor, string what)
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

        // Rml::Vertex: vec2 position (0), RGBA8 colour (8), vec2 texcoord (12); 20 bytes.
        var binding = new VertexInputBindingDescription(0, 20, VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R8G8B8A8Unorm, 8);
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 12);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = geometryInput ? 1u : 0u,
            PVertexBindingDescriptions = geometryInput ? &binding : null,
            VertexAttributeDescriptionCount = geometryInput ? 3u : 0u,
            PVertexAttributeDescriptions = geometryInput ? attributes : null,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
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

        var stencilOp = new StencilOpState
        {
            FailOp = StencilOp.Keep,
            DepthFailOp = StencilOp.Keep,
            PassOp = stencil switch
            {
                StencilMode.WriteReplace => StencilOp.Replace,
                StencilMode.WriteIncrement => StencilOp.IncrementAndClamp,
                _ => StencilOp.Keep,
            },
            CompareOp = stencil == StencilMode.TestEqual ? CompareOp.Equal : CompareOp.Always,
            CompareMask = 0xFF,
            WriteMask = stencil is StencilMode.WriteReplace or StencilMode.WriteIncrement ? 0xFFu : 0u,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
            StencilTestEnable = stencil != StencilMode.Off,
            Front = stencilOp,
            Back = stencilOp,
        };

        const ColorComponentFlags rgba = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        var blendAttachment = blend == BlendKind.Premultiplied
            ? new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = writeColor ? rgba : 0,
            }
            : new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = writeColor ? rgba : 0 };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 3,
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
            Layout = _layout,
            RenderPass = pass,
            Subpass = 0,
        };
        return _ctx.Pipelines.CreateGraphicsPipeline(info, what);
    }

    // ── Descriptor sets ──────────────────────────────────────────────────────────────────────────────────────

    private (DescriptorSet Set, DescriptorPool Pool) AllocateTextureSet(ImageView view, Sampler sampler, ImageLayout imageLayout)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_texturePools.Count > 0)
            {
                var pool = _texturePools[^1];
                var layout = _textureSetLayout;
                var info = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = pool,
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
                        ImageLayout = imageLayout,
                    });
                    return (set, pool);
                }

                if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
                    result.Check("vkAllocateDescriptorSets (UI texture)");
            }

            _texturePools.Add(CreateTexturePool());
        }

        throw new VulkanException("[UI] Could not allocate a texture descriptor set.");
    }

    private DescriptorPool CreateTexturePool()
    {
        var size = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = TextureSetsPerPool };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = TextureSetsPerPool,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        _ctx.Vk.CreateDescriptorPool(_ctx.Device, in info, null, out var pool).Check("vkCreateDescriptorPool (UI textures)");
        return pool;
    }

    /// <summary>Frees a set once the frames that may still bind it have completed.</summary>
    private void FreeTextureSet(DescriptorSet set, DescriptorPool pool)
    {
        if (set.Handle != 0 && pool.Handle != 0)
            _pendingSets.Enqueue((ReleaseFrame, pool, set));
    }

    private void CollectReleases(ulong completedFrame)
    {
        _arena.Collect(completedFrame);
        while (_pendingSets.TryPeek(out var head) && head.Frame <= completedFrame)
        {
            _pendingSets.Dequeue();
            var set = head.Set;
            _ctx.Vk.FreeDescriptorSets(_ctx.Device, head.Pool, 1, &set).Check("vkFreeDescriptorSets (UI)");
        }
    }

    private void EnsureGradientCapacity(int perFrame)
    {
        if (perFrame <= _gradientCapacity)
            return;
        var capacity = Math.Max(perFrame, _gradientCapacity * 2);
        _gradientBuffer?.Dispose(); // deletion queue
        _gradientBuffer = GpuBuffer.Create(_ctx, _gradientStride * (ulong)capacity * IVulkanContext.MaxFramesInFlight,
            BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        _gradientCapacity = capacity;

        // The set may be bound by a frame in flight: replace it rather than updating it in place.
        if (_uniformSet.Handle != 0)
        {
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_uniformPool));
            _uniformPool = PipelineBuilder.CreatePool(_ctx, 1,
                [new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = 1 }], "UI uniforms");
            _uniformSet = PipelineBuilder.AllocateSet(_ctx, _uniformPool, _uniformSetLayout, "UI uniforms");
        }

        var bufferInfo = new DescriptorBufferInfo { Buffer = _gradientBuffer.Handle, Offset = 0, Range = GradientUboSize };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _uniformSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            PBufferInfo = &bufferInfo,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
    }

    // ── Layer and filter targets (swapchain-sized) ───────────────────────────────────────────────────────────

    private sealed class Target
    {
        public GpuImage Image = null!;
        public Framebuffer Framebuffer;
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public bool Initialized; // written once: later passes may load it
    }

    private Extent2D _targetExtent;
    private GpuImage? _stencil;
    private readonly List<Target> _layers = [];
    private readonly Target?[] _post = new Target?[4]; // primary, secondary, tertiary, blend mask
    private int _primary;   // index into _post of the current primary (swapped by filters)
    private int _secondary = 1;
    private const int Tertiary = 2;
    private const int BlendMask = 3;

    private void EnsureTargets(Extent2D extent, int layerCount)
    {
        if (extent.Width != _targetExtent.Width || extent.Height != _targetExtent.Height)
        {
            DestroyTargets();
            _targetExtent = extent;
            var stencilDesc = new GpuImageDesc(extent.Width, extent.Height, _stencilFormat, ImageUsageFlags.DepthStencilAttachmentBit)
            {
                ViewAspect = FormatInfo.AttachmentAspect(_stencilFormat),
            };
            _stencil = GpuImage.Create(_ctx, stencilDesc);
        }

        while (_layers.Count < layerCount)
            _layers.Add(CreateTarget(_layerPassLoad, true, "UI layer"));
    }

    private Target Post(int index)
    {
        if (_post[index] is { } existing)
            return existing;
        var target = CreateTarget(_postPassDiscard, false, "UI filter target");
        _post[index] = target;
        return target;
    }

    private Target CreateTarget(RenderPass pass, bool withStencil, string what)
    {
        var image = GpuImage.Create(_ctx, new GpuImageDesc(_targetExtent.Width, _targetExtent.Height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit));
        var target = new Target
        {
            Image = image,
            Framebuffer = CreateFramebuffer(pass, image.View, withStencil ? _stencil!.View : default, _targetExtent.Width,
                _targetExtent.Height, what),
        };
        (target.Set, target.Pool) = AllocateTextureSet(image.View, _linearSampler, ImageLayout.ShaderReadOnlyOptimal);
        return target;
    }

    private void DestroyTarget(Target target)
    {
        _ctx.Deletions.Enqueue(GpuDeletion.Of(target.Framebuffer));
        FreeTextureSet(target.Set, target.Pool);
        target.Image.Dispose();
    }

    private void DestroyTargets()
    {
        foreach (var layer in _layers)
            DestroyTarget(layer);
        _layers.Clear();
        for (var i = 0; i < _post.Length; i++)
        {
            if (_post[i] is { } p)
                DestroyTarget(p);
            _post[i] = null;
        }

        _stencil?.Dispose();
        _stencil = null;
        _targetExtent = default;
    }

    // ── Teardown ─────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnDisposed()
    {
        _ctx.RemoveOverlayRenderer(this);
        DropPreloads();
        DestroyTargets();
        _arena.Dispose();
        foreach (var slot in _textures.AsSpan(0, _textureCount))
        {
            slot.Texture?.Dispose();
            slot.SavedImage?.Dispose();
            if (slot.SavedFramebuffer.Handle != 0)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(slot.SavedFramebuffer));
        }

        _textures = [];
        _textureCount = 0;
        _white.Dispose();
        _gradientBuffer?.Dispose();

        // Pools free their sets with them.
        _pendingSets.Clear();
        foreach (var pool in _texturePools)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pool));
        _texturePools.Clear();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_uniformPool));

        foreach (var p in (ReadOnlySpan<Pipeline>)[.. _geometryPipelines, .. _gradientPipelines, .. _compositeBlendPipelines,
                     .. _compositeReplacePipelines, _clipReplacePipeline, _clipIncrementPipeline, _postCopyPipeline, _postBlendPipeline,
                     _colorMatrixPipeline, _blendMaskPipeline, _blurPipeline, _dropShadowPipeline, _overlayPipeline])
            _ctx.Deletions.Enqueue(GpuDeletion.Of(p));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_textureSetLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_uniformSetLayout));
        foreach (var pass in (ReadOnlySpan<RenderPass>)[_layerPassClearAll, _layerPassClearColor, _layerPassLoad, _postPassDiscard, _postPassLoad])
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pass));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_linearSampler));
    }
}
