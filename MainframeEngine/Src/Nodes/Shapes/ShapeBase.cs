using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public abstract class ShapeBase : Node3D
{
    public Color Color { get; set; } = Color.White;

    private const int LightsUboSize = LightEnvironment.UboSize;

    protected IVulkanContext? VkCtx { get; private set; }
    protected static ShadowSystem? Shadows => ShadowSystem;
    [StructLayout(LayoutKind.Sequential)]
    protected struct VpUbo
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    [StructLayout(LayoutKind.Sequential)]
    protected struct PushConstant
    {
        public Matrix4x4 Model; // 64 bytes
        public Vector4   Color; // 16 bytes → total 80 bytes
    }

    // Vulkan lit pipeline resources
    private PipelineLayout      _pipelineLayout;
    private Pipeline            _pipeline;
    private DescriptorSetLayout _vpDescSetLayout;
    private DescriptorSetLayout _lightsDescSetLayout;
    private DescriptorPool      _descPool;
    private DescriptorSet[]     _vpDescSets      = null!;
    private VkBuffer[]          _vpUboBuffers    = null!;
    private DeviceMemory[]      _vpUboMemory     = null!;
    private nint[]              _vpUboMapped     = null!;
    private DescriptorSet[]     _lightsDescSets   = null!;
    private VkBuffer[]          _lightsUboBuffers = null!;
    private DeviceMemory[]      _lightsUboMemory  = null!;
    private nint[]              _lightsUboMapped  = null!;

    // -------------------------------------------------------------------------
    // Init — called once from subclass constructor, after geometry buffers are created
    // -------------------------------------------------------------------------

    protected unsafe void InitLitVulkan(
        IVulkanContext ctx,
        VertexInputBindingDescription vertexBinding,
        VertexInputAttributeDescription[] vertexAttribs,
        string vertSpvPath,
        string fragSpvPath,
        CullModeFlags cullMode = CullModeFlags.BackBit)
    {
        VkCtx   = ctx;

        var vk         = ctx.Vk;
        var device     = ctx.Device;
        var imageCount = ctx.SwapchainImageCount;

        // --- Descriptor set layouts ---
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
        if (vk.CreateDescriptorSetLayout(device, in vpLayoutInfo, null, out _vpDescSetLayout) != Result.Success)
            throw new VulkanException("[Vulkan] LitShape: Failed to create VP descriptor set layout.");

        var lightsBinding = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.FragmentBit,
        };
        var lightsLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &lightsBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, in lightsLayoutInfo, null, out _lightsDescSetLayout) != Result.Success)
            throw new VulkanException("[Vulkan] LitShape: Failed to create Lights descriptor set layout.");

        // --- UBO buffers ---
        _vpUboBuffers     = new VkBuffer[imageCount];
        _vpUboMemory      = new DeviceMemory[imageCount];
        _vpUboMapped      = new nint[imageCount];
        _lightsUboBuffers = new VkBuffer[imageCount];
        _lightsUboMemory  = new DeviceMemory[imageCount];
        _lightsUboMapped  = new nint[imageCount];

        for (int i = 0; i < imageCount; i++)
        {
            CreateBuffer(ctx, (ulong)sizeof(VpUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vpUboBuffers[i], out _vpUboMemory[i]);
            void* ptr;
            vk.MapMemory(device, _vpUboMemory[i], 0, (ulong)sizeof(VpUbo), 0, &ptr);
            _vpUboMapped[i] = (nint)ptr;

            CreateBuffer(ctx, (ulong)LightsUboSize,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _lightsUboBuffers[i], out _lightsUboMemory[i]);
            vk.MapMemory(device, _lightsUboMemory[i], 0, (ulong)LightsUboSize, 0, &ptr);
            _lightsUboMapped[i] = (nint)ptr;
        }

        // --- Descriptor pool (VP + Lights, imageCount each) ---
        var poolSize = new DescriptorPoolSize
        {
            Type            = DescriptorType.UniformBuffer,
            DescriptorCount = imageCount * 2,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType         = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes    = &poolSize,
            MaxSets       = imageCount * 2,
        };
        if (vk.CreateDescriptorPool(device, in poolInfo, null, out _descPool) != Result.Success)
            throw new VulkanException("[Vulkan] LitShape: Failed to create descriptor pool.");

        // --- Allocate VP descriptor sets ---
        var vpLayouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) vpLayouts[i] = _vpDescSetLayout;
        var vpAlloc = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _descPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = vpLayouts,
        };
        _vpDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _vpDescSets)
            if (vk.AllocateDescriptorSets(device, in vpAlloc, p) != Result.Success)
                throw new VulkanException("[Vulkan] LitShape: Failed to allocate VP descriptor sets.");

        // --- Allocate Lights descriptor sets ---
        var lightsLayouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) lightsLayouts[i] = _lightsDescSetLayout;
        var lightsAlloc = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _descPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = lightsLayouts,
        };
        _lightsDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _lightsDescSets)
            if (vk.AllocateDescriptorSets(device, in lightsAlloc, p) != Result.Success)
                throw new VulkanException("[Vulkan] LitShape: Failed to allocate Lights descriptor sets.");

        // --- Write descriptor sets ---
        for (int i = 0; i < imageCount; i++)
        {
            var vpBuf = new DescriptorBufferInfo
            { Buffer = _vpUboBuffers[i], Offset = 0, Range = (ulong)sizeof(VpUbo) };
            var vpWrite = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _vpDescSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &vpBuf,
            };
            var lightsBuf = new DescriptorBufferInfo
            { Buffer = _lightsUboBuffers[i], Offset = 0, Range = (ulong)LightsUboSize };
            var lightsWrite = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _lightsDescSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &lightsBuf,
            };
            var writes = stackalloc[] { vpWrite, lightsWrite };
            vk.UpdateDescriptorSets(device, 2, writes, 0, null);
        }

        // --- Shaders ---
        var vertCode   = File.ReadAllBytes(vertSpvPath);
        var fragCode   = File.ReadAllBytes(fragSpvPath);
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

        fixed (VertexInputAttributeDescription* attribsPtr = vertexAttribs)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType                           = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount   = 1,
                PVertexBindingDescriptions      = &vertexBinding,
                VertexAttributeDescriptionCount = (uint)vertexAttribs.Length,
                PVertexAttributeDescriptions    = attribsPtr,
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
                PolygonMode             = PolygonMode.Fill,
                LineWidth               = 1f,
                CullMode                = cullMode,
                FrontFace               = FrontFace.CounterClockwise,
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

            // Pipeline layout: set=0 (VP), set=1 (Lights), [set=2 (Shadows)], push constants
            var setLayouts = stackalloc DescriptorSetLayout[3];
            setLayouts[0] = _vpDescSetLayout;
            setLayouts[1] = _lightsDescSetLayout;
            uint numSetLayouts = 2;
            if (Shadows is not null) { setLayouts[2] = Shadows.MainDescSetLayout; numSetLayouts = 3; }
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset     = 0,
                Size       = (uint)sizeof(PushConstant),
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType                  = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount         = numSetLayouts,
                PSetLayouts            = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges    = &pushRange,
            };
            if (vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out _pipelineLayout) != Result.Success)
                throw new VulkanException("[Vulkan] LitShape: Failed to create pipeline layout.");

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
                Layout              = _pipelineLayout,
                RenderPass          = ctx.RenderPass,
                Subpass             = 0,
            };
            if (vk.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out _pipeline) != Result.Success)
                throw new VulkanException("[Vulkan] LitShape: Failed to create graphics pipeline.");
        }

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    // -------------------------------------------------------------------------
    // Draw — sealed; subclasses implement DrawGeometry instead
    // -------------------------------------------------------------------------

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        base.Draw(camera, lightEnvironment);
        if (VkCtx is not null)
            DrawLit(camera, lightEnvironment);
    }

    /// <summary>Bind vertex/index buffers and issue the draw call for the main pass.</summary>
    protected abstract void DrawGeometry(CommandBuffer cb);

    private unsafe void DrawLit(ICamera camera, LightEnvironment lightEnvironment)
    {
        var vk       = VkCtx!.Vk;
        var cb       = VkCtx.CurrentCommandBuffer;
        var extent   = VkCtx.SwapchainExtent;
        var imageIdx = VkCtx.CurrentImageIndex;

        *(VpUbo*)(void*)_vpUboMapped[imageIdx] = new VpUbo
        {
            View       = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix,
        };
        lightEnvironment.WriteUbo(new Span<byte>((void*)_lightsUboMapped[imageIdx], LightsUboSize), camera.Position);

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

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);

        var sets = stackalloc DescriptorSet[3];
        sets[0] = _vpDescSets[imageIdx];
        sets[1] = _lightsDescSets[imageIdx];
        uint numSets = 2;
        if (Shadows is not null) { sets[2] = Shadows.GetMainSet(); numSets = 3; }
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout,
            0, numSets, sets, 0, null);

        var push = new PushConstant
        {
            Model = ModelMatrix,
            Color = new Vector4(Color.R, Color.G, Color.B, Color.A) / 255f,
        };
        vk.CmdPushConstants(cb, _pipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(PushConstant), &push);

        DrawGeometry(cb);
    }

    // -------------------------------------------------------------------------
    // Dispose — call from subclass Dispose after cleaning up geometry buffers
    // -------------------------------------------------------------------------

    public override unsafe void Dispose()
    {
        if (VkCtx is null) return;
        var vk     = VkCtx.Vk;
        var device = VkCtx.Device;
        vk.DeviceWaitIdle(device);

        for (int i = 0; i < _vpUboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _vpUboMemory[i]);
            vk.DestroyBuffer(device, _vpUboBuffers[i], null);
            vk.FreeMemory(device, _vpUboMemory[i], null);
        }
        for (int i = 0; i < _lightsUboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _lightsUboMemory[i]);
            vk.DestroyBuffer(device, _lightsUboBuffers[i], null);
            vk.FreeMemory(device, _lightsUboMemory[i], null);
        }

        vk.DestroyDescriptorPool(device, _descPool, null);
        vk.DestroyDescriptorSetLayout(device, _lightsDescSetLayout, null);
        vk.DestroyDescriptorSetLayout(device, _vpDescSetLayout, null);
        vk.DestroyPipeline(device, _pipeline, null);
        vk.DestroyPipelineLayout(device, _pipelineLayout, null);

        base.Dispose();
    }


    // -------------------------------------------------------------------------
    // Vulkan helpers (available to subclasses for geometry buffer creation)
    // -------------------------------------------------------------------------

    protected static unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
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
        if (vk.CreateBuffer(ctx.Device, in bi, null, out buffer) != Result.Success)
            throw new VulkanException("[Vulkan] LitShape: Failed to create buffer.");
        vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var ai = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };
        if (vk.AllocateMemory(ctx.Device, in ai, null, out memory) != Result.Success)
            throw new VulkanException("[Vulkan] LitShape: Failed to allocate buffer memory.");
        vk.BindBufferMemory(ctx.Device, buffer, memory, 0);
    }

    protected static unsafe void CopyBuffer(IVulkanContext ctx, VkBuffer src, VkBuffer dst, ulong size)
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
        vk.AllocateCommandBuffers(ctx.Device, in allocInfo, &cb);
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
            SType              = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers    = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, in submitInfo, default);
        vk.QueueWaitIdle(ctx.GraphicsQueue);
        vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, &cb);
    }

    protected static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags properties)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        throw new VulkanException("[Vulkan] LitShape: No suitable memory type found.");
    }

    protected unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode    = (uint*)ptr,
            };
            if (ctx.Vk.CreateShaderModule(ctx.Device, in ci, null, out var module) != Result.Success)
                throw new VulkanException("[Vulkan] LitShape: Failed to create shader module.");
            return module;
        }
    }
}