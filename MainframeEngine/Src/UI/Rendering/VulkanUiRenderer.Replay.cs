using System.Numerics;
using System.Runtime.InteropServices;
using MainframeEngine.UI.Rml;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public sealed unsafe partial class VulkanUiRenderer
{
    // Replay state (valid during RecordOffscreen).
    private CommandBuffer _cb;
    private Vk _vk = null!;
    private int _top;
    private bool _passOpen;
    private Pipeline _boundPipeline;
    private DescriptorSet _boundSet0;
    private VkBuffer _boundBuffer;
    private int _pushedTransform = int.MinValue;
    private int _pushedRegion = -1;
    private Rect2D _boundScissor;
    private byte _boundStencilRef;
    private bool _stencilRefValid;
    private int _gradientIndex;
    private int _drawCalls;
    private int _passes;
    private bool _hasContent;

    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryPush
    {
        public Vector2 Translation;
        public uint TextureFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullscreenPush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public float Factor;
        public uint Linearize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurPush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public Vector2 TexelOffset;
        public Vector2 TexCoordMin;
        public Vector2 TexCoordMax;
        private Vector2 _pad;
        public Vector4 Weights;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropShadowPush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public Vector4 Color;
        public Vector2 TexCoordMin;
        public Vector2 TexCoordMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorMatrixPush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public Matrix4x4 Matrix;
    }

    /// <summary>A pixel rectangle (top-left origin), half-open: [X0, X1) × [Y0, Y1).</summary>
    private readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
    {
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

        public PixelRect Extend(int n) => new(X0 - n, Y0 - n, X1 + n, Y1 + n);

        public PixelRect Offset(int x, int y) => new(X0 + x, Y0 + y, X1 + x, Y1 + y);

        public PixelRect Intersect(in PixelRect other) => new(Math.Max(X0, other.X0), Math.Max(Y0, other.Y0),
            Math.Max(Math.Max(X0, other.X0), Math.Min(X1, other.X1)), Math.Max(Math.Max(Y0, other.Y0), Math.Min(Y1, other.Y1)));

        public PixelRect Clamp(Extent2D extent) => new(Math.Clamp(X0, 0, (int)extent.Width), Math.Clamp(Y0, 0, (int)extent.Height),
            Math.Clamp(X1, 0, (int)extent.Width), Math.Clamp(Y1, 0, (int)extent.Height));

        public Rect2D ToRect2D() => new(new Offset2D(X0, Y0), new Extent2D((uint)Math.Max(0, Width), (uint)Math.Max(0, Height)));
    }

    // ── IOverlayRenderer ─────────────────────────────────────────────────────────────────────────────────────

    void IOverlayRenderer.RecordOffscreen(CommandBuffer commandBuffer)
    {
        CollectReleases(_ctx.Deletions.CompletedFrame);
        var extent = _ctx.SwapchainExtent;

        // A render without an update in between (frame-rate cap): the list was already replayed and the resources it
        // references may have been released since, so it is not replayed again — the base layer still holds its
        // result and is composited as is (unless the swapchain was resized).
        if (_listConsumed)
        {
            _hasContent = _hasContent && Visible && !IsDisposed &&
                          extent.Width == _targetExtent.Width && extent.Height == _targetExtent.Height;
            return;
        }

        _hasContent = false;
        if (_commandCount == 0 || IsDisposed)
        {
            _listConsumed = true;
            Stats = new UiRenderStats(_commandCount, 0, 0, _liveGeometry, _liveTextures, _arena.ChunkCount, _layers.Count);
            return;
        }

        // Zero-sized swapchain: nothing can be drawn; the list stays unconsumed (BeginFrame regenerates saved textures).
        if (extent.Width == 0 || extent.Height == 0)
            return;

        // Hidden UI still replays a list that saves layers into textures (box-shadows), or they would stay empty.
        if (!Visible && !_listHasSavedTargets)
        {
            _listConsumed = true;
            return;
        }

        EnsureTargets(extent, _maxLayerDepth + 1);
        EnsureGradientCapacity(_shaderDraws);

        _cb = commandBuffer;
        _vk = _ctx.Vk;
        _drawCalls = 0;
        _passes = 0;
        _gradientIndex = 0;
        _top = 0;

        BeginLayerPass(0, _layerPassClearAll);
        for (var i = 0; i < _commandCount; i++)
        {
            ref readonly var c = ref _commands[i];
            switch (c.Op)
            {
                case UiOp.Geometry:
                    DrawGeometry(c, _geometryPipelines[c.Stencil ? 1 : 0], c.Set, c.TextureFlags);
                    break;
                case UiOp.Shader:
                    DrawGradient(c);
                    break;
                case UiOp.ClipMask:
                    DrawClipMask(c);
                    break;
                case UiOp.PushLayer:
                    EndPass();
                    _top = c.A;
                    BeginLayerPass(_top, _layerPassClearColor);
                    break;
                case UiOp.PopLayer:
                    EndPass();
                    _top = c.A;
                    BeginLayerPass(_top, _layerPassLoad);
                    break;
                case UiOp.Composite:
                    Composite(c);
                    break;
                case UiOp.SaveTexture:
                    SaveTexture(c);
                    break;
                case UiOp.SaveMask:
                    SaveMask(c);
                    break;
            }
        }

        EndPass();
        Barrier(); // the overlay pass samples the base layer
        _listConsumed = true;
        _hasContent = Visible;
        Stats = new UiRenderStats(_commandCount, _drawCalls, _passes, _liveGeometry, _liveTextures, _arena.ChunkCount, _layers.Count);
    }

    void IOverlayRenderer.RecordOverlay(CommandBuffer commandBuffer)
    {
        if (!_hasContent)
            return;
        var vk = _ctx.Vk;
        var extent = _ctx.SwapchainExtent;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _overlayPipeline);
        PipelineBuilder.SetViewport(vk, commandBuffer, extent, flipY: false);
        var set = _layers[0].Set;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new FullscreenPush { UvScale = Vector2.One, Factor = 1f, Linearize = _ctx.OverlayEncodesSrgb ? 1u : 0u };
        vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(FullscreenPush), &push);
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        _drawCalls++;
    }

    // ── Passes ───────────────────────────────────────────────────────────────────────────────────────────────

    private void BeginLayerPass(int layer, RenderPass pass)
    {
        var clears = stackalloc ClearValue[2];
        clears[0] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        BeginPass(pass, _layers[layer].Framebuffer, _targetExtent, clears, 2);
        SetFullViewport();
    }

    /// <summary>A pass on a filter target; its previous contents are kept once it has been written (like GL framebuffers).</summary>
    private void BeginPostPass(Target target)
    {
        BeginPass(target.Initialized ? _postPassLoad : _postPassDiscard, target.Framebuffer, _targetExtent, null, 0);
        target.Initialized = true;
    }

    private void BeginPass(RenderPass pass, Framebuffer framebuffer, Extent2D extent, ClearValue* clears, uint clearCount)
    {
        Barrier();

        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = extent },
            ClearValueCount = clearCount,
            PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(_cb, &info, SubpassContents.Inline);
        _passOpen = true;
        _passes++;

        // Bindings and push constants are re-established per pass (layer and filter passes alternate).
        _boundPipeline = default;
        _boundSet0 = default;
        _boundBuffer = default;
        _pushedTransform = int.MinValue;
        _boundScissor = new Rect2D(default, new Extent2D(uint.MaxValue, uint.MaxValue));
        _stencilRefValid = false;
    }

    /// <summary>
    /// Orders every earlier UI pass's attachment writes before the next pass's reads and writes. The render passes'
    /// external subpass dependencies already say this, but MoltenVK does not turn them into Metal synchronisation for
    /// sub-allocated (heap-placed) images, so later passes read stale layers; an explicit barrier is honoured
    /// everywhere and costs nothing measurable.
    /// </summary>
    private void Barrier()
    {
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit |
                                          PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit |
                            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };
        _vk.CmdPipelineBarrier(_cb, stages, stages, 0, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>
    /// After <c>vkCmdClearAttachments</c>: drivers may implement it as a draw with their own state (MoltenVK does),
    /// leaving the encoder's pipeline, stencil reference, viewport and scissor changed behind our cache's back.
    /// </summary>
    private void InvalidateStateAfterClear()
    {
        _boundPipeline = default;
        _boundSet0 = default;
        _boundBuffer = default;
        _pushedTransform = int.MinValue;
        _boundScissor = new Rect2D(default, new Extent2D(uint.MaxValue, uint.MaxValue));
        _stencilRefValid = false;
    }

    private void EndPass()
    {
        if (!_passOpen)
            return;
        _vk.CmdEndRenderPass(_cb);
        _passOpen = false;
    }

    private void SetFullViewport() => SetViewport(0, 0, (int)_targetExtent.Width, (int)_targetExtent.Height);

    private void SetViewport(int x, int y, int width, int height)
    {
        var viewport = new Viewport { X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height), MinDepth = 0, MaxDepth = 1 };
        _vk.CmdSetViewport(_cb, 0, 1, &viewport);
    }

    private void BindPipeline(Pipeline pipeline)
    {
        if (pipeline.Handle == _boundPipeline.Handle)
            return;
        _vk.CmdBindPipeline(_cb, PipelineBindPoint.Graphics, pipeline);
        _boundPipeline = pipeline;
    }

    private void BindSet(uint index, DescriptorSet set)
    {
        if (index == 0)
        {
            if (set.Handle == _boundSet0.Handle)
                return;
            _boundSet0 = set;
        }

        _vk.CmdBindDescriptorSets(_cb, PipelineBindPoint.Graphics, _layout, index, 1, &set, 0, null);
    }

    private void SetScissor(in PixelRect rect)
    {
        var r = rect.Clamp(_targetExtent).ToRect2D();
        if (r.Offset.X == _boundScissor.Offset.X && r.Offset.Y == _boundScissor.Offset.Y &&
            r.Extent.Width == _boundScissor.Extent.Width && r.Extent.Height == _boundScissor.Extent.Height)
            return;
        _vk.CmdSetScissor(_cb, 0, 1, &r);
        _boundScissor = r;
    }

    /// <summary>The command's scissor in target pixels: offset into its region and clipped to it (UiLayer.Region).</summary>
    private PixelRect ScissorOf(in UiCommand c)
    {
        if (c.Region < 0)
            return c.ScissorEnabled
                ? new PixelRect(c.ScissorX, c.ScissorY, c.ScissorX + c.ScissorW, c.ScissorY + c.ScissorH)
                : new PixelRect(0, 0, (int)_targetExtent.Width, (int)_targetExtent.Height);
        ref readonly var region = ref _regions[c.Region];
        return c.ScissorEnabled
            ? new PixelRect(c.ScissorX, c.ScissorY, c.ScissorX + c.ScissorW, c.ScissorY + c.ScissorH).Offset(region.X0, region.Y0).Intersect(region)
            : region;
    }

    private void SetStencilRef(byte value)
    {
        if (_stencilRefValid && value == _boundStencilRef)
            return;
        _vk.CmdSetStencilReference(_cb, StencilFaceFlags.FrontAndBack, value);
        _boundStencilRef = value;
        _stencilRefValid = true;
    }

    private void Push<T>(in T value, uint offset = 0) where T : unmanaged
    {
        fixed (T* p = &value)
            _vk.CmdPushConstants(_cb, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, offset, (uint)sizeof(T), p);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────────────

    private void PushTransform(int transform, int region)
    {
        if (transform == _pushedTransform && region == _pushedRegion)
            return;

        // Pixels → clip space (y down, no flip); z scaled into [0, 1] for 3D RCSS transforms (RmlUi's GL projection uses
        // near/far ±10000). Numerics matrices are row-vector, and the shaders read them as-is (row-major Slang matrices,
        // mul(v, M)).
        var w = (float)_targetExtent.Width;
        var h = (float)_targetExtent.Height;
        var projection = new Matrix4x4(
            2f / w, 0, 0, 0,
            0, 2f / h, 0, 0,
            0, 0, 1f / 20000f, 0,
            -1f, -1f, 0.5f, 1f);
        if (region >= 0) // RmlUi lays a region layer out from (0, 0): move it to the region's origin
            projection = Matrix4x4.CreateTranslation(_regions[region].X0, _regions[region].Y0, 0f) * projection;
        var m = transform < 0 ? projection : _transforms[transform] * projection;
        Push(m);
        _pushedTransform = transform;
        _pushedRegion = region;
    }

    private void BindGeometry(in UiCommand c)
    {
        if (c.Buffer.Handle != _boundBuffer.Handle)
        {
            var buffer = c.Buffer;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_cb, 0, 1, &buffer, &offset);
            _vk.CmdBindIndexBuffer(_cb, buffer, 0, IndexType.Uint32);
            _boundBuffer = buffer;
        }
    }

    private void DrawGeometry(in UiCommand c, Pipeline pipeline, DescriptorSet set, uint textureFlags)
    {
        BindPipeline(pipeline);
        if (c.Stencil)
            SetStencilRef(c.StencilRef);
        SetScissor(ScissorOf(c));
        PushTransform(c.Transform, c.Region);
        Push(new GeometryPush { Translation = c.Translation, TextureFlags = textureFlags }, 64);
        BindSet(0, set);
        BindGeometry(c);
        _vk.CmdDrawIndexed(_cb, c.IndexCount, 1, c.FirstIndex, c.VertexOffset, 0);
        _drawCalls++;
    }

    private void DrawGradient(in UiCommand c)
    {
        if (_gradientIndex >= _gradientCapacity)
            return; // capacity is ensured from the recorded count; defensive
        ref readonly var gradient = ref _gradientData[c.C];
        var offset = ((ulong)_ctx.FrameSlot * (ulong)_gradientCapacity + (ulong)_gradientIndex) * _gradientStride;
        _gradientBuffer!.Write(gradient, offset);
        _gradientIndex++;

        var dynamicOffset = (uint)offset;
        var set = _uniformSet;
        BindPipeline(_gradientPipelines[c.Stencil ? 1 : 0]);
        _vk.CmdBindDescriptorSets(_cb, PipelineBindPoint.Graphics, _layout, 2, 1, &set, 1, &dynamicOffset);
        DrawGeometry(c, _gradientPipelines[c.Stencil ? 1 : 0], _whiteSet, 0);
    }

    private void DrawClipMask(in UiCommand c)
    {
        var scissor = ScissorOf(c).Clamp(_targetExtent);
        if (c.ClipOp != RmlClipMaskOperation.Intersect && !scissor.IsEmpty)
        {
            // GL's glClear(GL_STENCIL_BUFFER_BIT) honours the scissor: clear just that region.
            var attachment = new ClearAttachment
            {
                AspectMask = ImageAspectFlags.StencilBit,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, c.ClipOp == RmlClipMaskOperation.SetInverse ? 1u : 0u) },
            };
            var rect = new ClearRect(scissor.ToRect2D(), 0, 1);
            _vk.CmdClearAttachments(_cb, 1, &attachment, 1, &rect);
            InvalidateStateAfterClear();
            SetFullViewport();
        }

        var pipeline = c.ClipOp == RmlClipMaskOperation.Intersect ? _clipIncrementPipeline : _clipReplacePipeline;
        BindPipeline(pipeline);
        SetStencilRef(c.StencilRef); // the value REPLACE writes
        SetScissor(scissor);
        PushTransform(c.Transform, c.Region);
        Push(new GeometryPush { Translation = c.Translation }, 64);
        BindSet(0, _whiteSet);
        BindGeometry(c);
        _vk.CmdDrawIndexed(_cb, c.IndexCount, 1, c.FirstIndex, c.VertexOffset, 0);
        _drawCalls++;
    }

    // ── Layers ───────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawFullscreen(Pipeline pipeline, DescriptorSet source, in PixelRect scissor, Vector2 uvOffset, Vector2 uvScale, float factor = 1f)
    {
        BindPipeline(pipeline);
        SetScissor(scissor);
        BindSet(0, source);
        Push(new FullscreenPush { UvOffset = uvOffset, UvScale = uvScale, Factor = factor });
        _pushedTransform = int.MinValue;
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
    }

    private void Composite(in UiCommand c)
    {
        EndPass();
        var window = ScissorOf(c).Clamp(_targetExtent);
        var source = (uint)c.A < (uint)_layers.Count ? _layers[c.A] : _layers[_top];
        var destination = (uint)c.B < (uint)_layers.Count ? c.B : 0;

        // Source layer → primary filter target (restricted to the scissor, like GL's blit).
        BeginPostPass(Post(_primary));
        SetFullViewport();
        DrawFullscreen(_postCopyPipeline, source.Set, window, Vector2.Zero, Vector2.One);
        EndPass();

        RenderFilters(c.C, (int)c.IndexCount, window);

        // Primary → destination layer (blend or replace, honouring the clip mask).
        BeginLayerPass(destination, _layerPassLoad);
        var pipelines = c.Blend == RmlBlendMode.Replace ? _compositeReplacePipelines : _compositeBlendPipelines;
        if (c.Stencil)
        {
            BindPipeline(pipelines[1]);
            SetStencilRef(c.StencilRef);
        }

        DrawFullscreen(pipelines[c.Stencil ? 1 : 0], Post(_primary).Set, window, Vector2.Zero, Vector2.One);
        if (destination != _top)
        {
            EndPass();
            BeginLayerPass(_top, _layerPassLoad);
        }
    }

    private void SaveTexture(in UiCommand c)
    {
        ref var slot = ref _textures[c.A];
        if (!slot.Live || slot.SavedImage is null || slot.Generation != c.IndexCount)
            return;

        EndPass();
        var bounds = ScissorOf(c).Clamp(_targetExtent);
        var extent = new Extent2D((uint)slot.Width, (uint)slot.Height);
        BeginPass(_postPassDiscard, slot.SavedFramebuffer, extent, null, 0);
        SetViewport(0, 0, slot.Width, slot.Height);
        var size = new Vector2(_targetExtent.Width, _targetExtent.Height);
        var full = new PixelRect(0, 0, slot.Width, slot.Height);
        // Map the texture's [0, 1] onto the saved region of the layer, texel for texel.
        BindPipeline(_postCopyPipeline);
        var scissor = full.ToRect2D();
        _vk.CmdSetScissor(_cb, 0, 1, &scissor);
        _boundScissor = scissor;
        BindSet(0, _layers[Math.Min(c.B, _layers.Count - 1)].Set);
        Push(new FullscreenPush
        {
            UvOffset = new Vector2(bounds.X0, bounds.Y0) / size,
            UvScale = new Vector2(slot.Width, slot.Height) / size,
            Factor = 1f,
        });
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
        EndPass();
        BeginLayerPass(_top, _layerPassLoad);
    }

    private void SaveMask(in UiCommand c)
    {
        EndPass();
        BeginPostPass(Post(BlendMask));
        SetFullViewport();
        DrawFullscreen(_postCopyPipeline, _layers[Math.Min(c.B, _layers.Count - 1)].Set, ScissorOf(c), Vector2.Zero, Vector2.One);
        EndPass();
        BeginLayerPass(_top, _layerPassLoad);
    }

    // ── Filters (ported from RmlUi's GL3 backend) ────────────────────────────────────────────────────────────

    private void SwapPrimarySecondary() => (_primary, _secondary) = (_secondary, _primary);

    private void RenderFilters(int start, int count, in PixelRect window)
    {
        for (var i = 0; i < count; i++)
        {
            ref readonly var filter = ref _filterData[start + i];
            switch (filter.Kind)
            {
                case UiFilterKind.Opacity:
                    BeginPostPass(Post(_secondary));
                    SetFullViewport();
                    DrawFullscreen(_postCopyPipeline, Post(_primary).Set, window, Vector2.Zero, Vector2.One, filter.Value);
                    EndPass();
                    SwapPrimarySecondary();
                    break;
                case UiFilterKind.ColorMatrix:
                    BeginPostPass(Post(_secondary));
                    SetFullViewport();
                    BindPipeline(_colorMatrixPipeline);
                    SetScissor(window);
                    BindSet(0, Post(_primary).Set);
                    Push(new ColorMatrixPush { UvScale = Vector2.One, Matrix = Matrix4x4.Transpose(filter.Matrix) });
                    _vk.CmdDraw(_cb, 3, 1, 0, 0);
                    _drawCalls++;
                    EndPass();
                    SwapPrimarySecondary();
                    break;
                case UiFilterKind.MaskImage:
                    BeginPostPass(Post(_secondary));
                    SetFullViewport();
                    BindPipeline(_blendMaskPipeline);
                    SetScissor(window);
                    BindSet(0, Post(_primary).Set);
                    BindSet(1, Post(BlendMask).Set);
                    Push(new FullscreenPush { UvScale = Vector2.One, Factor = 1f });
                    _vk.CmdDraw(_cb, 3, 1, 0, 0);
                    _drawCalls++;
                    EndPass();
                    SwapPrimarySecondary();
                    break;
                case UiFilterKind.Blur:
                    RenderBlur(filter.Sigma, Post(_primary), Post(_secondary), window);
                    break;
                case UiFilterKind.DropShadow:
                    DropShadow(filter, window);
                    break;
            }
        }
    }

    private void DropShadow(in UiFilter filter, in PixelRect window)
    {
        var primary = Post(_primary);
        var secondary = Post(_secondary);
        var size = new Vector2(_targetExtent.Width, _targetExtent.Height);

        BeginPostPass(secondary);
        SetFullViewport();
        BindPipeline(_dropShadowPipeline);
        SetScissor(window);
        BindSet(0, primary.Set);
        Push(new DropShadowPush
        {
            UvOffset = -filter.Offset / size,
            UvScale = Vector2.One,
            Color = filter.Color,
            TexCoordMin = (new Vector2(window.X0, window.Y0) + new Vector2(0.5f)) / size,
            TexCoordMax = (new Vector2(window.X1, window.Y1) - new Vector2(0.5f)) / size,
        });
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
        EndPass();

        if (filter.Sigma >= 0.5f)
            RenderBlur(filter.Sigma, secondary, Post(Tertiary), window);

        // The element over its shadow.
        BeginPass(_postPassLoad, secondary.Framebuffer, _targetExtent, null, 0);
        SetFullViewport();
        DrawFullscreen(_postBlendPipeline, primary.Set, window, Vector2.Zero, Vector2.One);
        EndPass();
        SwapPrimarySecondary();
    }

    /// <summary>Picks the number of half-resolution downscales and the remaining per-pass sigma (RmlUi's GL3 rule).</summary>
    internal static void SigmaToParameters(float desiredSigma, out int passLevel, out float sigma)
    {
        const int maxPasses = 10;
        const float maxSinglePassSigma = 3f;
        var scaled = (int)(desiredSigma * (2f / maxSinglePassSigma));
        passLevel = Math.Clamp(scaled > 0 ? (int)Math.Floor(Math.Log2(scaled)) : 0, 0, maxPasses);
        sigma = Math.Clamp(desiredSigma / (1 << passLevel), 0f, maxSinglePassSigma);
    }

    /// <summary>Normalised Gaussian weights for the 7-tap kernel (centre first).</summary>
    internal static Vector4 BlurWeights(float sigma)
    {
        Span<float> w = stackalloc float[4];
        var normalization = 0f;
        for (var i = 0; i < 4; i++)
        {
            w[i] = MathF.Abs(sigma) < 0.1f
                ? (i == 0 ? 1f : 0f)
                : MathF.Exp(-(i * i) / (2f * sigma * sigma)) / (MathF.Sqrt(2f * MathF.PI) * sigma);
            normalization += (i == 0 ? 1f : 2f) * w[i];
        }

        return new Vector4(w[0], w[1], w[2], w[3]) / normalization;
    }

    private void RenderBlur(float desiredSigma, Target sourceDestination, Target temp, in PixelRect window)
    {
        SigmaToParameters(desiredSigma, out var passLevel, out var sigma);
        var w = (int)_targetExtent.Width;
        var h = (int)_targetExtent.Height;
        var size = new Vector2(w, h);

        // Downscale by repeated halving with bilinear filtering (into the top-left quadrant of the other target).
        var uvScaling = new Vector2(w % 2 == 1 ? 1f - 1f / w : 1f, h % 2 == 1 ? 1f - 1f / h : 1f);
        var scissor = window;
        for (var i = 0; i < passLevel; i++)
        {
            var x0 = (scissor.X0 + 1) / 2;
            var y0 = (scissor.Y0 + 1) / 2;
            scissor = new PixelRect(x0, y0, Math.Max(scissor.X1 / 2, x0), Math.Max(scissor.Y1 / 2, y0));
            var fromSource = i % 2 == 0;
            BeginPostPass(fromSource ? temp : sourceDestination);
            SetViewport(0, 0, w / 2, h / 2);
            DrawFullscreen(_postCopyPipeline, (fromSource ? sourceDestination : temp).Set, scissor, Vector2.Zero, uvScaling);
            EndPass();
        }

        // The downscaled image must end up in temp.
        if (passLevel % 2 == 0)
        {
            BeginPostPass(temp);
            SetFullViewport();
            DrawFullscreen(_postCopyPipeline, sourceDestination.Set, scissor, Vector2.Zero, Vector2.One);
            EndPass();
        }

        var weights = BlurWeights(sigma);
        var texMin = (new Vector2(scissor.X0, scissor.Y0) + new Vector2(0.5f)) / size;
        var texMax = (new Vector2(scissor.X1, scissor.Y1) - new Vector2(0.5f)) / size;

        // Vertical: temp → source/destination.
        BeginPostPass(sourceDestination);
        SetFullViewport();
        BindPipeline(_blurPipeline);
        SetScissor(scissor);
        BindSet(0, temp.Set);
        Push(new BlurPush
        {
            UvScale = Vector2.One,
            TexelOffset = new Vector2(0f, 1f / h),
            TexCoordMin = texMin,
            TexCoordMax = texMax,
            Weights = weights,
        });
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
        EndPass();

        // Horizontal: source/destination → temp, with a 1 px transparent border so the upscale does not bleed.
        BeginPostPass(temp);
        SetFullViewport();
        var border = scissor.Extend(1).Clamp(_targetExtent);
        if (!border.IsEmpty)
        {
            var attachment = new ClearAttachment { AspectMask = ImageAspectFlags.ColorBit, ColorAttachment = 0 };
            var rect = new ClearRect(border.ToRect2D(), 0, 1);
            _vk.CmdClearAttachments(_cb, 1, &attachment, 1, &rect);
            InvalidateStateAfterClear();
            SetFullViewport();
        }

        BindPipeline(_blurPipeline);
        SetScissor(scissor);
        BindSet(0, sourceDestination.Set);
        Push(new BlurPush
        {
            UvScale = Vector2.One,
            TexelOffset = new Vector2(1f / w, 0f),
            TexCoordMin = texMin,
            TexCoordMax = texMax,
            Weights = weights,
        });
        _vk.CmdDraw(_cb, 3, 1, 0, 0);
        _drawCalls++;
        EndPass();

        // Upscale the blurred region back over the window (linear filtering), then again at an exact power-of-two
        // scale for stable positioning (RmlUi does both; the second may not cover the edges).
        BeginPostPass(sourceDestination);
        var srcOffset = new Vector2(scissor.X0, scissor.Y0) / size;
        var srcScale = new Vector2(scissor.Width, scissor.Height) / size;
        SetViewport(window.X0, window.Y0, window.Width, window.Height);
        DrawFullscreen(_postCopyPipeline, temp.Set, window, srcOffset, srcScale);
        var factor = 1 << passLevel;
        var target = new PixelRect(scissor.X0 * factor, scissor.Y0 * factor, scissor.X1 * factor, scissor.Y1 * factor);
        if (target != window && !target.IsEmpty)
        {
            SetViewport(target.X0, target.Y0, target.Width, target.Height);
            DrawFullscreen(_postCopyPipeline, temp.Set, window, srcOffset, srcScale);
        }

        EndPass();
    }
}
