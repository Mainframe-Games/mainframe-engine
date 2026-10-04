using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Base class for making scene grid.
/// </summary>
public abstract class SceneGrid : IDisposable
{
    protected static readonly Vector4 DefaultColor = new(1f, 1f, 1f, 0.1f);
    protected static readonly Vector4 Red = new(1f, 0f, 0f, 1f);
    protected static readonly Vector4 Yellow = new(1f, 1f, 0f, 1f);
    protected static readonly Vector4 Blue = new(0f, 0f, 1f, 1f);

    private protected readonly uint _vertexCount;

    // Vulkan
    private IVulkanContext? _vkCtx;
    private VkBuffer _vkVertexBuffer;
    private DeviceMemory _vkVertexBufferMemory;
    private PipelineLayout _vkPipelineLayout;
    private Pipeline _vkPipeline;
    private DescriptorSetLayout _vkDescriptorSetLayout;
    private DescriptorPool _vkDescriptorPool;
    private DescriptorSet[] _vkDescriptorSets = null!;
    private VkBuffer[] _vkUboBuffers = null!;
    private DeviceMemory[] _vkUboMemory = null!;
    private nint[] _vkUboMapped = null!;

    [StructLayout(LayoutKind.Sequential)]
    private struct VpUbo
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    protected struct Vertex(float x, float y, float z, Vector4 color)
    {
        public Vector3 Position = new(x, y, z);
        public Vector4 Color = color;
    }

    protected SceneGrid(IRenderer renderer, uint vertexCount)
    {
        _vertexCount = vertexCount;

        if (renderer is IVulkanContext vkCtx)
            _vkCtx = vkCtx;
    }

    public unsafe void Draw(ICamera camera)
    {
        if (_vkCtx is not null)
            DrawVulkan(camera);
    }

    public unsafe void Dispose()
    {
        if (_vkCtx is null) return;
        var vk = _vkCtx.Vk;
        var device = _vkCtx.Device;
        vk.DeviceWaitIdle(device);

        if (_vkUboBuffers is not null)
        {
            for (int i = 0; i < _vkUboBuffers.Length; i++)
            {
                vk.UnmapMemory(device, _vkUboMemory[i]);
                vk.DestroyBuffer(device, _vkUboBuffers[i], null);
                vk.FreeMemory(device, _vkUboMemory[i], null);
            }
        }

        vk.DestroyDescriptorPool(device, _vkDescriptorPool, null);
        vk.DestroyDescriptorSetLayout(device, _vkDescriptorSetLayout, null);
        vk.DestroyPipeline(device, _vkPipeline, null);
        vk.DestroyPipelineLayout(device, _vkPipelineLayout, null);
        vk.DestroyBuffer(device, _vkVertexBuffer, null);
        vk.FreeMemory(device, _vkVertexBufferMemory, null);
    }

    protected unsafe void BuildVertexArray(Vertex* vertices)
    {
        if (_vkCtx is not null)
            BuildVkVertexBuffer(vertices);
    }

    #region Vulkan

    private unsafe void DrawVulkan(ICamera camera)
    {
        var vk = _vkCtx!.Vk;
        var cb = _vkCtx.CurrentCommandBuffer;
        var extent = _vkCtx.SwapchainExtent;
        var frameSlot = _vkCtx.FrameSlot;

        // Update VP UBO for this image
        *(VpUbo*)(void*)_vkUboMapped[frameSlot] = new VpUbo
        {
            View = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix,
        };

        // Negative height flips Vulkan's Y axis to match right-handed convention (Y+ up)
        var viewport = new Viewport
        {
            X = 0,
            Y = extent.Height,
            Width = extent.Width,
            Height = -(float)extent.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _vkPipeline);

        var vb = _vkVertexBuffer;
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);

