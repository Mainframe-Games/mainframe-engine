using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Index range of one surface inside a <see cref="MeshGpu"/>'s shared buffers; <paramref name="HasStreams"/> when the
/// surface has its own colours or custom0 (it draws with <see cref="VertexLayoutId.MeshInstancedExt"/>).
/// </summary>
internal readonly record struct SurfaceRange(uint FirstIndex, uint IndexCount, int VertexOffset, bool HasStreams = false);

/// <summary>
/// A <see cref="Mesh"/> on the GPU: every surface's vertices in one device-local vertex buffer and indices in one
/// index buffer (uploaded through the upload queue), plus each surface's range. Shared by every instance of the
/// mesh (reference-counted by the nodes using it); re-uploaded when <see cref="Mesh.Version"/> changes.
/// </summary>
internal sealed class MeshGpu
{
    private readonly IVulkanContext _ctx;
    private int _uploadedVersion;

    public MeshGpu(IVulkanContext ctx, Mesh mesh, int id)
    {
        _ctx = ctx;
        Mesh = mesh;
        Id = id;
    }

    public Mesh Mesh { get; }

    /// <summary>Dense id for draw sort keys.</summary>
    public int Id { get; }

    public int RefCount { get; set; }

    /// <summary>Frame of the last <see cref="Update"/> (meshes are checked once per frame).</summary>
    public ulong PreparedFrame { get; set; }

    /// <summary>Result of the last <see cref="Update"/>: there is geometry to draw.</summary>
    public bool HasGeometry { get; set; }

    public GpuBuffer? Vertices { get; private set; }
    public GpuBuffer? Indices { get; private set; }

    /// <summary>
    /// The second vertex stream (<see cref="MeshVertexExt"/>, binding 2) covering every vertex of the mesh: uploaded
    /// when a surface has colours or custom0, or on demand (<see cref="EnsureStreams"/>) for a material that reads it.
    /// </summary>
    public GpuBuffer? Streams { get; private set; }

    private int _vertexCount;
    public SurfaceRange[] Surfaces { get; private set; } = [];
    public Aabb Bounds { get; private set; } = Aabb.Empty;

    /// <summary>Bumped on every re-upload (nodes refresh their per-surface materials).</summary>
    public int Generation { get; private set; }

    /// <summary>Uploads the mesh if it changed since the last upload. Returns false when it has nothing to draw.</summary>
    public bool Update()
    {
        var version = Mesh.Version;
        if (version == _uploadedVersion)
            return Vertices is not null;
        _uploadedVersion = version;
        Generation++;
        Release();
        try
        {
            Upload();
        }
        catch (InvalidDataException e)
        {
            Log.Error($"[Mesh] '{Mesh.ResourcePath ?? Mesh.ResourceName}' cannot be drawn: {e.Message}");
            Release();
            Surfaces = new SurfaceRange[Mesh.SurfaceCount];
        }

        return Vertices is not null;
    }

    private void Upload()
    {
        var surfaceCount = Mesh.SurfaceCount;
        var ranges = new SurfaceRange[surfaceCount];
        int vertexCount = 0, indexCount = 0;
        var streams = false;
        for (var s = 0; s < surfaceCount; s++)
        {
            var surface = Mesh.GetSurface(s);
            surface.Validate();
            ranges[s] = new SurfaceRange((uint)indexCount, (uint)surface.IndexCount, vertexCount, surface.HasVertexStreams);
            streams |= surface.HasVertexStreams;
            vertexCount += surface.VertexCount;
            indexCount += surface.IndexCount;
        }

        Surfaces = ranges;
        Bounds = Mesh.Bounds;
        _vertexCount = vertexCount;
        if (vertexCount == 0 || indexCount == 0)
            return;

        var vertices = ArrayPool<MeshVertex>.Shared.Rent(vertexCount);
        var indices = ArrayPool<uint>.Shared.Rent(indexCount);
        try
        {
            for (var s = 0; s < surfaceCount; s++)
            {
                var surface = Mesh.GetSurface(s);
                var range = ranges[s];
                surface.WriteVertices(vertices.AsSpan(range.VertexOffset, surface.VertexCount));
                var src = surface.Indices;
                var dst = indices.AsSpan((int)range.FirstIndex, src.Length);
                for (var i = 0; i < src.Length; i++)
                    dst[i] = (uint)src[i];
            }

            Vertices = GpuBuffer.CreateStatic<MeshVertex>(_ctx, vertices.AsSpan(0, vertexCount), BufferUsageFlags.VertexBufferBit);
            Indices = GpuBuffer.CreateStatic<uint>(_ctx, indices.AsSpan(0, indexCount), BufferUsageFlags.IndexBufferBit);
        }
        finally
        {
            ArrayPool<MeshVertex>.Shared.Return(vertices);
            ArrayPool<uint>.Shared.Return(indices);
        }

        if (streams)
            UploadStreams();
    }

