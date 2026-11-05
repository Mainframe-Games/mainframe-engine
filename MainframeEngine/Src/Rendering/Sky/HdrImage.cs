using StbImageSharp;

namespace MainframeEngine;

/// <summary>
/// Radiance <c>.hdr</c> images for HDR sky panoramas (G6.2): decoded through StbImageSharp's float path into linear
/// RGBA half floats (<c>R16G16B16A16_SFLOAT</c>, 8 bytes per texel), so the sky and the light it bakes keep values above 1.
/// </summary>
internal static class HdrImage
{
    /// <summary>True when <paramref name="path"/> names a Radiance HDR file (by extension).</summary>
    public static bool IsHdr(string? path) => path is not null && path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The image's texels as RGBA half floats (alpha 1), row after row from the top; values are clamped to the largest
    /// finite half (65 504) and negatives to 0.
    /// </summary>
    public static (byte[] Texels, int Width, int Height) DecodeHalf(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var image = ImageResultFloat.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha)
                    ?? throw new InvalidDataException("Not a decodable HDR image.");
        var data = image.Data;
        var texels = new byte[image.Width * image.Height * 8];
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(texels.AsSpan());
        for (var i = 0; i < halves.Length; i++)
        {
            var v = data[i];
            halves[i] = (Half)(float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 65504f));
        }

        return (texels, image.Width, image.Height);
    }
}
