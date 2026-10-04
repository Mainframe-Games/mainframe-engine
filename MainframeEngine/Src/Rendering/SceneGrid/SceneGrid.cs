using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Base class for making scene grid: a static line list (device-local vertex buffer) drawn with the camera from
/// the per-frame shared set 0 (<see cref="FrameContext"/>). Line colours are sRGB-authored with alpha; lines fade
/// with distance.
/// </summary>
public abstract class SceneGrid : IDisposable
{
    protected static readonly Vector4 DefaultColor = new(1f, 1f, 1f, 0.1f);
    protected static readonly Vector4 Red = new(1f, 0f, 0f, 1f);
    protected static readonly Vector4 Yellow = new(1f, 1f, 0f, 1f);
    protected static readonly Vector4 Blue = new(0f, 0f, 1f, 1f);

    private protected readonly uint _vertexCount;

    // Vulkan
    private readonly IVulkanContext? _vkCtx;
    private GpuBuffer? _vertexBuffer;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private bool _disposed;

    protected struct Vertex(float x, float y, float z, Vector4 color)
    {
        public Vector3 Position = new(x, y, z);
        public Vector4 Color = color;
    }

    protected SceneGrid(IRenderer renderer, uint vertexCount)
    {
        _vertexCount = vertexCount;

        if (renderer is IVulkanContext vkCtx)
            _vkCtx = vkCtx;
    }

    public unsafe void Draw(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_vkCtx is not { FrameStarted: true } ctx || _vertexBuffer is null)
            return;

        var vk = ctx.Vk;
        var cb = ctx.CurrentCommandBuffer;
        ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);

        // Negative height flips Vulkan's Y axis to match right-handed convention (Y+ up)
        PipelineBuilder.SetViewport(vk, cb, ctx.SwapchainExtent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var vb = _vertexBuffer.Handle;
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        ctx.Frame.Bind(cb, _pipelineLayout);
        vk.CmdDraw(cb, _vertexCount, 1, 0, 0);
    }

    /// <summary>Releases the GPU objects through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_vkCtx is null || _disposed) return;
        _disposed = true;
        _vertexBuffer?.Dispose();
        _vkCtx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        _vkCtx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
    }

    protected unsafe void BuildVertexArray(Vertex* vertices)
    {
        if (_vkCtx is null)
            return;

        _vertexBuffer = GpuBuffer.CreateStatic(_vkCtx, new ReadOnlySpan<Vertex>(vertices, (int)_vertexCount),
            BufferUsageFlags.VertexBufferBit);
        CreatePipeline(_vkCtx);
    }

    private unsafe void CreatePipeline(IVulkanContext ctx)
    {
        _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [], "scene grid");

        // Vertex layout: Vertex { Vector3 Position, Vector4 Color } = 28 bytes
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(Vertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 12 },
        ];

        var state = new PipelineState
        {
            Topology = PrimitiveTopology.LineList,
            DepthTest = true,
            DepthWrite = true,
            Blend = BlendMode.AlphaKeepSourceAlpha,
        };
        _pipeline = PipelineBuilder.Create(ctx, state, _pipelineLayout, ctx.RenderPass,
            "Shaders/SceneGrid/SceneGrid.vk.vert.spv", "Shaders/SceneGrid/SceneGrid.vk.frag.spv",
            bindings, attributes, "scene grid");
    }
}