    /// <summary>
    /// Makes sure <see cref="Streams"/> exists (defaults for surfaces without streams), for materials that always read
    /// it (<see cref="FoliageMaterial3D"/>). Uploads once per mesh version.
    /// </summary>
    public void EnsureStreams()
    {
        if (Streams is null && Vertices is not null)
            UploadStreams();
    }

    private void UploadStreams()
    {
        var data = ArrayPool<MeshVertexExt>.Shared.Rent(_vertexCount);
        try
        {
            for (var s = 0; s < Surfaces.Length; s++)
            {
                var surface = Mesh.GetSurface(s);
                surface.WriteVertexStreams(data.AsSpan(Surfaces[s].VertexOffset, surface.VertexCount));
            }

            Streams = GpuBuffer.CreateStatic<MeshVertexExt>(_ctx, data.AsSpan(0, _vertexCount), BufferUsageFlags.VertexBufferBit);
        }
        finally
        {
            ArrayPool<MeshVertexExt>.Shared.Return(data);
        }
    }

    /// <summary>Releases the buffers (deferred until frames in flight finish).</summary>
    public void Release()
    {
        Vertices?.Dispose();
        Indices?.Dispose();
        Streams?.Dispose();
        Vertices = null;
        Indices = null;
        Streams = null;
    }
}

/// <summary>A <see cref="Texture2D"/> uploaded in one colour space; shared by the materials using it.</summary>
internal sealed class TextureGpu
{
    private readonly IVulkanContext _ctx;
    private int _uploadedVersion;

    public TextureGpu(IVulkanContext ctx, Texture2D texture, TextureColorSpace colorSpace)
    {
        _ctx = ctx;
        Texture = texture;
        ColorSpace = colorSpace;
    }

    public Texture2D Texture { get; }
    public TextureColorSpace ColorSpace { get; }
    public int RefCount { get; set; }
    public GpuTexture? Gpu { get; private set; }

    /// <summary>Bumped on every re-upload (materials rewrite their descriptor sets).</summary>
    public int Generation { get; private set; }

    /// <summary>Uploads the texture if it changed. Returns false when it could not be decoded.</summary>
    public bool Update()
    {
        var version = Texture.Version;
        if (version == _uploadedVersion)
            return Gpu is not null;
        _uploadedVersion = version;
        Generation++;
        Gpu?.Dispose();
        Gpu = null;
        try
        {
            var settings = Texture.ImportSettings;
            var (rgba, width, height) = Texture.DecodePixels();
            Gpu = GpuTexture.Create2D(_ctx, (uint)width, (uint)height, rgba, ColorSpace, settings.ToSampling(), settings.Mipmaps);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            Log.Error($"[Texture] {Texture} cannot be uploaded: {e.Message}");
        }

        return Gpu is not null;
    }

    public void Release()
    {
        Gpu?.Dispose();
        Gpu = null;
    }
}

