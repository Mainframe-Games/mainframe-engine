using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using SilkVulkanExamples.Vulkan.Utils;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 2.1 Coordinate Systems - Rotating 3D cube with UBO (model/view/projection).
/// </summary>
public unsafe class CoordinateSystemsExample : ExampleBase
{
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct UniformBufferObject
    {
        [FieldOffset(0)]   public Matrix4x4 Model;
        [FieldOffset(64)]  public Matrix4x4 View;
        [FieldOffset(128)] public Matrix4x4 Projection;
    }

    // csharpier-ignore
    private static readonly float[] Vertices =
    {
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
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,
    };

    private Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;

    // Per-swapchain-image UBOs
    private Buffer[] _uniformBuffers = [];
    private DeviceMemory[] _uniformBufferMemories = [];
    private void*[] _uniformBufferMapped = [];

    private Image _textureImage;
    private DeviceMemory _textureImageMemory;
    private ImageView _textureImageView;
    private Sampler _textureSampler;

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[] _descriptorSets = [];

    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;

    private float _time;

    protected override void OnLoad()
    {
        CreateVertexBuffer();
        var (img, mem, view, sampler) = CreateTextureFromFile("Content/Textures/silk.png");
        _textureImage = img; _textureImageMemory = mem; _textureImageView = view; _textureSampler = sampler;
        CreateDescriptorSetLayout();
        CreateUniformBuffers();
        CreateDescriptorPoolAndSets();
        CreateGraphicsPipeline();
    }

    protected override void OnUpdate(double delta) => _time += (float)delta;

    private void CreateVertexBuffer()
    {
        ulong size = (ulong)(sizeof(float) * Vertices.Length);
        CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var s, out var sm);
        void* data;
        Vk.MapMemory(Device, sm, 0, size, 0, &data);
        fixed (float* v = Vertices) System.Buffer.MemoryCopy(v, data, size, size);
        Vk.UnmapMemory(Device, sm);
        CreateBuffer(size, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMemory);
        CopyBuffer(s, _vertexBuffer, size);
        Vk.DestroyBuffer(Device, s, null); Vk.FreeMemory(Device, sm, null);
    }

    private void CreateDescriptorSetLayout()
    {
        var uboBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        var samplerBinding = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var bindings = stackalloc[] { uboBinding, samplerBinding };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        if (Vk.CreateDescriptorSetLayout(Device, in layoutInfo, null, out _descriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("failed to create descriptor set layout!");
    }

    private void CreateUniformBuffers()
    {
        int count = SwapChainImages.Length;
        _uniformBuffers = new Buffer[count];
        _uniformBufferMemories = new DeviceMemory[count];
        _uniformBufferMapped = new void*[count];

        for (int i = 0; i < count; i++)
        {
            ulong size = (ulong)sizeof(UniformBufferObject);
            CreateBuffer(size, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _uniformBuffers[i], out _uniformBufferMemories[i]);
            void* mapped;
            Vk.MapMemory(Device, _uniformBufferMemories[i], 0, size, 0, &mapped);
            _uniformBufferMapped[i] = mapped;
        }
    }

    private void CreateDescriptorPoolAndSets()
    {
        int count = SwapChainImages.Length;
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)count },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)count },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = (uint)count,
        };
        if (Vk.CreateDescriptorPool(Device, in poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("failed to create descriptor pool!");

        var layouts = new DescriptorSetLayout[count];
        Array.Fill(layouts, _descriptorSetLayout);
        _descriptorSets = new DescriptorSet[count];

        fixed (DescriptorSetLayout* layoutsPtr = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = (uint)count,
                PSetLayouts = layoutsPtr,
            };
            fixed (DescriptorSet* setsPtr = _descriptorSets)
                if (Vk.AllocateDescriptorSets(Device, in allocInfo, setsPtr) != Result.Success)
                    throw new InvalidOperationException("failed to allocate descriptor sets!");
        }

        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < count; i++)
        {
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = _uniformBuffers[i],
                Offset = 0,
                Range = (ulong)sizeof(UniformBufferObject),
            };
            var imageInfo = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = _textureImageView,
                Sampler = _textureSampler,
            };
            writes[0] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[i],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[i],
                DstBinding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo,
            };
            Vk.UpdateDescriptorSets(Device, 2, writes, 0, null);
        }
    }

    private void CreateGraphicsPipeline()
    {
        var vertModule = CreateShaderModule("Content/Shaders/Coordinates/shader.vert.spv");
        var fragModule = CreateShaderModule("Content/Shaders/Coordinates/shader.frag.spv");
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
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = true,
            DepthCompareOp = CompareOp.Less,
        };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var colorBlending = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &colorBlendAttachment };

        var dsLayout = _descriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsLayout,
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

    private void UpdateUniformBuffer(uint imageIndex)
    {
        var size = Window.FramebufferSize;
        var aspectRatio = (float)size.X / size.Y;

        var model = Matrix4x4.CreateFromAxisAngle(new Vector3(0.5f, 1.0f, 0.0f), _time * 0.5f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspectRatio, 0.1f, 100f);
        // Flip Y for Vulkan
        proj.M22 = -proj.M22;

        var ubo = new UniformBufferObject { Model = model, View = view, Projection = proj };
        System.Buffer.MemoryCopy(&ubo, _uniformBufferMapped[imageIndex], sizeof(UniformBufferObject), sizeof(UniformBufferObject));
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        UpdateUniformBuffer(imageIndex);

        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);

        var vb = _vertexBuffer;
        ulong offset = 0;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        var ds = _descriptorSets[imageIndex];
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
        Vk.CmdDraw(cmd, 36, 1, 0, 0);
    }

    protected override void CleanupSwapchain()
    {
        // Clean uniform buffers
        if (_uniformBuffers.Length > 0)
        {
            for (int i = 0; i < _uniformBuffers.Length; i++)
            {
                Vk.UnmapMemory(Device, _uniformBufferMemories[i]);
                Vk.DestroyBuffer(Device, _uniformBuffers[i], null);
                Vk.FreeMemory(Device, _uniformBufferMemories[i], null);
            }
            _uniformBuffers = [];
            _uniformBufferMemories = [];
            _uniformBufferMapped = [];
        }

        if (_descriptorPool.Handle != 0)
        {
            Vk.DestroyDescriptorPool(Device, _descriptorPool, null);
            _descriptorPool = default;
        }

        base.CleanupSwapchain();
    }

    protected override void RecreateSwapchain()
    {
        base.RecreateSwapchain();
        // Recreate UBOs and descriptor sets after swapchain recreation
        if (_descriptorSetLayout.Handle != 0)
        {
            CreateUniformBuffers();
            CreateDescriptorPoolAndSets();
        }
    }

    protected override void OnClose()
    {
        Vk.DestroySampler(Device, _textureSampler, null);
        Vk.DestroyImageView(Device, _textureImageView, null);
        Vk.DestroyImage(Device, _textureImage, null);
        Vk.FreeMemory(Device, _textureImageMemory, null);
        Vk.DestroyDescriptorSetLayout(Device, _descriptorSetLayout, null);
        Vk.DestroyBuffer(Device, _vertexBuffer, null);
        Vk.FreeMemory(Device, _vertexBufferMemory, null);
        Vk.DestroyPipeline(Device, _graphicsPipeline, null);
        Vk.DestroyPipelineLayout(Device, _pipelineLayout, null);
    }
}
