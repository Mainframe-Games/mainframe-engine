using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 1.4 Abstractions - Demonstrates VkBuffer, VkTexture, VkPipeline wrappers. Same visual result as 1.3.
/// </summary>
public unsafe class AbstractionsExample : ExampleBase
{
    // csharpier-ignore
    private static readonly float[] Vertices =
    {
         0.5f,  0.5f, 0.0f, 1.0f, 1.0f,
         0.5f, -0.5f, 0.0f, 1.0f, 0.0f,
        -0.5f, -0.5f, 0.0f, 0.0f, 0.0f,
        -0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
    };

    private static readonly uint[] Indices = { 0, 1, 3, 1, 2, 3 };

    private VkBuffer? _vertexBuffer;
    private VkBuffer? _indexBuffer;
    private VkTexture? _texture;
    private VkPipeline? _pipeline;

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    protected override void OnLoad()
    {
        CreateDescriptorSetLayout();
        CreateVertexAndIndexBuffers();
        CreateTexture();
        CreateDescriptorPoolAndSet();
        CreatePipeline();
    }

    private void CreateDescriptorSetLayout()
    {
        var samplerBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &samplerBinding,
        };

        if (Vk.CreateDescriptorSetLayout(Device, in layoutInfo, null, out _descriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("failed to create descriptor set layout!");
    }

    private void CreateVertexAndIndexBuffers()
    {
        fixed (float* v = Vertices)
            _vertexBuffer = new VkBuffer(this, v, (ulong)(sizeof(float) * Vertices.Length), BufferUsageFlags.VertexBufferBit);

        fixed (uint* idx = Indices)
            _indexBuffer = new VkBuffer(this, idx, (ulong)(sizeof(uint) * Indices.Length), BufferUsageFlags.IndexBufferBit);
    }

    private void CreateTexture()
    {
        _texture = new VkTexture(this, "Content/Textures/silk.png");
    }

    private void CreateDescriptorPoolAndSet()
    {
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };
        if (Vk.CreateDescriptorPool(Device, in poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("failed to create descriptor pool!");

        var layout = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        if (Vk.AllocateDescriptorSets(Device, in allocInfo, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("failed to allocate descriptor set!");

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _texture!.View,
            Sampler = _texture!.Sampler,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };
        Vk.UpdateDescriptorSets(Device, 1, &write, 0, null);
    }

    private void CreatePipeline()
    {
        var vertModule = CreateShaderModule("Content/Shaders/Textures/shader.vert.spv");
        var fragModule = CreateShaderModule("Content/Shaders/Textures/shader.frag.spv");
        var vertStage = MakeShaderStage(ShaderStageFlags.VertexBit, vertModule);
        var fragStage = MakeShaderStage(ShaderStageFlags.FragmentBit, fragModule);
        var shaderStages = stackalloc[] { vertStage, fragStage };

        var bindingDesc = new VertexInputBindingDescription { Binding = 0, Stride = 5 * sizeof(float), InputRate = VertexInputRate.Vertex };
        var attrDescs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Binding = 0, Location = 1, Format = Format.R32G32Sfloat, Offset = 3 * sizeof(float) },
        };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 2,
            PVertexAttributeDescriptions = attrDescs,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };

        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
        var rasterizer = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise };
        var multisampling = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var colorBlending = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &colorBlendAttachment };

        var dsLayout = _descriptorSetLayout;
        var layoutCreateInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsLayout,
        };

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
            RenderPass = RenderPass,
            Subpass = 0,
        };

        _pipeline = new VkPipeline(this, layoutCreateInfo, pipelineInfo);

        Vk.DestroyShaderModule(Device, vertModule, null);
        Vk.DestroyShaderModule(Device, fragModule, null);
        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline!.Handle);

        var vb = _vertexBuffer!.Handle;
        ulong offset = 0;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);
        Vk.CmdBindIndexBuffer(cmd, _indexBuffer!.Handle, 0, IndexType.Uint32);

        var ds = _descriptorSet;
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeline!.Layout, 0, 1, &ds, 0, null);
        Vk.CmdDrawIndexed(cmd, (uint)Indices.Length, 1, 0, 0, 0);
    }

    protected override void OnClose()
    {
        _texture?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        Vk.DestroyDescriptorPool(Device, _descriptorPool, null);
        Vk.DestroyDescriptorSetLayout(Device, _descriptorSetLayout, null);
        _pipeline?.Dispose();
    }
}
