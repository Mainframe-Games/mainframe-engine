using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Colour blending of a pipeline's (single) colour attachment.</summary>
internal enum BlendMode : byte
{
    /// <summary>No blending.</summary>
    Opaque,

    /// <summary>Straight alpha: <c>rgb = src·a + dst·(1−a)</c>, <c>a = src + dst·(1−a)</c>.</summary>
    Alpha,

    /// <summary>Straight alpha that leaves destination alpha alone (<c>a = src</c>); the scene grid.</summary>
    AlphaKeepSourceAlpha,

    /// <summary>Premultiplied alpha: <c>rgb = src + dst·(1−a)</c> (Spine, anything whose colour is already × alpha).</summary>
    Premultiplied,

    /// <summary>
    /// Straight alpha that marks itself in the destination alpha: <c>rgb = src·a + dst·(1−a)</c>, <c>a = dst·(1−a)</c>.
    /// Water in the main view (ADR 0166): opaque surfaces leave the scene alpha at 1, so 1 − alpha is how much of the pixel
    /// is animated water with no motion vectors of its own, TAA's reactive mask.
    /// </summary>
    AlphaReactive,

    /// <summary>
    /// The colour replaces the destination, the source alpha marks reactivity: <c>rgb = src</c>, <c>a = dst·(1−src.a)</c>.
    /// Refracting water in the main view (ADR 0173): it composes what lies behind it from the scene copy itself, and its
    /// alpha says how much TAA's history to drop there.
    /// </summary>
    ColorOnlyReactive,

    /// <summary>The colour replaces the destination, the destination alpha stays: <c>rgb = src</c>, <c>a = dst</c> (ADR 0173).</summary>
    ColorOnly,
}

/// <summary>Fixed-function state for <see cref="PipelineBuilder"/>; viewport and scissor are always dynamic.</summary>
internal readonly record struct PipelineState()
{
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public CullModeFlags CullMode { get; init; } = CullModeFlags.None;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
    public CompareOp DepthCompare { get; init; } = CompareOp.Less;
    public BlendMode Blend { get; init; } = BlendMode.Opaque;
}

/// <summary>
/// Builds graphics pipelines with the engine's conventions (entry point <c>main</c>, dynamic viewport/scissor,
/// one colour attachment, single sample) through the shared <see cref="PipelineCache"/> and
/// <see cref="ShaderModuleCache"/>. Load-time only.
/// </summary>
internal static unsafe class PipelineBuilder
{
    private static readonly nint EntryPoint = SilkMarshal.StringToPtr("main"); // process lifetime

    public static Pipeline Create(IVulkanContext ctx, in PipelineState state, PipelineLayout layout, RenderPass renderPass,
        string vertexSpv, string fragmentSpv,
        ReadOnlySpan<VertexInputBindingDescription> bindings, ReadOnlySpan<VertexInputAttributeDescription> attributes,
        string what, SpecializationInfo* fragmentSpecialization = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = ctx.Shaders.Get(vertexSpv),
            PName = (byte*)EntryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = ctx.Shaders.Get(fragmentSpv),
            PName = (byte*)EntryPoint,
            PSpecializationInfo = fragmentSpecialization,
        };

