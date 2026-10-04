namespace MainframeEngine;

/// <summary>
/// Pixel layout for window icons. Silk.NET's SDL <c>SetWindowIcon</c> builds the surface with the masks
/// R = 0xFF000000, G = 0x00FF0000, B = 0x0000FF00, A = 0x000000FF over 32-bit pixels read in native byte order, so on a
/// little-endian machine it expects each pixel's bytes as A, B, G, R. Decoded images are R, G, B, A bytes: passed as
/// they are, red is read as alpha and so on (transparent corners turn opaque, amber turns pink).
/// </summary>
internal static class WindowIcon
{
    /// <summary>Reorders straight RGBA bytes in place into the byte order Silk's SDL window icon reads.</summary>
    public static void ToSdlByteOrder(Span<byte> rgba)
    {
        if (rgba.Length % 4 != 0)
            throw new ArgumentException("RGBA data must be a whole number of 4-byte pixels.", nameof(rgba));
        if (!BitConverter.IsLittleEndian)
            return; // big-endian reads the masks in memory order: R, G, B, A already
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 3]) = (rgba[i + 3], rgba[i]);
            (rgba[i + 1], rgba[i + 2]) = (rgba[i + 2], rgba[i + 1]);
        }
    }

    /// <summary>The channels Silk's surface masks extract from one pixel of <paramref name="bytes"/> (tests, diagnostics).</summary>
    public static (byte R, byte G, byte B, byte A) ReadAsSdl(ReadOnlySpan<byte> bytes)
    {
        var pixel = BitConverter.IsLittleEndian
            ? (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24)
            : (uint)(bytes[3] | bytes[2] << 8 | bytes[1] << 16 | bytes[0] << 24);
        return ((byte)(pixel >> 24), (byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);
    }
}
