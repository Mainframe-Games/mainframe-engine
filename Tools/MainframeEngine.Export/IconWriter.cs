using System.Buffers.Binary;
using StbImageSharp;

namespace MainframeEngine.Export;

/// <summary>
/// Writes the executable and bundle icons from one PNG, in C# (no <c>sips</c>, <c>iconutil</c> or ImageMagick): a Windows
/// <c>.ico</c> with PNG entries (valid since Windows Vista) and a macOS <c>.icns</c> with PNG chunks. Non-square sources are
/// centred on a transparent square; sizes above the source are left out (never enlarged).
/// </summary>
public static class IconWriter
{
    /// <summary>The <c>.ico</c> entry sizes.</summary>
    public static readonly IReadOnlyList<int> IcoSizes = [16, 32, 48, 64, 128, 256];

    /// <summary>The <c>.icns</c> PNG chunk types and their sizes (16 to 1024 px).</summary>
    public static readonly IReadOnlyList<(string Type, int Size)> IcnsChunks =
        [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];

    /// <summary>Decodes a PNG (or any image StbImageSharp reads) to square RGBA8; throws <see cref="InvalidDataException"/> when it cannot.</summary>
    public static (int Size, byte[] Rgba) LoadSquare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImageResult image;
        try
        {
            using var stream = File.OpenRead(path);
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
                    ?? throw new InvalidDataException($"'{path}' could not be decoded.");
        }
        catch (Exception e) when (e is not (InvalidDataException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException($"'{path}' is not an image StbImageSharp reads: {e.Message}", e);
        }

        return Square(image.Width, image.Height, image.Data);
    }

    /// <summary>The image centred on a transparent square of its larger side.</summary>
    public static (int Size, byte[] Rgba) Square(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width == height)
            return (width, rgba);
        var size = Math.Max(width, height);
        var square = new byte[size * size * 4];
        var left = (size - width) / 2;
        var top = (size - height) / 2;
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * width * 4, square, ((top + y) * size + left) * 4, width * 4);
        return (size, square);
    }

    /// <summary>A PNG of the square image at <paramref name="size"/> px (downscaled with <see cref="ImageResize.Downscale"/>).</summary>
    public static byte[] PngAt(int sourceSize, byte[] rgba, int size)
    {
        var (w, h, pixels) = ImageResize.Downscale(sourceSize, sourceSize, rgba, size);
        return Png.EncodeRgba8(w, h, pixels);
    }

    /// <summary>The <c>.ico</c> bytes (PNG entries at <see cref="IcoSizes"/> up to the source size; at least one).</summary>
    public static byte[] Ico(int sourceSize, byte[] rgba)
    {
        var sizes = IcoSizes.Where(s => s <= sourceSize).ToList();
        if (sizes.Count == 0)
            sizes.Add(sourceSize);
        var images = sizes.Select(s => (Size: s, Png: PngAt(sourceSize, rgba, s))).ToArray();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1); // icon
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)images.Length);
        stream.Write(header);
        var offset = 6 + 16 * images.Length;
        Span<byte> entry = stackalloc byte[16];
        foreach (var (size, png) in images)
        {
            entry.Clear();
            entry[0] = (byte)(size >= 256 ? 0 : size); // 0 = 256
            entry[1] = (byte)(size >= 256 ? 0 : size);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1); // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            stream.Write(entry);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
            stream.Write(png);
        return stream.ToArray();
    }

    /// <summary>The <c>.icns</c> bytes (PNG chunks of <see cref="IcnsChunks"/> up to the source size; at least one).</summary>
    public static byte[] Icns(int sourceSize, byte[] rgba)
    {
        var chunks = IcnsChunks.Where(c => c.Size <= sourceSize).ToList();
        if (chunks.Count == 0)
            chunks.Add(IcnsChunks[0]);
        var data = chunks.Select(c => (c.Type, Png: PngAt(sourceSize, rgba, c.Size))).ToArray();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        "icns"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(8 + data.Sum(d => 8 + d.Png.Length)));
        stream.Write(header);
        foreach (var (type, png) in data)
        {
            for (var i = 0; i < 4; i++)
                header[i] = (byte)type[i];
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(8 + png.Length));
            stream.Write(header);
            stream.Write(png);
        }

        return stream.ToArray();
    }

    /// <summary>Writes <c>.ico</c> from a PNG file.</summary>
    public static void WriteIco(string png, string ico)
    {
        var (size, rgba) = LoadSquare(png);
        WriteFile(ico, Ico(size, rgba));
    }

    /// <summary>Writes <c>.icns</c> from a PNG file.</summary>
    public static void WriteIcns(string png, string icns)
    {
        var (size, rgba) = LoadSquare(png);
        WriteFile(icns, Icns(size, rgba));
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }
}
