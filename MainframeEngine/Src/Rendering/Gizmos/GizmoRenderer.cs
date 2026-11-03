using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Draws a <see cref="GizmoDrawList"/> (<c>Shaders/Gizmos/Gizmo</c>): one triangle list per list, lines expanded to
/// pixel-wide quads in the vertex shader, the list's <see cref="GizmoIconAtlas"/> at set 1 (a white texel without one),
/// the per-frame shared set 0 for the camera; depth-tested (not written) or on top, alpha-blended. Like
/// <see cref="DebugLinesRenderer"/>: into the HDR scene pass, or after post into a post-processed view's overlay pass.
/// </summary>
/// <remarks>
/// Vertex buffers are <see cref="GpuMemoryUsage.Dynamic"/> <see cref="GpuBuffer"/>s keyed by
/// <see cref="IVulkanContext.FrameSlot"/>; every draw in a frame appends at the slot's next free offset, and a draw that
/// does not fit replaces the slot's buffer with one twice as large (the old one goes to the deletion queue). An atlas is
/// uploaded the first time it is drawn and again after it changes.
/// </remarks>
internal sealed class GizmoRenderer : IDisposable
{
    private const ulong InitialVertexCapacity = 8192;
    private const string VertexShader = "Shaders/Gizmos/Gizmo.vk.vert.spv";
    private const string FragmentShader = "Shaders/Gizmos/Gizmo.vk.frag.spv";

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private readonly ulong[] _usedBytes = new ulong[IVulkanContext.MaxFramesInFlight];
    private readonly ulong[] _usedFrame = new ulong[IVulkanContext.MaxFramesInFlight];
    private DescriptorSetLayout _atlasLayout;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private Pipeline _overlayPipeline;
    private RenderPass _afterPostPass;
    private Pipeline _afterPostPipeline;
    private Pipeline _afterPostOverlayPipeline;
    private AtlasGpu? _white;
    private bool _disposed;

    public GizmoRenderer(IVulkanContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        try
        {
            _atlasLayout = PipelineBuilder.CreateSetLayout(ctx,
                [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
                "gizmo atlas");
            _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [_atlasLayout], "gizmos");
            _pipeline = CreatePipeline(ctx.RenderPass, depthTest: true, displayOutput: false, "gizmos");
            _overlayPipeline = CreatePipeline(ctx.RenderPass, depthTest: false, displayOutput: false, "gizmos (overlay)");
            _white = new AtlasGpu(ctx, _atlasLayout, [255, 255, 255, 255], 1, 1);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Records the list into the HDR scene pass; <paramref name="overlay"/> draws it without depth testing.</summary>
    public void Draw(GizmoDrawList list, ICamera camera, bool overlay) =>
        Draw(list, camera, overlay ? _overlayPipeline : _pipeline);

    /// <summary>Records the list into a post-processed sub-viewport's overlay pass (ADR 0169), after its effects.</summary>
    public void DrawAfterPost(GizmoDrawList list, ICamera camera, RenderPass renderPass, bool overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_afterPostPass.Handle != renderPass.Handle)
        {
            if (_afterPostPipeline.Handle != 0)
            {
                _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
                _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostOverlayPipeline));
            }

            _afterPostPipeline = CreatePipeline(renderPass, depthTest: true, displayOutput: true, "gizmos (after post)");
            _afterPostOverlayPipeline = CreatePipeline(renderPass, depthTest: false, displayOutput: true, "gizmos (overlay, after post)");
            _afterPostPass = renderPass;
        }

