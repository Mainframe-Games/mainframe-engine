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
/// <see cref="IVulkanContext.Allocator"/>) keyed by <see cref="IVulkanContext.FrameSlot"/>; a slot's buffer grows
/// (doubling) when a frame has more lines than it holds, the old one going to the deletion queue. One buffer per frame
/// slot: <see cref="Draw"/> may be called once per frame (the engine renders one viewport). Several viewports per
/// frame (editor views) need per-call ring offsets.
/// </remarks>
internal sealed class DebugLinesRenderer : IDisposable
{
    private const ulong InitialVertexCapacity = 4096;
    private const string VertexShader = "Shaders/Debug/DebugLines.vk.vert.spv";
    private const string FragmentShader = "Shaders/Debug/DebugLines.vk.frag.spv";

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
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

    /// <summary>Records the batch into the current command buffer (inside the HDR scene pass).</summary>
    public unsafe void Draw(DebugLines lines, ICamera camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(camera);
        var vertices = lines.Vertices;
        if (vertices.Length == 0 || !_ctx.FrameStarted)
            return;

        var buffer = EnsureVertexBuffer(_ctx.FrameSlot, (ulong)vertices.Length * (ulong)sizeof(DebugLineVertex));
        buffer.Write(vertices);

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        // RenderServer.RenderMain already wrote this frame's camera; a no-op then.
        _ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        // Y-flipped viewport like every main-pass drawable (docs/design/coordinate-conventions.md).
        PipelineBuilder.SetViewport(vk, cb, _ctx.SwapchainExtent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var vb = buffer.Handle;
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        _ctx.Frame.Bind(cb, _pipelineLayout);
        vk.CmdDraw(cb, (uint)vertices.Length, 1, 0, 0);
    }

    // The slot's previous frame has completed once FrameStarted, so overwriting (or replacing) its buffer is safe.
    private unsafe GpuBuffer EnsureVertexBuffer(int slot, ulong required)
    {
        var buffer = _vertexBuffers[slot];
        if (buffer is not null && required <= buffer.Size)
            return buffer;

        var capacity = buffer?.Size ?? InitialVertexCapacity * (ulong)sizeof(DebugLineVertex);
        while (capacity < required)
            capacity *= 2;
        buffer?.Dispose();
        _vertexBuffers[slot] = null;
        buffer = GpuBuffer.Create(_ctx, capacity, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        _vertexBuffers[slot] = buffer;
        return buffer;
    }

    private unsafe void CreatePipeline()
    {
        _pipelineLayout = _ctx.Frame.CreatePipelineLayout(null, [], "debug lines");

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
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = CompareOp.LessOrEqual,
            Blend = BlendMode.AlphaKeepSourceAlpha,
        };
        _pipeline = PipelineBuilder.Create(_ctx, state, _pipelineLayout, _ctx.RenderPass, VertexShader, FragmentShader,
            bindings, attributes, "debug lines");
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
        if (_pipelineLayout.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
    }
}
