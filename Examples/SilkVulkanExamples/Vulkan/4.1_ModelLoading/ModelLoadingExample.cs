using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using SilkVulkanExamples.Vulkan.Utils;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 4.1 Model Loading - Loads a 3D model via Assimp with camera navigation.
/// </summary>
public unsafe class ModelLoadingExample : ExampleBase
{
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct UniformBufferObject
    {
        [FieldOffset(0)]   public Matrix4x4 Model;
        [FieldOffset(64)]  public Matrix4x4 View;
        [FieldOffset(128)] public Matrix4x4 Projection;
    }

    private VkModel? _model;

    private Image _textureImage;
    private DeviceMemory _textureImageMemory;
    private ImageView _textureImageView;
    private Sampler _textureSampler;

    private Buffer[] _uniformBuffers = [];
    private DeviceMemory[] _uniformBufferMemories = [];
    private void*[] _uniformBufferMapped = [];

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[] _descriptorSets = [];

    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;

    private Camera _camera = null!;
    private Vector2 _lastMousePosition;
    private bool _mouseCapture;
    private float _time;

    protected override void OnLoad()
    {
        var size = Window.FramebufferSize;
        _camera = new Camera(new Vector3(0, 0, 3), new Vector3(0, 0, -1), Vector3.UnitY, (float)size.X / size.Y);

        var (img, mem, view, sampler) = CreateTextureFromFile("Content/Textures/silk.png");
        _textureImage = img; _textureImageMemory = mem; _textureImageView = view; _textureSampler = sampler;

        CreateDescriptorSetLayout();
        CreateUniformBuffers();
        CreateDescriptorPoolAndSets();
        CreateGraphicsPipeline();

        _model = new VkModel(this, "Content/Models/cube.obj");
    }

    protected override void OnUpdate(double delta)
    {
        _time += (float)delta;
        var spd = 2.5f * (float)delta; var kb = InputContext.Keyboards[0];
        if (kb.IsKeyPressed(Key.W)) _camera.Position += spd * _camera.Front;
        if (kb.IsKeyPressed(Key.S)) _camera.Position -= spd * _camera.Front;
        if (kb.IsKeyPressed(Key.A)) _camera.Position -= Vector3.Normalize(Vector3.Cross(_camera.Front, _camera.Up)) * spd;
        if (kb.IsKeyPressed(Key.D)) _camera.Position += Vector3.Normalize(Vector3.Cross(_camera.Front, _camera.Up)) * spd;
    }

    protected override void OnResize() { var s = Window.FramebufferSize; _camera.AspectRatio = (float)s.X / s.Y; }
    protected override void OnMouseMove(IMouse mouse, Vector2 pos) { if (!_mouseCapture) return; if (_lastMousePosition == default) { _lastMousePosition = pos; return; } _camera.ModifyDirection((pos.X - _lastMousePosition.X) * 0.1f, (pos.Y - _lastMousePosition.Y) * 0.1f); _lastMousePosition = pos; }
    protected override void OnMouseWheel(IMouse mouse, ScrollWheel scroll) => _camera.ModifyZoom(scroll.Y);
    protected override void OnKeyDown(IKeyboard kb, Key key, int arg) { if (key == Key.Space) { _mouseCapture = !_mouseCapture; _lastMousePosition = default; if (InputContext.Mice.Count > 0) InputContext.Mice[0].Cursor.CursorMode = _mouseCapture ? CursorMode.Raw : CursorMode.Normal; } }