        Draw(list, camera, overlay ? _afterPostOverlayPipeline : _afterPostPipeline);
    }

    private unsafe void Draw(GizmoDrawList list, ICamera camera, Pipeline pipeline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);
        var vertices = list.Vertices;
        if (vertices.Length == 0 || !_ctx.FrameStarted)
            return;

        var slot = _ctx.FrameSlot;
        if (_usedFrame[slot] != _ctx.FrameNumber)
        {
            _usedFrame[slot] = _ctx.FrameNumber;
            _usedBytes[slot] = 0;
        }

        var size = (ulong)vertices.Length * (ulong)sizeof(GizmoVertex);
        var buffer = EnsureVertexBuffer(slot, size);
        var offset = _usedBytes[slot];
        buffer.Write(vertices, offset);
        _usedBytes[slot] = offset + size;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        _ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        PipelineBuilder.SetViewport(vk, cb, _ctx.Frame.Extent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        var vb = buffer.Handle;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        _ctx.Frame.Bind(cb, _pipelineLayout);
        var set = AtlasSet(list.Atlas);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 1, 1, &set, 0, null);
        vk.CmdDraw(cb, (uint)vertices.Length, 1, 0, 0);
    }

    // The atlas's descriptor set, uploading it when it is new or changed (load time: the old copy is deletion-queued).
    private DescriptorSet AtlasSet(GizmoIconAtlas? atlas)
    {
        if (atlas is null)
            return _white!.Set;
        if (atlas.Gpu is not AtlasGpu gpu || atlas.GpuVersion != atlas.Version)
        {
            atlas.Gpu?.Dispose();
            atlas.Gpu = gpu = new AtlasGpu(_ctx, _atlasLayout, atlas.Pixels, atlas.Width, atlas.Height);
            atlas.GpuVersion = atlas.Version;
        }

        return gpu.Set;
    }

    // The slot's previous frame has completed once FrameStarted, so overwriting its buffer is safe; a buffer replaced
    // mid-frame still backs this frame's earlier draws until the deletion queue releases it.
    private unsafe GpuBuffer EnsureVertexBuffer(int slot, ulong size)
    {
        var buffer = _vertexBuffers[slot];
        if (buffer is not null && _usedBytes[slot] + size <= buffer.Size)
            return buffer;

        var capacity = buffer?.Size ?? InitialVertexCapacity * (ulong)sizeof(GizmoVertex);
        while (capacity < size || (buffer is not null && capacity <= buffer.Size))
            capacity *= 2;
        buffer?.Dispose();
        _vertexBuffers[slot] = null;
        buffer = GpuBuffer.Create(_ctx, capacity, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        _vertexBuffers[slot] = buffer;
        _usedBytes[slot] = 0;
        return buffer;
    }

    // displayOutput: a post-processed view's overlay pass (display values).
    private unsafe Pipeline CreatePipeline(RenderPass renderPass, bool depthTest, bool displayOutput, string what)
    {
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(GizmoVertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
            new() { Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 24 },
            new() { Location = 3, Binding = 0, Format = Format.R32G32Sfloat, Offset = 40 },
            new() { Location = 4, Binding = 0, Format = Format.R32G32Sfloat, Offset = 48 },
        ];

        var state = new PipelineState
        {
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

        _white?.Dispose();
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
        if (_atlasLayout.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_atlasLayout));
    }

    /// <summary>An atlas texture (sRGB, linear clamp) and its descriptor set from a pool of its own.</summary>
    private sealed class AtlasGpu : IDisposable
    {
        private readonly IVulkanContext _ctx;
        private readonly GpuTexture _texture;
        private readonly DescriptorPool _pool;
        private bool _disposed;

        public AtlasGpu(IVulkanContext ctx, DescriptorSetLayout layout, ReadOnlySpan<byte> rgba, int width, int height)
        {
            _ctx = ctx;
            _texture = GpuTexture.Create2D(ctx, (uint)width, (uint)height, rgba, TextureColorSpace.Srgb, TextureSampling.LinearClamp);
            _pool = PipelineBuilder.CreatePool(ctx, 1, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "gizmo atlas");
            Set = PipelineBuilder.AllocateSet(ctx, _pool, layout, "gizmo atlas");
            PipelineBuilder.WriteImage(ctx, Set, 0, _texture.Descriptor);
        }

        public DescriptorSet Set { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _texture.Dispose();
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pool));
        }
    }
}
