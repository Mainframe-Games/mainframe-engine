using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Shader pairs mesh pipelines are built from (part of <see cref="PipelineKey"/>).</summary>
public enum ShaderSetId : byte
{
    /// <summary><c>Mesh/Mesh.vk.vert</c> + <c>Mesh/Mesh.vk.frag</c>: lit/unshaded StandardMaterial3D, HDR colour.</summary>
    MeshLit,

    /// <summary><c>Mesh/Mesh.vk.vert</c> + <c>Mesh/MeshId.vk.frag</c>: object ids into an <c>R32_UINT</c> target.</summary>
    MeshObjectId,

    /// <summary><c>Mesh/MeshOutline.vk.vert</c> + <c>Mesh/Mesh.vk.frag</c>: <see cref="OutlineMaterial3D"/> (inverted hull).</summary>
    MeshOutline,

    /// <summary>
    /// <c>Foliage/Foliage.vk.vert</c> + <c>Foliage/Foliage.vk.frag</c>: <see cref="FoliageMaterial3D"/> (vertex wind,
    /// translucency); always <see cref="VertexLayoutId.MeshInstancedExt"/>.
    /// </summary>
    MeshFoliage,

    /// <summary>
    /// <c>Water/Water.vk.vert</c> + <c>Water/Water.vk.frag</c>: <see cref="WaterMaterial3D"/> (flow normals, reflection,
    /// absorption, foam; ADR 0159); always <see cref="VertexLayoutId.MeshInstancedExt"/>.
    /// </summary>
    MeshWater,

    /// <summary>
    /// <c>Mesh/Mesh.vk.vert</c> + <c>Terrain/TerrainSplat.vk.frag</c>: <see cref="TerrainSplatMaterial3D"/>, with its own
    /// set 2 layout and pipeline layout (ADR 0156).
    /// </summary>
    MeshTerrainSplat,

    /// <summary>
    /// The depth prepass (ADR 0163) of lit, unshaded and terrain surfaces: <c>Mesh/MeshDepth.vk.vert</c> (or
    /// <c>MeshDepthExt.vk.vert</c> with vertex streams) + <c>Mesh/MeshDepth.vk.frag</c> (velocity, cutout alpha test)
    /// into the prepass render pass; previous model matrices at binding 3.
    /// </summary>
    MeshDepth,

    /// <summary>The depth prepass of <see cref="FoliageMaterial3D"/>: <c>Foliage/FoliageDepth.vk.vert</c> (wind now and a frame ago) + <c>Mesh/MeshDepth.vk.frag</c>.</summary>
    MeshDepthFoliage,
}

