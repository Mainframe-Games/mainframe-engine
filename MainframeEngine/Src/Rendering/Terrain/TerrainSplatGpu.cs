using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// std140 parameters of <c>Terrain/TerrainSplat.vk.frag</c> (304 bytes): per layer the tiling, height-blend contrast,
/// normal scale and linear tint; the distance bands, slope thresholds, macro variation and options.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TerrainSplatParams
{
    public const int Size = 304;

    /// <summary>x = 1 / tiling metres, y = height blend contrast, z = normal scale, w = 0.</summary>
    public fixed float Layers[TerrainSplatMaterial3D.MaxLayers * 4];

    /// <summary>rgb = linear tint, a = 1.</summary>
    public fixed float Tints[TerrainSplatMaterial3D.MaxLayers * 4];

    /// <summary>x = detail distance, y = far distance, z = 1 / far tiling scale, w = macro strength.</summary>
    public Vector4 Bands;

    /// <summary>x = cos(triplanar start), y = cos(triplanar end), z = 1 / macro scale, w = layer count.</summary>
    public Vector4 Slope;

    /// <summary>x = hex-tiling on.</summary>
    public Vector4 Options;

    /// <summary>Packs <paramref name="m"/> (tints from sRGB to linear).</summary>
    public static TerrainSplatParams From(TerrainSplatMaterial3D m)
    {
        var p = new TerrainSplatParams();
        var count = m.LayerCount;
        for (var i = 0; i < TerrainSplatMaterial3D.MaxLayers; i++)
        {
            var layer = i < count ? m.Layers[i] : null;
            var tiling = MathF.Max(layer?.TilingMeters ?? 4f, 1e-3f);
            p.Layers[i * 4] = 1f / tiling;
            p.Layers[i * 4 + 1] = Math.Clamp(layer?.HeightBlendContrast ?? 0.2f, 0f, 1f);
            p.Layers[i * 4 + 2] = layer?.NormalScale ?? 1f;
            var tint = layer?.Tint ?? System.Drawing.Color.White;
            var linear = ColorSpace.SrgbToLinear(new Vector3(tint.R, tint.G, tint.B) / 255f);
            p.Tints[i * 4] = linear.X;
            p.Tints[i * 4 + 1] = linear.Y;
            p.Tints[i * 4 + 2] = linear.Z;
            p.Tints[i * 4 + 3] = 1f;
        }

        var detail = MathF.Max(m.DetailDistance, 0f);
        var far = MathF.Max(m.FarDistance, detail + 1e-3f);
        p.Bands = new Vector4(detail, far, 1f / MathF.Max(m.FarTilingScale, 1e-3f), Math.Clamp(m.MacroStrength, 0f, 1f));
        var start = Math.Clamp(m.TriplanarStartDegrees, 0f, 90f);
        var end = Math.Clamp(MathF.Max(m.TriplanarEndDegrees, start + 0.01f), 0f, 90.01f);
        p.Slope = new Vector4(MathF.Cos(start * MathF.PI / 180f), MathF.Cos(end * MathF.PI / 180f), 1f / MathF.Max(m.MacroScaleMeters, 1e-3f), count);
        p.Options = new Vector4(m.AntiTiling ? 1f : 0f, 0f, 0f, 0f);
        return p;
    }
}

/// <summary>
/// A <see cref="TerrainSplatMaterial3D"/>'s own GPU state next to its <see cref="MaterialGpu"/> (which keeps a default
/// set 2 for the object-ID pass): the packed layer arrays, the terrain's weight maps (shared through the renderer's
/// texture cache), the parameter UBO and the terrain splat descriptor set (<see cref="MeshRenderer"/>'s second set
/// layout).
/// </summary>
internal sealed class TerrainSplatGpu
{
    public Texture2DArrayGpu? Albedo;
    public Texture2DArrayGpu? Normal;
    public Texture2DArrayGpu? Orm;

    /// <summary><see cref="TerrainLayerPacker.ContentStamp"/> of the packed arrays (null: never packed).</summary>
    public int? PackedStamp;

    public TextureGpu? Weights0;
    public TextureGpu? Weights1;

    public GpuBuffer? Params;
    public int UploadedVersion;

    public DescriptorSet Set;
    public DescriptorPool SetPool;

    /// <summary>The generations of every image the set was written with (any change rewrites it).</summary>
    public int SetStamp;

    public int CurrentStamp() => HashCode.Combine(Albedo?.Generation ?? 0, Normal?.Generation ?? 0, Orm?.Generation ?? 0,
        Weights0?.Generation ?? 0, Weights1?.Generation ?? 0);

    public void DisposeArrays()
    {
        Albedo?.Dispose();
        Normal?.Dispose();
        Orm?.Dispose();
        Albedo = Normal = Orm = null;
    }
}