        fixed (VertexInputBindingDescription* pBindings = bindings)
        fixed (VertexInputAttributeDescription* pAttributes = attributes)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindings.Length,
                PVertexBindingDescriptions = pBindings,
                VertexAttributeDescriptionCount = (uint)attributes.Length,
                PVertexAttributeDescriptions = pAttributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = state.Topology,
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
                CullMode = state.CullMode,
                FrontFace = state.FrontFace,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = state.DepthTest,
                DepthWriteEnable = state.DepthWrite,
                DepthCompareOp = state.DepthCompare,
            };
            var blendAttachment = BlendAttachment(state.Blend);
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
                RenderPass = renderPass,
                Subpass = 0,
            };
            return ctx.Pipelines.CreateGraphicsPipeline(info, what);
        }
    }

    private static PipelineColorBlendAttachmentState BlendAttachment(BlendMode mode)
    {
        const ColorComponentFlags all = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                        ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        return mode switch
        {
            BlendMode.Opaque => new() { BlendEnable = false, ColorWriteMask = all },
            BlendMode.Alpha => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.AlphaKeepSourceAlpha => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.AlphaReactive => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.ColorOnlyReactive => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.ColorOnly => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.Premultiplied => new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = all,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>A pipeline layout with the given set layouts and one push-constant range (size 0 = none).</summary>
    public static PipelineLayout CreateLayout(IVulkanContext ctx, ReadOnlySpan<DescriptorSetLayout> setLayouts,
        uint pushConstantSize, ShaderStageFlags pushStages, string what)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var range = new PushConstantRange { StageFlags = pushStages, Offset = 0, Size = pushConstantSize };
        fixed (DescriptorSetLayout* pLayouts = setLayouts)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = pLayouts,
                PushConstantRangeCount = pushConstantSize > 0 ? 1u : 0u,
                PPushConstantRanges = pushConstantSize > 0 ? &range : null,
            };
            ctx.Vk.CreatePipelineLayout(ctx.Device, in info, null, out var layout).Check($"vkCreatePipelineLayout ({what})");
            return layout;
        }
    }

    /// <summary>A descriptor set layout from bindings.</summary>
    public static DescriptorSetLayout CreateSetLayout(IVulkanContext ctx, ReadOnlySpan<DescriptorSetLayoutBinding> bindings, string what)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        fixed (DescriptorSetLayoutBinding* p = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = p,
            };
            ctx.Vk.CreateDescriptorSetLayout(ctx.Device, in info, null, out var layout).Check($"vkCreateDescriptorSetLayout ({what})");
            return layout;
        }
    }

    /// <summary>A descriptor pool for <paramref name="maxSets"/> sets with the given sizes (zero-count sizes are skipped).</summary>
    public static DescriptorPool CreatePool(IVulkanContext ctx, uint maxSets, ReadOnlySpan<DescriptorPoolSize> sizes, string what)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var filtered = stackalloc DescriptorPoolSize[sizes.Length];
        var count = 0;
        foreach (var size in sizes)
            if (size.DescriptorCount > 0)
                filtered[count++] = size;

        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = maxSets,
            PoolSizeCount = (uint)count,
            PPoolSizes = filtered,
        };
        ctx.Vk.CreateDescriptorPool(ctx.Device, in info, null, out var pool).Check($"vkCreateDescriptorPool ({what})");
        return pool;
    }

    /// <summary>Allocates one set of <paramref name="layout"/> from <paramref name="pool"/>.</summary>
    public static DescriptorSet AllocateSet(IVulkanContext ctx, DescriptorPool pool, DescriptorSetLayout layout, string what)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        ctx.Vk.AllocateDescriptorSets(ctx.Device, in info, out var set).Check($"vkAllocateDescriptorSets ({what})");
        return set;
    }

    /// <summary>Points a uniform-buffer binding at a buffer range.</summary>
    public static void WriteUniformBuffer(IVulkanContext ctx, DescriptorSet set, uint binding, in DescriptorBufferInfo info)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        fixed (DescriptorBufferInfo* p = &info)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = p,
            };
            ctx.Vk.UpdateDescriptorSets(ctx.Device, 1, &write, 0, null);
        }
    }

    /// <summary>Points a combined-image-sampler binding at an image.</summary>
    public static void WriteImage(IVulkanContext ctx, DescriptorSet set, uint binding, in DescriptorImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        fixed (DescriptorImageInfo* p = &info)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = p,
            };
            ctx.Vk.UpdateDescriptorSets(ctx.Device, 1, &write, 0, null);
        }
    }

    /// <summary>Points a sampled-image binding (no sampler) at an image; <paramref name="info"/>'s sampler is ignored.</summary>
    public static void WriteSampledImage(IVulkanContext ctx, DescriptorSet set, uint binding, in DescriptorImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var image = new DescriptorImageInfo { ImageView = info.ImageView, ImageLayout = info.ImageLayout };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            PImageInfo = &image,
        };
        ctx.Vk.UpdateDescriptorSets(ctx.Device, 1, &write, 0, null);
    }

    /// <summary>Viewport + scissor covering <paramref name="extent"/>; <paramref name="flipY"/> gives the main pass's +Y-up convention.</summary>
    public static void SetViewport(Vk vk, CommandBuffer cb, Extent2D extent, bool flipY)
    {
        ArgumentNullException.ThrowIfNull(vk);
        var viewport = flipY
            ? new Viewport { X = 0, Y = extent.Height, Width = extent.Width, Height = -(float)extent.Height, MinDepth = 0, MaxDepth = 1 }
            : new Viewport { X = 0, Y = 0, Width = extent.Width, Height = extent.Height, MinDepth = 0, MaxDepth = 1 };
        vk.CmdSetViewport(cb, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);
    }
}