/// <summary>
/// Everything that distinguishes one mesh pipeline from another: shader set, vertex layout, the material's
/// fixed-function state and the render pass. Equal keys share one <c>VkPipeline</c> (see
/// <see cref="PipelineStateCache"/>), so a thousand materials with the same state cost one pipeline.
/// </summary>
/// <param name="Shaders">Vertex + fragment shaders.</param>
/// <param name="VertexLayout">Vertex input bindings/attributes.</param>
/// <param name="Alpha">Blending and the alpha-test specialization constant.</param>
/// <param name="Cull">Faces culled (double-sided materials resolve to <see cref="CullMode.Disabled"/>).</param>
/// <param name="Mirrored">Front faces are clockwise (instances with a negative-determinant transform).</param>
/// <param name="DepthWrite">Depth writes (off for blended surfaces).</param>
/// <param name="RenderPass">The render pass (or a compatible one) the pipeline is used in.</param>
/// <param name="ExtraPass">A next pass or overlay draw: depth test less-or-equal, so it lands on the surface drawn before.</param>
/// <param name="Prepassed">
/// A colour draw of a surface the depth prepass already drew (ADR 0163): no depth writes, depth test less-or-equal, and
/// cutouts test EQUAL without their <c>discard</c> (the prepass kept only what passed the alpha test).
/// </param>
public readonly record struct PipelineKey(
    ShaderSetId Shaders,
    VertexLayoutId VertexLayout,
    AlphaMode Alpha,
    CullMode Cull,
    bool Mirrored,
    bool DepthWrite,
    ulong RenderPass,
    bool ExtraPass = false,
    bool Prepassed = false)
{
    /// <summary>
    /// The key for drawing a material's surfaces with <paramref name="shaders"/> into <paramref name="renderPass"/>.
    /// The object-ID shaders never blend (integer target) and always write depth, so blended materials share the
    /// opaque ID pipeline.
    /// </summary>
    /// <remarks>
    /// <paramref name="streams"/>: the surface draws with the second vertex stream
    /// (<see cref="VertexLayoutId.MeshInstancedExt"/>); foliage always does, the object-ID shaders never read it.
    /// </remarks>
    public static PipelineKey ForMaterial(ShaderSetId shaders, in MaterialRenderState state, bool mirrored, RenderPass renderPass,
        bool extraPass = false, bool streams = false, bool prepassed = false)
    {
        var alpha = state.Alpha;
        var depthWrite = state.DepthWrite && !prepassed;
        if (shaders == ShaderSetId.MeshObjectId)
        {
            if (alpha == AlphaMode.Blend)
                alpha = AlphaMode.Opaque;
            depthWrite = true;
        }

        var layout = shaders switch
        {
            ShaderSetId.MeshFoliage or ShaderSetId.MeshWater or ShaderSetId.MeshDepthFoliage => VertexLayoutId.MeshInstancedExt,
            ShaderSetId.MeshLit or ShaderSetId.MeshDepth when streams => VertexLayoutId.MeshInstancedExt,
            _ => VertexLayoutId.MeshInstanced,
        };
        return new PipelineKey(shaders, layout, alpha, state.EffectiveCull, mirrored, depthWrite, renderPass.Handle, extraPass, prepassed);
    }

    /// <summary>True for the depth prepass's shader sets (ADR 0163).</summary>
    public bool IsDepthPrepass => Shaders is ShaderSetId.MeshDepth or ShaderSetId.MeshDepthFoliage;
}

/// <summary>A cached pipeline and its small dense id (used in draw sort keys).</summary>
public readonly record struct PipelineEntry(Pipeline Pipeline, int Id);

/// <summary>Creates and destroys the pipelines a <see cref="PipelineStateCache"/> holds.</summary>
internal interface IPipelineFactory
{
    Pipeline Create(in PipelineKey key);

    void Destroy(Pipeline pipeline);
}

/// <summary>
/// State-hash cache of <c>VkPipeline</c>s: one pipeline per distinct <see cref="PipelineKey"/>, created on first
/// request (through the persisted <see cref="PipelineCache"/>, so a warm start is cheap) and kept until disposal.
/// Lookups are allocation-free; the renderer caches the entry per material so steady-state frames do not even hash.
/// </summary>
public sealed class PipelineStateCache : IDisposable
{
    /// <summary>Ids (1-based) must fit the 12-bit pipeline field of the draw sort keys.</summary>
    public const int MaxPipelines = 4095;

    private readonly IPipelineFactory _factory;
    private readonly Dictionary<PipelineKey, PipelineEntry> _entries = [];
    private bool _disposed;

    internal PipelineStateCache(IPipelineFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Distinct pipelines created.</summary>
    public int Count => _entries.Count;

    /// <summary>Requests answered from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>Requests that created a pipeline.</summary>
    public long Misses { get; private set; }

    /// <summary>The pipeline for <paramref name="key"/>, created on the first request.</summary>
    public PipelineEntry GetOrCreate(in PipelineKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_entries.TryGetValue(key, out var entry))
        {
            Hits++;
            return entry;
        }

        if (_entries.Count >= MaxPipelines)
            throw new InvalidOperationException($"More than {MaxPipelines} distinct mesh pipelines; check for runaway material state.");
        Misses++;
        entry = new PipelineEntry(_factory.Create(key), _entries.Count + 1);
        _entries.Add(key, entry);
        return entry;
    }

    /// <summary>True if a pipeline exists for <paramref name="key"/> (no creation).</summary>
    public bool Contains(in PipelineKey key) => _entries.ContainsKey(key);

    /// <summary>Destroys every pipeline (through the factory, which defers to the deletion queue).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values)
            _factory.Destroy(entry.Pipeline);
        _entries.Clear();
    }
}
