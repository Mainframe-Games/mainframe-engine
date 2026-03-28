using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public class Quad : ShapeBase, IDisposable
{
    // csharpier-ignore
    private static readonly float[] Vertices =
    [
         0.5f,  0.5f, 0.0f,
         0.5f, -0.5f, 0.0f,
        -0.5f, -0.5f, 0.0f,
        -0.5f,  0.5f, 0.0f,
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3,
    ];

    // Vulkan
    private IVulkanContext? _vkCtx;
    private VkBuffer _vkVertexBuffer;
    private DeviceMemory _vkVertexBufferMemory;
    private VkBuffer _vkIndexBuffer;
    private DeviceMemory _vkIndexBufferMemory;
    private PipelineLayout _vkPipelineLayout;
    private Pipeline _vkPipeline;

    // set=0  VP UBO
    private DescriptorSetLayout _vkVpDescSetLayout;
    private DescriptorPool _vkDescPool;
    private DescriptorSet[] _vkVpDescSets = null!;
    private VkBuffer[] _vkVpUboBuffers = null!;
    private DeviceMemory[] _vkVpUboMemory = null!;
    private nint[] _vkVpUboMapped = null!;

    [StructLayout(LayoutKind.Sequential)]
    private struct VpUbo
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstant
    {
        public Matrix4x4 Model; // 64 bytes
        public Vector4 Color;   // 16 bytes  → total 80 bytes
    }

    public Quad(IRenderer renderer)
    {
        if (renderer is IVulkanContext vkCtx)
        {
            _vkCtx = vkCtx;
            CreateVkVertexBuffer(vkCtx);
            CreateVkIndexBuffer(vkCtx);
            CreateVkPipeline(vkCtx);
        }

        Scale = new Vector3(1, 1, 0);
    }

    public void Draw(ICamera camera)
    {
        if (_vkCtx is not null)
            DrawVulkan(camera);
    }

    public unsafe void Dispose()
    {
        if (_vkCtx is null) return;
        var vk     = _vkCtx.Vk;
        var device = _vkCtx.Device;
        vk.DeviceWaitIdle(device);

        for (int i = 0; i < _vkVpUboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _vkVpUboMemory[i]);
            vk.DestroyBuffer(device, _vkVpUboBuffers[i], null);
            vk.FreeMemory(device, _vkVpUboMemory[i], null);
        }

        vk.DestroyDescriptorPool(device, _vkDescPool, null);
        vk.DestroyDescriptorSetLayout(device, _vkVpDescSetLayout, null);
        vk.DestroyPipeline(device, _vkPipeline, null);
        vk.DestroyPipelineLayout(device, _vkPipelineLayout, null);
        vk.DestroyBuffer(device, _vkIndexBuffer, null);
        vk.FreeMemory(device, _vkIndexBufferMemory, null);
        vk.DestroyBuffer(device, _vkVertexBuffer, null);
        vk.FreeMemory(device, _vkVertexBufferMemory, null);
    }

    // -------------------------------------------------------------------------
    // Vulkan draw
    // -------------------------------------------------------------------------

    private unsafe void DrawVulkan(ICamera camera)
    {
        var vk       = _vkCtx!.Vk;
        var cb       = _vkCtx.CurrentCommandBuffer;
        var extent   = _vkCtx.SwapchainExtent;
        var imageIdx = _vkCtx.CurrentImageIndex;

        // Update VP UBO
        *(VpUbo*)(void*)_vkVpUboMapped[imageIdx] = new VpUbo
        {
            View       = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix,
        };

        var viewport = new Viewport
        {
            X = 0, Y = extent.Height,
            Width = extent.Width, Height = -(float)extent.Height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _vkPipeline);

        var vb     = _vkVertexBuffer;
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        vk.CmdBindIndexBuffer(cb, _vkIndexBuffer, 0, IndexType.Uint32);

        var set = _vkVpDescSets[imageIdx];
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _vkPipelineLayout,
            0, 1, &set, 0, null);

        var push = new PushConstant { Model = ModelMatrix, Color = new Vector4(Color, 1f) };
        vk.CmdPushConstants(cb, _vkPipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(PushConstant), &push);

        vk.CmdDrawIndexed(cb, (uint)Indices.Length, 1, 0, 0, 0);
    }

    // -------------------------------------------------------------------------
    // Vulkan resource creation
    // -------------------------------------------------------------------------

    private unsafe void CreateVkVertexBuffer(IVulkanContext ctx)
    {
        var vk   = ctx.Vk;
        var size = (ulong)(Vertices.Length * sizeof(float));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        fixed (float* src = Vertices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        vk.UnmapMemory(ctx.Device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vkVertexBuffer, out _vkVertexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vkVertexBuffer, size);
        vk.DestroyBuffer(ctx.Device, stagingBuffer, null);
        vk.FreeMemory(ctx.Device, stagingMemory, null);
    }

    private unsafe void CreateVkIndexBuffer(IVulkanContext ctx)
    {
        var vk   = ctx.Vk;
        var size = (ulong)(Indices.Length * sizeof(uint));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        vk.MapMemory(ctx.Device, stagingMemory, 0, size, 0, &mapped);
        fixed (uint* src = Indices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        vk.UnmapMemory(ctx.Device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vkIndexBuffer, out _vkIndexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vkIndexBuffer, size);
        vk.DestroyBuffer(ctx.Device, stagingBuffer, null);
        vk.FreeMemory(ctx.Device, stagingMemory, null);
    }

    private unsafe void CreateVkPipeline(IVulkanContext ctx)
    {
        var vk         = ctx.Vk;
        var device     = ctx.Device;
        var imageCount = ctx.SwapchainImageCount;

        // --- Descriptor set layout: set=0, binding=0 = VP UBO ---
        var vpBinding = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.VertexBit,
        };
        var vpLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &vpBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, vpLayoutInfo, null, out _vkVpDescSetLayout) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to create VP descriptor set layout!");

        // --- VP UBO buffers ---
        _vkVpUboBuffers = new VkBuffer[imageCount];
        _vkVpUboMemory  = new DeviceMemory[imageCount];
        _vkVpUboMapped  = new nint[imageCount];

        for (int i = 0; i < imageCount; i++)
        {
            CreateBuffer(ctx, (ulong)sizeof(VpUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vkVpUboBuffers[i], out _vkVpUboMemory[i]);
            void* ptr;
            vk.MapMemory(device, _vkVpUboMemory[i], 0, (ulong)sizeof(VpUbo), 0, &ptr);
            _vkVpUboMapped[i] = (nint)ptr;
        }

        // --- Descriptor pool ---
        var poolSize = new DescriptorPoolSize
        {
            Type            = DescriptorType.UniformBuffer,
            DescriptorCount = imageCount,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType         = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes    = &poolSize,
            MaxSets       = imageCount,
        };
        if (vk.CreateDescriptorPool(device, poolInfo, null, out _vkDescPool) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to create descriptor pool!");

        // --- Allocate VP descriptor sets ---
        var layouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) layouts[i] = _vkVpDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _vkDescPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = layouts,
        };
        _vkVpDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _vkVpDescSets)
            if (vk.AllocateDescriptorSets(device, allocInfo, p) != Result.Success)
                throw new Exception("[Vulkan] Quad: Failed to allocate VP descriptor sets!");

        // --- Write descriptor sets ---
        for (int i = 0; i < imageCount; i++)
        {
            var bufInfo = new DescriptorBufferInfo
                { Buffer = _vkVpUboBuffers[i], Offset = 0, Range = (ulong)sizeof(VpUbo) };
            var write = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _vkVpDescSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &bufInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        }

        // --- Shaders ---
        var vertCode   = File.ReadAllBytes("Content/Shaders/Shapes/Quad.vk.vert.spv");
        var fragCode   = File.ReadAllBytes("Content/Shaders/Shapes/Quad.vk.frag.spv");
        var vertModule = CreateShaderModule(ctx, vertCode);
        var fragModule = CreateShaderModule(ctx, fragCode);
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc[]
        {
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName  = entryPoint,
            },
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName  = entryPoint,
            },
        };

        // Vertex layout: X Y Z → stride 12 bytes
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding   = 0,
            Stride    = 3 * sizeof(float),
            InputRate = VertexInputRate.Vertex,
        };
        var attrib = new VertexInputAttributeDescription
        {
            Location = 0, Binding = 0,
            Format   = Silk.NET.Vulkan.Format.R32G32B32Sfloat,
            Offset   = 0,
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType                           = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount   = 1,
            PVertexBindingDescriptions      = &bindingDesc,
            VertexAttributeDescriptionCount = 1,
            PVertexAttributeDescriptions    = &attrib,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType                  = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology               = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType         = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount  = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType                   = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable        = false,
            RasterizerDiscardEnable = false,
            PolygonMode             = Silk.NET.Vulkan.PolygonMode.Fill,
            LineWidth               = 1f,
            CullMode                = CullModeFlags.None,
            FrontFace               = FrontFace.Clockwise,
            DepthBiasEnable         = false,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType                = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable  = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType           = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable   = false,
            AttachmentCount = 1,
            PAttachments    = &colorBlendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState  = new PipelineDynamicStateCreateInfo
        {
            SType             = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates    = dynamicStates,
        };

        // Pipeline layout: set=0 (VP), push constants (model + color)
        var setLayout  = _vkVpDescSetLayout;
        var pushRange  = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset     = 0,
            Size       = (uint)sizeof(PushConstant),
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = 1,
            PSetLayouts            = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges    = &pushRange,
        };
        if (vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out _vkPipelineLayout) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to create pipeline layout!");

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType            = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable  = true,
            DepthWriteEnable = true,
            DepthCompareOp   = CompareOp.Less,
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType               = StructureType.GraphicsPipelineCreateInfo,
            StageCount          = 2,
            PStages             = stages,
            PVertexInputState   = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState      = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState   = &multisampling,
            PDepthStencilState  = &depthStencil,
            PColorBlendState    = &colorBlend,
            PDynamicState       = &dynamicState,
            Layout              = _vkPipelineLayout,
            RenderPass          = ctx.RenderPass,
            Subpass             = 0,
        };
        if (vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out _vkPipeline) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to create graphics pipeline!");

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    // -------------------------------------------------------------------------
    // Vulkan helpers
    // -------------------------------------------------------------------------

    private unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
        BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk = ctx.Vk;
        var bi = new BufferCreateInfo
        {
            SType       = StructureType.BufferCreateInfo,
            Size        = size,
            Usage       = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (vk.CreateBuffer(ctx.Device, bi, null, out buffer) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to create buffer!");
        vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var ai = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };
        if (vk.AllocateMemory(ctx.Device, ai, null, out memory) != Result.Success)
            throw new Exception("[Vulkan] Quad: Failed to allocate buffer memory!");
        vk.BindBufferMemory(ctx.Device, buffer, memory, 0);
    }

    private unsafe void CopyBuffer(IVulkanContext ctx, VkBuffer src, VkBuffer dst, ulong size)
    {
        var vk        = ctx.Vk;
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType              = StructureType.CommandBufferAllocateInfo,
            Level              = CommandBufferLevel.Primary,
            CommandPool        = ctx.CommandPool,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        vk.AllocateCommandBuffers(ctx.Device, allocInfo, &cb);
        vk.BeginCommandBuffer(cb, new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        });
        var region = new BufferCopy { Size = size };
        vk.CmdCopyBuffer(cb, src, dst, 1, &region);
        vk.EndCommandBuffer(cb);
        var submitInfo = new SubmitInfo
        {
            SType              = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers    = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, submitInfo, default);
        vk.QueueWaitIdle(ctx.GraphicsQueue);
        vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, &cb);
    }

    private static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags properties)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        throw new Exception("[Vulkan] Quad: No suitable memory type found!");
    }

    private unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode    = (uint*)ptr,
            };
            if (ctx.Vk.CreateShaderModule(ctx.Device, ci, null, out var module) != Result.Success)
                throw new Exception("[Vulkan] Quad: Failed to create shader module!");
            return module;
        }
    }
}
