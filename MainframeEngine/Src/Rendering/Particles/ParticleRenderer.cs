using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>What one <see cref="GpuParticles3D"/> holds in the <see cref="ParticleRenderer"/>: its draw pass mesh, materials and scratch arrays.</summary>
internal sealed class ParticleDrawState
{
    public Mesh? Mesh;
    public ParticleRenderer.MeshEntry? MeshEntry;
    public ParticleRenderer.MaterialEntry?[] Materials = [];

    /// <summary>Whether a surface blends (mix or add): the server draws the emitter after the blended meshes.</summary>
    public bool Blended;

    /// <summary>Whether a surface mixes (the only blending that needs back-to-front order).</summary>
    public bool Mixed;

    public int[] Order = [];
    public float[] Keys = [];
}

/// <summary>The push constants of <c>Particles/Particle.vk.*</c> (<c>include/particle_common.slang</c>, 128 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParticlePush
{
    public Matrix4x4 Model;
    public Vector4 Albedo;
    public Vector4 Emission;
    public Vector4 UvTransform;
    public uint Flags;
    public uint Shading;
    public uint Alpha;
    public uint Blend;

    public const uint HasAlbedo = 1, HasEmission = 2, VertexColor = 4, AlignY = 8, DisableZ = 16;
    public const int BillboardShift = 8;
}

/// <summary>
/// The GPU side of <see cref="GpuParticles3D"/> (ADR 0206), one per render server and separate from the mesh batcher:
/// draw pass meshes (<see cref="MeshGpu"/>) and <see cref="StandardMaterial3D"/>s (set 2: one sampler, albedo and emission
/// images; the parameters are push constants), reference-counted by the emitters; one per-frame-slot instance ring; and
/// pipelines per blend mode, culling and view (the main view's blended passes mark TAA's reactive mask in the scene alpha,
/// like water). Every emitter is one instanced draw per draw-pass surface, recorded in the scene pass after the blended
/// meshes (or after the opaque ones when nothing blends). Resources are prepared from the emitters' <c>OnProcess</c>,
/// before the frame records; draws allocate nothing.
/// </summary>
internal sealed unsafe class ParticleRenderer : IDisposable
{
    private const uint MaterialSetIndex = 2; // set 0 frame, set 1 shadows, set 2 material images

    /// <summary>The draw pass of an emitter without one: a 1×1 quad facing +Z.</summary>
    internal static readonly QuadMesh DefaultQuad = new() { ResourceName = "Default particle quad" };

    /// <summary>The material of a draw pass without a <see cref="StandardMaterial3D"/>: unshaded, mixed, the particle colour as albedo.</summary>
    internal static readonly StandardMaterial3D DefaultMaterial = new()
    {
        ResourceName = "Default particle material",
        ShadingMode = ShadingMode.Unshaded,
        Transparency = AlphaMode.Blend,
        VertexColorUseAsAlbedo = true,
        BillboardMode = BillboardMode.Particles,
    };

    internal sealed class MeshEntry(MeshGpu gpu)
    {
        public MeshGpu Gpu { get; } = gpu;
        public int RefCount;
        public ulong PreparedFrame = ulong.MaxValue;
    }

    internal sealed class MaterialEntry(StandardMaterial3D material)
    {
        public StandardMaterial3D Material { get; } = material;
        public int RefCount;
        public int Version = -1;
        public ulong PreparedFrame = ulong.MaxValue;
        public TextureGpu? Albedo;
        public TextureGpu? Emission;
        public int AlbedoGeneration = -1;
        public int EmissionGeneration = -1;
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public ParticlePush Push;
        public bool Blended;
        public bool Mixed;
    }

    private readonly IVulkanContext _ctx;
    private readonly IShadowDescriptors _shadows;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _layout;
    private readonly MaterialDescriptorAllocator _sets;
    private readonly GpuTexture _white;
    private readonly ParticleInstanceRing _instances;
    private readonly Dictionary<int, Pipeline> _pipelines = [];
    private readonly Dictionary<Mesh, MeshEntry> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StandardMaterial3D, MaterialEntry> _materials = new(ReferenceEqualityComparer.Instance);
    private int _nextMeshId;
    private bool _disposed;