/// <summary>std140 parameters of <c>include/material.slang</c> (96 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MaterialParams
{
    public Vector4 Albedo;
    public Vector4 Emission;
    public Vector4 UvTransform;
    public Vector4 Params;
    public uint TextureFlags;

    /// <summary><c>flags.y</c>: <see cref="ShadingBlinnPhong"/>, <see cref="ShadingUnshaded"/> or <see cref="ShadingPbr"/>.</summary>
    public uint Shading;
    public uint DoubleSided;

    /// <summary><see cref="OutlineMaterial3D.Width"/> as float bits (<c>flags.w</c>; read by the outline vertex shader).</summary>
    public uint OutlineWidth;

    /// <summary>
    /// x = metallic, y = roughness, z = ambient occlusion (<see cref="ShadingMode.Pbr"/>); w = 1 for a dithered cutout
    /// (<see cref="FoliageMaterial3D.AlphaDither"/>, 0 for every other material).
    /// </summary>
    public Vector4 Pbr;

    public const int Size = 96;
    public const uint HasAlbedo = 1, HasNormal = 2, HasEmission = 4, HasOrm = 8;
    public const uint ShadingBlinnPhong = 0, ShadingUnshaded = 1, ShadingPbr = 2;

    /// <summary>Packs a material (colours converted from sRGB to linear).</summary>
    public static MaterialParams From(StandardMaterial3D m, uint textureFlags) => new()
    {
        Albedo = Linear(m.AlbedoColor),
        Emission = new Vector4(ColorSpace.SrgbToLinear(Rgb(m.EmissionColor)) * m.EmissionEnergy, 0f),
        UvTransform = new Vector4(m.UvScale, m.UvOffset.X, m.UvOffset.Y),
        Params = new Vector4(m.Specular, MathF.Max(m.Shininess, 1f), m.AlphaCutoff, m.NormalScale),
        TextureFlags = textureFlags,
        Shading = m.ShadingMode switch
        {
            ShadingMode.Unshaded => ShadingUnshaded,
            ShadingMode.Pbr => ShadingPbr,
            _ => ShadingBlinnPhong,
        },
        DoubleSided = m.DoubleSided ? 1u : 0u,
        Pbr = new Vector4(Math.Clamp(m.Metallic, 0f, 1f), Math.Clamp(m.Roughness, 0f, 1f), Math.Clamp(m.AmbientOcclusion, 0f, 1f), 0f),
    };

    /// <summary>Packs an outline: unshaded colour, no textures, the width in pixels.</summary>
    public static MaterialParams From(OutlineMaterial3D m) => new()
    {
        Albedo = Linear(m.Color),
        UvTransform = new Vector4(1f, 1f, 0f, 0f),
        Params = new Vector4(0f, 1f, 0f, 1f),
        Shading = ShadingUnshaded,
        OutlineWidth = BitConverter.SingleToUInt32Bits(m.Width),
    };

    /// <summary>
    /// Packs a <see cref="FoliageMaterial3D"/> into the same block (<c>include/foliage.slang</c> reads it): the emission
    /// slot holds translucency, wind strength scale and branch bend; the cutoff is 0 unless the material cuts out (the
    /// foliage casters always run the alpha test); <c>flags.z</c> is the back-face mode + 1 (1 flip, 2 keep, 3 cull);
    /// <c>pbr.w</c> is 1 for a dithered cutout (<see cref="FoliageMaterial3D.AlphaDither"/>, <c>include/alpha_dither.slang</c>).
    /// </summary>
    public static MaterialParams From(FoliageMaterial3D m, uint textureFlags)
    {
        var (specular, shininess) = FoliageMaterial3D.BlinnFromRoughness(m.Roughness);
        return new MaterialParams
        {
            Albedo = Linear(m.AlbedoColor),
            Emission = new Vector4(Math.Clamp(m.Translucency, 0f, 1f), m.WindStrength, m.WindBranchBend, 0f),
            UvTransform = new Vector4(1f, 1f, 0f, 0f),
            Params = new Vector4(specular, shininess, m.AlphaCutout ? m.AlphaCutoff : 0f, m.NormalScale),
            TextureFlags = textureFlags,
            Shading = m.ShadingMode switch
            {
                ShadingMode.Unshaded => ShadingUnshaded,
                ShadingMode.Pbr => ShadingPbr,
                _ => ShadingBlinnPhong,
            },
            DoubleSided = (uint)m.BackFace + 1u,
            Pbr = new Vector4(0f, Math.Clamp(m.Roughness, 0f, 1f), 1f, m.AlphaCutout && m.AlphaDither ? 1f : 0f),
        };
    }

    /// <summary>
    /// Packs a <see cref="WaterMaterial3D"/> into the same block (<c>include/water.slang</c> reads it): albedo = scatter
    /// colour (linear) and strength, emission = absorption and reflection strength, uvTransform = 1 / near and far normal
    /// scale, normal strength, roughness; params = flow cycle, flow scale, shore foam distance, foam strength; pbr =
    /// 1 / foam scale, soft edge distance, wind drift. The albedo slot holds the foam texture, the normal slot the normal map.
    /// </summary>
    public static MaterialParams From(WaterMaterial3D m, uint textureFlags) => new()
    {
        Albedo = new Vector4(ColorSpace.SrgbToLinear(Rgb(m.ScatterColor)), MathF.Max(m.ScatterStrength, 0f)),
        Emission = new Vector4(Vector3.Max(m.Absorption, Vector3.Zero), MathF.Max(m.ReflectionStrength, 0f)),
        UvTransform = new Vector4(1f / MathF.Max(m.NormalScaleNear, 1e-3f), 1f / MathF.Max(m.NormalScaleFar, 1e-3f),
            MathF.Max(m.NormalStrength, 0f), Math.Clamp(m.Roughness, 0f, 1f)),
        Params = new Vector4(MathF.Max(m.FlowCycle, 0.05f), m.FlowScale, MathF.Max(m.ShoreFoamDistance, 0f), MathF.Max(m.FoamStrength, 0f)),
        TextureFlags = textureFlags,
        Shading = ShadingPbr,
        Pbr = new Vector4(1f / MathF.Max(m.FoamScale, 1e-3f), MathF.Max(m.SoftEdgeDistance, 0f), m.WindDrift, 0f),
    };

    /// <summary>
    /// Packs a <see cref="WaterfallMaterial3D"/> (<c>Water/Waterfall.vk.frag</c>, ADR 0173): albedo = foam colour (linear)
    /// and opacity, emission = water colour (linear) and aeration, uvTransform = streak scale, streak length, flow scale,
    /// roughness. The albedo slot holds the streak texture, the normal slot the built-in ripple normals.
    /// </summary>
    public static MaterialParams From(WaterfallMaterial3D m, uint textureFlags) => new()
    {
        Albedo = new Vector4(ColorSpace.SrgbToLinear(Rgb(m.FoamColor)), Math.Clamp(m.Opacity, 0f, 1f)),
        Emission = new Vector4(ColorSpace.SrgbToLinear(Rgb(m.WaterColor)), MathF.Max(m.Aeration, 0f)),
        UvTransform = new Vector4(MathF.Max(m.StreakScale, 0.01f), MathF.Max(m.StreakLength, 0.01f), MathF.Max(m.FlowScale, 0f),
            Math.Clamp(m.Roughness, 0f, 1f)),
        Params = new Vector4(0f, 1f, 0f, 1f),
        TextureFlags = textureFlags,
        Shading = ShadingPbr,
        DoubleSided = 1,
    };

    /// <summary>
    /// Packs a <see cref="SprayMaterial3D"/> (<c>Water/Spray.vk.*</c>, ADR 0173): albedo = colour (linear) and opacity,
    /// uvTransform = cycle (s), rise (m), growth, soft distance (m). The albedo slot holds the noise texture.
    /// </summary>
    public static MaterialParams From(SprayMaterial3D m, uint textureFlags) => new()
    {
        Albedo = new Vector4(ColorSpace.SrgbToLinear(Rgb(m.Color)), Math.Clamp(m.Opacity, 0f, 1f)),
        UvTransform = new Vector4(MathF.Max(m.Cycle, 0.05f), MathF.Max(m.Rise, 0f), MathF.Max(m.Growth, 1f), MathF.Max(m.SoftDistance, 0f)),
        Params = new Vector4(0f, 1f, 0f, 1f),
        TextureFlags = textureFlags,
        Shading = ShadingPbr,
        DoubleSided = 1,
    };

    private static Vector3 Rgb(System.Drawing.Color c) => new Vector3(c.R, c.G, c.B) / 255f;

    private static Vector4 Linear(System.Drawing.Color c) => new(ColorSpace.SrgbToLinear(Rgb(c)), c.A / 255f);
}

