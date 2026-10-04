using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace MainframeEngine;

/// <summary>
/// Vulkan ImGui renderer. Must be created after the Vulkan renderer is initialized.
/// Call Update() each frame before game OnImGui, then Render() inside the render pass — or
/// DiscardFrame() when no frame is rendered, so every NewFrame is paired with Render/EndFrame.
/// </summary>
/// <remarks>
/// HiDPI: ImGui works in window points (<c>DisplaySize</c>, SDL mouse coordinates) and
/// <c>DisplayFramebufferScale</c> = framebuffer pixels / points; clip rectangles are scaled to
/// framebuffer pixels for the scissor.
/// </remarks>
internal sealed unsafe class VulkanImGuiController : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly IWindow _window;
    private readonly IInputContext _input;

    // Font texture (coverage in alpha: data, so UNORM)
    private GpuTexture _fontTexture = null!;

    // Descriptor
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    // Pipeline
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;

    // Per-frame vertex/index buffers (one per frame slot), host-visible + persistently mapped; grown on demand
    // (the replaced buffer goes through the deletion queue)
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private readonly GpuBuffer?[] _indexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];

    private readonly nint _imguiCtx;
    private bool _frameBegun; // NewFrame called, Render/EndFrame not yet

    // ImDrawVert: vec2 pos (8) + vec2 uv (8) + uint col (4) = 20 bytes
    private const uint VertexSize = 20;
    // ImDrawIdx is ushort by default
    private const uint IndexSize = 2;

    public VulkanImGuiController(IVulkanContext ctx, IInputContext input, IWindow window)
    {
        _ctx = ctx;
        _input = input;
        _window = window;

        _imguiCtx = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiCtx);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        UploadFontTexture();
        CreateDescriptors();
        CreatePipeline();
        SetupInput();

        UpdateDisplayMetrics(io);
        io.DeltaTime = 1f / 60f;
    }

    /// <summary>Call once per frame before the game's OnImGui.</summary>
    public void Update(float deltaTime)
    {
        ImGui.SetCurrentContext(_imguiCtx);

        // Several updates per render (or a render that never came): close the open frame first.
        if (_frameBegun)
            ImGui.EndFrame();

        var io = ImGui.GetIO();
        UpdateDisplayMetrics(io);
        io.DeltaTime = deltaTime > 0f ? deltaTime : 1f / 60f;
        ImGui.NewFrame();
        _frameBegun = true;
    }

    /// <summary>Call inside the render pass, after game OnRender, before EndFrame.</summary>
    public void Render()
    {
        if (!_frameBegun) return;
        _frameBegun = false;

        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    /// <summary>Ends the open ImGui frame without drawing (the render frame was skipped).</summary>
    public void DiscardFrame()
    {
        if (!_frameBegun) return;
        _frameBegun = false;

        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.EndFrame();
    }

    private void UpdateDisplayMetrics(ImGuiIOPtr io)
    {
        var points = _window.Size;
        var pixels = WindowPixels.FramebufferSize(_window);
        io.DisplaySize = new Vector2(Math.Max(points.X, 0), Math.Max(points.Y, 0));
        io.DisplayFramebufferScale = points.X > 0 && points.Y > 0 && pixels.X > 0 && pixels.Y > 0
            ? new Vector2((float)pixels.X / points.X, (float)pixels.Y / points.Y)
            : Vector2.One;
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
        MouseButton.Left => 0,
        MouseButton.Right => 1,
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
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.GraveAccent => ImGuiKey.GraveAccent,
        Key.CapsLock => ImGuiKey.CapsLock,
        Key.ScrollLock => ImGuiKey.ScrollLock,
        Key.NumLock => ImGuiKey.NumLock,
        Key.PrintScreen => ImGuiKey.PrintScreen,
        Key.Pause => ImGuiKey.Pause,
        Key.F1 => ImGuiKey.F1,
        Key.F2 => ImGuiKey.F2,
        Key.F3 => ImGuiKey.F3,
        Key.F4 => ImGuiKey.F4,
        Key.F5 => ImGuiKey.F5,
        Key.F6 => ImGuiKey.F6,
        Key.F7 => ImGuiKey.F7,
        Key.F8 => ImGuiKey.F8,
        Key.F9 => ImGuiKey.F9,
        Key.F10 => ImGuiKey.F10,
        Key.F11 => ImGuiKey.F11,
        Key.F12 => ImGuiKey.F12,
        Key.A => ImGuiKey.A,
        Key.B => ImGuiKey.B,
        Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E,
        Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G,
        Key.H => ImGuiKey.H,
        Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J,
        Key.K => ImGuiKey.K,
        Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M,
        Key.N => ImGuiKey.N,
        Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P,
        Key.Q => ImGuiKey.Q,
        Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S,
        Key.T => ImGuiKey.T,
        Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V,
        Key.W => ImGuiKey.W,
        Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y,
        Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey._0,
        Key.Number1 => ImGuiKey._1,
        Key.Number2 => ImGuiKey._2,
        Key.Number3 => ImGuiKey._3,
        Key.Number4 => ImGuiKey._4,
        Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6,
        Key.Number7 => ImGuiKey._7,
        Key.Number8 => ImGuiKey._8,
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
        var size = width * height * bytesPerPixel;

        // Uploaded by the upload queue at the start of the first frame; no queue wait.
        _fontTexture = GpuTexture.Create2D(_ctx, (uint)width, (uint)height, new ReadOnlySpan<byte>(pixels, size),
            TextureColorSpace.Linear, TextureSampling.LinearRepeat);

        io.Fonts.SetTexID(1);
        io.Fonts.ClearTexData();
    }

    #endregion

    #region Descriptors

    private void CreateDescriptors()
    {
        _descriptorSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "ImGui font");
        _descriptorPool = PipelineBuilder.CreatePool(_ctx, 1,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "ImGui font");
        _descriptorSet = PipelineBuilder.AllocateSet(_ctx, _descriptorPool, _descriptorSetLayout, "ImGui font");
        PipelineBuilder.WriteImage(_ctx, _descriptorSet, 0, _fontTexture.Descriptor);
    }

    #endregion

    #region Pipeline

    private void CreatePipeline()
    {
        _pipelineLayout = PipelineBuilder.CreateLayout(_ctx, [_descriptorSetLayout], 16, ShaderStageFlags.VertexBit, "ImGui"); // vec2 scale + vec2 translate

        // ImDrawVert layout: vec2 pos, vec2 uv, R8G8B8A8Unorm color
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = VertexSize, InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 },
            new() { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 16 },
        ];

        // Straight alpha, no depth: ImGui's expected output.
        _pipeline = PipelineBuilder.Create(_ctx, new PipelineState { Blend = BlendMode.Alpha }, _pipelineLayout,
            _ctx.RenderPass, "Shaders/ImGui/ImGui.vk.vert.spv", "Shaders/ImGui/ImGui.vk.frag.spv",
            bindings, attributes, "ImGui");
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
        var imageIdx = _ctx.FrameSlot;

        var totalVtxBytes = (ulong)(drawData.TotalVtxCount * VertexSize);
        var totalIdxBytes = (ulong)(drawData.TotalIdxCount * IndexSize);

        var vertexBuffer = EnsureBuffer(ref _vertexBuffers[imageIdx], totalVtxBytes, BufferUsageFlags.VertexBufferBit);
        var indexBuffer = EnsureBuffer(ref _indexBuffers[imageIdx], totalIdxBytes, BufferUsageFlags.IndexBufferBit);

        // Upload all vertices and indices
        var vtxDst = (byte*)vertexBuffer.MappedPointer;
        var idxDst = (byte*)indexBuffer.MappedPointer;
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

        var vb = vertexBuffer.Handle;
        var vbOffset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &vbOffset);
        vk.CmdBindIndexBuffer(cb, indexBuffer.Handle, 0, IndexType.Uint16);

        var ds = _descriptorSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);

        // Standard (non-flipped) viewport — ImGui Y+ down matches Vulkan NDC
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = extent.Width,
            Height = extent.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        // Push scale/translate to convert ImGui display coords → NDC
        var scale = new Vector2(2f / drawData.DisplaySize.X, 2f / drawData.DisplaySize.Y);
        var translate = new Vector2(
            -1f - drawData.DisplayPos.X * scale.X,
            -1f - drawData.DisplayPos.Y * scale.Y);
        var pushData = stackalloc float[] { scale.X, scale.Y, translate.X, translate.Y };
        vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.VertexBit, 0, 16, pushData);

        // Clip rectangles are in display points; the scissor is in framebuffer pixels.
        var clipOffset = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;

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

                var clipMin = Vector2.Max(
                    new Vector2(cmd.ClipRect.X - clipOffset.X, cmd.ClipRect.Y - clipOffset.Y) * clipScale,
                    Vector2.Zero);
                var clipMax = Vector2.Min(
                    new Vector2(cmd.ClipRect.Z - clipOffset.X, cmd.ClipRect.W - clipOffset.Y) * clipScale,
                    new Vector2(extent.Width, extent.Height));
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;

                var scissor = new Rect2D
                {
                    Offset = new Offset2D { X = (int)clipMin.X, Y = (int)clipMin.Y },
                    Extent = new Extent2D
                    {
                        Width  = (uint)(clipMax.X - clipMin.X),
                        Height = (uint)(clipMax.Y - clipMin.Y),
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

    private GpuBuffer EnsureBuffer(ref GpuBuffer? buffer, ulong required, BufferUsageFlags usage)
    {
        if (buffer is not null && required <= buffer.Size)
            return buffer;

        // Grow to at least 1 MB or double the current capacity, whichever is larger. The old buffer was last
        // used by this slot's previous frame, which has finished; the deletion queue retires it anyway.
        var capacity = Math.Max(required, buffer is null ? 1024ul * 1024ul : buffer.Size * 2);
        buffer?.Dispose();
        buffer = GpuBuffer.Create(_ctx, capacity, usage, GpuMemoryUsage.Dynamic);
        return buffer;
    }

    #endregion

    /// <summary>Releases input hooks, the ImGui context and (through the deletion queue) every GPU object.</summary>
    public void Dispose()
    {
        TeardownInput();

        foreach (var buffer in _vertexBuffers)
            buffer?.Dispose();
        foreach (var buffer in _indexBuffers)
            buffer?.Dispose();

        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
        deletions.Enqueue(GpuDeletion.Of(_descriptorPool));
        deletions.Enqueue(GpuDeletion.Of(_descriptorSetLayout));
        _fontTexture.Dispose();

        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.DestroyContext(_imguiCtx);
    }
}
