using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// Packs a <see cref="TerrainSplatMaterial3D"/>'s layers into the three texture arrays its shader samples (ADR 0156):
/// albedo (sRGB, height in alpha), normal and ORM (linear), one layer per <see cref="TerrainLayer"/>, every image
/// resampled to <see cref="TerrainSplatMaterial3D.LayerTextureSize"/>² (box filter down, wrapping bilinear up, so tiling
/// stays seamless). Mips are generated on the GPU. CPU only: unit-testable without a device.
/// </summary>
internal static class TerrainLayerPacker
{
    /// <summary>The packed arrays (every one <c>size × size × max(layers, 1)</c>).</summary>
    public readonly record struct Packed(Texture2DArray Albedo, Texture2DArray Normal, Texture2DArray Orm);

    /// <summary>Albedo of a layer without one: white (the tint colours it).</summary>
    public static readonly byte[] DefaultAlbedo = [255, 255, 255, 255];

    /// <summary>Normal of a layer without one: flat.</summary>
    public static readonly byte[] DefaultNormal = [128, 128, 255, 255];

    /// <summary>ORM of a layer without one: occlusion 1, roughness 0.9, metallic 0.</summary>
    public static readonly byte[] DefaultOrm = [255, 230, 0, 255];

    /// <summary>Height of a layer with no height source.</summary>
    public const byte DefaultHeight = 128;

    private static readonly TextureImportSettings AlbedoSettings = new() { ColorSpace = TextureImportColorSpace.Srgb, Mipmaps = true, Wrap = TextureWrap.Repeat };
    private static readonly TextureImportSettings DataSettings = new() { ColorSpace = TextureImportColorSpace.Linear, Mipmaps = true, Wrap = TextureWrap.Repeat };

    /// <summary>Packs <paramref name="material"/>'s first <see cref="TerrainSplatMaterial3D.LayerCount"/> layers.</summary>
    public static Packed Pack(TerrainSplatMaterial3D material)
    {
        var count = material.LayerCount;
        var layers = new TerrainLayer?[count];
        for (var i = 0; i < count; i++)
            layers[i] = material.Layers[i];
        return Pack(layers, material.LayerTextureSize);
    }

    /// <summary>Packs <paramref name="layers"/> (null entries and an empty list pack as default layers) at <paramref name="size"/>².</summary>
    public static Packed Pack(IReadOnlyList<TerrainLayer?> layers, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var count = Math.Max(layers.Count, 1);
        var albedo = new byte[count][];
        var normal = new byte[count][];
        var orm = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var layer = i < layers.Count ? layers[i] : null;
            normal[i] = Load(layer?.Normal, size) ?? Fill(DefaultNormal, size);
            var ormPixels = Load(layer?.Orm, size);
            orm[i] = ormPixels ?? Fill(DefaultOrm, size);
            albedo[i] = PackAlbedo(Load(layer?.Albedo, size), Load(layer?.Height, size), ormPixels, size);
        }