/// <summary>
/// A <see cref="Material"/> on the GPU: its parameter UBO (device-local, updated through the upload queue) and
/// descriptor set 2 (parameters, sampler, albedo/normal/emission/ORM images). Cached pipeline entries make the
/// steady-state draw-list build hash-free. Shared by every surface drawn with the material.
/// </summary>
internal sealed class MaterialGpu
{
    public MaterialGpu(Material material, int id)
    {
        Material = material;
        Id = id;
    }

    public Material Material { get; }
    public int Id { get; }
    public int RefCount { get; set; }

    public GpuBuffer? Params { get; set; }
    public DescriptorSet Set { get; set; }
    public DescriptorPool SetPool { get; set; }
    public TextureGpu? Albedo;
    public TextureGpu? Normal;
    public TextureGpu? Emission;
    public TextureGpu? Orm;
    public int AlbedoGeneration { get; set; }
    public int NormalGeneration { get; set; }
    public int EmissionGeneration { get; set; }
    public int OrmGeneration { get; set; }

    /// <summary>Material version the GPU copy reflects (0 = never uploaded).</summary>
    public int UploadedVersion { get; set; }

    /// <summary>Frame number of the last <see cref="MeshRenderer"/> refresh (once per frame).</summary>
    public ulong PreparedFrame { get; set; }

