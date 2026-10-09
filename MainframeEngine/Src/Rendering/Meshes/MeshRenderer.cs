using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Per-frame counters of the mesh renderer (shown by <see cref="RendererDebugWindow"/>).</summary>
public struct MeshDrawStats
{
    /// <summary>Geometry instances considered (visible in the tree, with a mesh).</summary>
    public int Instances;

    /// <summary>Instances outside the camera frustum.</summary>
    public int Culled;

    /// <summary>Surface draws (instance × surface) submitted after culling.</summary>
    public int SurfaceInstances;

    /// <summary><c>vkCmdDrawIndexed</c> calls for meshes in colour passes (each draws a run of instances).</summary>
    public int DrawCalls;

    public int PipelineBinds;
    public int MaterialBinds;

    /// <summary>Instanced draws into shadow maps (every light, every face).</summary>
    public int ShadowDrawCalls;

    /// <summary>Caster instances drawn into shadow maps (summed over passes, after per-pass culling).</summary>
    public int ShadowInstances;

    /// <summary>Caster surfaces culled from shadow passes (summed over passes).</summary>
    public int ShadowCulled;

    /// <summary>Draws in object-ID passes.</summary>
    public int ObjectIdDrawCalls;

    /// <summary>Instances outside their visibility range (<see cref="GeometryInstance3D.VisibilityRangeBegin"/>/End).</summary>
    public int OutOfRange;

    /// <summary><see cref="MultiMesh"/> instances in the surface draws submitted (summed over surfaces).</summary>
    public int MultiMeshInstances;

    /// <summary>Instanced draws in the depth prepass (ADR 0163).</summary>
    public int PrepassDrawCalls;
}

/// <summary>One surface of one instance in a colour/ID pass.</summary>
internal struct MeshDrawItem
{
    public GeometryInstance3D Node;
    public MeshGpu Mesh;
    public MaterialGpu Material;
    public PipelineEntry Pipeline;
    public int Surface;
    public bool Mirrored;

    /// <summary>A next-pass or overlay draw (skipped by the object-ID pass).</summary>
    public bool Extra;

    /// <summary>A <see cref="MultiMeshInstance3D"/>'s own instance buffer (drawn alone, never merged), else null.</summary>
    public GpuBuffer? Instances;
    public uint InstanceCount;
}

/// <summary>One surface of one shadow caster.</summary>
internal struct ShadowCasterItem
{
    public GeometryInstance3D Node;
    public MeshGpu Mesh;
    public int Surface;
    public CullMode Cull;
    public bool Mirrored;

    /// <summary>The material of a cutout or foliage caster (bound as set 1 in the shadow pass); null for opaque casters.</summary>
    public MaterialGpu? Cutout;

    /// <summary>A foliage caster: the wind vertex shader, the second vertex stream (<see cref="Cutout"/> is its material).</summary>
    public bool Foliage;

    /// <summary>World bounds of the instance (culled per shadow pass).</summary>
    public Aabb Bounds;

    /// <summary>A <see cref="MultiMeshInstance3D"/>'s own instance buffer (drawn alone), else null.</summary>
    public GpuBuffer? Instances;
    public uint InstanceCount;

    /// <summary>The passes it casts into (ADR 0167): <see cref="MeshRenderer.FinePasses"/>, <see cref="MeshRenderer.CoarsePasses"/> or both.</summary>
    public byte Passes;
}

/// <summary>A run of caster instances drawn with one instanced draw in one shadow pass.</summary>
internal struct ShadowCasterRun
{
    public MeshGpu Mesh;
    public MaterialGpu? Cutout;
    public bool Foliage;
    public GpuBuffer Instances;
    public int Surface;
    public CullMode Cull;
    public bool Mirrored;
    public uint FirstInstance;
    public uint InstanceCount;
}

/// <summary>The sorted draws of one view (main viewport, an offscreen view) for the current frame.</summary>
internal sealed class MeshViewDraws
{
    public readonly DrawList<MeshDrawItem> Opaque = new(256);
    public readonly DrawList<MeshDrawItem> Transparent = new(64);
    public readonly DrawList<ShadowCasterItem> Casters = new(256);

    /// <summary>Union of the casters' world bounds (cascades pull their near plane back to it).</summary>
    public Aabb CasterBounds = Aabb.Empty;

    /// <summary>The world's wind (and fog) when the view was prepared: foliage casters read the wind as push constants.</summary>
    public FrameEnvironment Environment;

    /// <summary>This frame's shadow draws: runs of culled casters per pass (<see cref="PassRuns"/>).</summary>
    public ShadowCasterRun[] ShadowRuns = new ShadowCasterRun[64];
    public int ShadowRunCount;

    /// <summary>Per shadow pass index: (first run, run count) in <see cref="ShadowRuns"/>.</summary>
    public readonly (int First, int Count)[] PassRuns = new (int, int)[ShadowSystem.MaxShadowPasses];

    public ulong PreparedFrame;
    public ulong InstancesFrame;
    public ulong ShadowFrame;
    public GpuBuffer? InstanceBuffer;
    public uint FirstInstance;

    /// <summary>
    /// The depth prepass draws this view this frame (ADR 0163; the render server sets it before anything draws the view):
    /// its instances carry a previous-model block, and the scene pass draws the prepassed surfaces against the prepass depth.
    /// </summary>
    public bool Prepassed;

    /// <summary>Byte offset of the previous-model block in <see cref="InstanceBuffer"/> (binding 3), when <see cref="HasPreviousInstances"/>.</summary>
    public ulong PreviousInstanceOffset;
    public bool HasPreviousInstances;

    public void Clear()
    {
        Opaque.Clear();
        Transparent.Clear();
        Casters.Clear();
        CasterBounds = Aabb.Empty;
        InstanceBuffer = null;
        ClearShadowRuns();
    }

    public void ClearShadowRuns()
    {
        Array.Clear(ShadowRuns, 0, ShadowRunCount); // drop the references
        ShadowRunCount = 0;
        Array.Clear(PassRuns);
    }
}