    private void CreateDescriptorSetLayout()
    {
        var uboB = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit };
        var samplerB = new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        var bindings = stackalloc[] { uboB, samplerB };
        var li = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        if (Vk.CreateDescriptorSetLayout(Device, in li, null, out _descriptorSetLayout) != Result.Success) throw new Exception("failed!");
    }

    private void CreateUniformBuffers()
    {
        int n = SwapChainImages.Length; _uniformBuffers = new Buffer[n]; _uniformBufferMemories = new DeviceMemory[n]; _uniformBufferMapped = new void*[n];
        for (int i = 0; i < n; i++) { ulong sz = (ulong)sizeof(UniformBufferObject); CreateBuffer(sz, BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _uniformBuffers[i], out _uniformBufferMemories[i]); void* mapped; Vk.MapMemory(Device, _uniformBufferMemories[i], 0, sz, 0, &mapped); _uniformBufferMapped[i] = mapped; }
    }

    private void CreateDescriptorPoolAndSets()
    {
        int n = SwapChainImages.Length;
        var poolSizes = stackalloc DescriptorPoolSize[] { new() { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)n }, new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)n } };
        var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 2, PPoolSizes = poolSizes, MaxSets = (uint)n };
        if (Vk.CreateDescriptorPool(Device, in pi, null, out _descriptorPool) != Result.Success) throw new Exception("failed!");
        var ls = new DescriptorSetLayout[n]; Array.Fill(ls, _descriptorSetLayout); _descriptorSets = new DescriptorSet[n];
        fixed (DescriptorSetLayout* lp = ls) { var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = (uint)n, PSetLayouts = lp }; fixed (DescriptorSet* sp = _descriptorSets) Vk.AllocateDescriptorSets(Device, in ai, sp); }
        for (int i = 0; i < n; i++)
        {
            var bi = new DescriptorBufferInfo { Buffer = _uniformBuffers[i], Offset = 0, Range = (ulong)sizeof(UniformBufferObject) };
            var imgInfo = new DescriptorImageInfo { ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _textureImageView, Sampler = _textureSampler };
            var writes = stackalloc WriteDescriptorSet[]
            {
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSets[i], DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSets[i], DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imgInfo },
            };
            Vk.UpdateDescriptorSets(Device, 2, writes, 0, null);
        }
    }

    private void CreateGraphicsPipeline()
    {
        var vertModule = CreateShaderModule("Content/Shaders/ModelLoading/shader.vert.spv");
        var fragModule = CreateShaderModule("Content/Shaders/ModelLoading/shader.frag.spv");
        var vs = MakeShaderStage(ShaderStageFlags.VertexBit, vertModule);
        var fs = MakeShaderStage(ShaderStageFlags.FragmentBit, fragModule);
        var stages = stackalloc[] { vs, fs };

        var bd = new VertexInputBindingDescription { Binding = 0, Stride = 8 * sizeof(float), InputRate = VertexInputRate.Vertex };
        var attrs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = 3 * sizeof(float) },
            new() { Binding = 0, Location = 2, Format = Format.R32G32Sfloat, Offset = 6 * sizeof(float) },
        };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &bd, VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attrs };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vps = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var dynSt = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor }; var dyn = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynSt };
        var rast = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var dss = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less };
        var cba = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cba };

        var dsl = _descriptorSetLayout;
        var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &dsl };
        if (Vk.CreatePipelineLayout(Device, in pli, null, out _pipelineLayout) != Result.Success) throw new Exception("failed!");

        var pci = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vps, PRasterizationState = &rast, PMultisampleState = &ms, PDepthStencilState = &dss, PColorBlendState = &cb, PDynamicState = &dyn, Layout = _pipelineLayout, RenderPass = RenderPass, Subpass = 0 };
        if (Vk.CreateGraphicsPipelines(Device, default, 1, in pci, null, out _graphicsPipeline) != Result.Success) throw new Exception("failed!");
        Vk.DestroyShaderModule(Device, vertModule, null); Vk.DestroyShaderModule(Device, fragModule, null);
        SilkMarshal.Free((nint)vs.PName); SilkMarshal.Free((nint)fs.PName);
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        if (_model == null) return;

        var model = Matrix4x4.CreateRotationY(_time) * Matrix4x4.CreateRotationX(_time * 0.5f);
        var ubo = new UniformBufferObject { Model = model, View = _camera.GetViewMatrix(), Projection = _camera.GetProjectionMatrix() };
        System.Buffer.MemoryCopy(&ubo, _uniformBufferMapped[imageIndex], sizeof(UniformBufferObject), sizeof(UniformBufferObject));

        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _graphicsPipeline);
        var ds = _descriptorSets[imageIndex];
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);

        foreach (var mesh in _model.Meshes)
        {
            var vb = mesh.VertexBuffer; ulong off = 0;
            Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &off);
            Vk.CmdBindIndexBuffer(cmd, mesh.IndexBuffer, 0, IndexType.Uint32);
            Vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    protected override void CleanupSwapchain()
    {
        if (_uniformBuffers.Length > 0) { for (int i = 0; i < _uniformBuffers.Length; i++) { Vk.UnmapMemory(Device, _uniformBufferMemories[i]); Vk.DestroyBuffer(Device, _uniformBuffers[i], null); Vk.FreeMemory(Device, _uniformBufferMemories[i], null); } _uniformBuffers = []; _uniformBufferMemories = []; _uniformBufferMapped = []; }
        if (_descriptorPool.Handle != 0) { Vk.DestroyDescriptorPool(Device, _descriptorPool, null); _descriptorPool = default; }
        base.CleanupSwapchain();
    }

    protected override void RecreateSwapchain() { base.RecreateSwapchain(); if (_descriptorSetLayout.Handle != 0) { CreateUniformBuffers(); CreateDescriptorPoolAndSets(); } }

    protected override void OnClose()
    {
        _model?.Dispose();
        Vk.DestroySampler(Device, _textureSampler, null); Vk.DestroyImageView(Device, _textureImageView, null); Vk.DestroyImage(Device, _textureImage, null); Vk.FreeMemory(Device, _textureImageMemory, null);
        Vk.DestroyDescriptorSetLayout(Device, _descriptorSetLayout, null);
        Vk.DestroyPipeline(Device, _graphicsPipeline, null);
        Vk.DestroyPipelineLayout(Device, _pipelineLayout, null);
    }
}
