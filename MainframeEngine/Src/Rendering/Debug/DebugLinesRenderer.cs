using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Draws a <see cref="DebugLines"/> batch in the main render pass: a line-list pipeline over the scene grid's shaders
/// (position + colour, view/projection UBO), depth-tested but not depth-writing, alpha-blended. Vertex buffers and
/// UBOs are host-visible, persistently mapped and keyed by <see cref="IVulkanContext.FrameSlot"/>; a slot's vertex
/// buffer grows (doubling) when a frame has more lines than it holds, which is safe because the slot's previous
/// frame has completed once <see cref="IVulkanContext.FrameStarted"/>.
/// </summary>
/// <remarks>
/// One set of buffers per frame slot: <see cref="Draw"/> may be called once per frame (the engine renders one
/// viewport). Several viewports per frame (editor views) need per-call ring offsets.
/// </remarks>
internal sealed class DebugLinesRenderer : IDisposable
{
    private const int InitialVertexCapacity = 4096;

    private readonly IVulkanContext _ctx;
    private readonly VkBuffer[] _vertexBuffers = new VkBuffer[IVulkanContext.MaxFramesInFlight];
    private readonly DeviceMemory[] _vertexMemory = new DeviceMemory[IVulkanContext.MaxFramesInFlight];
    private readonly nint[] _vertexMapped = new nint[IVulkanContext.MaxFramesInFlight];
    private readonly int[] _vertexCapacity = new int[IVulkanContext.MaxFramesInFlight];
    private readonly VkBuffer[] _uboBuffers = new VkBuffer[IVulkanContext.MaxFramesInFlight];
    private readonly DeviceMemory[] _uboMemory = new DeviceMemory[IVulkanContext.MaxFramesInFlight];
    private readonly nint[] _uboMapped = new nint[IVulkanContext.MaxFramesInFlight];
    private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[IVulkanContext.MaxFramesInFlight];
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct VpUbo
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    public DebugLinesRenderer(IVulkanContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        try
        {
            CreateDescriptors();
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Records the batch into the current command buffer (inside the main render pass).</summary>
    public unsafe void Draw(DebugLines lines, ICamera camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vertices = lines.Vertices;
        if (vertices.Length == 0 || !_ctx.FrameStarted)
            return;

        var slot = _ctx.FrameSlot;
        EnsureVertexCapacity(slot, vertices.Length);
        fixed (DebugLineVertex* src = vertices)
            System.Buffer.MemoryCopy(src, (void*)_vertexMapped[slot], (long)_vertexCapacity[slot] * sizeof(DebugLineVertex),
                (long)vertices.Length * sizeof(DebugLineVertex));
        *(VpUbo*)_uboMapped[slot] = new VpUbo { View = camera.ViewMatrix, Projection = camera.ProjectionMatrix };

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        var extent = _ctx.SwapchainExtent;
        // Y-flipped viewport like every main-pass drawable (docs/design/coordinate-conventions.md).
        var viewport = new Viewport { X = 0, Y = extent.Height, Width = extent.Width, Height = -(float)extent.Height, MinDepth = 0, MaxDepth = 1 };
        vk.CmdSetViewport(cb, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var vb = _vertexBuffers[slot];
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        var set = _descriptorSets[slot];
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
        vk.CmdDraw(cb, (uint)vertices.Length, 1, 0, 0);
    }

    private unsafe void EnsureVertexCapacity(int slot, int vertexCount)
    {
        if (_vertexCapacity[slot] >= vertexCount)
            return;
        var capacity = Math.Max(InitialVertexCapacity, _vertexCapacity[slot]);
        while (capacity < vertexCount)
            capacity *= 2;

        // The slot's last use has completed (its fence was waited on before FrameStarted), so its buffer is idle.
        DestroyVertexBuffer(slot);
        var size = (ulong)capacity * (ulong)sizeof(DebugLineVertex);
        CreateHostBuffer(size, BufferUsageFlags.VertexBufferBit, out _vertexBuffers[slot], out _vertexMemory[slot], out _vertexMapped[slot]);
        _vertexCapacity[slot] = capacity;
    }

    private unsafe void DestroyVertexBuffer(int slot)
    {
        var vk = _ctx.Vk;
        if (_vertexMapped[slot] != 0)
            vk.UnmapMemory(_ctx.Device, _vertexMemory[slot]);
        if (_vertexBuffers[slot].Handle != 0)
            vk.DestroyBuffer(_ctx.Device, _vertexBuffers[slot], null);
        if (_vertexMemory[slot].Handle != 0)
            vk.FreeMemory(_ctx.Device, _vertexMemory[slot], null);
        _vertexBuffers[slot] = default;
        _vertexMemory[slot] = default;
        _vertexMapped[slot] = 0;
        _vertexCapacity[slot] = 0;
    }

    private unsafe void CreateDescriptors()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;
        const uint slotCount = IVulkanContext.MaxFramesInFlight;

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        Check(vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out _descriptorSetLayout), "descriptor set layout");

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = slotCount };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = slotCount };
        Check(vk.CreateDescriptorPool(device, in poolInfo, null, out _descriptorPool), "descriptor pool");

