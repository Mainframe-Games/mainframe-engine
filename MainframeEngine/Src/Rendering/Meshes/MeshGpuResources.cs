using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Index range of one surface inside a <see cref="MeshGpu"/>'s shared buffers.</summary>
internal readonly record struct SurfaceRange(uint FirstIndex, uint IndexCount, int VertexOffset);

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
        for (var s = 0; s < surfaceCount; s++)
        {
            var surface = Mesh.GetSurface(s);
            surface.Validate();
            ranges[s] = new SurfaceRange((uint)indexCount, (uint)surface.IndexCount, vertexCount);
            vertexCount += surface.VertexCount;
            indexCount += surface.IndexCount;
        }

        Surfaces = ranges;
        Bounds = Mesh.Bounds;
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
    }

    /// <summary>Releases the buffers (deferred until frames in flight finish).</summary>
    public void Release()
    {
        Vertices?.Dispose();
        Indices?.Dispose();
        Vertices = null;
        Indices = null;
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

    /// <summary>x = metallic, y = roughness, z = ambient occlusion (<see cref="ShadingMode.Pbr"/>).</summary>
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

    /// <summary>The shaders of the colour pass: <see cref="ShaderSetId.MeshOutline"/> for outlines, else lit.</summary>
    public ShaderSetId ColorShaders { get; set; }

    // [shader set × extra pass × mirrored] → pipeline; reset when the state changes.
    public readonly PipelineEntry[] Pipelines = new PipelineEntry[ShaderSetCount * 4];

    public const int ShaderSetCount = 3;

    public static int PipelineIndex(ShaderSetId shaders, bool extraPass, bool mirrored) =>
        ((int)shaders * 2 + (extraPass ? 1 : 0)) * 2 + (mirrored ? 1 : 0);

    public void ResetPipelines() => Array.Clear(Pipelines);
}

/// <summary>An extra draw of one surface of an instance: a next pass of its material, or the overlay chain.</summary>
internal readonly record struct MeshExtraPass(int Surface, MaterialGpu Material);