    public ParticleRenderer(IVulkanContext ctx, ShadowSystem? shadows)
    {
        _ctx = ctx;
        _shadows = ShadowFallback.Resolve(shadows, ctx);
        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        _setLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "particle material set 2");
        _layout = ctx.Frame.CreatePipelineLayout(_shadows, [_setLayout], "particles");
        _sets = new MaterialDescriptorAllocator(ctx, _setLayout, samplers: 1, images: 2);
        _white = GpuTexture.Create2D(ctx, 1, 1, [255, 255, 255, 255], TextureColorSpace.Srgb, TextureSampling.LinearClamp);
        _instances = new ParticleInstanceRing(ctx);
    }

    /// <summary>Instances written this frame (tests, diagnostics).</summary>
    public int InstancesThisFrame => _instances.UsedThisFrame;

    /// <summary>Draw pass meshes and materials resident (tests).</summary>
    public (int Meshes, int Materials) Resident => (_meshes.Count, _materials.Count);

    /// <summary>
    /// Resolves an emitter's draw pass (<paramref name="mesh"/>, else <see cref="DefaultQuad"/>; each surface's material or
    /// <paramref name="materialOverride"/>, else <see cref="DefaultMaterial"/>) and uploads what changed. Call before the
    /// frame records (the emitter's <c>OnProcess</c>): uploads then join the frame. Allocates only when something changes.
    /// </summary>
    public void Prepare(ParticleDrawState state, Mesh? mesh, Material? materialOverride, int amount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        mesh ??= DefaultQuad;
        if (!ReferenceEquals(state.Mesh, mesh))
        {
            ReleaseMesh(state.MeshEntry);
            state.Mesh = mesh;
            state.MeshEntry = AcquireMesh(mesh);
        }

        var entry = state.MeshEntry!;
        if (entry.PreparedFrame != _ctx.FrameNumber)
        {
            entry.PreparedFrame = _ctx.FrameNumber;
            entry.Gpu.HasGeometry = entry.Gpu.Update();
        }

        var surfaces = Math.Max(mesh.SurfaceCount, 0);
        if (state.Materials.Length != surfaces)
        {
            foreach (var old in state.Materials)
                ReleaseMaterial(old);
            state.Materials = new MaterialEntry?[surfaces];
        }

        state.Blended = state.Mixed = false;
        for (var s = 0; s < surfaces; s++)
        {
            var material = (materialOverride ?? mesh.GetSurfaceMaterial(s)) as StandardMaterial3D ?? DefaultMaterial;
            var current = state.Materials[s];
            if (current is null || !ReferenceEquals(current.Material, material))
            {
                ReleaseMaterial(current);
                current = state.Materials[s] = AcquireMaterial(material);
            }

            PrepareMaterial(current);
            state.Blended |= current.Blended;
            state.Mixed |= current.Mixed;
        }

        if (state.Order.Length != amount && amount >= 0)
        {
            state.Order = new int[amount];
            state.Keys = new float[amount];
        }
    }

    /// <summary>Returns an emitter's references (it left the tree or was freed).</summary>
    public void Release(ParticleDrawState state)
    {
        ReleaseMesh(state.MeshEntry);
        state.MeshEntry = null;
        state.Mesh = null;
        foreach (var material in state.Materials)
            ReleaseMaterial(material);
        state.Materials = [];
        state.Blended = state.Mixed = false;
    }

    /// <summary>
    /// Draws <paramref name="node"/>'s live particles into the current scene pass for <paramref name="camera"/>: culled by
    /// its visibility box, ordered, written into this frame's instance ring, one instanced draw per surface.
    /// </summary>
    public void Draw(ParticleDrawState state, GpuParticles3D node, ICamera camera)
    {
        if (_disposed || !_ctx.FrameStarted || state.MeshEntry is not { } meshEntry || !meshEntry.Gpu.HasGeometry ||
            meshEntry.Gpu.Vertices is not { } vertices || meshEntry.Gpu.Indices is not { } indices || state.Materials.Length == 0)
            return;
        var simulation = node.Simulation;
        var live = simulation.LiveCount;
        if (live == 0 || state.Order.Length < simulation.Particles.Length)
            return;

        var model = node.ModelMatrix;
        var view = camera.ViewMatrix;
        var box = node.VisibilityAabb;
        if (!box.IsEmpty && !new Frustum(view * camera.ProjectionMatrix).Intersects(box.Transform(model)))
            return;

        var local = node.LocalCoords;
        var toWorld = local ? model : Matrix4x4.Identity;
        // The camera's forward from the view matrix (row-vector: its columns are the camera's axes, +Z pointing back).
        var forward = -new Vector3(view.M13, view.M23, view.M33);
        var cameraPosition = camera.Position;
        var order = node.DrawOrder == GpuParticles3D.DrawOrderEnum.ViewDepth && !state.Mixed
            ? GpuParticles3D.DrawOrderEnum.Index // additive and opaque passes need no back-to-front order
            : node.DrawOrder;

        var destination = _instances.Allocate(live, out var buffer, out var first);
        var count = ParticleInstances.Write(simulation.Particles, node.ProcessMaterial, node.Lifetime, node.InterpolationFraction,
            order, toWorld, cameraPosition, forward, state.Order, state.Keys, destination);
        if (count == 0)
            return;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        var frame = _ctx.Frame;
        PipelineBuilder.SetViewport(vk, cb, frame.Extent, flipY: true);
        var buffers = stackalloc VkBuffer[2] { vertices.Handle, buffer.Handle };
        var offsets = stackalloc ulong[2] { 0, 0 };
        vk.CmdBindVertexBuffers(cb, 0, 2, buffers, offsets);
        vk.CmdBindIndexBuffer(cb, indices.Handle, 0, IndexType.Uint32);
        frame.Bind(cb, _layout, _shadows);

        var reactive = frame.CurrentView == 0; // ADR 0166: the main view's blended surfaces mark TAA's reactive mask
        var processFlags = 0u;
        if (node.ProcessMaterial is { } process)
        {
            if (process.ParticleFlagAlignY)
                processFlags |= ParticlePush.AlignY;
            if (process.ParticleFlagDisableZ)
                processFlags |= ParticlePush.DisableZ;
        }

        var surfaces = meshEntry.Gpu.Surfaces;
        Pipeline bound = default;
        for (var s = 0; s < state.Materials.Length && s < surfaces.Length; s++)
        {
            if (state.Materials[s] is not { Set.Handle: not 0 } material || surfaces[s].IndexCount == 0)
                continue;
            var pipeline = GetPipeline(material, reactive);
            if (pipeline.Handle != bound.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                bound = pipeline;
            }

            var set = material.Set;
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, MaterialSetIndex, 1, &set, 0, null);
            var push = material.Push;
            push.Model = toWorld;
            push.Flags |= processFlags;
            vk.CmdPushConstants(cb, _layout, FrameContext.PushConstantStages, 0, (uint)sizeof(ParticlePush), &push);
            var range = surfaces[s];
            vk.CmdDrawIndexed(cb, range.IndexCount, (uint)count, range.FirstIndex, range.VertexOffset, first);
        }
    }

    // ── Meshes and materials ───────────────────────────────────────────────────

    private MeshEntry AcquireMesh(Mesh mesh)
    {
        if (!_meshes.TryGetValue(mesh, out var entry))
        {
            entry = new MeshEntry(new MeshGpu(_ctx, mesh, _nextMeshId++));
            _meshes.Add(mesh, entry);
        }

        entry.RefCount++;
        return entry;
    }

    private void ReleaseMesh(MeshEntry? entry)
    {
        if (entry is null || --entry.RefCount > 0)
            return;
        entry.Gpu.Release();
        _meshes.Remove(entry.Gpu.Mesh);
    }

    private MaterialEntry AcquireMaterial(StandardMaterial3D material)
    {
        if (!_materials.TryGetValue(material, out var entry))
        {
            entry = new MaterialEntry(material);
            _materials.Add(material, entry);
        }

        entry.RefCount++;
        return entry;
    }

    private void ReleaseMaterial(MaterialEntry? entry)
    {
        if (entry is null || --entry.RefCount > 0)
            return;
        _sets.Free(entry.Pool, entry.Set);
        entry.Albedo?.Release();
        entry.Emission?.Release();
        _materials.Remove(entry.Material);
    }

    // Uploads the material's textures and rewrites its set when they changed; refreshes its push constants (once a frame).
    private void PrepareMaterial(MaterialEntry entry)
    {
        if (entry.PreparedFrame == _ctx.FrameNumber)
            return;
        entry.PreparedFrame = _ctx.FrameNumber;
        var m = entry.Material;
        var changed = entry.Version != m.Version;
        if (changed)
        {
            entry.Version = m.Version;
            entry.Albedo = Retarget(entry.Albedo, m.AlbedoTexture);
            entry.Emission = Retarget(entry.Emission, m.EmissionTexture);
            entry.Push = PushFor(m);
            var blended = m.Transparency == AlphaMode.Blend || m.BlendMode == StandardMaterial3D.BlendModeEnum.Add;
            entry.Blended = blended || m.NoDepthTest;
            entry.Mixed = blended && m.BlendMode == StandardMaterial3D.BlendModeEnum.Mix;
        }

        var albedoOk = entry.Albedo?.Update() ?? false;
        var emissionOk = entry.Emission?.Update() ?? false;
        var albedoGeneration = entry.Albedo?.Generation ?? 0;
        var emissionGeneration = entry.Emission?.Generation ?? 0;
        var flags = entry.Push.Flags & ~(ParticlePush.HasAlbedo | ParticlePush.HasEmission);
        if (albedoOk)
            flags |= ParticlePush.HasAlbedo;
        if (emissionOk)
            flags |= ParticlePush.HasEmission;
        entry.Push.Flags = flags;
        if (entry.Set.Handle == 0 || albedoGeneration != entry.AlbedoGeneration || emissionGeneration != entry.EmissionGeneration)
        {
            entry.AlbedoGeneration = albedoGeneration;
            entry.EmissionGeneration = emissionGeneration;
            WriteSet(entry, albedoOk ? entry.Albedo!.Gpu! : _white, emissionOk ? entry.Emission!.Gpu! : _white);
        }
    }

    // A texture slot following the material: the same TextureGpu while the texture is the same.
    private TextureGpu? Retarget(TextureGpu? current, Texture2D? texture)
    {
        if (texture is null)
        {
            current?.Release();
            return null;
        }

        var colorSpace = texture.ImportSettings.ResolveColorSpace(colorUsage: true);
        if (current is not null && ReferenceEquals(current.Texture, texture) && current.ColorSpace == colorSpace)
            return current;
        current?.Release();
        return new TextureGpu(_ctx, texture, colorSpace);
    }

    private void WriteSet(MaterialEntry entry, GpuTexture albedo, GpuTexture emission)
    {
        // A set may still be bound by frames in flight: write a fresh one, return the old one when they finish.
        _sets.Free(entry.Pool, entry.Set);
        entry.Set = _sets.Allocate(out var pool);
        entry.Pool = pool;
        var images = stackalloc DescriptorImageInfo[3];
        images[0] = new DescriptorImageInfo { Sampler = albedo.Sampler };
        images[1] = new DescriptorImageInfo { ImageView = albedo.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[2] = new DescriptorImageInfo { ImageView = emission.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = entry.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = images,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = entry.Set,
            DstBinding = 1,
            DescriptorCount = 2,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = images + 1,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 2, writes, 0, null);
    }

    /// <summary>The push constants of <paramref name="m"/> (the model matrix and texture bits are filled in later).</summary>
    internal static ParticlePush PushFor(StandardMaterial3D m)
    {
        var albedo = m.AlbedoColor;
        var emission = m.EmissionColor;
        var e = ColorSpace.SrgbToLinear(new Vector3(emission.R, emission.G, emission.B) / 255f) * m.EmissionEnergy;
        var additive = m.BlendMode == StandardMaterial3D.BlendModeEnum.Add;
        var flags = (uint)m.BillboardMode << ParticlePush.BillboardShift;
        if (m.VertexColorUseAsAlbedo)
            flags |= ParticlePush.VertexColor;
        return new ParticlePush
        {
            Model = Matrix4x4.Identity,
            Albedo = new Vector4(ColorSpace.SrgbToLinear(new Vector3(albedo.R, albedo.G, albedo.B) / 255f), albedo.A / 255f),
            Emission = new Vector4(e, m.AlphaCutoff),
            UvTransform = new Vector4(m.UvScale, m.UvOffset.X, m.UvOffset.Y),
            Flags = flags,
            Shading = m.ShadingMode == ShadingMode.Unshaded ? 1u : 0u,
            Alpha = additive || m.Transparency == AlphaMode.Blend ? 2u : m.Transparency == AlphaMode.Cutout ? 1u : 0u,
            Blend = additive ? 1u : 0u,
        };
    }

    // ── Pipelines ──────────────────────────────────────────────────────────────

    private Pipeline GetPipeline(MaterialEntry material, bool reactive)
    {
        var m = material.Material;
        // Billboards face the camera: no culling. Unturned particles cull like the material.
        var cull = m.BillboardMode == BillboardMode.Disabled ? m.RenderState.EffectiveCull : CullMode.Disabled;
        var blend = material.Push.Blend == 1u ? 2 : material.Push.Alpha == 2u ? 1 : 0;
        var noDepthTest = m.NoDepthTest;
        var key = blend | (reactive ? 1 << 2 : 0) | ((int)cull << 3) | (noDepthTest ? 1 << 5 : 0);
        if (_pipelines.TryGetValue(key, out var pipeline))
            return pipeline;

        var state = new PipelineState
        {
            CullMode = cull switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Disabled => CullModeFlags.None,
                _ => CullModeFlags.BackBit,
            },
            FrontFace = FrontFace.CounterClockwise,
            DepthTest = !noDepthTest,
            DepthWrite = blend == 0 && !noDepthTest,
            DepthCompare = CompareOp.LessOrEqual,
            Blend = blend switch
            {
                2 => reactive ? BlendMode.AdditiveReactive : BlendMode.Additive,
                1 => reactive ? BlendMode.AlphaReactive : BlendMode.Alpha,
                _ => reactive ? BlendMode.AlphaReactive : BlendMode.Opaque,
            },
        };
        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new() { Binding = 0, Stride = MeshVertex.Size, InputRate = VertexInputRate.Vertex },
            new() { Binding = 1, Stride = ParticleInstance.Size, InputRate = VertexInputRate.Instance },
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
            new() { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
            new() { Location = 3, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
            new() { Location = 4, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
            new() { Location = 5, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
        ];
        pipeline = PipelineBuilder.Create(_ctx, state, _layout, _ctx.RenderPass,
            "Shaders/Particles/Particle.vk.vert.spv", "Shaders/Particles/Particle.vk.frag.spv", bindings, attributes, "particles");
        _pipelines.Add(key, pipeline);
        return pipeline;
    }

    /// <summary>Releases every GPU object through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _meshes.Values)
            entry.Gpu.Release();
        _meshes.Clear();
        foreach (var entry in _materials.Values)
        {
            entry.Albedo?.Release();
            entry.Emission?.Release();
        }

        _materials.Clear();
        _sets.Dispose();
        _instances.Dispose();
        _white.Dispose();
        var deletions = _ctx.Deletions;
        foreach (var pipeline in _pipelines.Values)
            deletions.Enqueue(GpuDeletion.Of(pipeline));
        _pipelines.Clear();
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_setLayout));
    }
}
