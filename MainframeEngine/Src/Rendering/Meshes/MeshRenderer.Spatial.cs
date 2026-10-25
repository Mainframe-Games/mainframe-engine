using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// One spatial <see cref="Shader"/> in the mesh renderer (ADR 0205): its material set layout (b0 the uniform block, b1…n its
/// samplers; vertex and fragment stages), the pipeline layout sharing sets 0 and 1 and the push range with the mesh layout,
/// the caster layouts (set 0 the light matrix, set 1 this set) and the caster pipelines. Lives until the renderer is
/// disposed: its dense <see cref="Id"/> keys pipelines in the shared <see cref="PipelineStateCache"/>.
/// </summary>
internal sealed class SpatialShaderGpu(Shader shader, SpatialShaderProgram program, int id)
{
    public Shader Shader { get; } = shader;
    public SpatialShaderProgram Program { get; } = program;

    /// <summary><see cref="PipelineKey.UserShader"/> (from 1).</summary>
    public int Id { get; } = id;

    /// <summary>The SPIR-V is missing: materials using the shader draw with the default material.</summary>
    public bool Missing;

    public DescriptorSetLayout SetLayout;
    public PipelineLayout Layout;
    public PipelineLayout CasterLayout2D;
    public PipelineLayout CasterLayoutPoint;
    public MaterialDescriptorAllocator Sets = null!;

    // (point, cull, mirrored) → caster pipeline; vertex SPIR-V path → the attribute locations it reads.
    public readonly Dictionary<int, Pipeline> Casters = [];
    public readonly Dictionary<string, HashSet<int>> VertexInputs = new(StringComparer.Ordinal);
}

/// <summary>A spatial <see cref="ShaderMaterial"/> on the GPU (ADR 0205): its uniform block, sampler textures and set.</summary>
internal sealed class SpatialMaterialGpu(SpatialShaderGpu shader)
{
    public SpatialShaderGpu Shader { get; } = shader;
    public GpuBuffer? Params;
    public DescriptorSet Set;
    public DescriptorPool SetPool;
    public int UploadedVersion;
    public readonly TextureGpu?[] Textures = new TextureGpu?[shader.Program.SamplerCount];
    public readonly int[] Generations = new int[shader.Program.SamplerCount];
}

/// <summary>
/// <see cref="ShaderMaterial"/>s with a <c>shader_type spatial</c> shader in the mesh renderer (ADR 0205). Each shader
/// gets its own set-2 layout and pipeline layout (like the terrain splat material's, sets 0 and 1 stay bound), and every
/// pass draws it with the shader's own SPIR-V: the colour pass, the depth prepass and velocity, both shadow casters and
/// the object-ID pass, so vertex displacement and the alpha scissor agree in all of them. Its pipelines live in the
/// shared <see cref="PipelineStateCache"/> under <see cref="PipelineKey.UserShader"/>; the casters' are cached per shader.
/// </summary>
internal sealed unsafe partial class MeshRenderer
{
    private readonly Dictionary<Shader, SpatialShaderGpu> _spatialShaders = new(ReferenceEqualityComparer.Instance);
    private readonly List<SpatialShaderGpu> _spatialById = [];
    private readonly Dictionary<TextureSampling, Sampler> _spatialSamplers = [];
    private GpuTexture? _spatialBlack;
    private GpuTexture? _spatialTransparent;

    // The shader's GPU entry, created on first use; null (once warned) when its SPIR-V is missing.
    private SpatialShaderGpu? SpatialShaderFor(Shader shader)
    {
        if (!_spatialShaders.TryGetValue(shader, out var entry))
        {
            entry = new SpatialShaderGpu(shader, shader.Spatial!, _spatialById.Count + 1);
            _spatialShaders.Add(shader, entry);
            _spatialById.Add(entry);
            if (CanvasShaderBuild.SpatialSpvFiles(shader.SpvBasePath ?? "").Any(f => shader.SpvBasePath is null || !File.Exists(f)))
            {
                entry.Missing = true;
                Log.Warning($"[Mesh] The spatial shader '{shader.ResourcePath ?? shader.Spatial!.Name}' has no SPIR-V (build it with `just canvas-shaders <folder>`); drawing with the default material.");
                return null;
            }

            CreateSpatialLayouts(entry);
        }

        return entry.Missing ? null : entry;
    }