        return new Packed(
            Texture2DArray.FromImages(size, size, albedo, AlbedoSettings),
            Texture2DArray.FromImages(size, size, normal, DataSettings),
            Texture2DArray.FromImages(size, size, orm, DataSettings));
    }

    /// <summary>
    /// The albedo layer with the height in alpha: <paramref name="height"/>'s red when given; else the albedo's alpha
    /// when it has one (a texel below 255); else the ORM's occlusion; else <see cref="DefaultHeight"/>.
    /// </summary>
    internal static byte[] PackAlbedo(byte[]? albedo, byte[]? height, byte[]? orm, int size)
    {
        var pixels = albedo ?? Fill(DefaultAlbedo, size);
        if (height is not null)
            CopyChannel(height, 0, pixels);
        else if (albedo is null || IsOpaque(albedo))
        {
            if (orm is not null)
                CopyChannel(orm, 0, pixels);
            else
                for (var p = 3; p < pixels.Length; p += 4)
                    pixels[p] = DefaultHeight;
        }

        return pixels;
    }

    /// <summary>
    /// An identity of the packed content: the size, and every layer's textures (reference and version). Layer settings
    /// that do not change pixels (tiling, tint, contrast) do not change it. Allocation-free.
    /// </summary>
    public static int ContentStamp(TerrainSplatMaterial3D material)
    {
        var hash = new HashCode();
        hash.Add(material.LayerTextureSize);
        var count = material.LayerCount;
        hash.Add(count);
        var layers = material.Layers;
        for (var i = 0; i < count; i++)
        {
            var layer = layers[i];
            AddTexture(ref hash, layer?.Albedo);
            AddTexture(ref hash, layer?.Normal);
            AddTexture(ref hash, layer?.Orm);
            AddTexture(ref hash, layer?.Height);
        }

        return hash.ToHashCode();
    }

    private static void AddTexture(ref HashCode hash, Texture2D? texture)
    {
        hash.Add(texture is null ? 0 : RuntimeHelpers.GetHashCode(texture));
        hash.Add(texture?.Version ?? 0);
    }

    // Decoded and resampled to size², or null when absent or undecodable (logged).
    private static byte[]? Load(Texture2D? texture, int size)
    {
        if (texture is null)
            return null;
        try
        {
            var (rgba, width, height) = texture.DecodePixelsReadOnly();
            return Resample(rgba, width, height, size);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            Log.Error($"[Terrain] layer texture {texture} cannot be packed: {e.Message}");
            return null;
        }
    }

    private static byte[] Fill(ReadOnlySpan<byte> rgba, int size)
    {
        var pixels = new byte[size * size * 4];
        for (var p = 0; p < pixels.Length; p += 4)
            rgba.CopyTo(pixels.AsSpan(p, 4));
        return pixels;
    }

    private static bool IsOpaque(byte[] rgba)
    {
        for (var p = 3; p < rgba.Length; p += 4)
            if (rgba[p] != 255)
                return false;
        return true;
    }

    private static void CopyChannel(byte[] source, int channel, byte[] destinationAlpha)
    {
        for (var p = 0; p < destinationAlpha.Length; p += 4)
            destinationAlpha[p + 3] = source[p + channel];
    }

    /// <summary>
    /// <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>) resampled to
    /// <paramref name="size"/>²: a copy when the size matches, a box average when both sides shrink, else wrapping bilinear.
    /// </summary>
    internal static byte[] Resample(byte[] rgba, int width, int height, int size)
    {
        if (width == size && height == size)
            return (byte[])rgba.Clone();
        var result = new byte[size * size * 4];
        if (width >= size && height >= size)
        {
            for (var y = 0; y < size; y++)
            {
                var y0 = (int)((long)y * height / size);
                var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / size));
                for (var x = 0; x < size; x++)
                {
                    var x0 = (int)((long)x * width / size);
                    var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * width / size));
                    int r = 0, g = 0, b = 0, a = 0;
                    for (var sy = y0; sy < y1; sy++)
                        for (var sx = x0; sx < x1; sx++)
                        {
                            var s = (sy * width + sx) * 4;
                            r += rgba[s];
                            g += rgba[s + 1];
                            b += rgba[s + 2];
                            a += rgba[s + 3];
                        }

                    var n = (x1 - x0) * (y1 - y0);
                    var d = (y * size + x) * 4;
                    result[d] = (byte)((r + n / 2) / n);
                    result[d + 1] = (byte)((g + n / 2) / n);
                    result[d + 2] = (byte)((b + n / 2) / n);
                    result[d + 3] = (byte)((a + n / 2) / n);
                }
            }

            return result;
        }

        for (var y = 0; y < size; y++)
        {
            // Texel centres map to texel centres; neighbours wrap (the layers tile).
            var fy = (y + 0.5f) * height / size - 0.5f;
            var iy = (int)MathF.Floor(fy);
            var ty = fy - iy;
            var ya = Wrap(iy, height);
            var yb = Wrap(iy + 1, height);
            for (var x = 0; x < size; x++)
            {
                var fx = (x + 0.5f) * width / size - 0.5f;
                var ix = (int)MathF.Floor(fx);
                var tx = fx - ix;
                var xa = Wrap(ix, width);
                var xb = Wrap(ix + 1, width);
                var d = (y * size + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var top = rgba[(ya * width + xa) * 4 + c] * (1f - tx) + rgba[(ya * width + xb) * 4 + c] * tx;
                    var bottom = rgba[(yb * width + xa) * 4 + c] * (1f - tx) + rgba[(yb * width + xb) * 4 + c] * tx;
                    result[d + c] = (byte)Math.Clamp(MathF.Round(top * (1f - ty) + bottom * ty), 0f, 255f);
                }
            }
        }

        return result;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;
}