    public MaterialRenderState State { get; set; }
    public int RenderPriority { get; set; }

    /// <summary>
    /// The shaders of the colour pass: <see cref="ShaderSetId.MeshOutline"/> for outlines,
    /// <see cref="ShaderSetId.MeshFoliage"/> for foliage, <see cref="ShaderSetId.MeshWater"/> for water,
    /// <see cref="ShaderSetId.MeshTerrainSplat"/> for terrain splat materials, else lit.
    /// </summary>
    public ShaderSetId ColorShaders { get; set; }

    /// <summary>A <see cref="TerrainSplatMaterial3D"/>'s own set and textures (its colour pass binds them, not <see cref="Set"/>).</summary>
    public TerrainSplatGpu? Splat;

    /// <summary>
    /// A <see cref="WaterMaterial3D"/> with <see cref="WaterMaterial3D.RefractionEnabled"/> (ADR 0173): views that draw it
    /// split their scene pass for a scene copy, and draw it with <see cref="ShaderSetId.MeshWaterScene"/>.
    /// </summary>
    public bool RefractsScene;

    /// <summary>The material's vertex shaders read the second vertex stream whatever the surface (foliage, water).</summary>
    public bool NeedsStreams => ColorShaders is ShaderSetId.MeshFoliage or ShaderSetId.MeshWater or ShaderSetId.MeshWaterfall or ShaderSetId.MeshSpray;

    /// <summary>Shadow casters run the foliage wind (<see cref="FoliageMaterial3D"/>).</summary>
    public bool FoliageCaster => ColorShaders == ShaderSetId.MeshFoliage;

    // [shader set × extra pass × mirrored × vertex streams × prepassed] → pipeline; reset when the state changes.
    public readonly PipelineEntry[] Pipelines = new PipelineEntry[ShaderSetCount * 16];

    public const int ShaderSetCount = 11;

    public static int PipelineIndex(ShaderSetId shaders, bool extraPass, bool mirrored, bool streams = false, bool prepassed = false) =>
        ((((int)shaders * 2 + (extraPass ? 1 : 0)) * 2 + (mirrored ? 1 : 0)) * 2 + (streams ? 1 : 0)) * 2 + (prepassed ? 1 : 0);

    /// <summary>
    /// The depth prepass draws this material's opaque and cutout surfaces (ADR 0163): lit, unshaded, foliage and terrain
    /// splat materials. Outlines (an inverted hull) and water draw only in the scene pass.
    /// </summary>
    public bool Prepassable => !State.IsTransparent &&
                               ColorShaders is ShaderSetId.MeshLit or ShaderSetId.MeshFoliage or ShaderSetId.MeshTerrainSplat;

    /// <summary>The prepass shader set of a <see cref="Prepassable"/> material.</summary>
    public ShaderSetId DepthShaders => ColorShaders == ShaderSetId.MeshFoliage ? ShaderSetId.MeshDepthFoliage : ShaderSetId.MeshDepth;

    public void ResetPipelines() => Array.Clear(Pipelines);
}

/// <summary>An extra draw of one surface of an instance: a next pass of its material, or the overlay chain.</summary>
internal readonly record struct MeshExtraPass(int Surface, MaterialGpu Material);
