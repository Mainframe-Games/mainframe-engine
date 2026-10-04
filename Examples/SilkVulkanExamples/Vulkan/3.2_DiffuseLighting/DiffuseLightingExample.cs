using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using SilkVulkanExamples.Vulkan.Utils;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// 3.2 Diffuse Lighting - Ambient + diffuse shading with normals. pos+normal vertex format.
/// </summary>
public unsafe class DiffuseLightingExample : ExampleBase
{
    // std140 padded
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LightingUBO
    {
        [FieldOffset(0)]   public Matrix4x4 Model;
        [FieldOffset(64)]  public Matrix4x4 View;
        [FieldOffset(128)] public Matrix4x4 Projection;
        [FieldOffset(192)] public Vector3 LightPos;
        [FieldOffset(208)] public Vector3 ViewPos;
        [FieldOffset(224)] public Vector3 LightColor;
        [FieldOffset(240)] public Vector3 ObjectColor;
    }

    // csharpier-ignore
    private static readonly float[] Vertices =
    {
        -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
         0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
         0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
         0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f,  0.0f, -1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
         0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
         0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
         0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f,  0.0f,  1.0f,
        -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f,  0.5f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f,  0.5f, -1.0f,  0.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,
         0.5f,  0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
         0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
         0.5f, -0.5f, -0.5f,  1.0f,  0.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  1.0f,  0.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, -1.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,
         0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f,  1.0f,  0.0f,
    };

    private static readonly Vector3 LightPos = new(1.2f, 1.0f, 2.0f);
    private static readonly Vector3 ObjectColor = new(1.0f, 0.5f, 0.31f);
    private static readonly Vector3 LightColor = new(1.0f, 1.0f, 1.0f);

    private Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;

    private Buffer[] _uniformBuffers = [];
    private DeviceMemory[] _uniformBufferMemories = [];
    private void*[] _uniformBufferMapped = [];

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[] _descriptorSets = [];

    private PipelineLayout _pipelineLayout;
    private Pipeline _lightingPipeline;
    private Pipeline _lampPipeline;

    private Camera _camera = null!;
    private Vector2 _lastMousePosition;
    private bool _mouseCapture;

    protected override void OnLoad()
    {
        var size = Window.FramebufferSize;
        _camera = new Camera(new Vector3(0, 0, 3), new Vector3(0, 0, -1), Vector3.UnitY, (float)size.X / size.Y);
        CreateVertexBuffer();
        CreateDescriptorSetLayout();
        CreateUniformBuffers();
        CreateDescriptorPoolAndSets();
        CreatePipelines();
    }

    protected override void OnResize() { var s = Window.FramebufferSize; _camera.AspectRatio = (float)s.X / s.Y; }

    protected override void OnUpdate(double delta)
    {
        var spd = 2.5f * (float)delta;
        var kb = InputContext.Keyboards[0];
        if (kb.IsKeyPressed(Key.W)) _camera.Position += spd * _camera.Front;
        if (kb.IsKeyPressed(Key.S)) _camera.Position -= spd * _camera.Front;
        if (kb.IsKeyPressed(Key.A)) _camera.Position -= Vector3.Normalize(Vector3.Cross(_camera.Front, _camera.Up)) * spd;
        if (kb.IsKeyPressed(Key.D)) _camera.Position += Vector3.Normalize(Vector3.Cross(_camera.Front, _camera.Up)) * spd;
    }

