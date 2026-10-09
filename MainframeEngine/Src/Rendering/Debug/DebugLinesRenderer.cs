using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Draws a <see cref="DebugLines"/> batch in the HDR scene pass (<see cref="IVulkanContext.RenderPass"/>): a line-list
/// pipeline (<c>Shaders/Debug/DebugLines</c>) using the per-frame shared set 0 (<see cref="FrameContext"/>) for the
/// camera, depth-tested but not depth-writing, alpha-blended. Line colours are sRGB-authored; the fragment shader
/// converts them to linear like every scene colour (docs/design/color-pipeline.md).
/// </summary>
/// <remarks>
/// Vertex buffers are <see cref="GpuMemoryUsage.Dynamic"/> <see cref="GpuBuffer"/>s (persistently mapped, from
/// <see cref="IVulkanContext.Allocator"/>) keyed by <see cref="IVulkanContext.FrameSlot"/>. Every <see cref="Draw"/>
/// in a frame (the main view and each <see cref="SubViewport"/>) appends at the slot's next free offset; when a draw
/// does not fit, the slot gets a bigger buffer (doubling) and the old one goes to the deletion queue, which keeps it
/// alive until the frame's earlier draws have executed.
/// </remarks>
internal sealed class DebugLinesRenderer : IDisposable
{
    private const ulong InitialVertexCapacity = 4096;
    private const string VertexShader = "Shaders/Debug/DebugLines.vk.vert.spv";
    private const string FragmentShader = "Shaders/Debug/DebugLines.vk.frag.spv";

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private readonly ulong[] _usedBytes = new ulong[IVulkanContext.MaxFramesInFlight];
    private readonly ulong[] _usedFrame = new ulong[IVulkanContext.MaxFramesInFlight];
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private Pipeline _overlayPipeline; // no depth test: SceneViewport.OverlayLines (gizmos)
    private RenderPass _afterPostPass;  // ADR 0169: a post-processed view's overlay pass, which the two below draw into
    private Pipeline _afterPostPipeline;
    private Pipeline _afterPostOverlayPipeline;
    private bool _disposed;

    public DebugLinesRenderer(IVulkanContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        try
        {
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records the batch into the current command buffer (inside the HDR scene pass); <paramref name="overlay"/> draws
    /// it without depth testing (on top of everything drawn before).
    /// </summary>
    public void Draw(DebugLines lines, ICamera camera, bool overlay = false) =>
        Draw(lines, camera, overlay ? _overlayPipeline : _pipeline);

    /// <summary>
    /// Records the batch into a post-processed sub-viewport's overlay pass (ADR 0169, <paramref name="renderPass"/>: the
    /// display-encoded view image and the scene depth, read-only), after its post effects: colours as authored (display
    /// values), depth-tested unless <paramref name="overlay"/>.
    /// </summary>
    internal void DrawAfterPost(DebugLines lines, ICamera camera, RenderPass renderPass, bool overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_afterPostPass.Handle != renderPass.Handle)
        {
            if (_afterPostPipeline.Handle != 0)
            {
                _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
                _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostOverlayPipeline));
            }

            _afterPostPipeline = CreatePipeline(renderPass, depthTest: true, displayOutput: true, "debug lines (after post)");
            _afterPostOverlayPipeline = CreatePipeline(renderPass, depthTest: false, displayOutput: true, "debug lines (overlay, after post)");
            _afterPostPass = renderPass;
        }

        Draw(lines, camera, overlay ? _afterPostOverlayPipeline : _afterPostPipeline);
    }

    private unsafe void Draw(DebugLines lines, ICamera camera, Pipeline pipeline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(camera);
        var vertices = lines.Vertices;
        if (vertices.Length == 0 || !_ctx.FrameStarted)
            return;

        var slot = _ctx.FrameSlot;
        if (_usedFrame[slot] != _ctx.FrameNumber)
        {
            _usedFrame[slot] = _ctx.FrameNumber;
            _usedBytes[slot] = 0;
        }

        var size = (ulong)vertices.Length * (ulong)sizeof(DebugLineVertex);
        var buffer = EnsureVertexBuffer(slot, size);
        var offset = _usedBytes[slot];
        buffer.Write(vertices, offset);
        _usedBytes[slot] = offset + size;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        // RenderServer.RenderMain already wrote this frame's camera; a no-op then.
        _ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        // Y-flipped viewport like every main-pass drawable (docs/design/coordinate-conventions.md).
        PipelineBuilder.SetViewport(vk, cb, _ctx.Frame.Extent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        var vb = buffer.Handle;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        _ctx.Frame.Bind(cb, _pipelineLayout);
        vk.CmdDraw(cb, (uint)vertices.Length, 1, 0, 0);
    }

    // The slot's previous frame has completed once FrameStarted, so overwriting its buffer is safe; a buffer replaced
    // mid-frame still backs this frame's earlier draws until the deletion queue releases it.
    private unsafe GpuBuffer EnsureVertexBuffer(int slot, ulong size)
    {
        var buffer = _vertexBuffers[slot];
        if (buffer is not null && _usedBytes[slot] + size <= buffer.Size)
            return buffer;

        var capacity = buffer?.Size ?? InitialVertexCapacity * (ulong)sizeof(DebugLineVertex);
        while (capacity < size || (buffer is not null && capacity <= buffer.Size))
            capacity *= 2;
        buffer?.Dispose();
        _vertexBuffers[slot] = null;
        buffer = GpuBuffer.Create(_ctx, capacity, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        _vertexBuffers[slot] = buffer;
        _usedBytes[slot] = 0;
        return buffer;
    }

    private void CreatePipeline()
    {
        _pipelineLayout = _ctx.Frame.CreatePipelineLayout(null, [], "debug lines");
        _pipeline = CreatePipeline(_ctx.RenderPass, depthTest: true, displayOutput: false, "debug lines");
        _overlayPipeline = CreatePipeline(_ctx.RenderPass, depthTest: false, displayOutput: false, "debug lines (overlay)");
    }

    // displayOutput: a post-processed view's overlay pass (display values; destination alpha kept opaque for the UI).
    private unsafe Pipeline CreatePipeline(RenderPass renderPass, bool depthTest, bool displayOutput, string what)
    {

        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(DebugLineVertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 12 },
        ];

        // Depth-tested (hidden lines stay hidden) but not written, so lines never occlude what draws after them.
        var state = new PipelineState
        {
            Topology = PrimitiveTopology.LineList,
            DepthTest = depthTest,
            DepthWrite = false,
            DepthCompare = CompareOp.LessOrEqual,
            Blend = displayOutput ? BlendMode.Alpha : BlendMode.AlphaKeepSourceAlpha,
        };
        var display = displayOutput ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &display };
        return PipelineBuilder.Create(_ctx, state, _pipelineLayout, renderPass, VertexShader, FragmentShader,
            bindings, attributes, what, &specialization);
    }

    /// <summary>Releases the GPU objects through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = 0; i < _vertexBuffers.Length; i++)
        {
            _vertexBuffers[i]?.Dispose();
            _vertexBuffers[i] = null;
        }

        if (_pipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        if (_overlayPipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_overlayPipeline));
        if (_afterPostPipeline.Handle != 0)
        {
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostOverlayPipeline));
        }
        if (_pipelineLayout.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
    }
}