/// <summary>
/// Draws <see cref="GeometryInstance3D"/>s: owns the GPU copies of meshes, materials and textures (reference
/// counted by the nodes using them), the shared mesh pipeline layout and material set layout, the state-hash
/// <see cref="PipelineStateCache"/>, and the per-frame instance buffer. Each frame and view it culls the world's
/// instances against the camera, sorts opaque draws by pipeline → material → mesh and transparent ones back to
/// front, writes one <see cref="MeshInstanceData"/> per instance and records one instanced draw per run of equal
/// (pipeline, material, mesh surface). Shadow casters are batched by (cull mode, mesh surface) the same way.
/// </summary>
/// <remarks>Render thread only. Building and drawing are allocation-free once the lists and buffers have grown.</remarks>
internal sealed unsafe partial class MeshRenderer : IDisposable, IPipelineFactory
{
    private readonly IVulkanContext _ctx;
    private readonly ShadowSystem? _shadows;
    private readonly IShadowDescriptors _shadowDescriptors;
    private readonly DescriptorSetLayout _materialLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly RenderPass _idPassPrototype;
    private readonly MaterialDescriptorAllocator _materialSets;
    private readonly GpuTexture _whiteSrgb;
    private readonly GpuTexture _flatNormal;
    private readonly GpuTexture _whiteLinear;
    private readonly InstanceBuffer _instances;
    private readonly Dictionary<Mesh, MeshGpu> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Material, MaterialGpu> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Texture2D, TextureColorSpace), TextureGpu> _textures = [];
    private readonly HashSet<MultiMeshGpu> _multiMeshes = [];
    private int _nextMeshId = 1, _nextMaterialId = 1;
    private ulong _statsFrame;
    private int[] _visibleCasters = new int[256];
    private bool _warnedUnsupportedMaterial;
    private bool _disposed;

    public MeshRenderer(IVulkanContext ctx, ShadowSystem? shadows)
    {
        _ctx = ctx;
        _shadows = shadows;
        _shadowDescriptors = ShadowFallback.Resolve(shadows, ctx);

        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            // The parameters are read by the outline vertex shader too (its width).
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 5, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        _materialLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "material set 2");
        _pipelineLayout = ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [_materialLayout], "mesh");
        _idPassPrototype = RenderTarget.CreateRenderPass(ctx, ObjectIdTargetDesc(FindDepthFormat(ctx)));
        _materialSets = new MaterialDescriptorAllocator(ctx, _materialLayout);
        shadows?.SetMaterialSetLayout(_materialLayout); // cutout casters alpha-test against the material
        Pipelines = new PipelineStateCache(this);
        _instances = new InstanceBuffer(ctx);

        // Fallbacks bound in empty texture slots (the shader ignores them unless its flag is set; the sampler of a
        // material without textures comes from the white texture: linear, repeat).
        ReadOnlySpan<byte> white = [255, 255, 255, 255];
        ReadOnlySpan<byte> flat = [128, 128, 255, 255];
        _whiteSrgb = GpuTexture.Create2D(ctx, 1, 1, white, TextureColorSpace.Srgb, TextureSampling.LinearRepeat);
        _flatNormal = GpuTexture.Create2D(ctx, 1, 1, flat, TextureColorSpace.Linear, TextureSampling.LinearRepeat);
        _whiteLinear = GpuTexture.Create2D(ctx, 1, 1, white, TextureColorSpace.Linear, TextureSampling.LinearRepeat); // ORM: × 1
        CreateSplatResources();
    }

    /// <summary>The state-hash pipeline cache (stats for tools and tests).</summary>
    public PipelineStateCache Pipelines { get; }

    public MeshDrawStats Stats;

    /// <summary>Layout shared by every mesh pipeline: set 0 frame, set 1 shadows, set 2 material.</summary>
    public PipelineLayout PipelineLayout => _pipelineLayout;

    public int MeshCount => _meshes.Count;
    public int MaterialCount => _materials.Count;
    public int TextureCount => _textures.Count;
    public int InstanceCapacity => _instances.Capacity;

    /// <summary>The object-ID target layout: <c>R32_UINT</c> read back after the pass + depth.</summary>
    public static RenderTargetDesc ObjectIdTargetDesc(Format depthFormat) =>
        new("object ids", [RenderTargetAttachment.Readback(Format.R32Uint)], depthFormat);

    /// <summary>The scene target's depth format (offscreen views use the same, so they share pipelines).</summary>
    public static Format FindDepthFormat(IVulkanContext ctx) => ctx.SceneTarget.Depth?.Format ?? Format.D32Sfloat;

    /// <summary>
    /// The frame being built: <see cref="IVulkanContext.FrameNumber"/> while it records, the next one before
    /// <c>BeginFrame</c> (the render server prepares draw lists before the frame starts).
    /// </summary>
    /// <remarks>
    /// Combined with a preparation epoch (<see cref="BeginPreparation"/>): when <c>BeginFrame</c> skips a frame
    /// (swapchain recreation) the predicted frame number repeats, and the epoch makes the next preparation rebuild
    /// instead of reusing lists that may reference freed nodes and resources.
    /// </remarks>
    private ulong CurrentFrame => ((_ctx.FrameStarted ? _ctx.FrameNumber : _ctx.FrameNumber + 1) << 16) | (_epoch & 0xFFFF);

    private ulong _epoch;

    /// <summary>Starts a frame's preparation: every view, mesh and material is rebuilt/refreshed once more.</summary>
    public void BeginPreparation() => _epoch++;

    // ── Node resources ─────────────────────────────────────────────────────────

    /// <summary>
    /// Brings <paramref name="node"/>'s GPU references up to date (mesh, per-surface materials) and refreshes those
    /// resources once per frame. Returns false when the node has nothing to draw.
    /// </summary>
    private bool Sync(GeometryInstance3D node)
    {
        var mesh = node.GetRenderMesh();
        if (!ReferenceEquals(mesh, node.ResolvedMesh))
        {
            ReleaseGeometry(node);
            node.ResolvedMesh = mesh;
            if (mesh is not null)
                node.GpuMesh = AcquireMesh(mesh);
        }

        if (node.GpuMesh is not { } gpuMesh)
            return false;

        var frame = CurrentFrame;
        if (gpuMesh.PreparedFrame != frame)
        {
            gpuMesh.PreparedFrame = frame;
            gpuMesh.HasGeometry = gpuMesh.Update();
        }

        if (!gpuMesh.HasGeometry)
            return false;

        if (node.ResolvedMeshGeneration != gpuMesh.Generation || !ReferenceEquals(node.ResolvedOverride, node.MaterialOverride) ||
            node.ResolvedStamp != node.RenderStamp || node.GpuMaterials.Length != gpuMesh.Surfaces.Length ||
            !ReferenceEquals(node.ResolvedOverlay, node.MaterialOverlay) || node.ResolvedChainGeneration != Material.ChainGeneration)
            ResolveMaterials(node, mesh!, gpuMesh);

        foreach (var material in node.GpuMaterials)
            if (material is not null && material.PreparedFrame != frame)
                PrepareMaterial(material);
        foreach (var extra in node.GpuExtraPasses)
            if (extra.Material.PreparedFrame != frame)
                PrepareMaterial(extra.Material);
        return true;
    }

    private void ResolveMaterials(GeometryInstance3D node, Mesh mesh, MeshGpu gpuMesh)
    {
        // Acquire the new references before releasing the old ones, so shared materials are not torn down.
        var chainGeneration = Material.ChainGeneration;
        var previous = node.GpuMaterials;
        var previousExtras = node.GpuExtraPasses;
        var surfaces = gpuMesh.Surfaces.Length;
        var resolved = new MaterialGpu?[surfaces];
        for (var s = 0; s < resolved.Length; s++)
            resolved[s] = AcquireMaterial(node.GetRenderMaterial(mesh, s));

        // Extra passes (Godot's material chains): each surface material's next passes, then the overlay and its next
        // passes over every surface. Only on change, so the allocation is not per frame.
        var overlay = node.MaterialOverlay;
        var extraCount = 0;
        for (var s = 0; s < surfaces; s++)
            extraCount += ChainLength(node.GetRenderMaterial(mesh, s).NextPass) + ChainLength(overlay);
        var extras = extraCount == 0 ? [] : new MeshExtraPass[extraCount];
        var e = 0;
        for (var s = 0; s < surfaces; s++)
        {
            var link = node.GetRenderMaterial(mesh, s).NextPass;
            for (var n = 0; link is not null && n < Material.MaxPassChain; n++, link = link.NextPass)
                extras[e++] = new MeshExtraPass(s, AcquireMaterial(link));
        }

        for (var s = 0; s < surfaces; s++)
        {
            var link = overlay;
            for (var n = 0; link is not null && n < Material.MaxPassChain; n++, link = link.NextPass)
                extras[e++] = new MeshExtraPass(s, AcquireMaterial(link));
        }

        foreach (var material in previous)
            if (material is not null)
                ReleaseMaterial(material);
        foreach (var extra in previousExtras)
            ReleaseMaterial(extra.Material);
        node.GpuMaterials = resolved;
        node.GpuExtraPasses = extras;
        node.ResolvedMeshGeneration = gpuMesh.Generation;
        node.ResolvedOverride = node.MaterialOverride;
        node.ResolvedOverlay = overlay;
        node.ResolvedChainGeneration = chainGeneration;
        node.ResolvedStamp = node.RenderStamp;
    }

    private static int ChainLength(Material? first)
    {
        var n = 0;
        for (var link = first; link is not null && n < Material.MaxPassChain; link = link.NextPass)
            n++;
        return n;
    }

    /// <summary>Drops the node's references (it was freed, or its mesh changed).</summary>
    public void ReleaseGeometry(GeometryInstance3D node)
    {
        foreach (var material in node.GpuMaterials)
            if (material is not null)
                ReleaseMaterial(material);
        node.GpuMaterials = [];
        foreach (var extra in node.GpuExtraPasses)
            ReleaseMaterial(extra.Material);
        node.GpuExtraPasses = [];
        node.ResolvedOverlay = null;
        if (node.GpuMesh is { } mesh)
            ReleaseMesh(mesh);
        node.GpuMesh = null;
        node.ResolvedMesh = null;
        node.ResolvedMeshGeneration = -1;
        node.ResolvedOverride = null;
        if (node is MultiMeshInstance3D { GpuMultiMesh: { } multi } instance)
        {
            multi.Release();
            _multiMeshes.Remove(multi);
            instance.GpuMultiMesh = null;
        }
    }

    // The node's instance buffer, rebuilt when its multimesh, transform or mesh bounds changed; null when nothing is drawn.
    private MultiMeshGpu? SyncMultiMesh(MultiMeshInstance3D node, MeshGpu mesh, in Matrix4x4 model)
    {
        if (node.Multimesh is not { } multimesh)
            return null;
        if (node.GpuMultiMesh is not { } gpu)
        {
            gpu = node.GpuMultiMesh = new MultiMeshGpu(_ctx);
            _multiMeshes.Add(gpu);
        }

        return gpu.Update(multimesh, model, mesh.Bounds, node.ObjectId) ? gpu : null;
    }

    private MeshGpu AcquireMesh(Mesh mesh)
    {
        if (!_meshes.TryGetValue(mesh, out var gpu))
        {
            gpu = new MeshGpu(_ctx, mesh, _nextMeshId++);
            _meshes.Add(mesh, gpu);
        }

        gpu.RefCount++;
        return gpu;
    }

    private void ReleaseMesh(MeshGpu gpu)
    {
        if (--gpu.RefCount > 0)
            return;
        gpu.Release();
        _meshes.Remove(gpu.Mesh);
    }

    private MaterialGpu AcquireMaterial(Material material)
    {
        if (!_materials.TryGetValue(material, out var gpu))
        {
            gpu = new MaterialGpu(material, _nextMaterialId++);
            _materials.Add(material, gpu);
        }

        gpu.RefCount++;
        return gpu;
    }

    private void ReleaseMaterial(MaterialGpu gpu)
    {
        if (--gpu.RefCount > 0)
            return;
        DestroyMaterial(gpu);
        _materials.Remove(gpu.Material);
    }

    private void DestroyMaterial(MaterialGpu gpu)
    {
        gpu.Params?.Dispose();
        gpu.Params = null;
        _materialSets.Free(gpu.SetPool, gpu.Set);
        gpu.Set = default;
        ReleaseTexture(gpu.Albedo);
        ReleaseTexture(gpu.Normal);
        ReleaseTexture(gpu.Emission);
        ReleaseTexture(gpu.Orm);
        gpu.Albedo = gpu.Normal = gpu.Emission = gpu.Orm = null;
        DestroySplat(gpu);
    }

    private TextureGpu? AcquireTexture(Texture2D? texture, bool colorUsage)
    {
        if (texture is null)
            return null;
        var colorSpace = texture.ImportSettings.ResolveColorSpace(colorUsage);
        if (!_textures.TryGetValue((texture, colorSpace), out var gpu))
        {
            gpu = new TextureGpu(_ctx, texture, colorSpace);
            _textures.Add((texture, colorSpace), gpu);
        }

        gpu.RefCount++;
        return gpu;
    }

    private void ReleaseTexture(TextureGpu? gpu)
    {
        if (gpu is null || --gpu.RefCount > 0)
            return;
        gpu.Release();
        _textures.Remove((gpu.Texture, gpu.ColorSpace));
    }

    // ── Materials ──────────────────────────────────────────────────────────────

    private void PrepareMaterial(MaterialGpu gpu)
    {
        gpu.PreparedFrame = CurrentFrame;
        var material = gpu.Material;
        var outline = material as OutlineMaterial3D;
        var foliage = material as FoliageMaterial3D;
        var water = material as WaterMaterial3D;
        var splat = material as TerrainSplatMaterial3D;
        gpu.ColorShaders = outline is not null ? ShaderSetId.MeshOutline : foliage is not null ? ShaderSetId.MeshFoliage
            : water is not null ? ShaderSetId.MeshWater : splat is not null ? ShaderSetId.MeshTerrainSplat : ShaderSetId.MeshLit;
        if (material is not StandardMaterial3D standard)
        {
            // Splat materials keep this default set too: the object-ID pass binds it with the shared layout.
            standard = StandardMaterial3D.Default;
            if (outline is null && foliage is null && water is null && splat is null && !_warnedUnsupportedMaterial)
            {
                _warnedUnsupportedMaterial = true;
                Log.Warning($"[Mesh] {material.GetType().Name} is not supported by the renderer yet; drawing with the default material.");
            }
        }

        var changed = gpu.UploadedVersion != material.Version || gpu.Params is null;
        var rewrite = false;
        if (changed)
        {
            gpu.UploadedVersion = material.Version;
            var state = material.RenderState;
            if (state != gpu.State || gpu.Params is null)
            {
                gpu.State = state;
                gpu.ResetPipelines();
            }

            gpu.RenderPriority = material.RenderPriority;
        }

        // Slots follow the material's textures and their colour space (an import-settings change moves a texture
        // to another (texture, colour space) upload without touching the material).
        // Water: the albedo slot holds the foam mask (linear), the normal slot the ripple normals (built-in when null).
        rewrite |= water is not null
            ? SwapTexture(water.FoamTexture ?? WaterTextures.Foam, colorUsage: false, ref gpu.Albedo)
            : SwapTexture(outline is not null ? null : foliage is not null ? foliage.AlbedoTexture : standard.AlbedoTexture, colorUsage: true, ref gpu.Albedo);
        rewrite |= SwapTexture(water is not null ? water.NormalMap ?? WaterTextures.Normal
            : outline is not null ? null : foliage is not null ? foliage.NormalTexture : standard.NormalTexture, colorUsage: false, ref gpu.Normal);
        rewrite |= SwapTexture(outline is null && foliage is null && water is null ? standard.EmissionTexture : null, colorUsage: true, ref gpu.Emission);
        rewrite |= SwapTexture(outline is not null || water is not null ? null : foliage is not null ? foliage.OrmTexture : standard.OrmTexture, colorUsage: false, ref gpu.Orm);

        // Textures re-uploaded (reimport, new import settings) since the set was written.
        rewrite |= RefreshTexture(gpu.Albedo, gpu.AlbedoGeneration);
        rewrite |= RefreshTexture(gpu.Normal, gpu.NormalGeneration);
        rewrite |= RefreshTexture(gpu.Emission, gpu.EmissionGeneration);
        rewrite |= RefreshTexture(gpu.Orm, gpu.OrmGeneration);

        if (changed || rewrite)
        {
            var flags = (gpu.Albedo?.Gpu is not null ? MaterialParams.HasAlbedo : 0) |
                        (gpu.Normal?.Gpu is not null ? MaterialParams.HasNormal : 0) |
                        (gpu.Emission?.Gpu is not null ? MaterialParams.HasEmission : 0) |
                        (gpu.Orm?.Gpu is not null ? MaterialParams.HasOrm : 0);
            var parameters = outline is not null ? MaterialParams.From(outline)
                : foliage is not null ? MaterialParams.From(foliage, flags)
                : water is not null ? MaterialParams.From(water, flags)
                : MaterialParams.From(standard, flags);
            if (gpu.Params is null)
            {
                gpu.Params = GpuBuffer.Create(_ctx, MaterialParams.Size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.DeviceLocal);
                rewrite = true;
            }

            // Device-local: recorded by the upload queue at the start of the next frame, after earlier frames' reads.
            gpu.Params.Upload(MemoryMarshal.AsBytes(new ReadOnlySpan<MaterialParams>(in parameters)));
        }

        if (rewrite || gpu.Set.Handle == 0)
            WriteMaterialSet(gpu);
        if (splat is not null)
            PrepareSplat(gpu, splat);
    }

    // Points a texture slot at a texture (acquiring the new reference, releasing the old); true when it changed.
    private bool SwapTexture(Texture2D? texture, bool colorUsage, ref TextureGpu? slot)
    {
        var space = texture?.ImportSettings.ResolveColorSpace(colorUsage);
        if (ReferenceEquals(slot?.Texture, texture) && slot?.ColorSpace == space)
            return false;
        var next = AcquireTexture(texture, colorUsage);
        next?.Update();
        ReleaseTexture(slot);
        slot = next;
        return true;
    }

    private static bool RefreshTexture(TextureGpu? texture, int seenGeneration)
    {
        if (texture is null)
            return false;
        texture.Update(); // no-op unless the texture's version moved
        return texture.Generation != seenGeneration;
    }

    private void WriteMaterialSet(MaterialGpu gpu)
    {
        // A set may still be bound by frames in flight: write a fresh one, return the old one when they finish.
        _materialSets.Free(gpu.SetPool, gpu.Set);
        gpu.Set = _materialSets.Allocate(out var pool);
        gpu.SetPool = pool;

        var albedo = gpu.Albedo?.Gpu ?? _whiteSrgb;
        var normal = gpu.Normal?.Gpu ?? _flatNormal;
        var emission = gpu.Emission?.Gpu ?? _whiteSrgb;
        var orm = gpu.Orm?.Gpu ?? _whiteLinear;
        gpu.AlbedoGeneration = gpu.Albedo?.Generation ?? 0;
        gpu.NormalGeneration = gpu.Normal?.Generation ?? 0;
        gpu.EmissionGeneration = gpu.Emission?.Generation ?? 0;
        gpu.OrmGeneration = gpu.Orm?.Generation ?? 0;
        // One sampler for every texture of the material (MoltenVK's sampler budget): the first texture's.
        var sampler = (gpu.Albedo?.Gpu ?? gpu.Normal?.Gpu ?? gpu.Emission?.Gpu ?? gpu.Orm?.Gpu ?? _whiteSrgb).Sampler;

        var buffer = gpu.Params!.Descriptor(0, MaterialParams.Size);
        var images = stackalloc DescriptorImageInfo[5];
        images[0] = new DescriptorImageInfo { Sampler = sampler };
        images[1] = new DescriptorImageInfo { ImageView = albedo.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[2] = new DescriptorImageInfo { ImageView = normal.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[3] = new DescriptorImageInfo { ImageView = emission.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[4] = new DescriptorImageInfo { ImageView = orm.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = gpu.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &buffer,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = gpu.Set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = images,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = gpu.Set,
            DstBinding = 2,
            DescriptorCount = 4,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = images + 1,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
    }

    /// <summary>The pipeline for drawing <paramref name="gpu"/>'s surfaces (cached on the material).</summary>
    /// <param name="prepassed">A scene-pass draw of a surface the depth prepass drew (ADR 0163).</param>
    private PipelineEntry GetPipeline(MaterialGpu gpu, ShaderSetId shaders, bool mirrored, bool extraPass = false, bool streams = false,
        bool prepassed = false)
    {
        // Only the lit shaders (and their prepass) have a variant for the second stream (foliage always reads it; outlines
        // and ids never do).
        streams &= shaders is ShaderSetId.MeshLit or ShaderSetId.MeshDepth;
        ref var entry = ref gpu.Pipelines[MaterialGpu.PipelineIndex(shaders, extraPass, mirrored, streams, prepassed)];
        if (entry.Pipeline.Handle == 0)
        {
            var pass = shaders switch
            {
                ShaderSetId.MeshObjectId => _idPassPrototype,
                ShaderSetId.MeshDepth or ShaderSetId.MeshDepthFoliage => PrepassRenderPass,
                _ => _ctx.RenderPass,
            };
            entry = Pipelines.GetOrCreate(PipelineKey.ForMaterial(shaders, gpu.State, mirrored, pass, extraPass, streams, prepassed));
        }

        return entry;
    }

    // The renderer's depth prepass render pass (ADR 0163; created on first use).
    private RenderPass PrepassRenderPass => (_ctx as IPostProcessHost)?.PrepassRenderPass
        ?? throw new InvalidOperationException("The renderer has no depth prepass.");

    // ── Frame: build ───────────────────────────────────────────────────────────

    private void ResetStatsIfNewFrame()
    {
        if (_statsFrame == CurrentFrame)
            return;
        _statsFrame = CurrentFrame;
        Stats = default;
    }

    /// <summary>
    /// Culls and sorts <paramref name="world"/>'s geometry for <paramref name="camera"/> into <paramref name="view"/>
    /// (once per frame), collecting shadow casters when <paramref name="collectCasters"/>.
    /// </summary>
    public void Prepare(MeshViewDraws view, World3D world, ICamera camera, bool collectCasters)
    {
        if (view.PreparedFrame == CurrentFrame)
            return;
        ResetStatsIfNewFrame();
        view.PreparedFrame = CurrentFrame;
        view.Clear();
        view.Environment = world.Environment?.FrameEnvironment ?? default;

        var viewMatrix = camera.ViewMatrix;
        var frustum = new Frustum(viewMatrix * camera.ProjectionMatrix);
        var cameraPosition = camera.Position;
        // Camera forward in world space: -Z of the view matrix's rotation (row-vector convention: its third column).
        var forward = -new Vector3(viewMatrix.M13, viewMatrix.M23, viewMatrix.M33);

        var geometry = world.GeometryList;
        for (var n = 0; n < geometry.Count; n++)
        {
            var node = geometry[n];
            if (!node.IsVisibleInTree() || !Sync(node))
                continue;

            Stats.Instances++;
            var mesh = node.GpuMesh!;
            var model = node.ModelMatrix;
            var mirrored = Determinant3(model) < 0f;
            var materials = node.GpuMaterials;
            var surfaces = mesh.Surfaces;

            Aabb bounds;
            GpuBuffer? multiInstances = null;
            uint multiCount = 0;
            if (node is MultiMeshInstance3D multiNode)
            {
                if (SyncMultiMesh(multiNode, mesh, model) is not { } multi)
                    continue;
                bounds = multi.WorldBounds;
                multiInstances = multi.Instances;
                multiCount = multi.Count;
            }
            else
            {
                bounds = mesh.Bounds.Transform(model);
            }

            if (node.CustomAabb is { } custom)
                bounds = custom.Transform(model);

            if (!node.IsInVisibilityRange(cameraPosition, bounds))
            {
                Stats.OutOfRange++;
                // A coarse caster (a tree's coarsest level, an impostor) casts into the coarse passes from any distance.
                if (collectCasters && node.CastShadows && node.ShadowCasterLod == ShadowCasterLod.Coarse)
                    AddCasters(view, node, mesh, mirrored, bounds, multiInstances, multiCount, CoarsePasses);
                continue;
            }

            for (var s = 0; s < surfaces.Length; s++)
                if (materials[s] is { NeedsStreams: true })
                    mesh.EnsureStreams();

            if (collectCasters && node.CastShadows)
            {
                // In range, a coarse caster casts everywhere; out of range (above), only into the coarse passes.
                var passes = node.ShadowCasterLod == ShadowCasterLod.Fine ? FinePasses : (byte)(FinePasses | CoarsePasses);
                AddCasters(view, node, mesh, mirrored, bounds, multiInstances, multiCount, passes);
            }

            if (!frustum.Intersects(bounds))
            {
                Stats.Culled++;
                continue;
            }

            var depth = Vector3.Dot(bounds.Center - cameraPosition, forward);
            for (var s = 0; s < surfaces.Length; s++)
            {
                if (surfaces[s].IndexCount == 0 || materials[s] is not { } material)
                    continue;
                var pipeline = GetPipeline(material, material.ColorShaders, mirrored, streams: surfaces[s].HasStreams);
                var item = new MeshDrawItem
                {
                    Node = node,
                    Mesh = mesh,
                    Material = material,
                    Pipeline = pipeline,
                    Surface = s,
                    Mirrored = mirrored,
                    Instances = multiInstances,
                    InstanceCount = multiCount,
                };
                if (material.State.IsTransparent)
                    view.Transparent.Add(DrawSortKey.Transparent(material.RenderPriority, depth, pipeline.Id, material.Id), item);
                else
                    view.Opaque.Add(DrawSortKey.Opaque(pipeline.Id, material.Id, mesh.Id, s), item);
                Stats.SurfaceInstances++;
                Stats.MultiMeshInstances += (int)multiCount;
            }

            foreach (var extra in node.GpuExtraPasses)
            {
                if (surfaces[extra.Surface].IndexCount == 0)
                    continue;
                var material = extra.Material;
                if (material.NeedsStreams)
                    mesh.EnsureStreams();
                var pipeline = GetPipeline(material, material.ColorShaders, mirrored, extraPass: true, streams: surfaces[extra.Surface].HasStreams);
                var item = new MeshDrawItem
                {
                    Node = node,
                    Mesh = mesh,
                    Material = material,
                    Pipeline = pipeline,
                    Surface = extra.Surface,
                    Mirrored = mirrored,
                    Extra = true,
                    Instances = multiInstances,
                    InstanceCount = multiCount,
                };
                if (material.State.IsTransparent)
                    view.Transparent.Add(DrawSortKey.Transparent(material.RenderPriority, depth, pipeline.Id, material.Id), item);
                else
                    view.Opaque.Add(DrawSortKey.Opaque(pipeline.Id, material.Id, mesh.Id, extra.Surface), item);
                Stats.SurfaceInstances++;
            }
        }

        view.Opaque.Sort();
        view.Transparent.Sort();
        view.Casters.Sort();
    }

    /// <summary>Caster pass mask: the fine shadow passes (nearer cascades, atlas tiles, cubes).</summary>
    internal const byte FinePasses = 1;

    /// <summary>Caster pass mask: the coarse shadow passes (<see cref="ShadowPass.Coarse"/>: far cascades, the far shadow).</summary>
    internal const byte CoarsePasses = 2;

    // One caster item per shadow-casting surface of node.
    private static void AddCasters(MeshViewDraws view, GeometryInstance3D node, MeshGpu mesh, bool mirrored, in Aabb bounds,
        GpuBuffer? multiInstances, uint multiCount, byte passes)
    {
        var materials = node.GpuMaterials;
        var surfaces = mesh.Surfaces;
        var casts = false;
        for (var s = 0; s < surfaces.Length; s++)
        {
            if (surfaces[s].IndexCount == 0 || materials[s] is not { } m || !m.State.CastsShadows)
                continue;
            if (m.NeedsStreams)
                mesh.EnsureStreams();
            var cull = m.State.EffectiveCull;
            var foliage = m.FoliageCaster;
            var cutout = m.State.Alpha == AlphaMode.Cutout || foliage ? m : null;
            view.Casters.Add(CasterKey(cull, mirrored, cutout?.Id ?? 0, mesh.Id, s),
                new ShadowCasterItem
                {
                    Node = node,
                    Mesh = mesh,
                    Surface = s,
                    Cull = cull,
                    Mirrored = mirrored,
                    Cutout = cutout,
                    Foliage = foliage,
                    Bounds = bounds,
                    Instances = multiInstances,
                    InstanceCount = multiCount,
                    Passes = passes,
                });
            casts = true;
        }

        if (casts)
            view.CasterBounds = view.CasterBounds.Merge(bounds);
    }

    /// <summary>
    /// Caster sort key: <c>[cull 2][mirrored 1][cutout material 20][mesh 20][surface 12]</c>: opaque casters (material
    /// 0) first, so pipelines and material sets change as rarely as possible.
    /// </summary>
    internal static ulong CasterKey(CullMode cull, bool mirrored, int cutoutMaterialId, int meshId, int surface) =>
        ((ulong)cull & 0x3) << 53 | (mirrored ? 1ul << 52 : 0ul) | ((ulong)(uint)cutoutMaterialId & 0xFFFFF) << 32 |
        ((ulong)(uint)meshId & 0xFFFFF) << 12 | ((ulong)(uint)surface & 0xFFF);

    internal static float Determinant3(in Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) -
        m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) +
        m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    // ── Frame: record ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the view's colour-pass instances (opaque then transparent) once per frame; for a prepassed view (ADR 0163)
    /// followed by a block of the opaque instances' previous model matrices, read at the same instance index through
    /// binding 3 bound <see cref="MeshViewDraws.PreviousInstanceOffset"/> bytes further in.
    /// </summary>
    private void EnsureInstances(MeshViewDraws view)
    {
        if (view.InstancesFrame == CurrentFrame && view.InstanceBuffer is not null && (!view.Prepassed || view.HasPreviousInstances))
            return;
        view.InstancesFrame = CurrentFrame;
        view.HasPreviousInstances = false;
        var count = view.Opaque.Count + view.Transparent.Count;
        if (count == 0)
        {
            view.InstanceBuffer = null;
            return;
        }

        var previous = view.Prepassed ? view.Opaque.Count : 0;
        var data = _instances.Allocate(count + previous, out var buffer, out var first);
        view.InstanceBuffer = buffer;
        view.FirstInstance = first;
        var i = 0;
        for (var k = 0; k < view.Opaque.Count; k++)
        {
            ref var item = ref view.Opaque[k];
            data[i++] = new MeshInstanceData(item.Node.ModelMatrix, item.Node.ObjectId);
        }

        for (var k = 0; k < view.Transparent.Count; k++)
        {
            ref var item = ref view.Transparent[k];
            data[i++] = new MeshInstanceData(item.Node.ModelMatrix, item.Node.ObjectId);
        }

        if (previous == 0)
            return;
        var frame = _ctx.FrameNumber;
        for (var k = 0; k < view.Opaque.Count; k++)
        {
            ref var item = ref view.Opaque[k];
            data[i++] = new MeshInstanceData(item.Node.Motion.Previous(item.Node.ModelMatrix, frame), item.Node.ObjectId);
        }

        view.PreviousInstanceOffset = (ulong)count * MeshInstanceData.Size;
        view.HasPreviousInstances = true;
    }

    /// <summary>Records the view's opaque and cutout draws (inside a scene pass; the frame view must be current).</summary>
    public void DrawOpaque(MeshViewDraws view, CommandBuffer cb) => DrawList(view, view.Opaque, 0, cb, ShaderSetId.MeshLit);

    /// <summary>Records the view's blended draws, back to front.</summary>
    public void DrawTransparent(MeshViewDraws view, CommandBuffer cb) =>
        DrawList(view, view.Transparent, view.Opaque.Count, cb, ShaderSetId.MeshLit);

    /// <summary>Records every draw of the view into an object-ID pass (pipelines built for <see cref="ObjectIdTargetDesc"/>).</summary>
    public void DrawObjectIds(MeshViewDraws view, CommandBuffer cb)
    {
        DrawList(view, view.Opaque, 0, cb, ShaderSetId.MeshObjectId);
        DrawList(view, view.Transparent, view.Opaque.Count, cb, ShaderSetId.MeshObjectId);
    }

    /// <summary>
    /// Records the view's depth prepass (ADR 0163; inside the prepass render pass, the frame view current, the view
    /// <see cref="MeshViewDraws.Prepassed"/>): every opaque and cutout surface of a <see cref="MaterialGpu.Prepassable"/>
    /// material, with this frame's and last frame's model matrices (binding 3) for the velocity. Next passes and overlays
    /// are not prepassed.
    /// </summary>
    public void DrawPrepass(MeshViewDraws view, CommandBuffer cb)
    {
        var list = view.Opaque;
        if (list.Count == 0 || !view.Prepassed)
            return;
        ResetStatsIfNewFrame();
        EnsureInstances(view);
        var instanceBuffer = view.InstanceBuffer!;
        var vk = _ctx.Vk;
        var frame = _ctx.Frame;
        PipelineBuilder.SetViewport(vk, cb, frame.Extent, flipY: true);
        frame.Bind(cb, _pipelineLayout, _shadowDescriptors);

        var instanceHandle = instanceBuffer.Handle;
        var zero = 0ul;
        var previousOffset = view.PreviousInstanceOffset;
        vk.CmdBindVertexBuffers(cb, 1, 1, &instanceHandle, &zero);
        vk.CmdBindVertexBuffers(cb, VertexLayouts.PreviousInstanceBinding, 1, &instanceHandle, &previousOffset);
        var boundInstances = instanceHandle;

        Pipeline boundPipeline = default;
        MaterialGpu? boundMaterial = null;
        MeshGpu? boundMesh = null;
        var count = list.Count;
        var i = 0;
        while (i < count)
        {
            ref var item = ref list[i];
            var end = i + 1;
            while (end < count && SameDraw(ref item, ref list[end]))
                end++;

            var material = item.Material;
            if (item.Extra || !material.Prepassable)
            {
                i = end;
                continue;
            }

            var range = item.Mesh.Surfaces[item.Surface];
            var pipeline = GetPipeline(material, material.DepthShaders, item.Mirrored, streams: range.HasStreams).Pipeline;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
                Stats.PipelineBinds++;
            }

            if (!ReferenceEquals(material, boundMaterial))
            {
                // The shared layout's set 2 (a terrain splat material's default set: the prepass reads no splat data).
                var set = material.Set;
                vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 2, 1, &set, 0, null);
                boundMaterial = material;
                Stats.MaterialBinds++;
            }

            if (!ReferenceEquals(item.Mesh, boundMesh))
            {
                BindMesh(vk, cb, item.Mesh);
                boundMesh = item.Mesh;
            }

            // A multimesh draws its own instances, at binding 1 and (static: no previous transforms) binding 3.
            var instances = item.Instances?.Handle ?? instanceHandle;
            if (instances.Handle != boundInstances.Handle)
            {
                var previous = item.Instances is null ? previousOffset : 0ul;
                vk.CmdBindVertexBuffers(cb, 1, 1, &instances, &zero);
                vk.CmdBindVertexBuffers(cb, VertexLayouts.PreviousInstanceBinding, 1, &instances, &previous);
                boundInstances = instances;
            }

            if (item.Instances is not null)
                vk.CmdDrawIndexed(cb, range.IndexCount, item.InstanceCount, range.FirstIndex, range.VertexOffset, 0);
            else
                vk.CmdDrawIndexed(cb, range.IndexCount, (uint)(end - i), range.FirstIndex, range.VertexOffset, view.FirstInstance + (uint)i);
            Stats.PrepassDrawCalls++;
            i = end;
        }
    }

    private void DrawList(MeshViewDraws view, DrawList<MeshDrawItem> list, int instanceOffset, CommandBuffer cb, ShaderSetId shaders)
    {
        if (list.Count == 0)
            return;
        ResetStatsIfNewFrame();
        EnsureInstances(view);
        var instanceBuffer = view.InstanceBuffer!;
        var vk = _ctx.Vk;
        var frame = _ctx.Frame;
        PipelineBuilder.SetViewport(vk, cb, frame.Extent, flipY: true);
        frame.Bind(cb, _pipelineLayout, _shadowDescriptors);

        var instanceHandle = instanceBuffer.Handle;
        var zero = 0ul;
        vk.CmdBindVertexBuffers(cb, 1, 1, &instanceHandle, &zero);
        var boundInstances = instanceHandle;

        Pipeline boundPipeline = default;
        MaterialGpu? boundMaterial = null;
        MeshGpu? boundMesh = null;
        var count = list.Count;
        var i = 0;
        while (i < count)
        {
            ref var item = ref list[i];
            var end = i + 1;
            while (end < count && SameDraw(ref item, ref list[end]))
                end++;

            if (shaders == ShaderSetId.MeshObjectId && item.Extra)
            {
                i = end; // overlays and next passes are not pickable
                continue;
            }

            // ADR 0163: after a depth prepass its surfaces test the prepass depth and do not write it (cutouts: EQUAL, no discard).
            var pipeline = shaders == ShaderSetId.MeshObjectId ? GetPipeline(item.Material, shaders, item.Mirrored).Pipeline
                : view.Prepassed && !item.Extra && item.Material.Prepassable
                    ? GetPipeline(item.Material, item.Material.ColorShaders, item.Mirrored, streams: item.Mesh.Surfaces[item.Surface].HasStreams, prepassed: true).Pipeline
                    : item.Pipeline.Pipeline;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
                Stats.PipelineBinds++;
            }

            if (!ReferenceEquals(item.Material, boundMaterial))
            {
                // Terrain splat colour draws bind their own set 2 with their layout (sets 0 and 1 stay valid: same layouts).
                var splat = shaders != ShaderSetId.MeshObjectId && item.Material.ColorShaders == ShaderSetId.MeshTerrainSplat;
                var set = splat ? item.Material.Splat!.Set : item.Material.Set;
                vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, splat ? _splatPipelineLayout : _pipelineLayout, 2, 1, &set, 0, null);
                boundMaterial = item.Material;
                Stats.MaterialBinds++;
            }

            if (!ReferenceEquals(item.Mesh, boundMesh))
            {
                BindMesh(vk, cb, item.Mesh);
                boundMesh = item.Mesh;
            }

            // A multimesh draws its own instances; the view's buffer comes back for the next run.
            var instances = item.Instances?.Handle ?? instanceHandle;
            if (instances.Handle != boundInstances.Handle)
            {
                vk.CmdBindVertexBuffers(cb, 1, 1, &instances, &zero);
                boundInstances = instances;
            }

            var range = item.Mesh.Surfaces[item.Surface];
            if (item.Instances is not null)
                vk.CmdDrawIndexed(cb, range.IndexCount, item.InstanceCount, range.FirstIndex, range.VertexOffset, 0);
            else
                vk.CmdDrawIndexed(cb, range.IndexCount, (uint)(end - i), range.FirstIndex, range.VertexOffset,
                    view.FirstInstance + (uint)(instanceOffset + i));
            if (shaders == ShaderSetId.MeshObjectId)
                Stats.ObjectIdDrawCalls++;
            else
                Stats.DrawCalls++;
            i = end;
        }
    }

    // Binds a mesh's vertices (binding 0), its second stream when it has one (binding 2) and its indices.
    private static void BindMesh(Vk vk, CommandBuffer cb, MeshGpu mesh)
    {
        var zero = 0ul;
        var vertices = mesh.Vertices!.Handle;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vertices, &zero);
        if (mesh.Streams is { } streams)
        {
            var handle = streams.Handle;
            vk.CmdBindVertexBuffers(cb, 2, 1, &handle, &zero);
        }

        vk.CmdBindIndexBuffer(cb, mesh.Indices!.Handle, 0, IndexType.Uint32);
    }

    private static bool SameDraw(ref MeshDrawItem a, ref MeshDrawItem b) =>
        a.Instances is null && b.Instances is null && ReferenceEquals(a.Mesh, b.Mesh) && a.Surface == b.Surface && ReferenceEquals(a.Material, b.Material) &&
        a.Pipeline.Pipeline.Handle == b.Pipeline.Pipeline.Handle && a.Extra == b.Extra;

    /// <summary>
    /// Culls the view's casters against one shadow pass (its light frustum, and the light's sphere when it has a range)
    /// and writes the surviving instances, in sort order, as runs of equal (cull, mirrored, cutout material, mesh
    /// surface) for <see cref="DrawShadowCasters"/>. Returns false when nothing casts into the pass. Call for every
    /// pass of the frame before recording.
    /// </summary>
    public bool CullShadowCasters(MeshViewDraws view, in ShadowPass pass)
    {
        var frame = CurrentFrame;
        if (view.ShadowFrame != frame)
        {
            view.ShadowFrame = frame;
            view.ClearShadowRuns();
        }

        ResetStatsIfNewFrame();
        var casters = view.Casters;
        var count = casters.Count;
        view.PassRuns[pass.Index] = (view.ShadowRunCount, 0);
        if (count == 0)
            return false;

        if (_visibleCasters.Length < count)
            _visibleCasters = new int[Math.Max(count, _visibleCasters.Length * 2)];
        var visible = 0;
        var frustum = pass.Frustum;
        var sphere = pass.LightRange > 0f;
        var center = pass.LightPosition;
        var radius = pass.LightRange;
        var mask = pass.Coarse ? CoarsePasses : FinePasses;
        for (var k = 0; k < count; k++)
        {
            ref var item = ref casters[k];
            if ((item.Passes & mask) == 0)
                continue;
            if (sphere && !SphereIntersects(item.Bounds, center, radius))
                continue;
            if (frustum.Intersects(item.Bounds))
                _visibleCasters[visible++] = k;
        }

        Stats.ShadowCulled += count - visible;
        if (visible == 0)
            return false;

        // Multimesh casters draw their own buffer; the others get this pass's instances.
        var plain = 0;
        for (var v = 0; v < visible; v++)
            if (casters[_visibleCasters[v]].Instances is null)
                plain++;
        GpuBuffer? buffer = null;
        uint first = 0;
        var data = plain > 0 ? _instances.Allocate(plain, out buffer, out first) : default;

        var firstRun = view.ShadowRunCount;
        var written = 0;
        for (var v = 0; v < visible; v++)
        {
            ref var item = ref casters[_visibleCasters[v]];
            if (item.Instances is null)
            {
                data[written] = new MeshInstanceData(item.Node.ModelMatrix, item.Node.ObjectId);
                if (v > 0 && SameCaster(ref casters[_visibleCasters[v - 1]], ref item))
                {
                    view.ShadowRuns[view.ShadowRunCount - 1].InstanceCount++;
                    written++;
                    Stats.ShadowInstances++;
                    continue;
                }
            }

            if (view.ShadowRunCount == view.ShadowRuns.Length)
                Array.Resize(ref view.ShadowRuns, view.ShadowRuns.Length * 2);
            var own = item.Instances is not null;
            view.ShadowRuns[view.ShadowRunCount++] = new ShadowCasterRun
            {
                Mesh = item.Mesh,
                Cutout = item.Cutout,
                Foliage = item.Foliage,
                Instances = own ? item.Instances! : buffer!,
                Surface = item.Surface,
                Cull = item.Cull,
                Mirrored = item.Mirrored,
                FirstInstance = own ? 0 : first + (uint)written,
                InstanceCount = own ? item.InstanceCount : 1,
            };
            Stats.ShadowInstances += own ? (int)item.InstanceCount : 1;
            if (!own)
                written++;
        }

        view.PassRuns[pass.Index] = (firstRun, view.ShadowRunCount - firstRun);
        return true;
    }

    private static bool SphereIntersects(in Aabb box, Vector3 center, float radius)
    {
        var closest = Vector3.Clamp(center, box.Min, box.Max);
        return Vector3.DistanceSquared(closest, center) <= radius * radius;
    }

    /// <summary>
    /// Records the casters <see cref="CullShadowCasters"/> kept for <paramref name="pass"/> (inside its shadow render
    /// pass; the light matrix is bound by the shadow system). Cutout casters bind their material as set 1.
    /// </summary>
    public void DrawShadowCasters(MeshViewDraws view, CommandBuffer cb, in ShadowPass pass)
    {
        if (_shadows is null || view.ShadowFrame != CurrentFrame)
            return;
        var (firstRun, runCount) = view.PassRuns[pass.Index];
        if (runCount == 0)
            return;

        var vk = _ctx.Vk;
        var zero = 0ul;
        var point = pass.IsPoint;
        var lightPosRange = new Vector4(pass.LightPosition, pass.LightRange);
        if (point)
            vk.CmdPushConstants(cb, _shadows.ShadowPointLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 64, 16, &lightPosRange);

        Pipeline bound = default;
        MeshGpu? boundMesh = null;
        MaterialGpu? boundMaterial = null;
        GpuBuffer? boundInstances = null;
        var windPushed = false;
        for (var r = firstRun; r < firstRun + runCount; r++)
        {
            ref var run = ref view.ShadowRuns[r];
            if (run.Foliage && !windPushed)
            {
                // The casters' set 0 is the light matrix: the wind and time come as push constants (offset 0, unused by
                // the instanced casters, whose model matrices are per instance).
                var wind = new FoliageCasterWind(view.Environment, _ctx.Frame.Time);
                vk.CmdPushConstants(cb, point ? _shadows.ShadowPointLayout : _shadows.Shadow2DLayout,
                    point ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit : ShaderStageFlags.VertexBit, 0,
                    (uint)sizeof(FoliageCasterWind), &wind);
                windPushed = true;
            }

            var pipeline = _shadows.GetInstancedCasterPipeline(point, run.Cull, run.Mirrored, run.Cutout is not null, run.Foliage);
            if (pipeline.Handle != bound.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                bound = pipeline;
            }

            if (run.Cutout is { } material && !ReferenceEquals(material, boundMaterial))
            {
                // Set 0 (the light VP) stays bound: the cutout layouts share it and the push range.
                var layout = _shadows.CutoutLayout(point);
                var set = material.Set;
                vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 1, 1, &set, 0, null);
                boundMaterial = material;
            }

            if (!ReferenceEquals(run.Instances, boundInstances))
            {
                var instances = run.Instances.Handle;
                vk.CmdBindVertexBuffers(cb, 1, 1, &instances, &zero);
                boundInstances = run.Instances;
            }

            if (!ReferenceEquals(run.Mesh, boundMesh))
            {
                BindMesh(vk, cb, run.Mesh);
                boundMesh = run.Mesh;
            }

            var range = run.Mesh.Surfaces[run.Surface];
            vk.CmdDrawIndexed(cb, range.IndexCount, run.InstanceCount, range.FirstIndex, range.VertexOffset, run.FirstInstance);
            Stats.ShadowDrawCalls++;
        }
    }

    private static bool SameCaster(ref ShadowCasterItem a, ref ShadowCasterItem b) =>
        a.Instances is null && b.Instances is null && ReferenceEquals(a.Mesh, b.Mesh) && a.Surface == b.Surface && a.Cull == b.Cull && a.Mirrored == b.Mirrored &&
        ReferenceEquals(a.Cutout, b.Cutout);

    // ── IPipelineFactory ───────────────────────────────────────────────────────

    Pipeline IPipelineFactory.Create(in PipelineKey key)
    {
        if (key.IsDepthPrepass)
            return CreatePrepassPipeline(key);

        var state = new PipelineState
        {
            CullMode = key.Cull switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Disabled => CullModeFlags.None,
                _ => CullModeFlags.BackBit,
            },
            // The main pass flips Y (negative viewport height), which keeps counter-clockwise = front; mirrored
            // instances (negative determinant) reverse the winding.
            FrontFace = key.Mirrored ? FrontFace.Clockwise : FrontFace.CounterClockwise,
            DepthTest = true,
            DepthWrite = key.DepthWrite,
            // ADR 0163: a prepassed surface lands on its own prepass depth; a cutout only where the prepass kept it.
            DepthCompare = key.Prepassed ? key.Alpha == AlphaMode.Cutout ? CompareOp.Equal : CompareOp.LessOrEqual
                : key.ExtraPass ? CompareOp.LessOrEqual : CompareOp.Less,
            Blend = key.Alpha != AlphaMode.Blend || key.Shaders == ShaderSetId.MeshObjectId ? BlendMode.Opaque
                // ADR 0166: water in the main scene pass marks the scene alpha (TAA's reactive mask); sub-viewports keep theirs.
                : key.Shaders == ShaderSetId.MeshWater && key.RenderPass == _ctx.RenderPass.Handle ? BlendMode.AlphaReactive
                : BlendMode.Alpha,
        };

        // Constant 0: the alpha mode (a prepassed cutout runs as opaque: the EQUAL depth test replaces its discard, and
        // keeps early depth testing on); 1: multiply the albedo by the vertex colour (Mesh.vk.frag, second-stream surfaces).
        var streams = key.VertexLayout == VertexLayoutId.MeshInstancedExt;
        var alphaMode = key.Prepassed && key.Alpha == AlphaMode.Cutout ? AlphaMode.Opaque : key.Alpha;
        var constants = stackalloc int[2] { (int)alphaMode, streams ? 1 : 0 };
        var entries = stackalloc SpecializationMapEntry[2]
        {
            new() { ConstantID = 0, Offset = 0, Size = sizeof(int) },
            new() { ConstantID = 1, Offset = sizeof(int), Size = sizeof(int) },
        };
        var specialization = new SpecializationInfo { MapEntryCount = 2, PMapEntries = entries, DataSize = 2 * sizeof(int), PData = constants };
        var fragment = key.Shaders switch
        {
            ShaderSetId.MeshObjectId => "Shaders/Mesh/MeshId.vk.frag.spv",
            ShaderSetId.MeshFoliage => "Shaders/Foliage/Foliage.vk.frag.spv",
            ShaderSetId.MeshWater => "Shaders/Water/Water.vk.frag.spv",
            ShaderSetId.MeshTerrainSplat => "Shaders/Terrain/TerrainSplat.vk.frag.spv",
            _ => "Shaders/Mesh/Mesh.vk.frag.spv",
        };
        // Each vertex shader writes only what its fragment shader reads (Slang drops unread fragment inputs, and an
        // unread vertex output is a validation warning): the ID pass has its own.
        var vertex = key.Shaders switch
        {
            ShaderSetId.MeshOutline => "Shaders/Mesh/MeshOutline.vk.vert.spv",
            ShaderSetId.MeshObjectId => "Shaders/Mesh/MeshId.vk.vert.spv",
            ShaderSetId.MeshFoliage => "Shaders/Foliage/Foliage.vk.vert.spv",
            ShaderSetId.MeshWater => "Shaders/Water/Water.vk.vert.spv",
            _ when streams => "Shaders/Mesh/MeshExt.vk.vert.spv",
            _ => "Shaders/Mesh/Mesh.vk.vert.spv",
        };
        var attributes = key.Shaders switch
        {
            ShaderSetId.MeshObjectId => VertexLayouts.MeshIdAttributes,
            ShaderSetId.MeshFoliage => VertexLayouts.FoliageAttributes,
            ShaderSetId.MeshWater => VertexLayouts.WaterAttributes,
            _ when streams => VertexLayouts.MeshExtAttributes,
            _ => VertexLayouts.MeshAttributes,
        };
        return PipelineBuilder.Create(_ctx, state, LayoutFor(key.Shaders), new RenderPass(key.RenderPass),
            vertex, fragment, streams ? VertexLayouts.MeshInstancedExtBindings : VertexLayouts.MeshInstancedBindings, attributes,
            $"mesh ({key.Shaders}, {key.Alpha}, cull {key.Cull}{(key.Mirrored ? ", mirrored" : "")}{(key.ExtraPass ? ", extra pass" : "")}{(streams ? ", streams" : "")}{(key.Prepassed ? ", prepassed" : "")})",
            &specialization);
    }

    // The depth prepass (ADR 0163): the material's culling and winding, depth LESS with writes, the velocity attachment;
    // constants 0: the alpha mode (cutouts alpha-test), 1: the vertex colour's alpha multiplies the albedo's.
    private Pipeline CreatePrepassPipeline(in PipelineKey key)
    {
        var state = new PipelineState
        {
            CullMode = key.Cull switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Disabled => CullModeFlags.None,
                _ => CullModeFlags.BackBit,
            },
            FrontFace = key.Mirrored ? FrontFace.Clockwise : FrontFace.CounterClockwise,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = CompareOp.Less,
            Blend = BlendMode.Opaque,
        };

        var foliage = key.Shaders == ShaderSetId.MeshDepthFoliage;
        var streams = key.VertexLayout == VertexLayoutId.MeshInstancedExt;
        var constants = stackalloc int[2] { (int)key.Alpha, streams ? 1 : 0 };
        var entries = stackalloc SpecializationMapEntry[2]
        {
            new() { ConstantID = 0, Offset = 0, Size = sizeof(int) },
            new() { ConstantID = 1, Offset = sizeof(int), Size = sizeof(int) },
        };
        var specialization = new SpecializationInfo { MapEntryCount = 2, PMapEntries = entries, DataSize = 2 * sizeof(int), PData = constants };
        var vertex = foliage ? "Shaders/Foliage/FoliageDepth.vk.vert.spv"
            : streams ? "Shaders/Mesh/MeshDepthExt.vk.vert.spv"
            : "Shaders/Mesh/MeshDepth.vk.vert.spv";
        var attributes = foliage ? VertexLayouts.DepthFoliageAttributes : streams ? VertexLayouts.DepthExtAttributes : VertexLayouts.DepthAttributes;
        return PipelineBuilder.Create(_ctx, state, _pipelineLayout, new RenderPass(key.RenderPass), vertex, "Shaders/Mesh/MeshDepth.vk.frag.spv",
            streams ? VertexLayouts.DepthExtBindings : VertexLayouts.DepthBindings, attributes,
            $"mesh prepass ({key.Shaders}, {key.Alpha}, cull {key.Cull}{(key.Mirrored ? ", mirrored" : "")}{(streams ? ", streams" : "")})",
            &specialization);
    }

    void IPipelineFactory.Destroy(Pipeline pipeline) => _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));

    /// <summary>
    /// Push constants of the foliage shadow casters (offset 0, 48 bytes; <c>Shadows/Shadow*FoliageInstanced.vk.vert</c>):
    /// the frame's wind and time, which the caster layouts cannot read from set 0.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FoliageCasterWind(in FrameEnvironment environment, float time)
    {
        public Vector4 Wind = environment.Wind;
        public Vector4 WindParams = environment.WindParams;
        public Vector4 Time = new(time, 0f, 0f, 0f);
    }

    /// <summary>Releases every GPU object (nodes still holding references are reset by the render server).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var mesh in _meshes.Values)
            mesh.Release();
        _meshes.Clear();
        foreach (var multi in _multiMeshes)
            multi.Invalidate();
        _multiMeshes.Clear();
        foreach (var material in _materials.Values)
        {
            material.Params?.Dispose();
            material.Params = null;
            material.Splat?.Params?.Dispose();
            material.Splat?.DisposeArrays();
        }

        _materials.Clear();
        foreach (var texture in _textures.Values)
            texture.Release();
        _textures.Clear();
        _instances.Dispose();
        _materialSets.Dispose();
        Pipelines.Dispose();
        _whiteSrgb.Dispose();
        _flatNormal.Dispose();
        _whiteLinear.Dispose();
        DisposeSplatResources();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_materialLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_idPassPrototype));
    }
}
