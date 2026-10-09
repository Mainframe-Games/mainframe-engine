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
