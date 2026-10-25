using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Spine;
using Skeleton = Spine.Skeleton;

namespace MainframeEngine;

/// <summary>
/// Draws one Spine skeleton: CPU-built vertices (<see cref="BuildVertices"/>) streamed once per frame into the frame
/// slot's host-visible buffer, lit by the shared per-frame set 0 (camera + lights) and shadowed by set 1
/// (<see cref="ShadowSystem"/> or the renderer's fallback); the atlas page is set 2 and the model matrix a push
/// constant. The shadow casters draw the same buffer, alpha-tested against the atlas page (ADR 0204).
/// </summary>
internal sealed class SpineRenderer : IDisposable, ISpineGeometrySink
{
    /// <summary>Initial CPU/GPU vertex capacity; arrays and buffers grow (doubling) when a pose needs more.</summary>
    internal const int DefaultVertexCapacity = 8192;

    /// <summary>Bytes per vertex (<see cref="Vertex"/>): the colour pass and the shadow casters read the same buffer.</summary>
    internal const uint VertexStride = 40;

    private float[] _worldVerticesPositions;
    private Vertex[] _vertices;
    private int _preparedVertexCount;
    private int _uploadedSlots; // bit per frame slot: vertices uploaded since the last BuildVertices
    private float _shadowAlphaThreshold;

    private const uint TexSetIndex = 2; // set 0 frame, set 1 shadows, set 2 atlas page
    private const uint ShadowTexSetIndex = 1; // shadow casters: set 0 light VP, set 1 atlas page

    private readonly IVulkanContext? _vkCtx;
    private GpuTexture[] _textures = [];
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private DescriptorSetLayout _texLayout;
    private DescriptorPool _descPool;
    private DescriptorSet[] _texDescSets = []; // [texIdx]: static, shared by every frame slot
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private IShadowDescriptors? _shadowDescriptors;
    private bool _disposed;

    // Per-frame draw batches: (texIdx, vertexStart)
    private readonly List<(int texIdx, int start)> _vkBatches = new();

    // The model matrix of the last BuildVertices, before billboarding (SpineNode turns it per view).
    private Matrix4x4 _modelMatrix;

    private readonly ShadowSystem? _shadowSystem;
    private readonly bool _pma;
    private readonly Skeleton _skeleton;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;     // offset  0
        public Vector2 Uv;           // offset 12
        public Vector4 Color;        // offset 20 (the shadow casters read Color.W with the threshold, offset 32)
        public float ShadowAlphaThreshold; // offset 36
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstant
    {
        public Matrix4x4 Model;       // 64 bytes
        public Vector4   WorldNormal; // 16 bytes
    }

    public SpineRenderer(IRenderer renderer, Skeleton skeleton, bool pma, SpineTextureLoader textureLoader,
        ShadowSystem? shadowSystem = null, int initialVertexCapacity = DefaultVertexCapacity)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(textureLoader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialVertexCapacity);
        _skeleton = skeleton;
        _pma = pma;
        _shadowSystem = shadowSystem;
        _worldVerticesPositions = new float[Math.Max(8, initialVertexCapacity)]; // a region needs 8 floats
        _vertices = new Vertex[initialVertexCapacity];

        if (renderer is IVulkanContext vkCtx)
        {
            _vkCtx = vkCtx;
            CreateVkResources(vkCtx, textureLoader.VkImageData);
            shadowSystem?.EnsureSpineCasters();
        }

