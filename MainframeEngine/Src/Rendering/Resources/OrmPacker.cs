namespace MainframeEngine;

/// <summary>
/// Packs separate occlusion, roughness and metallic maps into one ORM texture (glTF's and Godot's packing: R = ambient
/// occlusion, G = roughness, B = metallic, linear), the layout of <see cref="StandardMaterial3D.OrmTexture"/> and
/// <see cref="FoliageMaterial3D.OrmTexture"/>. Texture sets such as ambientCG's ship one greyscale image per map
/// (<c>_Roughness.jpg</c>); this builds the packed texture at load (ADR 0158).
/// </summary>
public static class OrmPacker
{
    /// <summary>The import settings of packed textures: linear data, mipmapped, repeating.</summary>
    public static TextureImportSettings Settings { get; } = new() { ColorSpace = TextureImportColorSpace.Linear };

    /// <summary>
    /// Packs RGBA8 sources of <paramref name="width"/> × <paramref name="height"/> pixels into RGBA8 ORM pixels. Each map
    /// is read from its source's red channel (greyscale images decode with R = G = B); an empty source stands for a
    /// constant (<paramref name="occlusion"/> 255: none, <paramref name="roughness"/> 255: fully rough,
    /// <paramref name="metallic"/> 0: dielectric, or the given defaults). Alpha is 255.
    /// </summary>
    public static byte[] Pack(int width, int height, ReadOnlySpan<byte> occlusion, ReadOnlySpan<byte> roughness, ReadOnlySpan<byte> metallic,
        byte defaultOcclusion = 255, byte defaultRoughness = 255, byte defaultMetallic = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var length = width * height * 4;
        Check(occlusion, length, nameof(occlusion));
        Check(roughness, length, nameof(roughness));
        Check(metallic, length, nameof(metallic));

        var orm = new byte[length];
        for (var i = 0; i < length; i += 4)
        {
            orm[i] = occlusion.IsEmpty ? defaultOcclusion : occlusion[i];
            orm[i + 1] = roughness.IsEmpty ? defaultRoughness : roughness[i];
            orm[i + 2] = metallic.IsEmpty ? defaultMetallic : metallic[i];
            orm[i + 3] = 255;
        }

        return orm;
    }

    /// <summary>
    /// An ORM texture (<see cref="Settings"/> unless <paramref name="settings"/> is given) from the red channels of the
    /// given maps, which must share one size; a null map stands for its constant (see the byte overload). At least one
    /// map is required. Decodes the sources on the calling thread; the result is a code-created texture (not saved with
    /// scenes).
    /// </summary>
    public static Texture2D Pack(Texture2D? occlusion, Texture2D? roughness, Texture2D? metallic, TextureImportSettings? settings = null)
    {
        if (occlusion is null && roughness is null && metallic is null)
            throw new ArgumentException("An ORM texture needs at least one source map.");

        int width = 0, height = 0;
        var o = Decode(occlusion, ref width, ref height);
        var r = Decode(roughness, ref width, ref height);
        var m = Decode(metallic, ref width, ref height);
        return Texture2D.FromPixels(width, height, Pack(width, height, o, r, m), settings ?? Settings);
    }

    /// <summary>
    /// glTF's maps as one ORM texture: occlusion from <paramref name="occlusion"/>'s red channel, roughness and metallic
    /// from <paramref name="metallicRoughness"/>'s green and blue channels (glTF's layout). Missing maps stand for 1, 1 and
    /// 1 (the factors scale them). Maps of different sizes are resampled (nearest) to the larger.
    /// </summary>
    public static Texture2D PackGltf(Texture2D? occlusion, Texture2D? metallicRoughness, TextureImportSettings? settings = null)
    {
        if (occlusion is null && metallicRoughness is null)
            throw new ArgumentException("An ORM texture needs at least one source map.");
        var o = occlusion?.DecodePixels();
        var mr = metallicRoughness?.DecodePixels();
        var width = Math.Max(o?.Width ?? 0, mr?.Width ?? 0);
        var height = Math.Max(o?.Height ?? 0, mr?.Height ?? 0);
        var orm = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                orm[i] = o is { } ao ? Texel(ao, x, y, width, height, 0) : (byte)255;
                orm[i + 1] = mr is { } g ? Texel(g, x, y, width, height, 1) : (byte)255;
                orm[i + 2] = mr is { } b ? Texel(b, x, y, width, height, 2) : (byte)255;
                orm[i + 3] = 255;
            }
        }

        return Texture2D.FromPixels(width, height, orm, settings ?? Settings);
    }

    // Channel c of the source texel under (x, y) of a width × height target (nearest).
    private static byte Texel((byte[] Rgba, int Width, int Height) source, int x, int y, int width, int height, int c)
    {
        var sx = Math.Min(x * source.Width / width, source.Width - 1);
        var sy = Math.Min(y * source.Height / height, source.Height - 1);
        return source.Rgba[(sy * source.Width + sx) * 4 + c];
    }

    private static byte[] Decode(Texture2D? texture, ref int width, ref int height)
    {
        if (texture is null)
            return [];
        var (rgba, w, h) = texture.DecodePixels();
        if (width == 0)
        {
            (width, height) = (w, h);
        }
        else if (w != width || h != height)
        {
            throw new ArgumentException($"ORM source maps differ in size: {width}×{height} and {w}×{h}.");
        }

        return rgba;
    }

    private static void Check(ReadOnlySpan<byte> source, int length, string name)
    {
        if (!source.IsEmpty && source.Length != length)
            throw new ArgumentException($"Expected {length} bytes of RGBA8 pixels or none, got {source.Length}.", name);
    }
}