    protected override void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (!_mouseCapture) return;
        if (_lastMousePosition == default) { _lastMousePosition = pos; return; }
        _camera.ModifyDirection((pos.X - _lastMousePosition.X) * 0.1f, (pos.Y - _lastMousePosition.Y) * 0.1f);
        _lastMousePosition = pos;
    }

    protected override void OnMouseWheel(IMouse mouse, ScrollWheel scroll) => _camera.ModifyZoom(scroll.Y);

    protected override void OnKeyDown(IKeyboard kb, Key key, int arg)
    {
        if (key == Key.Space) { _mouseCapture = !_mouseCapture; _lastMousePosition = default; if (InputContext.Mice.Count > 0) InputContext.Mice[0].Cursor.CursorMode = _mouseCapture ? CursorMode.Raw : CursorMode.Normal; }
    }

    private void CreateVertexBuffer()
    {
        ulong size = (ulong)(sizeof(float) * Vertices.Length);
        CreateBuffer(size, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var s, out var sm);
        void* data; Vk.MapMemory(Device, sm, 0, size, 0, &data);
        fixed (float* v = Vertices) System.Buffer.MemoryCopy(v, data, size, size);
        Vk.UnmapMemory(Device, sm);
        CreateBuffer(size, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMemory);
        CopyBuffer(s, _vertexBuffer, size); Vk.DestroyBuffer(Device, s, null); Vk.FreeMemory(Device, sm, null);
    }

    private void CreateDescriptorSetLayout()
    {
        var uboBinding = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };
        var li = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &uboBinding };
        if (Vk.CreateDescriptorSetLayout(Device, in li, null, out _descriptorSetLayout) != Result.Success) throw new InvalidOperationException("failed to create descriptor set layout!");
    }

    private void CreateUniformBuffers()
    {
        int count = SwapChainImages.Length;
        _uniformBuffers = new Buffer[count]; _uniformBufferMemories = new DeviceMemory[count]; _uniformBufferMapped = new void*[count];
        for (int i = 0; i < count; i++)
        {
            ulong size = (ulong)sizeof(LightingUBO);
            CreateBuffer(size, BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _uniformBuffers[i], out _uniformBufferMemories[i]);
            void* mapped;
            Vk.MapMemory(Device, _uniformBufferMemories[i], 0, size, 0, &mapped);
            _uniformBufferMapped[i] = mapped;
        }
    }

    private void CreateDescriptorPoolAndSets()
    {
        int count = SwapChainImages.Length;
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)count * 2 };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = (uint)count * 2 };
        if (Vk.CreateDescriptorPool(Device, in poolInfo, null, out _descriptorPool) != Result.Success) throw new InvalidOperationException("failed to create descriptor pool!");
        var layouts = new DescriptorSetLayout[count]; Array.Fill(layouts, _descriptorSetLayout);
        _descriptorSets = new DescriptorSet[count];
        fixed (DescriptorSetLayout* lp = layouts) { var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = (uint)count, PSetLayouts = lp }; fixed (DescriptorSet* sp = _descriptorSets) if (Vk.AllocateDescriptorSets(Device, in ai, sp) != Result.Success) throw new InvalidOperationException("failed to allocate descriptor sets!"); }
        for (int i = 0; i < count; i++) { var bi = new DescriptorBufferInfo { Buffer = _uniformBuffers[i], Offset = 0, Range = (ulong)sizeof(LightingUBO) }; var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSets[i], DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi }; Vk.UpdateDescriptorSets(Device, 1, &w, 0, null); }
    }

    private Pipeline BuildPipeline(ShaderModule vert, ShaderModule frag, PipelineLayout layout)
    {
        var vs = MakeShaderStage(ShaderStageFlags.VertexBit, vert);
        var fs = MakeShaderStage(ShaderStageFlags.FragmentBit, frag);
        var stages = stackalloc[] { vs, fs };
        var bd = new VertexInputBindingDescription { Binding = 0, Stride = 6 * sizeof(float), InputRate = VertexInputRate.Vertex };
        var attrs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = 3 * sizeof(float) },
        };
        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &bd, VertexAttributeDescriptionCount = 2, PVertexAttributeDescriptions = attrs };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vps = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var dynStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dyn = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
        var rast = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less };
        var cba = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cba };
        var pi = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vps, PRasterizationState = &rast, PMultisampleState = &ms, PDepthStencilState = &ds, PColorBlendState = &cb, PDynamicState = &dyn, Layout = layout, RenderPass = RenderPass, Subpass = 0 };
        if (Vk.CreateGraphicsPipelines(Device, default, 1, in pi, null, out var pipeline) != Result.Success) throw new InvalidOperationException("failed to create pipeline!");
        SilkMarshal.Free((nint)vs.PName); SilkMarshal.Free((nint)fs.PName);
        return pipeline;
    }

    private void CreatePipelines()
    {
        var dsLayout = _descriptorSetLayout;
        var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &dsLayout };
        if (Vk.CreatePipelineLayout(Device, in pli, null, out _pipelineLayout) != Result.Success) throw new InvalidOperationException("failed to create pipeline layout!");
        var lv = CreateShaderModule("Content/Shaders/DiffuseLighting/shader.vert.spv");
        var lf = CreateShaderModule("Content/Shaders/DiffuseLighting/shader.frag.spv");
        var lampFrag = CreateShaderModule("Content/Shaders/DiffuseLighting/lamp.frag.spv");
        _lightingPipeline = BuildPipeline(lv, lf, _pipelineLayout);
        _lampPipeline = BuildPipeline(lv, lampFrag, _pipelineLayout);
        Vk.DestroyShaderModule(Device, lv, null); Vk.DestroyShaderModule(Device, lf, null); Vk.DestroyShaderModule(Device, lampFrag, null);
    }

    private void UpdateUniformBuffer(uint imageIndex, Matrix4x4 model)
    {
        var ubo = new LightingUBO { Model = model, View = _camera.GetViewMatrix(), Projection = _camera.GetProjectionMatrix(), LightPos = LightPos, ViewPos = _camera.Position, LightColor = LightColor, ObjectColor = ObjectColor };
        System.Buffer.MemoryCopy(&ubo, _uniformBufferMapped[imageIndex], sizeof(LightingUBO), sizeof(LightingUBO));
    }

    protected override void OnRender(CommandBuffer cmd, uint imageIndex, double delta)
    {
        var vb = _vertexBuffer; ulong off = 0; var ds = _descriptorSets[imageIndex];

        UpdateUniformBuffer(imageIndex, Matrix4x4.Identity);
        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _lightingPipeline);
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &off);
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
        Vk.CmdDraw(cmd, 36, 1, 0, 0);

        var lampModel = Matrix4x4.CreateScale(0.2f) * Matrix4x4.CreateTranslation(LightPos);
        var lampUbo = new LightingUBO { Model = lampModel, View = _camera.GetViewMatrix(), Projection = _camera.GetProjectionMatrix(), LightPos = LightPos, ViewPos = _camera.Position, LightColor = LightColor, ObjectColor = LightColor };
        System.Buffer.MemoryCopy(&lampUbo, _uniformBufferMapped[imageIndex], sizeof(LightingUBO), sizeof(LightingUBO));
        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _lampPipeline);
        Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
        Vk.CmdDraw(cmd, 36, 1, 0, 0);
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
        Vk.DestroyDescriptorSetLayout(Device, _descriptorSetLayout, null);
        Vk.DestroyBuffer(Device, _vertexBuffer, null); Vk.FreeMemory(Device, _vertexBufferMemory, null);
        Vk.DestroyPipeline(Device, _lightingPipeline, null); Vk.DestroyPipeline(Device, _lampPipeline, null);
        Vk.DestroyPipelineLayout(Device, _pipelineLayout, null);
    }
}