        var layouts = stackalloc DescriptorSetLayout[(int)slotCount];
        for (var i = 0; i < slotCount; i++)
            layouts[i] = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = slotCount,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _descriptorSets)
            Check(vk.AllocateDescriptorSets(device, in allocInfo, sets), "descriptor sets");

        for (var i = 0; i < slotCount; i++)
        {
            CreateHostBuffer((ulong)sizeof(VpUbo), BufferUsageFlags.UniformBufferBit, out _uboBuffers[i], out _uboMemory[i], out _uboMapped[i]);
            var bufferInfo = new DescriptorBufferInfo { Buffer = _uboBuffers[i], Offset = 0, Range = (ulong)sizeof(VpUbo) };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[i],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        }
    }

    private unsafe void CreatePipeline()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;

        // The scene grid's shaders already take exactly this vertex layout and UBO.
        var vertModule = CreateShaderModule(File.ReadAllBytes("Content/Shaders/SceneGrid/SceneGrid.vk.vert.spv"));
        ShaderModule fragModule = default;
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            fragModule = CreateShaderModule(File.ReadAllBytes("Content/Shaders/SceneGrid/SceneGrid.vk.frag.spv"));
            var stages = stackalloc PipelineShaderStageCreateInfo[]
            {
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = entryPoint },
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entryPoint },
            };

            var bindingDesc = new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(DebugLineVertex), InputRate = VertexInputRate.Vertex };
            var attribs = stackalloc VertexInputAttributeDescription[]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 12 },
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
            };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
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
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };
            // Depth-tested (hidden lines stay hidden) but not written, so lines never occlude what draws after them.
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

            var setLayout = _descriptorSetLayout;
            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
            Check(vk.CreatePipelineLayout(device, in layoutInfo, null, out _pipelineLayout), "pipeline layout");

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
                Layout = _pipelineLayout,
                RenderPass = _ctx.RenderPass,
                Subpass = 0,
            };
            Check(vk.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out _pipeline), "graphics pipeline");
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
            vk.DestroyShaderModule(device, vertModule, null);
            if (fragModule.Handle != 0)
                vk.DestroyShaderModule(device, fragModule, null);
        }
    }

    private unsafe ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)ptr };
            Check(_ctx.Vk.CreateShaderModule(_ctx.Device, in info, null, out var module), "shader module");
            return module;
        }
    }

    private unsafe void CreateHostBuffer(ulong size, BufferUsageFlags usage, out VkBuffer buffer, out DeviceMemory memory, out nint mapped)
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;
        var bufferInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(vk.CreateBuffer(device, in bufferInfo, null, out buffer), "buffer");
        vk.GetBufferMemoryRequirements(device, buffer, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        var allocResult = vk.AllocateMemory(device, in allocInfo, null, out memory);
        if (allocResult != Result.Success)
        {
            vk.DestroyBuffer(device, buffer, null);
            buffer = default;
            throw new VulkanException($"[Vulkan] DebugLines: failed to allocate buffer memory ({allocResult}).");
        }

        Check(vk.BindBufferMemory(device, buffer, memory, 0), "bind buffer memory");
        void* ptr;
        Check(vk.MapMemory(device, memory, 0, size, 0, &ptr), "map memory");
        mapped = (nint)ptr;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var memProps);
        for (var i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << i)) != 0 && (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        throw new VulkanException("[Vulkan] DebugLines: no host-visible memory type.");
    }

    private static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new VulkanException($"[Vulkan] DebugLines: failed to create {what} ({result}).");
    }

    /// <summary>Destroys the GPU objects (waits for the device: frames in flight may still read the buffers).</summary>
    public unsafe void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var vk = _ctx.Vk;
        var device = _ctx.Device;
        vk.DeviceWaitIdle(device);
        for (var i = 0; i < IVulkanContext.MaxFramesInFlight; i++)
        {
            DestroyVertexBuffer(i);
            if (_uboMapped[i] != 0)
                vk.UnmapMemory(device, _uboMemory[i]);
            if (_uboBuffers[i].Handle != 0)
                vk.DestroyBuffer(device, _uboBuffers[i], null);
            if (_uboMemory[i].Handle != 0)
                vk.FreeMemory(device, _uboMemory[i], null);
        }

        if (_pipeline.Handle != 0)
            vk.DestroyPipeline(device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
            vk.DestroyPipelineLayout(device, _pipelineLayout, null);
        if (_descriptorPool.Handle != 0)
            vk.DestroyDescriptorPool(device, _descriptorPool, null);
        if (_descriptorSetLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(device, _descriptorSetLayout, null);
    }
}