        // The pixels live on the GPU now (or are not needed); keep only the dimensions.
        textureLoader.ReleasePixelData();
    }

    /// <summary>Vertices built by the last <see cref="BuildVertices"/>.</summary>
    internal int PreparedVertexCount => _preparedVertexCount;

    /// <summary>Vertex <paramref name="index"/> of the last <see cref="BuildVertices"/>, in world space (model matrix applied).</summary>
    internal Vector3 PreparedWorldPosition(int index) => Vector3.Transform(_vertices[index].Position, _modelMatrix);

    /// <summary>The tint alpha (skeleton × slot) of prepared vertex <paramref name="index"/>; the shadow test multiplies it in.</summary>
    internal float PreparedAlpha(int index) => _vertices[index].Color.W;

    /// <summary>The shadow alpha threshold carried by prepared vertex <paramref name="index"/>.</summary>
    internal float PreparedShadowAlphaThreshold(int index) => _vertices[index].ShadowAlphaThreshold;

    /// <summary>Current CPU vertex capacity (grows on demand).</summary>
    internal int VertexCapacity => _vertices.Length;

    /// <summary>
    /// Rebuilds the CPU vertex arrays from the current skeleton pose.
    /// Call this from <c>OnUpdate</c> so the data is ready for both the shadow pass
    /// and the main render pass within the same frame. The shadow casters discard where the atlas alpha × the tint
    /// alpha is below <paramref name="shadowAlphaThreshold"/>; 0 casts the whole triangles.
    /// </summary>
    public void BuildVertices(float zSpacing, Matrix4x4 model, float shadowAlphaThreshold = 0f)
    {
        _modelMatrix = model;
        _vkBatches.Clear();
        _uploadedSlots = 0;

        _zSpacing = zSpacing;
        _shadowAlphaThreshold = float.IsNaN(shadowAlphaThreshold) ? 0f : Math.Max(0f, shadowAlphaThreshold);
        _buildVertexIndex = 0;
        _geometry.Build(_skeleton, this);
        _preparedVertexCount = _buildVertexIndex;
    }

    private readonly SpineGeometry _geometry = new();
    private float _zSpacing;
    private int _buildVertexIndex;

    // ISpineGeometrySink: one attachment in draw order → triangle-list vertices, z pushed ZSpacing per draw-order slot.
    void ISpineGeometrySink.Add(in SpineDrawItem item)
    {
        var texIdx = ResolveTexIdx(item.Region);
        var triangles = item.Triangles;
        EnsureVertexCapacity(_buildVertexIndex + triangles.Length);
        BeginBatch(texIdx, _buildVertexIndex);
        var z = _zSpacing * item.DrawIndex;
        var c = item.Color; // straight, sRGB-authored: premultiplied alpha is handled once, in SpineLit.vk.frag
        var vertices = item.Vertices;
        var uvs = item.Uvs;
        for (var j = 0; j < triangles.Length; j++)
        {
            var idx = triangles[j] << 1;
            AddVertex(vertices[idx], vertices[idx + 1], z, uvs[idx], uvs[idx + 1], c, ref _buildVertexIndex);
        }
    }

    // Grows the CPU vertex array (doubling) so `required` vertices fit. Steady-state poses never
    // grow, so this allocates only while a skeleton reaches a new maximum.
    private void EnsureVertexCapacity(int required)
    {
        if (required > _vertices.Length)
            Array.Resize(ref _vertices, GrowCapacity(_vertices.Length, required));
    }

    private static int GrowCapacity(int current, int required) => Math.Max(required, current * 2);

    /// <summary>
    /// Uploads pre-built vertex data to the GPU and issues main-pass draw commands with the model matrix of the last
    /// <see cref="BuildVertices"/>. Call <see cref="BuildVertices"/> from <c>OnUpdate</c> before calling this.
    /// </summary>
    public void Draw(Matrix4x4 view, Matrix4x4 projection, LightEnvironment lights, Vector3 cameraPosition) =>
        Draw(_modelMatrix, view, projection, lights, cameraPosition);

    /// <summary>
    /// <see cref="Draw(Matrix4x4, Matrix4x4, LightEnvironment, Vector3)"/> with <paramref name="model"/> (this view's
    /// billboarded pose) in place of the built one.
    /// </summary>
    public void Draw(in Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection, LightEnvironment lights, Vector3 cameraPosition)
    {
        if (_vkCtx is not null)
            DrawVulkan(_preparedVertexCount, model, view, projection, lights, cameraPosition);
    }

    private static int ResolveTexIdx(object region)
    {
        var atlasRegion = (AtlasRegion)region;
        return (int)atlasRegion.page.rendererObject;
    }

    private void BeginBatch(int texIdx, int vertexStart)
    {
        if (_vkCtx is null) return;
        if (_vkBatches.Count > 0 && _vkBatches[^1].texIdx == texIdx) return;
        _vkBatches.Add((texIdx, vertexStart));
    }

    private void AddVertex(float x, float y, float z, float u, float v, in Vector4 color, ref int vertexIndex)
    {
        _vertices[vertexIndex++] = new Vertex
        {
            Position = new Vector3(x, y, z),
            Uv = new Vector2(u, v),
            Color = color,
            ShadowAlphaThreshold = _shadowAlphaThreshold,
        };
    }

    // ── Shadow pass ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called from the shadow pass draw callback for directional and spot lights, with the pose to cast from
    /// (<paramref name="model"/>: the main view's billboarded one, else the built one). Requires
    /// <see cref="BuildVertices"/> to have been called this frame (from <c>OnUpdate</c>).
    /// </summary>
    public void DrawShadow2D(CommandBuffer cb, in Matrix4x4 model) => DrawShadow(cb, model, point: false, default, 0f);

    /// <summary>
    /// Called from the shadow pass draw callback for point lights.
    /// Requires <see cref="BuildVertices"/> to have been called this frame (from <c>OnUpdate</c>).
    /// </summary>
    public void DrawShadowPoint(CommandBuffer cb, in Matrix4x4 model, Vector3 lightPos, float lightRange) =>
        DrawShadow(cb, model, point: true, lightPos, lightRange);

    // The alpha-tested, double-sided casters (ADR 0204): the frame slot's colour-pass vertices, one draw per atlas page
    // batch with the page at set 1. Set 0 (the pass's light VP) stays bound: the Spine caster layouts share it and the
    // push range with the plain caster layouts.
    private unsafe void DrawShadow(CommandBuffer cb, in Matrix4x4 model, bool point, Vector3 lightPos, float lightRange)
    {
        if (_vkCtx is null || _shadowSystem is null || _preparedVertexCount == 0 || _vkBatches.Count == 0) return;
        var vk = _vkCtx.Vk;
        var buffer = UploadVertices(_vkCtx.FrameSlot);
        var layout = _shadowSystem.SpineCasterLayout(point);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _shadowSystem.SpineCasterPipeline(point));

        var vb  = buffer.Handle;
        var off = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &off);

        if (point)
        {
            // Point shadow push constant: mat4 model (64) + vec4 lightPosRange (16) = 80 bytes
            var push = stackalloc float[20];
            *(Matrix4x4*)push = model;
            push[16] = lightPos.X; push[17] = lightPos.Y; push[18] = lightPos.Z; push[19] = lightRange;
            vk.CmdPushConstants(cb, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 80, push);
        }
        else
        {
            var m = model;
            vk.CmdPushConstants(cb, layout, ShaderStageFlags.VertexBit, 0, 64, &m);
        }

        DrawBatches(vk, cb, layout, ShadowTexSetIndex, _preparedVertexCount);
    }

    // Writes the prepared vertices into the frame slot's buffer once per BuildVertices: the shadow passes and every view
    // of the colour pass draw the same data (a buffer bound by an earlier pass of this frame must not be replaced).
    private unsafe GpuBuffer UploadVertices(int slot)
    {
        var bit = 1 << slot;
        var buffer = EnsureBuffer(ref _vertexBuffers[slot], (ulong)(_preparedVertexCount * sizeof(Vertex)),
            (ulong)(DefaultVertexCapacity * sizeof(Vertex)), out var created);
        if ((_uploadedSlots & bit) == 0 || created)
        {
            _uploadedSlots |= bit;
            buffer.Write<Vertex>(_vertices.AsSpan(0, _preparedVertexCount));
        }

        return buffer;
    }

    // One draw per atlas page batch, binding the page at `setIndex` of `layout`.
    private unsafe void DrawBatches(Vk vk, CommandBuffer cb, PipelineLayout layout, uint setIndex, int vertexCount)
    {
        for (int b = 0; b < _vkBatches.Count; b++)
        {
            var (texIdx, start) = _vkBatches[b];
            int count = b + 1 < _vkBatches.Count ? _vkBatches[b + 1].start - start : vertexCount - start;
            if (count <= 0) continue;

            var texDs = _texDescSets[texIdx];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, setIndex, 1, &texDs, 0, null);
            vk.CmdDraw(cb, (uint)count, 1, (uint)start, 0);
        }
    }

    #region Vulkan draw

    private unsafe void DrawVulkan(int vertexCount, in Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection,
                                   LightEnvironment lights, Vector3 cameraPosition)
    {
        if (_vkBatches.Count == 0 || vertexCount == 0) return;

        var ctx      = _vkCtx!;
        var vk       = ctx.Vk;
        var cb       = ctx.CurrentCommandBuffer;
        var imageIdx = ctx.FrameSlot;

        // Shared per-frame data: written once per frame by whoever draws first.
        var frame = ctx.Frame;
        frame.EnsureCamera(view, projection, cameraPosition);
        frame.EnsureLights(lights, cameraPosition);

        var buffer = UploadVertices(imageIdx);

        // Compute world-space normal (sprite faces local +Z)
        var worldNormal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, model));

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);

        var vb       = buffer.Handle;
        var vbOffset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &vbOffset);

        // Right-handed viewport (negative height flips Y)
        PipelineBuilder.SetViewport(vk, cb, ctx.Frame.Extent, flipY: true);

        // Set 0 frame (camera + lights), set 1 shadows (real or fallback)
        frame.Bind(cb, _pipelineLayout, _shadowDescriptors);

        // Push model matrix + world-space normal
        var push = new PushConstant { Model = model, WorldNormal = new Vector4(worldNormal, 0f) };
        vk.CmdPushConstants(cb, _pipelineLayout, FrameContext.PushConstantStages, 0, (uint)sizeof(PushConstant), &push);

        // Draw each texture batch, binding the texture descriptor set per batch
        DrawBatches(vk, cb, _pipelineLayout, TexSetIndex, vertexCount);
    }

    // Grows a frame slot's streaming buffer (doubling, at least `initial`); the old one goes to the deletion queue.
    private GpuBuffer EnsureBuffer(ref GpuBuffer? buffer, ulong required, ulong initial, out bool created)
    {
        created = false;
        if (buffer is not null && required <= buffer.Size)
            return buffer;

        var capacity = Math.Max(required, buffer is null ? initial : buffer.Size * 2);
        buffer?.Dispose();
        buffer = GpuBuffer.Create(_vkCtx!, capacity, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        created = true;
        return buffer;
    }

    #endregion

    #region Vulkan setup

    /// <summary>
    /// The atlas page set: one combined image sampler at binding 0, fragment stage. The colour pass binds it at set 2 and
    /// the shadow casters (<see cref="ShadowSystem.EnsureSpineCasters"/>) at set 1; both layouts come from here, so they
    /// are identically defined and a page's set works with either.
    /// </summary>
    internal static DescriptorSetLayout CreateTextureSetLayout(IVulkanContext ctx) => PipelineBuilder.CreateSetLayout(ctx,
        [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
        "Spine texture");

    private unsafe void CreateVkResources(IVulkanContext ctx, List<(byte[] Pixels, int Width, int Height)> imageData)
    {
        var texCount = imageData.Count;
        _shadowDescriptors = ShadowFallback.Resolve(_shadowSystem, ctx);

        // --- Textures (uploaded by the upload queue at the start of the next frame) ---
        // Straight-alpha atlases are sRGB images (decoded by the sampler). Premultiplied atlases were multiplied in
        // sRGB space, so they are stored UNORM and the shader un-premultiplies, decodes and re-premultiplies.
        var colorSpace = _pma ? TextureColorSpace.Linear : TextureColorSpace.Srgb;
        _textures = new GpuTexture[texCount];
        for (int t = 0; t < texCount; t++)
        {
            var (pixels, width, height) = imageData[t];
            _textures[t] = GpuTexture.Create2D(ctx, (uint)width, (uint)height, pixels, colorSpace, TextureSampling.LinearClamp);
        }

        // --- Set 2: one combined image sampler per atlas page (set 1 of the shadow casters) ---
        _texLayout = CreateTextureSetLayout(ctx);
        _descPool = PipelineBuilder.CreatePool(ctx, (uint)Math.Max(1, texCount),
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)texCount }], "Spine textures");
        _texDescSets = new DescriptorSet[texCount];
        for (int t = 0; t < texCount; t++)
        {
            _texDescSets[t] = PipelineBuilder.AllocateSet(ctx, _descPool, _texLayout, "Spine texture");
            PipelineBuilder.WriteImage(ctx, _texDescSets[t], 0, _textures[t].Descriptor);
        }

        // Set 0 = frame, set 1 = shadows (real or fallback), set 2 = texture: the same indices whether or not a
        // ShadowSystem exists, matching SpineLit.vk.frag.
        _pipelineLayout = ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [_texLayout], "Spine");

        // Vertex layout: Position(vec3,0) + UV(vec2,12) + Color(vec4,20) + ShadowAlphaThreshold(float,36) = stride 40
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = VertexStride, InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32Sfloat, Offset = 12 },
            new() { Location = 2, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, Offset = 20 },
        ];

        // The fragment shader outputs premultiplied colour for both atlas kinds (constant 0 = atlas is PMA).
        var premultiplied = _pma ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &premultiplied };

        var state = new PipelineState { DepthTest = true, DepthWrite = true, Blend = BlendMode.Premultiplied };
        _pipeline = PipelineBuilder.Create(ctx, state, _pipelineLayout, ctx.RenderPass,
            "Shaders/Spine/SpineLit.vk.vert.spv", "Shaders/Spine/SpineLit.vk.frag.spv", bindings, attributes, "Spine",
            &specialization);
    }

    #endregion

    /// <summary>Releases every GPU object through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_vkCtx is null || _disposed) return;
        _disposed = true;

        foreach (var buffer in _vertexBuffers)
            buffer?.Dispose();
        foreach (var texture in _textures)
            texture.Dispose();

        var deletions = _vkCtx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
        deletions.Enqueue(GpuDeletion.Of(_descPool));
        deletions.Enqueue(GpuDeletion.Of(_texLayout));
    }
}
