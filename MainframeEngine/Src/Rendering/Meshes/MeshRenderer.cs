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

    /// <summary>Draws in object-ID passes.</summary>
    public int ObjectIdDrawCalls;
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
}

/// <summary>One surface of one shadow caster.</summary>
internal struct ShadowCasterItem
{
    public GeometryInstance3D Node;
    public MeshGpu Mesh;
    public int Surface;
    public CullMode Cull;
    public bool Mirrored;
}

/// <summary>The sorted draws of one view (main viewport, an offscreen view) for the current frame.</summary>
internal sealed class MeshViewDraws
{
    public readonly DrawList<MeshDrawItem> Opaque = new(256);
    public readonly DrawList<MeshDrawItem> Transparent = new(64);
    public readonly DrawList<ShadowCasterItem> Casters = new(256);

    public ulong PreparedFrame;
    public ulong InstancesFrame;
    public ulong CastersFrame;
    public GpuBuffer? InstanceBuffer;
    public uint FirstInstance;
    public GpuBuffer? CasterBuffer;
    public uint FirstCaster;

    public void Clear()
    {
        Opaque.Clear();
        Transparent.Clear();
        Casters.Clear();
        InstanceBuffer = null;
        CasterBuffer = null;
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
internal sealed unsafe class MeshRenderer : IDisposable, IPipelineFactory
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
    private readonly InstanceBuffer _instances;
    private readonly Dictionary<Mesh, MeshGpu> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Material, MaterialGpu> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Texture2D, TextureColorSpace), TextureGpu> _textures = [];
    private int _nextMeshId = 1, _nextMaterialId = 1;
    private ulong _statsFrame;
    private bool _warnedUnsupportedMaterial;
    private bool _disposed;