    private void CreateSpatialLayouts(SpatialShaderGpu entry)
    {
        var samplers = entry.Program.SamplerCount;
        var bindings = stackalloc DescriptorSetLayoutBinding[1 + samplers];
        const ShaderStageFlags stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        bindings[0] = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = stages };
        for (var i = 0; i < samplers; i++)
            bindings[1 + i] = new DescriptorSetLayoutBinding { Binding = (uint)(1 + i), DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = stages };
        var name = entry.Program.Name;
        entry.SetLayout = PipelineBuilder.CreateSetLayout(_ctx, new ReadOnlySpan<DescriptorSetLayoutBinding>(bindings, 1 + samplers), $"spatial set 2 ({name})");
        entry.Layout = _ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [entry.SetLayout], $"mesh (spatial {name})");
        entry.Sets = new MaterialDescriptorAllocator(_ctx, entry.SetLayout, samplers: 0, images: 0, combined: samplers);
        if (_shadows is not null)
        {
            entry.CasterLayout2D = _shadows.CreateMaterialCasterLayout(point: false, entry.SetLayout, $"shadow 2D (spatial {name})");
            entry.CasterLayoutPoint = _shadows.CreateMaterialCasterLayout(point: true, entry.SetLayout, $"shadow point (spatial {name})");
        }
    }

    /// <summary>
    /// Brings a spatial material's GPU state up to date (once per frame): its textures (sRGB for <c>source_color</c>
    /// samplers), the uniform block on a material version change (source colours to linear), and the set when either moved.
    /// </summary>
    private void PrepareSpatial(MaterialGpu gpu, ShaderMaterial material, SpatialShaderGpu shader)
    {
        if (gpu.Spatial is { } previous && !ReferenceEquals(previous.Shader, shader))
        {
            DestroySpatial(gpu);
            gpu.ResetPipelines(); // another shader: other pipelines (the user shader id is cached per entry)
        }

        var spatial = gpu.Spatial ??= new SpatialMaterialGpu(shader);
        var rewrite = spatial.Set.Handle == 0;
        var uniforms = shader.Program.Uniforms;
        var slot = 0;
        for (var u = 0; u < uniforms.Count; u++)
        {
            var uniform = uniforms[u];
            if (!uniform.IsSampler)
                continue;
            var texture = material.GetShaderParameter(uniform.Name) as Texture2D;
            rewrite |= SwapTexture(texture, colorUsage: uniform.SourceColor && uniform.TextureHint != ShaderTextureHint.Normal, ref spatial.Textures[slot]);
            rewrite |= RefreshTexture(spatial.Textures[slot], spatial.Generations[slot]);
            slot++;
        }

        if (spatial.UploadedVersion != material.Version || spatial.Params is null)
        {
            spatial.UploadedVersion = material.Version;
            var size = Math.Max(shader.Program.UniformBlockSize, 16);
            if (spatial.Params is null)
            {
                spatial.Params = GpuBuffer.Create(_ctx, (ulong)size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.DeviceLocal);
                rewrite = true;
            }

            Span<byte> block = size <= 2048 ? stackalloc byte[size] : new byte[size];
            shader.Shader.WriteUniformBlock(material.Parameters, block);
            spatial.Params.Upload(block); // device-local: recorded by the upload queue at the next frame's start
        }

        if (rewrite)
            WriteSpatialSet(spatial);
    }

    private void WriteSpatialSet(SpatialMaterialGpu spatial)
    {
        var entry = spatial.Shader;
        entry.Sets.Free(spatial.SetPool, spatial.Set);
        spatial.Set = entry.Sets.Allocate(out var pool);
        spatial.SetPool = pool;

        var samplers = entry.Program.SamplerCount;
        var images = stackalloc DescriptorImageInfo[Math.Max(samplers, 1)];
        var slot = 0;
        var uniforms = entry.Program.Uniforms;
        for (var u = 0; u < uniforms.Count; u++)
        {
            var uniform = uniforms[u];
            if (!uniform.IsSampler)
                continue;
            var texture = spatial.Textures[slot];
            spatial.Generations[slot] = texture?.Generation ?? 0;
            var gpu = texture?.Gpu ?? FallbackTexture(uniform.TextureHint);
            var settings = texture?.Texture.ImportSettings;
            var sampling = settings?.ToSampling() ?? TextureSampling.LinearRepeat;
            // Godot's filter_* / repeat_* hints override the texture's own sampling.
            if (uniform.Filter == CanvasTextureFilter.Nearest)
                sampling = sampling with { Filter = Filter.Nearest };
            else if (uniform.Filter == CanvasTextureFilter.Linear)
                sampling = sampling with { Filter = Filter.Linear };
            if (uniform.Repeat == CanvasTextureRepeat.Enabled)
                sampling = sampling with { AddressMode = SamplerAddressMode.Repeat };
            else if (uniform.Repeat == CanvasTextureRepeat.Disabled)
                sampling = sampling with { AddressMode = SamplerAddressMode.ClampToEdge };
            images[slot] = new DescriptorImageInfo { Sampler = SpatialSampler(sampling), ImageView = gpu.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            slot++;
        }

        var buffer = spatial.Params!.Descriptor(0, (ulong)Math.Max(entry.Program.UniformBlockSize, 16));
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = spatial.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &buffer,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = spatial.Set,
            DstBinding = 1,
            DescriptorCount = (uint)samplers,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = images,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, samplers > 0 ? 2u : 1u, writes, 0, null);
    }

    // What an unset sampler samples (Godot's hint_default_*; hint_normal: a flat normal).
    private GpuTexture FallbackTexture(ShaderTextureHint hint)
    {
        switch (hint)
        {
            case ShaderTextureHint.Normal:
                return _flatNormal;
            case ShaderTextureHint.Black:
                ReadOnlySpan<byte> black = [0, 0, 0, 255];
                return _spatialBlack ??= GpuTexture.Create2D(_ctx, 1, 1, black, TextureColorSpace.Linear, TextureSampling.LinearRepeat);
            case ShaderTextureHint.Transparent:
                ReadOnlySpan<byte> clear = [0, 0, 0, 0];
                return _spatialTransparent ??= GpuTexture.Create2D(_ctx, 1, 1, clear, TextureColorSpace.Linear, TextureSampling.LinearRepeat);
            default:
                return _whiteSrgb;
        }
    }

    // One sampler per distinct sampling (mipmapped: any mip count), shared by every spatial material.
    private Sampler SpatialSampler(TextureSampling sampling)
    {
        if (!_spatialSamplers.TryGetValue(sampling, out var sampler))
        {
            sampler = GpuTexture.CreateSampler(_ctx, sampling, 16);
            _spatialSamplers.Add(sampling, sampler);
        }

        return sampler;
    }

    private void DestroySpatial(MaterialGpu gpu)
    {
        if (gpu.Spatial is not { } spatial)
            return;
        spatial.Params?.Dispose();
        spatial.Params = null;
        spatial.Shader.Sets.Free(spatial.SetPool, spatial.Set);
        spatial.Set = default;
        for (var i = 0; i < spatial.Textures.Length; i++)
        {
            ReleaseTexture(spatial.Textures[i]);
            spatial.Textures[i] = null;
        }

        gpu.Spatial = null;
    }

    /// <summary>The shader set's pass of a spatial pipeline.</summary>
    private static SpatialPass SpatialPassOf(ShaderSetId shaders) => shaders switch
    {
        ShaderSetId.MeshDepthSpatial => SpatialPass.Depth,
        ShaderSetId.MeshObjectIdSpatial => SpatialPass.ObjectId,
        _ => SpatialPass.Color,
    };

    // The attributes a compiled vertex stage reads (Slang drops the rest; an unread attribute is a validation warning).
    private static VertexInputAttributeDescription[] SpatialAttributes(SpatialShaderGpu entry, string vertexPath)
    {
        if (!entry.VertexInputs.TryGetValue(vertexPath, out var locations))
        {
            locations = SpirvInputs.InputLocations(File.ReadAllBytes(vertexPath));
            entry.VertexInputs.Add(vertexPath, locations);
        }

        return VertexLayouts.Select(VertexLayouts.SpatialAttributes, locations);
    }

    // IPipelineFactory for spatial keys: the shader's SPIR-V of the key's pass, its pipeline layout, the material's state.
    private Pipeline CreateSpatialPipeline(in PipelineKey key)
    {
        var entry = _spatialById[key.UserShader - 1];
        var pass = SpatialPassOf(key.Shaders);
        var depth = pass == SpatialPass.Depth;
        var state = new PipelineState
        {
            CullMode = key.Cull switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Disabled => CullModeFlags.None,
                _ => CullModeFlags.BackBit,
            },
            FrontFace = key.Mirrored ? FrontFace.Clockwise : FrontFace.CounterClockwise,
            DepthTest = depth || !key.NoDepthTest,
            DepthWrite = depth || key.DepthWrite,
            DepthCompare = depth ? CompareOp.Less
                : key.Prepassed ? key.Alpha == AlphaMode.Cutout ? CompareOp.Equal : CompareOp.LessOrEqual
                : key.ExtraPass ? CompareOp.LessOrEqual : CompareOp.Less,
            // A next pass or overlay lands on a surface another vertex module drew (the standard mesh shaders, another
            // spatial shader): the same position by other instructions, a rounding step apart, which LESS_OR_EQUAL alone
            // rejects. A small bias towards the camera keeps it on the surface (decals do the same).
            DepthBiasConstant = key.ExtraPass ? -2f : 0f,
            DepthBiasSlope = key.ExtraPass ? -1f : 0f,
            Blend = depth || pass == SpatialPass.ObjectId || key.Alpha != AlphaMode.Blend ? BlendMode.Opaque
                : entry.Program.BlendMode switch
                {
                    SpatialBlendMode.Add => BlendMode.Additive,
                    SpatialBlendMode.PremultAlpha => BlendMode.Premultiplied,
                    _ => BlendMode.Alpha,
                },
        };

        // Constant 0: the alpha mode (a prepassed cutout runs as opaque: the EQUAL test replaces its discard).
        var alphaMode = key.Prepassed && key.Alpha == AlphaMode.Cutout ? AlphaMode.Opaque : key.Alpha;
        var constants = stackalloc int[1] { (int)alphaMode };
        var entries = stackalloc SpecializationMapEntry[1] { new() { ConstantID = 0, Offset = 0, Size = sizeof(int) } };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = entries, DataSize = sizeof(int), PData = constants };
        var vertex = entry.Shader.SpatialSpvPath(pass, vertex: true)!;
        var fragment = entry.Shader.SpatialSpvPath(pass, vertex: false)!;
        return PipelineBuilder.Create(_ctx, state, entry.Layout, new RenderPass(key.RenderPass), vertex, fragment,
            depth ? VertexLayouts.DepthExtBindings : VertexLayouts.MeshInstancedExtBindings, SpatialAttributes(entry, vertex),
            $"mesh (spatial {entry.Program.Name}, {pass}, {key.Alpha}, cull {key.Cull}{(key.Mirrored ? ", mirrored" : "")}{(key.ExtraPass ? ", extra pass" : "")}{(key.Prepassed ? ", prepassed" : "")})",
            &specialization);
    }

    /// <summary>A spatial material's caster pipeline for a shadow pass (built on first use, kept with its shader).</summary>
    private Pipeline SpatialCasterPipeline(SpatialShaderGpu entry, bool point, CullMode cull, bool mirrored)
    {
        var key = (point ? 1 : 0) | ((int)cull << 1) | (mirrored ? 8 : 0);
        if (entry.Casters.TryGetValue(key, out var pipeline))
            return pipeline;
        var pass = point ? SpatialPass.ShadowPoint : SpatialPass.Shadow;
        var vertex = entry.Shader.SpatialSpvPath(pass, vertex: true)!;
        pipeline = _shadows!.BuildCasterPipeline(vertex, entry.Shader.SpatialSpvPath(pass, vertex: false)!, cull, mirrored,
            point ? entry.CasterLayoutPoint : entry.CasterLayout2D, VertexLayouts.MeshInstancedExtBindings, SpatialAttributes(entry, vertex),
            $"shadow (spatial {entry.Program.Name}{(point ? ", point" : "")})");
        entry.Casters.Add(key, pipeline);
        return pipeline;
    }

    private void DisposeSpatialResources()
    {
        foreach (var entry in _spatialById)
        {
            if (entry.Missing)
                continue;
            entry.Sets.Dispose();
            foreach (var pipeline in entry.Casters.Values)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.Layout));
            if (entry.CasterLayout2D.Handle != 0)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.CasterLayout2D));
            if (entry.CasterLayoutPoint.Handle != 0)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.CasterLayoutPoint));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.SetLayout));
        }

        _spatialById.Clear();
        _spatialShaders.Clear();
        foreach (var sampler in _spatialSamplers.Values)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(sampler));
        _spatialSamplers.Clear();
        _spatialBlack?.Dispose();
        _spatialTransparent?.Dispose();
    }
}
