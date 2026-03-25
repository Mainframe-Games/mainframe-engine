using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Vulkan ImGui renderer. Must be created after the Vulkan renderer is initialized.
/// Call Update() each frame before game OnImGui, then Render() inside the render pass.
/// </summary>
internal unsafe class VulkanImGuiController : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly IWindow _window;
    private readonly IInputContext _input;

    // Font texture
    private Image _fontImage;
    private DeviceMemory _fontImageMemory;
    private ImageView _fontImageView;
    private Sampler _fontSampler;

    // Descriptor
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    // Pipeline
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;

    // Per-frame vertex/index buffers (one per swapchain image), host-visible + persistently mapped
    private VkBuffer[] _vertexBuffers;
    private DeviceMemory[] _vertexMemory;
    private nint[] _vertexMapped;
    private ulong[] _vertexCapacity;

    private VkBuffer[] _indexBuffers;
    private DeviceMemory[] _indexMemory;
    private nint[] _indexMapped;
    private ulong[] _indexCapacity;

    private readonly nint _imguiCtx;

    // ImDrawVert: vec2 pos (8) + vec2 uv (8) + uint col (4) = 20 bytes
    private const uint VertexSize = 20;
    // ImDrawIdx is ushort by default
    private const uint IndexSize = 2;

    public VulkanImGuiController(IVulkanContext ctx, IInputContext input, IWindow window)
    {
        _ctx = ctx;
        _input = input;
        _window = window;

        var n = (int)ctx.SwapchainImageCount;
        _vertexBuffers = new VkBuffer[n];
        _vertexMemory = new DeviceMemory[n];
        _vertexMapped = new nint[n];
        _vertexCapacity = new ulong[n];
        _indexBuffers = new VkBuffer[n];
        _indexMemory = new DeviceMemory[n];
        _indexMapped = new nint[n];
        _indexCapacity = new ulong[n];

        _imguiCtx = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiCtx);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        UploadFontTexture();
        CreateDescriptors();
        CreatePipeline();
        SetupInput();

        io.DisplaySize = new Vector2(_window.Size.X, _window.Size.Y);
        io.DeltaTime = 1f / 60f;
    }

    /// <summary>Call once per frame before the game's OnImGui.</summary>
    public void Update(float deltaTime)
    {
        ImGui.SetCurrentContext(_imguiCtx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_window.Size.X, _window.Size.Y);
        io.DeltaTime = deltaTime;
        ImGui.NewFrame();
    }

    /// <summary>Call inside the render pass, after game OnRender, before EndFrame.</summary>
    public void Render()
    {
        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    #region Input

    private void SetupInput()
    {
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.Scroll += OnScroll;
        }
        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += OnKeyDown;
            kb.KeyUp += OnKeyUp;
            kb.KeyChar += OnKeyChar;
        }
    }

    private void TeardownInput()
    {
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseMove -= OnMouseMove;
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.Scroll -= OnScroll;
        }
        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown -= OnKeyDown;
            kb.KeyUp -= OnKeyUp;
            kb.KeyChar -= OnKeyChar;
        }
    }

    private static void OnMouseMove(IMouse _, Vector2 pos) =>
        ImGui.GetIO().AddMousePosEvent(pos.X, pos.Y);

    private static void OnMouseDown(IMouse _, MouseButton btn) =>
        ImGui.GetIO().AddMouseButtonEvent(MapMouseButton(btn), true);

    private static void OnMouseUp(IMouse _, MouseButton btn) =>
        ImGui.GetIO().AddMouseButtonEvent(MapMouseButton(btn), false);

    private static void OnScroll(IMouse _, ScrollWheel scroll) =>
        ImGui.GetIO().AddMouseWheelEvent(scroll.X, scroll.Y);

    private static int MapMouseButton(MouseButton btn) => btn switch
    {
        MouseButton.Left   => 0,
        MouseButton.Right  => 1,
        MouseButton.Middle => 2,
        MouseButton.Button4 => 3,
        MouseButton.Button5 => 4,
        _ => -1,
    };

    private static void OnKeyDown(IKeyboard _, Key key, int __) => DispatchKey(key, true);
    private static void OnKeyUp(IKeyboard _, Key key, int __) => DispatchKey(key, false);

    private static void OnKeyChar(IKeyboard _, char c) =>
        ImGui.GetIO().AddInputCharacter(c);

    private static void DispatchKey(Key key, bool down)
    {
        var io = ImGui.GetIO();
        var imkey = MapKey(key);
        if (imkey != ImGuiKey.None)
            io.AddKeyEvent(imkey, down);

        // Modifier events
        if (key is Key.ControlLeft or Key.ControlRight)
            io.AddKeyEvent(ImGuiKey.ModCtrl, down);
        else if (key is Key.ShiftLeft or Key.ShiftRight)
            io.AddKeyEvent(ImGuiKey.ModShift, down);
        else if (key is Key.AltLeft or Key.AltRight)
            io.AddKeyEvent(ImGuiKey.ModAlt, down);
        else if (key is Key.SuperLeft or Key.SuperRight)
            io.AddKeyEvent(ImGuiKey.ModSuper, down);
    }

    private static ImGuiKey MapKey(Key key) => key switch
    {
        Key.Tab        => ImGuiKey.Tab,
        Key.Left       => ImGuiKey.LeftArrow,
        Key.Right      => ImGuiKey.RightArrow,
        Key.Up         => ImGuiKey.UpArrow,
        Key.Down       => ImGuiKey.DownArrow,
        Key.PageUp     => ImGuiKey.PageUp,
        Key.PageDown   => ImGuiKey.PageDown,
        Key.Home       => ImGuiKey.Home,
        Key.End        => ImGuiKey.End,
        Key.Insert     => ImGuiKey.Insert,
        Key.Delete     => ImGuiKey.Delete,
        Key.Backspace  => ImGuiKey.Backspace,
        Key.Space      => ImGuiKey.Space,
        Key.Enter      => ImGuiKey.Enter,
        Key.Escape     => ImGuiKey.Escape,
        Key.GraveAccent => ImGuiKey.GraveAccent,
        Key.CapsLock   => ImGuiKey.CapsLock,
        Key.ScrollLock => ImGuiKey.ScrollLock,
        Key.NumLock    => ImGuiKey.NumLock,
        Key.PrintScreen => ImGuiKey.PrintScreen,
        Key.Pause      => ImGuiKey.Pause,
        Key.F1  => ImGuiKey.F1,  Key.F2  => ImGuiKey.F2,  Key.F3  => ImGuiKey.F3,
        Key.F4  => ImGuiKey.F4,  Key.F5  => ImGuiKey.F5,  Key.F6  => ImGuiKey.F6,
        Key.F7  => ImGuiKey.F7,  Key.F8  => ImGuiKey.F8,  Key.F9  => ImGuiKey.F9,
        Key.F10 => ImGuiKey.F10, Key.F11 => ImGuiKey.F11, Key.F12 => ImGuiKey.F12,
        Key.A => ImGuiKey.A, Key.B => ImGuiKey.B, Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D, Key.E => ImGuiKey.E, Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G, Key.H => ImGuiKey.H, Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J, Key.K => ImGuiKey.K, Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M, Key.N => ImGuiKey.N, Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P, Key.Q => ImGuiKey.Q, Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S, Key.T => ImGuiKey.T, Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V, Key.W => ImGuiKey.W, Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y, Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey._0, Key.Number1 => ImGuiKey._1, Key.Number2 => ImGuiKey._2,
        Key.Number3 => ImGuiKey._3, Key.Number4 => ImGuiKey._4, Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6, Key.Number7 => ImGuiKey._7, Key.Number8 => ImGuiKey._8,
        Key.Number9 => ImGuiKey._9,
        Key.ShiftLeft or Key.ShiftRight => ImGuiKey.None,
        Key.ControlLeft or Key.ControlRight => ImGuiKey.None,
        Key.AltLeft or Key.AltRight => ImGuiKey.None,
        Key.SuperLeft or Key.SuperRight => ImGuiKey.None,
        _ => ImGuiKey.None,
    };

    #endregion

    #region Font Texture

    private void UploadFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        var imageSize = (ulong)(width * height * bytesPerPixel);

        // Staging buffer
        CreateBuffer(imageSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuf, out var stagingMem);

        void* mapped;
        _ctx.Vk.MapMemory(_ctx.Device, stagingMem, 0, imageSize, 0, &mapped);
        Unsafe.CopyBlock(mapped, pixels, (uint)imageSize);
        _ctx.Vk.UnmapMemory(_ctx.Device, stagingMem);

        // Create device-local image
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        _ctx.Vk.CreateImage(_ctx.Device, imageInfo, null, out _fontImage);

        _ctx.Vk.GetImageMemoryRequirements(_ctx.Device, _fontImage, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        _ctx.Vk.AllocateMemory(_ctx.Device, allocInfo, null, out _fontImageMemory);
        _ctx.Vk.BindImageMemory(_ctx.Device, _fontImage, _fontImageMemory, 0);

        // Upload via staging buffer
        var cb = BeginOneTimeCommands();
        TransitionImageLayout(cb, _fontImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1,
            },
            ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
        };
        _ctx.Vk.CmdCopyBufferToImage(cb, stagingBuf, _fontImage, ImageLayout.TransferDstOptimal, 1, &region);

        TransitionImageLayout(cb, _fontImage, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndOneTimeCommands(cb);

        _ctx.Vk.DestroyBuffer(_ctx.Device, stagingBuf, null);
        _ctx.Vk.FreeMemory(_ctx.Device, stagingMem, null);

        // Image view
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _fontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        _ctx.Vk.CreateImageView(_ctx.Device, viewInfo, null, out _fontImageView);

        // Sampler
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
        };
        _ctx.Vk.CreateSampler(_ctx.Device, samplerInfo, null, out _fontSampler);

        io.Fonts.SetTexID(1);
        io.Fonts.ClearTexData();
    }

    #endregion

    #region Descriptors

    private void CreateDescriptors()
    {
        var binding = new DescriptorSetLayoutBinding
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
            PBindings = &binding,
        };
        _ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, layoutInfo, null, out _descriptorSetLayout);

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };
        _ctx.Vk.CreateDescriptorPool(_ctx.Device, poolInfo, null, out _descriptorPool);

        var setLayout = _descriptorSetLayout;
        var dsAlloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        _ctx.Vk.AllocateDescriptorSets(_ctx.Device, dsAlloc, out _descriptorSet);

        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _fontSampler,
            ImageView = _fontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
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
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
    }

    #endregion

    #region Pipeline

    private void CreatePipeline()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;

        var vertCode = File.ReadAllBytes("Content/Shaders/ImGui/ImGui.vk.vert.spv");
        var fragCode = File.ReadAllBytes("Content/Shaders/ImGui/ImGui.vk.frag.spv");
        var vertModule = CreateShaderModule(vertCode);
        var fragModule = CreateShaderModule(fragCode);
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,   Module = vertModule, PName = entryPoint },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entryPoint },
        };

        // ImDrawVert layout: vec2 pos, vec2 uv, R8G8B8A8Unorm color
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = VertexSize,
            InputRate = VertexInputRate.Vertex,
        };
        var attribs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat,    Offset = 0  },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat,    Offset = 8  },
            new() { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm,   Offset = 16 },
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,   PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attribs,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1, ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        // Pre-multiplied alpha blend — matches ImGui's expected output
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1, PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2, PDynamicStates = dynamicStates,
        };

        var descLayout = _descriptorSetLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = 16, // vec2 scale + vec2 translate
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &descLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
        };
        vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out _pipelineLayout);

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2, PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = _ctx.RenderPass,
        };
        vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out _pipeline);

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    #endregion

    #region Render

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;
        if (drawData.TotalVtxCount == 0) return;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        var extent = _ctx.SwapchainExtent;
        var imageIdx = (int)_ctx.CurrentImageIndex;

        var totalVtxBytes = (ulong)(drawData.TotalVtxCount * VertexSize);
        var totalIdxBytes = (ulong)(drawData.TotalIdxCount * IndexSize);

        EnsureBuffer(ref _vertexBuffers[imageIdx], ref _vertexMemory[imageIdx],
            ref _vertexMapped[imageIdx], ref _vertexCapacity[imageIdx],
            totalVtxBytes, BufferUsageFlags.VertexBufferBit);
        EnsureBuffer(ref _indexBuffers[imageIdx], ref _indexMemory[imageIdx],
            ref _indexMapped[imageIdx], ref _indexCapacity[imageIdx],
            totalIdxBytes, BufferUsageFlags.IndexBufferBit);

        // Upload all vertices and indices
        var vtxDst = (byte*)_vertexMapped[imageIdx];
        var idxDst = (byte*)_indexMapped[imageIdx];
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var cmdList = drawData.CmdLists[i];
            var vtxBytes = (uint)(cmdList.VtxBuffer.Size * VertexSize);
            var idxBytes = (uint)(cmdList.IdxBuffer.Size * IndexSize);
            Unsafe.CopyBlock(vtxDst, (void*)cmdList.VtxBuffer.Data, vtxBytes);
            Unsafe.CopyBlock(idxDst, (void*)cmdList.IdxBuffer.Data, idxBytes);
            vtxDst += vtxBytes;
            idxDst += idxBytes;
        }

        // Bind pipeline and resources
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);

        var vb = _vertexBuffers[imageIdx];
        var vbOffset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &vbOffset);
        vk.CmdBindIndexBuffer(cb, _indexBuffers[imageIdx], 0, IndexType.Uint16);

        var ds = _descriptorSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);

        // Standard (non-flipped) viewport — ImGui Y+ down matches Vulkan NDC
        var viewport = new Viewport
        {
            X = 0, Y = 0,
            Width = (float)extent.Width,
            Height = (float)extent.Height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        // Push scale/translate to convert ImGui display coords → NDC
        var scale = new Vector2(2f / drawData.DisplaySize.X, 2f / drawData.DisplaySize.Y);
        var translate = new Vector2(
            -1f - drawData.DisplayPos.X * scale.X,
            -1f - drawData.DisplayPos.Y * scale.Y);
        var pushData = stackalloc float[] { scale.X, scale.Y, translate.X, translate.Y };
        vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.VertexBit, 0, 16, pushData);

        // Draw each command list
        var vtxOffset = 0;
        var idxOffset = 0u;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var cmdList = drawData.CmdLists[i];
            for (int j = 0; j < cmdList.CmdBuffer.Size; j++)
            {
                var cmd = cmdList.CmdBuffer[j];

                // Skip user callbacks
                if (cmd.UserCallback != 0) continue;

                var clipMin = new Vector2(
                    cmd.ClipRect.X - drawData.DisplayPos.X,
                    cmd.ClipRect.Y - drawData.DisplayPos.Y);
                var clipMax = new Vector2(
                    cmd.ClipRect.Z - drawData.DisplayPos.X,
                    cmd.ClipRect.W - drawData.DisplayPos.Y);
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;

                var scissor = new Rect2D
                {
                    Offset = new Offset2D
                    {
                        X = Math.Max(0, (int)clipMin.X),
                        Y = Math.Max(0, (int)clipMin.Y),
                    },
                    Extent = new Extent2D
                    {
                        Width  = (uint)Math.Min(clipMax.X - clipMin.X, extent.Width),
                        Height = (uint)Math.Min(clipMax.Y - clipMin.Y, extent.Height),
                    },
                };
                vk.CmdSetScissor(cb, 0, 1, &scissor);

                vk.CmdDrawIndexed(cb, cmd.ElemCount, 1,
                    idxOffset + cmd.IdxOffset,
                    vtxOffset + (int)cmd.VtxOffset, 0);
            }
            vtxOffset += cmdList.VtxBuffer.Size;
            idxOffset += (uint)cmdList.IdxBuffer.Size;
        }
    }

    private void EnsureBuffer(ref VkBuffer buffer, ref DeviceMemory memory,
        ref nint mapped, ref ulong capacity, ulong required, BufferUsageFlags usage)
    {
        if (required <= capacity) return;

        if (capacity > 0)
        {
            _ctx.Vk.UnmapMemory(_ctx.Device, memory);
            _ctx.Vk.DestroyBuffer(_ctx.Device, buffer, null);
            _ctx.Vk.FreeMemory(_ctx.Device, memory, null);
        }

        // Grow to at least 1 MB or double the current capacity, whichever is larger
        capacity = Math.Max(required, capacity == 0 ? 1024ul * 1024ul : capacity * 2);

        CreateBuffer(capacity, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out buffer, out memory);

        void* ptr;
        _ctx.Vk.MapMemory(_ctx.Device, memory, 0, capacity, 0, &ptr);
        mapped = (nint)ptr;
    }

    #endregion

    #region Vulkan helpers

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var bufInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        _ctx.Vk.CreateBuffer(_ctx.Device, bufInfo, null, out buffer);

        _ctx.Vk.GetBufferMemoryRequirements(_ctx.Device, buffer, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, properties),
        };
        _ctx.Vk.AllocateMemory(_ctx.Device, allocInfo, null, out memory);
        _ctx.Vk.BindBufferMemory(_ctx.Device, buffer, memory, 0);
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        throw new Exception("[Vulkan] No suitable memory type found!");
    }

    private CommandBuffer BeginOneTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _ctx.CommandPool,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, allocInfo, &cb);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _ctx.Vk.BeginCommandBuffer(cb, beginInfo);
        return cb;
    }

    private void EndOneTimeCommands(CommandBuffer cb)
    {
        _ctx.Vk.EndCommandBuffer(cb);
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        _ctx.Vk.QueueSubmit(_ctx.GraphicsQueue, 1, submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.GraphicsQueue);
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, &cb);
    }

    private void TransitionImageLayout(CommandBuffer cb, Image image, ImageLayout oldLayout, ImageLayout newLayout)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        PipelineStageFlags srcStage, dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new InvalidOperationException($"[Vulkan] Unsupported image layout transition: {oldLayout} → {newLayout}");
        }

        _ctx.Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)ptr,
            };
            _ctx.Vk.CreateShaderModule(_ctx.Device, createInfo, null, out var module);
            return module;
        }
    }

    #endregion

    public void Dispose()
    {
        TeardownInput();

        var vk = _ctx.Vk;
        vk.DeviceWaitIdle(_ctx.Device);

        for (int i = 0; i < _vertexCapacity.Length; i++)
        {
            if (_vertexCapacity[i] > 0)
            {
                vk.UnmapMemory(_ctx.Device, _vertexMemory[i]);
                vk.DestroyBuffer(_ctx.Device, _vertexBuffers[i], null);
                vk.FreeMemory(_ctx.Device, _vertexMemory[i], null);
            }
            if (_indexCapacity[i] > 0)
            {
                vk.UnmapMemory(_ctx.Device, _indexMemory[i]);
                vk.DestroyBuffer(_ctx.Device, _indexBuffers[i], null);
                vk.FreeMemory(_ctx.Device, _indexMemory[i], null);
            }
        }

        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _descriptorSetLayout, null);
        vk.DestroySampler(_ctx.Device, _fontSampler, null);
        vk.DestroyImageView(_ctx.Device, _fontImageView, null);
        vk.DestroyImage(_ctx.Device, _fontImage, null);
        vk.FreeMemory(_ctx.Device, _fontImageMemory, null);

        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.DestroyContext(_imguiCtx);
    }
}