    public MeshRenderer(IVulkanContext ctx, ShadowSystem? shadows)
    {
        _ctx = ctx;
        _shadows = shadows;
        _shadowDescriptors = ShadowFallback.Resolve(shadows, ctx);

        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        _materialLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "material set 2");
        _pipelineLayout = ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [_materialLayout], "mesh");
        _idPassPrototype = RenderTarget.CreateRenderPass(ctx, ObjectIdTargetDesc(FindDepthFormat(ctx)));
        _materialSets = new MaterialDescriptorAllocator(ctx, _materialLayout);
        Pipelines = new PipelineStateCache(this);
        _instances = new InstanceBuffer(ctx);

        // Fallbacks bound in empty texture slots (the shader ignores them unless its flag is set; the sampler of a
        // material without textures comes from the white texture: linear, repeat).
        ReadOnlySpan<byte> white = [255, 255, 255, 255];
        ReadOnlySpan<byte> flat = [128, 128, 255, 255];
        _whiteSrgb = GpuTexture.Create2D(ctx, 1, 1, white, TextureColorSpace.Srgb, TextureSampling.LinearRepeat);
        _flatNormal = GpuTexture.Create2D(ctx, 1, 1, flat, TextureColorSpace.Linear, TextureSampling.LinearRepeat);
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
    private ulong CurrentFrame => _ctx.FrameStarted ? _ctx.FrameNumber : _ctx.FrameNumber + 1;

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
            node.ResolvedStamp != node.RenderStamp || node.GpuMaterials.Length != gpuMesh.Surfaces.Length)
            ResolveMaterials(node, mesh!, gpuMesh);

        foreach (var material in node.GpuMaterials)
            if (material is not null && material.PreparedFrame != frame)
                PrepareMaterial(material);
        return true;
    }

    private void ResolveMaterials(GeometryInstance3D node, Mesh mesh, MeshGpu gpuMesh)
    {
        // Acquire the new references before releasing the old ones, so shared materials are not torn down.
        var previous = node.GpuMaterials;
        var resolved = new MaterialGpu?[gpuMesh.Surfaces.Length];
        for (var s = 0; s < resolved.Length; s++)
            resolved[s] = AcquireMaterial(node.GetRenderMaterial(mesh, s));
        foreach (var material in previous)
            if (material is not null)
                ReleaseMaterial(material);
        node.GpuMaterials = resolved;
        node.ResolvedMeshGeneration = gpuMesh.Generation;
        node.ResolvedOverride = node.MaterialOverride;
        node.ResolvedStamp = node.RenderStamp;
    }

    /// <summary>Drops the node's references (it was freed, or its mesh changed).</summary>
    public void ReleaseGeometry(GeometryInstance3D node)
    {
        foreach (var material in node.GpuMaterials)
            if (material is not null)
                ReleaseMaterial(material);
        node.GpuMaterials = [];
        if (node.GpuMesh is { } mesh)
            ReleaseMesh(mesh);
        node.GpuMesh = null;
        node.ResolvedMesh = null;
        node.ResolvedMeshGeneration = -1;
        node.ResolvedOverride = null;
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
        gpu.Albedo = gpu.Normal = gpu.Emission = null;
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
        if (material is not StandardMaterial3D standard)
        {
            if (!_warnedUnsupportedMaterial)
            {
                _warnedUnsupportedMaterial = true;
                Log.Warning($"[Mesh] {material.GetType().Name} is not supported by the renderer yet; drawing with the default material.");
            }

            standard = StandardMaterial3D.Default;
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
            rewrite |= SwapTexture(standard.AlbedoTexture, colorUsage: true, ref gpu.Albedo);
            rewrite |= SwapTexture(standard.NormalTexture, colorUsage: false, ref gpu.Normal);
            rewrite |= SwapTexture(standard.EmissionTexture, colorUsage: true, ref gpu.Emission);
        }

        // Textures re-uploaded (reimport, new import settings) since the set was written.
        rewrite |= RefreshTexture(gpu.Albedo, gpu.AlbedoGeneration);
        rewrite |= RefreshTexture(gpu.Normal, gpu.NormalGeneration);
        rewrite |= RefreshTexture(gpu.Emission, gpu.EmissionGeneration);

        if (changed || rewrite)
        {
            var flags = (gpu.Albedo?.Gpu is not null ? MaterialParams.HasAlbedo : 0) |
                        (gpu.Normal?.Gpu is not null ? MaterialParams.HasNormal : 0) |
                        (gpu.Emission?.Gpu is not null ? MaterialParams.HasEmission : 0);
            var parameters = MaterialParams.From(standard, flags);
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
        gpu.AlbedoGeneration = gpu.Albedo?.Generation ?? 0;
        gpu.NormalGeneration = gpu.Normal?.Generation ?? 0;
        gpu.EmissionGeneration = gpu.Emission?.Generation ?? 0;
        // One sampler for every texture of the material (MoltenVK's sampler budget): the first texture's.
        var sampler = (gpu.Albedo?.Gpu ?? gpu.Normal?.Gpu ?? gpu.Emission?.Gpu ?? _whiteSrgb).Sampler;

        var buffer = gpu.Params!.Descriptor(0, MaterialParams.Size);
        var images = stackalloc DescriptorImageInfo[4];
        images[0] = new DescriptorImageInfo { Sampler = sampler };
        images[1] = new DescriptorImageInfo { ImageView = albedo.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[2] = new DescriptorImageInfo { ImageView = normal.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[3] = new DescriptorImageInfo { ImageView = emission.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

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
            DescriptorCount = 3,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = images + 1,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
    }

    /// <summary>The pipeline for drawing <paramref name="gpu"/>'s surfaces (cached on the material).</summary>
    private PipelineEntry GetPipeline(MaterialGpu gpu, ShaderSetId shaders, bool mirrored)
    {
        ref var entry = ref gpu.Pipelines[(int)shaders * 2 + (mirrored ? 1 : 0)];
        if (entry.Pipeline.Handle == 0)
        {
            var pass = shaders == ShaderSetId.MeshObjectId ? _idPassPrototype : _ctx.RenderPass;
            entry = Pipelines.GetOrCreate(PipelineKey.ForMaterial(shaders, gpu.State, mirrored, pass));
        }

        return entry;
    }

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

            if (collectCasters && node.CastShadows)
            {
                for (var s = 0; s < surfaces.Length; s++)
                {
                    if (surfaces[s].IndexCount == 0 || materials[s] is not { } m || !m.State.CastsShadows)
                        continue;
                    var cull = m.State.EffectiveCull;
                    view.Casters.Add(CasterKey(cull, mirrored, mesh.Id, s),
                        new ShadowCasterItem { Node = node, Mesh = mesh, Surface = s, Cull = cull, Mirrored = mirrored });
                }
            }

            var bounds = mesh.Bounds.Transform(model);
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
                var pipeline = GetPipeline(material, ShaderSetId.MeshLit, mirrored);
                var item = new MeshDrawItem
                {
                    Node = node,
                    Mesh = mesh,
                    Material = material,
                    Pipeline = pipeline,
                    Surface = s,
                    Mirrored = mirrored,
                };
                if (material.State.IsTransparent)
                    view.Transparent.Add(DrawSortKey.Transparent(material.RenderPriority, depth, pipeline.Id, material.Id), item);
                else
                    view.Opaque.Add(DrawSortKey.Opaque(pipeline.Id, material.Id, mesh.Id, s), item);
                Stats.SurfaceInstances++;
            }
        }

        view.Opaque.Sort();
        view.Transparent.Sort();
        view.Casters.Sort();
    }

    internal static ulong CasterKey(CullMode cull, bool mirrored, int meshId, int surface) =>
        (ulong)cull << 40 | (mirrored ? 1ul << 39 : 0ul) | ((ulong)(uint)meshId & 0xFFFFFF) << 12 | ((ulong)(uint)surface & 0xFFF);

    internal static float Determinant3(in Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) -
        m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) +
        m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    // ── Frame: record ──────────────────────────────────────────────────────────

    /// <summary>Writes the view's colour-pass instances (opaque then transparent) once per frame.</summary>
    private void EnsureInstances(MeshViewDraws view)
    {
        if (view.InstancesFrame == CurrentFrame && view.InstanceBuffer is not null)
            return;
        view.InstancesFrame = CurrentFrame;
        var count = view.Opaque.Count + view.Transparent.Count;
        if (count == 0)
        {
            view.InstanceBuffer = null;
            return;
        }

        var data = _instances.Allocate(count, out var buffer, out var first);
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

            var pipeline = shaders == ShaderSetId.MeshLit ? item.Pipeline.Pipeline : GetPipeline(item.Material, shaders, item.Mirrored).Pipeline;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
                Stats.PipelineBinds++;
            }

            if (!ReferenceEquals(item.Material, boundMaterial))
            {
                var set = item.Material.Set;
                vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 2, 1, &set, 0, null);
                boundMaterial = item.Material;
                Stats.MaterialBinds++;
            }

            if (!ReferenceEquals(item.Mesh, boundMesh))
            {
                var vertices = item.Mesh.Vertices!.Handle;
                vk.CmdBindVertexBuffers(cb, 0, 1, &vertices, &zero);
                vk.CmdBindIndexBuffer(cb, item.Mesh.Indices!.Handle, 0, IndexType.Uint32);
                boundMesh = item.Mesh;
            }

            var range = item.Mesh.Surfaces[item.Surface];
            vk.CmdDrawIndexed(cb, range.IndexCount, (uint)(end - i), range.FirstIndex, range.VertexOffset,
                view.FirstInstance + (uint)(instanceOffset + i));
            if (shaders == ShaderSetId.MeshObjectId)
                Stats.ObjectIdDrawCalls++;
            else
                Stats.DrawCalls++;
            i = end;
        }
    }

    private static bool SameDraw(ref MeshDrawItem a, ref MeshDrawItem b) =>
        ReferenceEquals(a.Mesh, b.Mesh) && a.Surface == b.Surface && ReferenceEquals(a.Material, b.Material) &&
        a.Pipeline.Pipeline.Handle == b.Pipeline.Pipeline.Handle;

    /// <summary>Writes the casters' instances once per frame (call before the shadow passes).</summary>
    public void PrepareShadowCasters(MeshViewDraws view)
    {
        if (view.CastersFrame == CurrentFrame)
            return;
        view.CastersFrame = CurrentFrame;
        view.CasterBuffer = null;
        if (view.Casters.Count == 0)
            return;
        var data = _instances.Allocate(view.Casters.Count, out var buffer, out var first);
        view.CasterBuffer = buffer;
        view.FirstCaster = first;
        for (var k = 0; k < view.Casters.Count; k++)
        {
            ref var item = ref view.Casters[k];
            data[k] = new MeshInstanceData(item.Node.ModelMatrix, item.Node.ObjectId);
        }
    }

    /// <summary>
    /// Records the view's shadow casters into the current shadow sub-pass (light matrix bound by the shadow system).
    /// <paramref name="point"/> selects the cube-face pipelines, which take the light position and range.
    /// </summary>
    public void DrawShadowCasters(MeshViewDraws view, CommandBuffer cb, bool point, Vector3 lightPosition, float lightRange)
    {
        if (_shadows is null || view.CasterBuffer is null || view.Casters.Count == 0)
            return;
        var vk = _ctx.Vk;
        var zero = 0ul;
        var instances = view.CasterBuffer.Handle;
        vk.CmdBindVertexBuffers(cb, 1, 1, &instances, &zero);
        if (point)
        {
            var lightPosRange = new Vector4(lightPosition, lightRange);
            vk.CmdPushConstants(cb, _shadows.ShadowPointLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 64, 16, &lightPosRange);
        }

        Pipeline bound = default;
        MeshGpu? boundMesh = null;
        var count = view.Casters.Count;
        var i = 0;
        while (i < count)
        {
            ref var item = ref view.Casters[i];
            var end = i + 1;
            while (end < count && SameCaster(ref item, ref view.Casters[end]))
                end++;

            var pipeline = _shadows.GetInstancedCasterPipeline(point, item.Cull, item.Mirrored);
            if (pipeline.Handle != bound.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                bound = pipeline;
            }

            if (!ReferenceEquals(item.Mesh, boundMesh))
            {
                var vertices = item.Mesh.Vertices!.Handle;
                vk.CmdBindVertexBuffers(cb, 0, 1, &vertices, &zero);
                vk.CmdBindIndexBuffer(cb, item.Mesh.Indices!.Handle, 0, IndexType.Uint32);
                boundMesh = item.Mesh;
            }

            var range = item.Mesh.Surfaces[item.Surface];
            vk.CmdDrawIndexed(cb, range.IndexCount, (uint)(end - i), range.FirstIndex, range.VertexOffset, view.FirstCaster + (uint)i);
            Stats.ShadowDrawCalls++;
            i = end;
        }
    }

    private static bool SameCaster(ref ShadowCasterItem a, ref ShadowCasterItem b) =>
        ReferenceEquals(a.Mesh, b.Mesh) && a.Surface == b.Surface && a.Cull == b.Cull && a.Mirrored == b.Mirrored;

    // ── IPipelineFactory ───────────────────────────────────────────────────────

    Pipeline IPipelineFactory.Create(in PipelineKey key)
    {
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
            DepthCompare = CompareOp.Less,
            Blend = key.Alpha == AlphaMode.Blend && key.Shaders == ShaderSetId.MeshLit ? BlendMode.Alpha : BlendMode.Opaque,
        };

        var alphaMode = (int)key.Alpha;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(int) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(int), PData = &alphaMode };
        var fragment = key.Shaders == ShaderSetId.MeshObjectId ? "Shaders/Mesh/MeshId.vk.frag.spv" : "Shaders/Mesh/Mesh.vk.frag.spv";
        return PipelineBuilder.Create(_ctx, state, _pipelineLayout, new RenderPass(key.RenderPass),
            "Shaders/Mesh/Mesh.vk.vert.spv", fragment, VertexLayouts.MeshInstancedBindings, VertexLayouts.MeshInstancedAttributes,
            $"mesh ({key.Shaders}, {key.Alpha}, cull {key.Cull}{(key.Mirrored ? ", mirrored" : "")})", &specialization);
    }

    void IPipelineFactory.Destroy(Pipeline pipeline) => _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));

    /// <summary>Releases every GPU object (nodes still holding references are reset by the render server).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var mesh in _meshes.Values)
            mesh.Release();
        _meshes.Clear();
        foreach (var material in _materials.Values)
        {
            material.Params?.Dispose();
            material.Params = null;
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
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_materialLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_idPassPrototype));
    }
}
