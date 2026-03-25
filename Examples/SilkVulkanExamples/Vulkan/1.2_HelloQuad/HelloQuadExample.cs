using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 1.2 Hello Quad - Orange quad using vertex and index buffers with staging.
/// </summary>
public unsafe class HelloQuadExample : ExampleBase
{
    // csharpier-ignore
    private static readonly float[] Vertices =
    {
         0.5f,  0.5f, 0.0f,
         0.5f, -0.5f, 0.0f,
        -0.5f, -0.5f, 0.0f,
        -0.5f,  0.5f, 0.5f,
    };

    // csharpier-ignore
    private static readonly uint[] Indices = { 0, 1, 3, 1, 2, 3 };

    private Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private Buffer _indexBuffer;
    private DeviceMemory _indexBufferMemory;
    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;

    protected override void OnLoad()
    {
        CreateVertexBuffer();
        CreateIndexBuffer();
        CreateGraphicsPipeline();
    }

    private void CreateVertexBuffer()
    {
        ulong bufferSize = (ulong)(sizeof(float) * Vertices.Length);

        CreateBuffer(bufferSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* data;
        Vk.MapMemory(Device, stagingMemory, 0, bufferSize, 0, &data);
        fixed (float* v = Vertices)
            System.Buffer.MemoryCopy(v, data, bufferSize, bufferSize);
        Vk.UnmapMemory(Device, stagingMemory);

        CreateBuffer(bufferSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vertexBuffer, out _vertexBufferMemory);

        CopyBuffer(stagingBuffer, _vertexBuffer, bufferSize);
        Vk.DestroyBuffer(Device, stagingBuffer, null);
        Vk.FreeMemory(Device, stagingMemory, null);
    }

    private void CreateIndexBuffer()
    {
        ulong bufferSize = (ulong)(sizeof(uint) * Indices.Length);

        CreateBuffer(bufferSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* data;
        Vk.MapMemory(Device, stagingMemory, 0, bufferSize, 0, &data);
        fixed (uint* idx = Indices)
            System.Buffer.MemoryCopy(idx, data, bufferSize, bufferSize);
        Vk.UnmapMemory(Device, stagingMemory);

        CreateBuffer(bufferSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _indexBuffer, out _indexBufferMemory);

        CopyBuffer(stagingBuffer, _indexBuffer, bufferSize);
        Vk.DestroyBuffer(Device, stagingBuffer, null);
        Vk.FreeMemory(Device, stagingMemory, null);
    }

    private void CreateGraphicsPipeline()
    {
        var vertModule = CreateShaderModule("Content/Shaders/HelloQuad/shader.vert.spv");
        var fragModule = CreateShaderModule("Content/Shaders/HelloQuad/shader.frag.spv");

        var vertStage = MakeShaderStage(ShaderStageFlags.VertexBit, vertModule);
        var fragStage = MakeShaderStage(ShaderStageFlags.FragmentBit, fragModule);
        var shaderStages = stackalloc[] { vertStage, fragStage };

        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = 3 * sizeof(float),
            InputRate = VertexInputRate.Vertex,
        };

        var attributeDesc = new VertexInputAttributeDescription
        {
            Binding = 0,
            Location = 0,
            Format = Format.R32G32B32Sfloat,
            Offset = 0,
        };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 1,
            PVertexAttributeDescriptions = &attributeDesc,
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

        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
        };

        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
        };

        if (Vk.CreatePipelineLayout(Device, in pipelineLayoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new Exception("failed to create pipeline layout!");

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = shaderStages,
            PVertexInputState = &vertexInputInfo,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlending,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = RenderPass,
            Subpass = 0,
        };

        if (Vk.CreateGraphicsPipelines(Device, default, 1, in pipelineInfo, null, out _graphicsPipeline) != Result.Success)
            throw new Exception("failed to create graphics pipeline!");

        Vk.DestroyShaderModule(Device, vertModule, null);
        Vk.DestroyShaderModule(Device, fragModule, null);
        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);

        var vertexBuffer = _vertexBuffer;
        ulong offset = 0;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
        Vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint32);
        Vk.CmdDrawIndexed(cmd, (uint)Indices.Length, 1, 0, 0, 0);
    }

    protected override void OnClose()
    {
        Vk.DestroyBuffer(Device, _indexBuffer, null);
        Vk.FreeMemory(Device, _indexBufferMemory, null);
        Vk.DestroyBuffer(Device, _vertexBuffer, null);
        Vk.FreeMemory(Device, _vertexBufferMemory, null);
        Vk.DestroyPipeline(Device, _graphicsPipeline, null);
        Vk.DestroyPipelineLayout(Device, _pipelineLayout, null);
    }
}
