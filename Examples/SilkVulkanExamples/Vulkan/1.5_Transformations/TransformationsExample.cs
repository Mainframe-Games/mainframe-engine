using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 1.5 Transformations - 4 quads with different model transforms via push constants.
/// </summary>
public unsafe class TransformationsExample : ExampleBase
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

    private Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private Buffer _indexBuffer;
    private DeviceMemory _indexBufferMemory;

    private Image _textureImage;
    private DeviceMemory _textureImageMemory;
    private ImageView _textureImageView;
    private Sampler _textureSampler;

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;

    private float _time;

    protected override void OnLoad()
    {
        CreateBuffers();
        var (img, mem, view, sampler) = CreateTextureFromFile("Content/Textures/silk.png");
        _textureImage = img; _textureImageMemory = mem; _textureImageView = view; _textureSampler = sampler;
        CreateDescriptors();
        CreateGraphicsPipeline();
    }

    protected override void OnUpdate(double delta) => _time += (float)delta;

    private void CreateBuffers()
    {
        ulong vSize = (ulong)(sizeof(float) * Vertices.Length);
        CreateBuffer(vSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var vs, out var vsm);
        void* data;
        Vk.MapMemory(Device, vsm, 0, vSize, 0, &data);
        fixed (float* v = Vertices) System.Buffer.MemoryCopy(v, data, vSize, vSize);
        Vk.UnmapMemory(Device, vsm);
        CreateBuffer(vSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMemory);
        CopyBuffer(vs, _vertexBuffer, vSize);
        Vk.DestroyBuffer(Device, vs, null); Vk.FreeMemory(Device, vsm, null);

        ulong iSize = (ulong)(sizeof(uint) * Indices.Length);
        CreateBuffer(iSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var is_, out var ism);
        Vk.MapMemory(Device, ism, 0, iSize, 0, &data);
        fixed (uint* idx = Indices) System.Buffer.MemoryCopy(idx, data, iSize, iSize);
        Vk.UnmapMemory(Device, ism);
        CreateBuffer(iSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out _indexBuffer, out _indexBufferMemory);
        CopyBuffer(is_, _indexBuffer, iSize);
        Vk.DestroyBuffer(Device, is_, null); Vk.FreeMemory(Device, ism, null);
    }

    private void CreateDescriptors()
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

        var dsLayout = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsLayout,
        };
        if (Vk.AllocateDescriptorSets(Device, in allocInfo, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("failed to allocate descriptor set!");

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _textureImageView,
            Sampler = _textureSampler,
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

    private void CreateGraphicsPipeline()
    {
        var vertModule = CreateShaderModule("Content/Shaders/Transformations/shader.vert.spv");
        var fragModule = CreateShaderModule("Content/Shaders/Transformations/shader.frag.spv");
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
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
        var rasterizer = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise };
        var multisampling = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var colorBlending = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &colorBlendAttachment };

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(Matrix4x4),
        };
        var dsLayout = _descriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange,
        };
        if (Vk.CreatePipelineLayout(Device, in pipelineLayoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new InvalidOperationException("failed to create pipeline layout!");

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
            throw new InvalidOperationException("failed to create graphics pipeline!");

        Vk.DestroyShaderModule(Device, vertModule, null);
        Vk.DestroyShaderModule(Device, fragModule, null);
        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);

        var vb = _vertexBuffer;
        ulong offset = 0;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);
        Vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint32);

        var ds = _descriptorSet;
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);

        // 4 transforms matching OGL example
        Matrix4x4[] models =
        [
            // 1. Translation
            Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0.5f, -0.5f, 0f),
            // 2. Rotation + translation
            Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateRotationZ(_time) * Matrix4x4.CreateTranslation(-0.75f, 0.75f, 0f),
            // 3. Scale + translation
            Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0f),
            // 4. Rotation + scale + translation
            Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateRotationZ(-_time) * Matrix4x4.CreateTranslation(-0.5f, -0.5f, 0f),
        ];

        foreach (var model in models)
        {
            var m = model;
            Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &m);
            Vk.CmdDrawIndexed(cmd, (uint)Indices.Length, 1, 0, 0, 0);
        }
    }

    protected override void OnClose()
    {
        Vk.DestroySampler(Device, _textureSampler, null);
        Vk.DestroyImageView(Device, _textureImageView, null);
        Vk.DestroyImage(Device, _textureImage, null);
        Vk.FreeMemory(Device, _textureImageMemory, null);
        Vk.DestroyDescriptorPool(Device, _descriptorPool, null);
        Vk.DestroyDescriptorSetLayout(Device, _descriptorSetLayout, null);
        Vk.DestroyBuffer(Device, _indexBuffer, null);
        Vk.FreeMemory(Device, _indexBufferMemory, null);
        Vk.DestroyBuffer(Device, _vertexBuffer, null);
        Vk.FreeMemory(Device, _vertexBufferMemory, null);
        Vk.DestroyPipeline(Device, _graphicsPipeline, null);
        Vk.DestroyPipelineLayout(Device, _pipelineLayout, null);
    }
}
