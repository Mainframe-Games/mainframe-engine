using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public class Box3d : ShapeBase, IDisposable
{
    #region Vertices

    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //X    Y      Z     U   V
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,
        0.5f, -0.5f, -0.5f,  1.0f, 0.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,

        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,

        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f,

        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,

        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,
        0.5f, -0.5f, -0.5f,  1.0f, 1.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        0.5f, -0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,

        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,
        0.5f,  0.5f, -0.5f,  1.0f, 1.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        0.5f,  0.5f,  0.5f,  1.0f, 0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f
    ];

    // csharpier-ignore
    private static readonly uint[] Indices =
    [
        0, 1, 3,
        1, 2, 3
    ];

    #endregion

    // OpenGL
    private readonly GL? _gl;
    private readonly VertexArrayObject<float, uint>? _vertexArray;
    private readonly Shader? _shader;

    // Vulkan
    private IVulkanContext? _vkCtx;
    private VkBuffer _vkVertexBuffer;
    private DeviceMemory _vkVertexBufferMemory;
    private PipelineLayout _vkPipelineLayout;
    private Pipeline _vkPipeline;

    [StructLayout(LayoutKind.Sequential)]
    private struct MvpPushConstants
    {
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    public Box3d(IRenderer renderer)
    {
        if (renderer.Backend == RenderingBackend.OpenGL)
        {
            _gl = renderer.GetGL();

            var vertexBuffer = new BufferObject<float>(_gl, Vertices, BufferTargetARB.ArrayBuffer);
            var indexBuffer = new BufferObject<uint>(_gl, Indices, BufferTargetARB.ElementArrayBuffer);
            _vertexArray = new VertexArrayObject<float, uint>(_gl, vertexBuffer, indexBuffer);

            _vertexArray.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 5, 0);
            _vertexArray.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 5, 3);

            _shader = new Shader(_gl,
                "Content/Shaders/Shapes/Shapes.vert",
                "Content/Shaders/Shapes/Shapes.frag");
        }
        else if (renderer is IVulkanContext vkCtx)
        {
            _vkCtx = vkCtx;
            CreateVkVertexBuffer(vkCtx);
            CreateVkPipeline(vkCtx);
        }
    }

    public void Draw(ICamera camera)
    {
        if (_gl is not null)
        {
            _shader!.Use();
            _shader.SetUniform("uModel", ModelMatrix);
            _shader.SetUniform("uView", camera.ViewMatrix);
            _shader.SetUniform("uProjection", camera.ProjectionMatrix);

            _vertexArray!.Bind();
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
        }
        else if (_vkCtx is not null)
        {
            DrawVulkan(camera);
        }
    }

    public unsafe void Dispose()
    {
        if (_vkCtx is null) return;
        var vk = _vkCtx.Vk;
        var device = _vkCtx.Device;
        vk.DeviceWaitIdle(device);
        vk.DestroyPipeline(device, _vkPipeline, null);
        vk.DestroyPipelineLayout(device, _vkPipelineLayout, null);
        vk.DestroyBuffer(device, _vkVertexBuffer, null);
        vk.FreeMemory(device, _vkVertexBufferMemory, null);
    }

    private unsafe void DrawVulkan(ICamera camera)
    {
        var vk = _vkCtx!.Vk;
        var cb = _vkCtx.CurrentCommandBuffer;
        var extent = _vkCtx.SwapchainExtent;

        // Negative height flips Vulkan's Y axis to match right-handed convention (Y+ up)
        var viewport = new Viewport
        {
            X = 0,
            Y = (float)extent.Height,
            Width = (float)extent.Width,
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

        var mvp = new MvpPushConstants
        {
            Model = ModelMatrix,
            View = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix,
        };
        vk.CmdPushConstants(cb, _vkPipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(MvpPushConstants), &mvp);

        vk.CmdDraw(cb, 36, 1, 0, 0);
    }

    private unsafe void CreateVkVertexBuffer(IVulkanContext ctx)
    {
        var vk = ctx.Vk;
        var device = ctx.Device;
        var size = (ulong)(Vertices.Length * sizeof(float));

        // Staging buffer (CPU-visible)
        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        vk.MapMemory(device, stagingMemory, 0, size, 0, &mapped);
        fixed (float* src = Vertices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        vk.UnmapMemory(device, stagingMemory);

        // Device-local buffer
        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vkVertexBuffer, out _vkVertexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vkVertexBuffer, size);

        vk.DestroyBuffer(device, stagingBuffer, null);
        vk.FreeMemory(device, stagingMemory, null);
    }

    private unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
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

        if (vk.CreateBuffer(device, bufferInfo, null, out buffer) != Result.Success)
            throw new Exception("[Vulkan] Failed to create buffer!");

        vk.GetBufferMemoryRequirements(device, buffer, out var memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };

        if (vk.AllocateMemory(device, allocInfo, null, out memory) != Result.Success)
            throw new Exception("[Vulkan] Failed to allocate buffer memory!");

        vk.BindBufferMemory(device, buffer, memory, 0);
    }

    private unsafe void CopyBuffer(IVulkanContext ctx, VkBuffer src, VkBuffer dst, ulong size)
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
        vk.AllocateCommandBuffers(device, allocInfo, &cb);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cb, beginInfo);

        var region = new BufferCopy { Size = size };
        vk.CmdCopyBuffer(cb, src, dst, 1, &region);

        vk.EndCommandBuffer(cb);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, submitInfo, default);
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
        throw new Exception("[Vulkan] No suitable memory type found!");
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

            if (ctx.Vk.CreateShaderModule(ctx.Device, createInfo, null, out var module) != Result.Success)
                throw new Exception("[Vulkan] Failed to create shader module!");

            return module;
        }
    }

    private unsafe void CreateVkPipeline(IVulkanContext ctx)
    {
        var vk = ctx.Vk;
        var device = ctx.Device;

        var vertCode = File.ReadAllBytes("Content/Shaders/Shapes/Shapes.vk.vert.spv");
        var fragCode = File.ReadAllBytes("Content/Shaders/Shapes/Shapes.vk.frag.spv");

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

        // Vertex layout: stride 20 bytes (X, Y, Z, U, V)
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = 5 * sizeof(float),
            InputRate = VertexInputRate.Vertex,
        };

        var attribs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat, Offset = 0  },  // position
            new() { Location = 1, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32Sfloat,    Offset = 12 },  // UV
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
            Topology = PrimitiveTopology.TriangleList,
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

        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask =
                ColorComponentFlags.RBit |
                ColorComponentFlags.GBit |
                ColorComponentFlags.BBit |
                ColorComponentFlags.ABit,
            BlendEnable = false,
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

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(MvpPushConstants),
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };

        if (vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out _vkPipelineLayout) != Result.Success)
            throw new Exception("[Vulkan] Failed to create pipeline layout!");

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
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = _vkPipelineLayout,
            RenderPass = ctx.RenderPass,
            Subpass = 0,
        };

        if (vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out _vkPipeline) != Result.Success)
            throw new Exception("[Vulkan] Failed to create graphics pipeline!");

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }
}
