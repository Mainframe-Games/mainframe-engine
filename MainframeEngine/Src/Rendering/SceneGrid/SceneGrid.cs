using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Base class for making scene grid: a static line list (device-local vertex buffer) drawn with the camera from
/// the per-frame shared set 0 (<see cref="FrameContext"/>). Line colours are sRGB-authored with alpha; lines fade
/// with distance.
/// </summary>
/// <remarks>
/// Each vertex also carries the other end of its line, and the vertex shader clips the line to the view volume
/// (plus a small guard band) itself. Grid lines pass beside and behind the camera; where they cross the near plane
/// they project ~10⁵ pixels off-screen, and lavapipe rasterizes such lines with stray fragments across the frame.
/// Clipping first means the rasterizer only ever sees endpoints close to the viewport, on every driver.
/// </remarks>
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
    private RenderPass _afterPostPass; // the post-processed view's overlay pass _afterPostPipeline was built for
    private Pipeline _afterPostPipeline;
    private bool _disposed;

    protected struct Vertex(float x, float y, float z, Vector4 color)
    {
        public Vector3 Position = new(x, y, z);
        public Vector4 Color = color;

        /// <summary>The other end of this vertex's line (filled in by <see cref="BuildVertexArray"/>).</summary>
        public Vector3 Other;
    }

    protected SceneGrid(IRenderer renderer, uint vertexCount)
    {
        _vertexCount = vertexCount;

        if (renderer is IVulkanContext vkCtx)
            _vkCtx = vkCtx;
    }

    public void Draw(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_vkCtx is { FrameStarted: true } && _vertexBuffer is not null)
            Draw(camera, _pipeline);
    }

    /// <summary>
    /// Draws into a post-processed sub-viewport's overlay pass (ADR 0169): the display-encoded view image with the scene
    /// depth read-only (no depth writes), colours written as display values.
    /// </summary>
    internal void DrawAfterPost(ICamera camera, RenderPass renderPass)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_vkCtx is not { FrameStarted: true } ctx || _vertexBuffer is null)
            return;
        if (_afterPostPass.Handle != renderPass.Handle)
        {
            if (_afterPostPipeline.Handle != 0)
                ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
            _afterPostPipeline = CreatePipeline(ctx, renderPass, displayOutput: true);
            _afterPostPass = renderPass;
        }

        Draw(camera, _afterPostPipeline);
    }

    private unsafe void Draw(ICamera camera, Pipeline pipeline)
    {
        var ctx = _vkCtx!;
        var vk = ctx.Vk;
        var cb = ctx.CurrentCommandBuffer;
        ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);

        // Negative height flips Vulkan's Y axis to match right-handed convention (Y+ up)
        PipelineBuilder.SetViewport(vk, cb, ctx.Frame.Extent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        var vb = _vertexBuffer!.Handle;
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
        if (_afterPostPipeline.Handle != 0)
            _vkCtx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
        _vkCtx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
    }

    /// <summary>Uploads the line list: <c>vertexCount</c> vertices, each consecutive pair one line.</summary>
    protected void BuildVertexArray(Span<Vertex> vertices)
    {
        if (vertices.Length != _vertexCount || (_vertexCount & 1) != 0)
            throw new ArgumentException($"Expected {_vertexCount} vertices (an even count), got {vertices.Length}.", nameof(vertices));
        if (_vkCtx is null)
            return;

        for (var i = 0; i < vertices.Length; i += 2)
        {
            vertices[i].Other = vertices[i + 1].Position;
            vertices[i + 1].Other = vertices[i].Position;
        }

        _vertexBuffer = GpuBuffer.CreateStatic(_vkCtx, (ReadOnlySpan<Vertex>)vertices, BufferUsageFlags.VertexBufferBit);
        _pipelineLayout = _vkCtx.Frame.CreatePipelineLayout(null, [], "scene grid");
        _pipeline = CreatePipeline(_vkCtx, _vkCtx.RenderPass, displayOutput: false);
    }

    // displayOutput: the post-processed view's overlay pass (no depth writes into the read-only scene depth, display values,
    // and destination alpha kept opaque for the UI that shows the view).
    private unsafe Pipeline CreatePipeline(IVulkanContext ctx, RenderPass renderPass, bool displayOutput)
    {
        // Vertex layout: Vertex { Vector3 Position, Vector4 Color, Vector3 Other } = 40 bytes
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(Vertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 12 },
            new() { Location = 2, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 28 },
        ];

        var state = new PipelineState
        {
            Topology = PrimitiveTopology.LineList,
            DepthTest = true,
            DepthWrite = !displayOutput,
            Blend = displayOutput ? BlendMode.Alpha : BlendMode.AlphaKeepSourceAlpha,
        };
        var display = displayOutput ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &display };
        return PipelineBuilder.Create(ctx, state, _pipelineLayout, renderPass,
            "Shaders/SceneGrid/SceneGrid.vk.vert.spv", "Shaders/SceneGrid/SceneGrid.vk.frag.spv",
            bindings, attributes, displayOutput ? "scene grid (after post)" : "scene grid", &specialization);
    }
}