        var descSet = _vkDescriptorSets[frameSlot];
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _vkPipelineLayout,
            0, 1, &descSet, 0, null);

        vk.CmdDraw(cb, _vertexCount, 1, 0, 0);
    }

    private unsafe void BuildVkVertexBuffer(Vertex* vertices)
    {
        var ctx = _vkCtx!;
        var size = (ulong)(_vertexCount * (uint)sizeof(Vertex));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        ctx.Vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        Unsafe.CopyBlock(mapped, vertices, (uint)size);
        ctx.Vk.UnmapMemory(ctx.Device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vkVertexBuffer, out _vkVertexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vkVertexBuffer, size);

        ctx.Vk.DestroyBuffer(ctx.Device, stagingBuffer, null);
        ctx.Vk.FreeMemory(ctx.Device, stagingMemory, null);

        CreateVkPipeline(ctx);
    }

    private unsafe void CreateVkPipeline(IVulkanContext ctx)
    {
        var vk = ctx.Vk;
        var device = ctx.Device;
        const uint slotCount = IVulkanContext.MaxFramesInFlight; // per frame slot, never per swapchain image

        // --- Descriptor set layout (binding 0 = VP UBO) ---
        var uboBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        var descLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &uboBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, in descLayoutInfo, null, out _vkDescriptorSetLayout) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create descriptor set layout!");

        // --- UBO buffers (one per frame slot, persistently mapped) ---
        _vkUboBuffers = new VkBuffer[slotCount];
        _vkUboMemory = new DeviceMemory[slotCount];
        _vkUboMapped = new nint[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            CreateBuffer(ctx, (ulong)sizeof(VpUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vkUboBuffers[i], out _vkUboMemory[i]);

            void* ptr;
            vk.MapMemory(device, _vkUboMemory[i], 0, (ulong)sizeof(VpUbo), 0, &ptr);
            _vkUboMapped[i] = (nint)ptr;
        }

        // --- Descriptor pool ---
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = slotCount,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = slotCount,
        };
        if (vk.CreateDescriptorPool(device, in poolInfo, null, out _vkDescriptorPool) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create descriptor pool!");

        // --- Descriptor sets ---
        var layouts = stackalloc DescriptorSetLayout[(int)slotCount];
        for (int i = 0; i < slotCount; i++) layouts[i] = _vkDescriptorSetLayout;

        var dsAllocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _vkDescriptorPool,
            DescriptorSetCount = slotCount,
            PSetLayouts = layouts,
        };
        _vkDescriptorSets = new DescriptorSet[slotCount];
        fixed (DescriptorSet* ptr = _vkDescriptorSets)
            if (vk.AllocateDescriptorSets(device, in dsAllocInfo, ptr) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to allocate descriptor sets!");

        for (int i = 0; i < slotCount; i++)
        {
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = _vkUboBuffers[i],
                Offset = 0,
                Range = (ulong)sizeof(VpUbo),
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _vkDescriptorSets[i],
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        }

        // --- Shaders ---
        var vertCode = File.ReadAllBytes(ContentPaths.Resolve("Shaders/SceneGrid/SceneGrid.vk.vert.spv"));
        var fragCode = File.ReadAllBytes(ContentPaths.Resolve("Shaders/SceneGrid/SceneGrid.vk.frag.spv"));
        var vertModule = CreateShaderModule(ctx, vertCode);
        var fragModule = CreateShaderModule(ctx, fragCode);
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var vertStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertModule,
            PName = entryPoint,
        };
        var fragStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule,
            PName = entryPoint,
        };
        var stages = stackalloc[] { vertStage, fragStage };

        // Vertex layout: Vertex { Vector3 Position, Vector4 Color } = 28 bytes
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)sizeof(Vertex),
            InputRate = VertexInputRate.Vertex,
        };
        var attribs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat,    Offset = 0  },
            new() { Location = 1, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, Offset = 12 },
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 2,
            PVertexAttributeDescriptions = attribs,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.LineList,
            PrimitiveRestartEnable = false,
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
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill,
            LineWidth = 1f,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        // Alpha blending
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        // Pipeline layout: descriptor set only, no push constants
        var descSetLayout = _vkDescriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descSetLayout,
            PushConstantRangeCount = 0,
        };
        if (vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out _vkPipelineLayout) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create SceneGrid pipeline layout!");

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType            = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable  = true,
            DepthWriteEnable = true,
            DepthCompareOp   = CompareOp.Less,
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
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
            Layout = _vkPipelineLayout,
            RenderPass = ctx.RenderPass,
            Subpass = 0,
        };
        if (vk.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out _vkPipeline) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create SceneGrid graphics pipeline!");

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    private static unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
        BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk = ctx.Vk;
        var device = ctx.Device;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (vk.CreateBuffer(device, in bufferInfo, null, out buffer) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to create buffer!");

        vk.GetBufferMemoryRequirements(device, buffer, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };
        if (vk.AllocateMemory(device, in allocInfo, null, out memory) != Result.Success)
            throw new VulkanException("[Vulkan] Failed to allocate buffer memory!");
        vk.BindBufferMemory(device, buffer, memory, 0);
    }

    private static unsafe void CopyBuffer(IVulkanContext ctx, VkBuffer src, VkBuffer dst, ulong size)
    {
        var vk = ctx.Vk;
        var device = ctx.Device;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = ctx.CommandPool,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        vk.AllocateCommandBuffers(device, in allocInfo, &cb);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cb, in beginInfo);
        var region = new BufferCopy { Size = size };
        vk.CmdCopyBuffer(cb, src, dst, 1, &region);
        vk.EndCommandBuffer(cb);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, in submitInfo, default);
        vk.QueueWaitIdle(ctx.GraphicsQueue);
        vk.FreeCommandBuffers(device, ctx.CommandPool, 1, &cb);
    }

    private static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags properties)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        throw new VulkanException("[Vulkan] No suitable memory type found!");
    }

    private unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)ptr,
            };
            if (ctx.Vk.CreateShaderModule(ctx.Device, in createInfo, null, out var module) != Result.Success)
                throw new VulkanException("[Vulkan] Failed to create shader module!");
            return module;
        }
    }

    #endregion
}
