using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Draws a <see cref="ScreenGizmoBatch"/> in the overlay pass (after the tonemap, between the 2D canvas and the UI) and
/// clears it. Colours are sRGB-authored and alpha-blended in sRGB space like the UI; the fragment shader linearises them
/// only when the overlay target encodes sRGB on store (<see cref="IVulkanContext.OverlayEncodesSrgb"/>).
/// </summary>
/// <remarks>
/// One draw per frame into a <see cref="GpuMemoryUsage.Dynamic"/> vertex buffer keyed by
/// <see cref="IVulkanContext.FrameSlot"/>, so the buffer being rewritten is never one a frame in flight still reads;
/// when the batch outgrows the slot's buffer the buffer is replaced (the old one goes to the deletion queue).
/// </remarks>
internal sealed unsafe class ScreenGizmosRenderer : IOverlayRenderer, IDisposable
{
    private const ulong MinimumBufferBytes = 64 * 1024;
    private const string VertexShader = "Shaders/Gizmos/ScreenGizmo.vk.vert.spv";
    private const string FragmentShader = "Shaders/Gizmos/ScreenGizmo.vk.frag.spv";

    private readonly IVulkanContext _ctx;
    private readonly ScreenGizmoBatch _batch;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private bool _disposed;

    // Pixel (x, y) -> clip space (x * Scale.X + Translate.X, ...): the overlay viewport is not flipped, so
    // (0, 0) is the top-left corner and +Y points down, matching the batch's convention.
    private struct Push
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    public ScreenGizmosRenderer(IVulkanContext ctx, ScreenGizmoBatch batch)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
        try
        {
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }

        ctx.AddOverlayRenderer(this, OverlayOrder.Gizmos);
    }

    public void RecordOffscreen(CommandBuffer commandBuffer)
    {
    }

    public void RecordOverlay(CommandBuffer commandBuffer)
    {
        try
        {
            var vertices = _batch.Vertices;
            if (vertices.Length == 0)
                return;

            var slot = _ctx.FrameSlot;
            var size = (ulong)vertices.Length * (ulong)sizeof(ScreenGizmoVertex);
            var buffer = _buffers[slot];
            if (buffer is null || buffer.Size < size)
            {
                buffer?.Dispose();
                _buffers[slot] = null;
                buffer = _buffers[slot] = GpuBuffer.Create(_ctx, Math.Max(size * 2, MinimumBufferBytes),
                    BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
            }

            buffer.Write(vertices);

            var vk = _ctx.Vk;
            var extent = _ctx.SwapchainExtent;
            PipelineBuilder.SetViewport(vk, commandBuffer, extent, flipY: false);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
            var handle = buffer.Handle;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &handle, &offset);
            var push = new Push
            {
                Scale = new Vector2(2f / extent.Width, 2f / extent.Height),
                Translate = new Vector2(-1f, -1f),
            };
            vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Push), &push);
            vk.CmdDraw(commandBuffer, (uint)vertices.Length, 1, 0, 0);
        }
        finally
        {
            _batch.Clear();
        }
    }

    private void CreatePipeline()
    {
        _layout = PipelineBuilder.CreateLayout(_ctx, [], (uint)sizeof(Push), ShaderStageFlags.VertexBit, "screen gizmos");
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(ScreenGizmoVertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 8 },
        ];

        // Specialization constant 0: linearise the sRGB-authored colours when the overlay target encodes sRGB.
        var linearize = _ctx.OverlayEncodesSrgb ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &linearize };
        _pipeline = PipelineBuilder.Create(_ctx, new PipelineState { Blend = BlendMode.Alpha }, _layout, _ctx.OverlayRenderPass,
            VertexShader, FragmentShader, bindings, attributes, "screen gizmos", &specialization);
    }

    /// <summary>Releases the GPU objects through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ctx.RemoveOverlayRenderer(this);
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }

        if (_pipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        if (_layout.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
    }
}
