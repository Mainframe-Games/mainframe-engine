using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace MainframeEngine;

/// <summary>An RGBA8 image decoded by <see cref="Png.ReadRgba8(Stream)"/>; rows are top first.</summary>
public sealed record PngImage(int Width, int Height, byte[] Pixels);

/// <summary>A 16-bit greyscale image decoded by <see cref="Png.ReadGray16(Stream)"/>; rows are top first.</summary>
public sealed record PngGray16Image(int Width, int Height, ushort[] Pixels);

/// <summary>
/// Minimal in-house PNG codec for screenshots, golden images and terrain layers. Writes 8-bit RGBA and 16-bit grey,
/// non-interlaced, with per-row adaptive filtering. <see cref="ReadRgba8(Stream)"/> reads 8-bit RGB/RGBA;
/// <see cref="ReadGray16(Stream)"/> reads 8- or 16-bit grey, grey + alpha, RGB or RGBA (the first channel). Both
/// refuse interlaced images and other formats with <see cref="NotSupportedException"/>.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const byte ColorTypeGray = 0;
    private const byte ColorTypeRgb = 2;
    private const byte ColorTypeGrayAlpha = 4;
    private const byte ColorTypeRgba = 6;

    private static readonly uint[] CrcTable = CreateCrcTable();

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>Encodes tightly packed RGBA8 pixels (top row first) as a PNG.</summary>
    public static void WriteRgba8(Stream stream, int width, int height, ReadOnlySpan<byte> rgba,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = checked(width * 4);
        if (rgba.Length != checked(stride * height))
            throw new ArgumentException($"Expected {stride * height} bytes of RGBA8, got {rgba.Length}.", nameof(rgba));

        WriteImage(stream, width, height, bitDepth: 8, ColorTypeRgba, rgba, stride, compression);
    }

    /// <summary>
    /// Encodes 16-bit greyscale samples (top row first) as a PNG (bit depth 16, colour type 0): terrain heightmaps.
    /// </summary>
    public static void WriteGray16(Stream stream, int width, int height, ReadOnlySpan<ushort> gray,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (gray.Length != checked(width * height))
            throw new ArgumentException($"Expected {width * height} samples, got {gray.Length}.", nameof(gray));

        var stride = checked(width * 2);
        var bytes = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            var data = bytes.AsSpan(0, stride * height);
            for (var i = 0; i < gray.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(data[(i * 2)..], gray[i]);
            WriteImage(stream, width, height, bitDepth: 16, ColorTypeGray, data, stride, compression);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Writes a 16-bit greyscale PNG file, creating its directory if needed.</summary>
    public static void WriteGray16(string path, int width, int height, ReadOnlySpan<ushort> gray)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        WriteGray16(stream, width, height, gray);
    }

    private static void WriteImage(Stream stream, int width, int height, byte bitDepth, byte colorType, ReadOnlySpan<byte> rows,
        int stride, CompressionLevel compression)
    {
        stream.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0;              // compression: deflate
        ihdr[11] = 0;              // filter method: adaptive
        ihdr[12] = 0;              // no interlace
        WriteChunk(stream, "IHDR"u8, ihdr);

        var bpp = BytesPerPixel(bitDepth, colorType);
        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, compression, leaveOpen: true))
                WriteFilteredRows(z, rows, stride, height, bpp);
            WriteChunk(stream, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(stream, "IEND"u8, []);
    }

    /// <summary>Encodes tightly packed RGBA8 pixels (top row first) and returns the PNG bytes.</summary>
    public static byte[] EncodeRgba8(int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var ms = new MemoryStream();
        WriteRgba8(ms, width, height, rgba);
        return ms.ToArray();
    }

    /// <summary>Writes an RGBA8 PNG file, creating its directory if needed.</summary>
    public static void WriteRgba8(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        WriteRgba8(stream, width, height, rgba);
    }

    private static void WriteFilteredRows(Stream output, ReadOnlySpan<byte> rgba, int stride, int height, int bpp)
    {
        // One candidate row per filter type (0..4), each prefixed with its filter byte.
        var pool = ArrayPool<byte>.Shared;
        var candidates = pool.Rent((stride + 1) * 5);
        try
        {
            ReadOnlySpan<byte> prev = default;
            for (var y = 0; y < height; y++)
            {
                var row = rgba.Slice(y * stride, stride);
                var best = 0;
                var bestScore = long.MaxValue;
                for (var filter = 0; filter < 5; filter++)
                {
                    var dst = candidates.AsSpan(filter * (stride + 1), stride + 1);
                    dst[0] = (byte)filter;
                    var score = Filter(filter, row, prev, dst[1..], bpp);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = filter;
                    }
                }

                output.Write(candidates, best * (stride + 1), stride + 1);
                prev = row;
            }
        }
        finally
        {
            pool.Return(candidates);
        }
    }

    // Applies one filter and returns the "minimum sum of absolute differences" heuristic score.
    private static long Filter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, Span<byte> dst, int bpp)
    {
        long score = 0;
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = prev.IsEmpty ? 0 : prev[i];
            int c = i >= bpp && !prev.IsEmpty ? prev[i - bpp] : 0;
            var predicted = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            var v = (byte)(row[i] - predicted);
            dst[i] = v;
            score += (sbyte)v < 0 ? -(sbyte)v : v;
        }

        return score;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        type.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(data);

        var crc = UpdateCrc(0xFFFFFFFFu, type);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    /// <summary>Decodes an 8-bit RGB or RGBA, non-interlaced PNG file into RGBA8.</summary>
    public static PngImage ReadRgba8(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadRgba8(stream);
    }

    /// <summary>Decodes an 8-bit RGB or RGBA, non-interlaced PNG into RGBA8.</summary>
    public static PngImage ReadRgba8(Stream stream)
    {
        var (width, height, bitDepth, colorType, raw, stride) = Decode(stream, static (bitDepth, colorType) =>
            bitDepth == 8 && colorType is ColorTypeRgba or ColorTypeRgb);

        var bpp = BytesPerPixel(bitDepth, colorType);
        var rgba = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = rgba.AsSpan(y * width * 4, width * 4);
            if (bpp == 4)
            {
                src.CopyTo(dst);
                continue;
            }

            for (int x = 0, s = 0, d = 0; x < width; x++, s += 3, d += 4)
            {
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = 255;
            }
        }

        return new PngImage(width, height, rgba);
    }

    /// <summary>Decodes a greyscale (or colour, by its first channel) PNG file into 16-bit samples.</summary>
    public static PngGray16Image ReadGray16(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadGray16(stream);
    }

    /// <summary>
    /// Decodes an 8- or 16-bit grey, grey + alpha, RGB or RGBA non-interlaced PNG into 16-bit samples of its first
    /// channel (8-bit values are widened: <c>v · 257</c>, so 255 becomes 65535). Heightmaps from other tools load
    /// through it.
    /// </summary>
    public static PngGray16Image ReadGray16(Stream stream)
    {
        var (width, height, bitDepth, colorType, raw, stride) = Decode(stream, static (bitDepth, colorType) =>
            bitDepth is 8 or 16 && colorType is ColorTypeGray or ColorTypeGrayAlpha or ColorTypeRgb or ColorTypeRgba);

        var bpp = BytesPerPixel(bitDepth, colorType);
        var pixels = new ushort[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = pixels.AsSpan(y * width, width);
            if (bitDepth == 16)
                for (var x = 0; x < width; x++)
                    dst[x] = BinaryPrimitives.ReadUInt16BigEndian(src[(x * bpp)..]);
            else
                for (var x = 0; x < width; x++)
                    dst[x] = (ushort)(src[x * bpp] * 257);
        }

        return new PngGray16Image(width, height, pixels);
    }

    /// <summary>
    /// Reads the chunks, checks the header with <paramref name="supported"/>, inflates and unfilters the image data.
    /// Returns the rows as <c>[filter byte][stride bytes]</c> (the filter bytes are stale after unfiltering).
    /// </summary>
    private static (int Width, int Height, byte BitDepth, byte ColorType, byte[] Raw, int Stride) Decode(Stream stream,
        Func<byte, byte, bool> supported)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> sig = stackalloc byte[8];
        stream.ReadExactly(sig);
        if (!sig.SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0;
        byte colorType = 0, bitDepth = 0;
        var sawHeader = false;
        using var compressed = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        Span<byte> crcBytes = stackalloc byte[4];

        while (true)
        {
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0)
                throw new InvalidDataException("Corrupt PNG chunk length.");
            var type = header[4..8];

            var data = new byte[length];
            stream.ReadExactly(data);
            stream.ReadExactly(crcBytes);
            var crc = UpdateCrc(UpdateCrc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(crcBytes))
                throw new InvalidDataException($"PNG chunk {System.Text.Encoding.ASCII.GetString(type)} has a bad CRC.");

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13)
                    throw new InvalidDataException("Corrupt PNG header.");
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                bitDepth = data[8];
                colorType = data[9];
                var interlace = data[12];
                if (width <= 0 || height <= 0)
                    throw new InvalidDataException("PNG has invalid dimensions.");
                if (!supported(bitDepth, colorType) || interlace != 0)
                    throw new NotSupportedException(
                        $"Unsupported PNG format (bit depth {bitDepth}, color type {colorType}, interlace {interlace}).");
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            // Ancillary chunks (gAMA, sRGB, pHYs, tEXt, ...) are ignored.
        }

        if (!sawHeader)
            throw new InvalidDataException("PNG has no IHDR chunk.");

        var bpp = BytesPerPixel(bitDepth, colorType);
        var stride = checked(width * bpp);
        var raw = new byte[checked((stride + 1) * height)];
        compressed.Position = 0;
        using (var z = new ZLibStream(compressed, CompressionMode.Decompress))
            z.ReadExactly(raw);

        Unfilter(raw, stride, height, bpp);
        return (width, height, bitDepth, colorType, raw, stride);
    }

    private static int BytesPerPixel(byte bitDepth, byte colorType)
    {
        var channels = colorType switch
        {
            ColorTypeGray => 1,
            ColorTypeGrayAlpha => 2,
            ColorTypeRgb => 3,
            _ => 4,
        };
        return channels * (bitDepth / 8);
    }

    // Reverses the per-row filters in place; each row is [filter byte][stride bytes].
    private static void Unfilter(Span<byte> raw, int stride, int height, int bpp)
    {
        Span<byte> prev = default;
        for (var y = 0; y < height; y++)
        {
            var line = raw.Slice(y * (stride + 1), stride + 1);
            var filter = line[0];
            var row = line[1..];
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0;
                int b = prev.IsEmpty ? 0 : prev[i];
                int c = i >= bpp && !prev.IsEmpty ? prev[i - bpp] : 0;
                row[i] += filter switch
                {
                    0 => 0,
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) >> 1),
                    4 => (byte)Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filter}."),
                };
            }

            prev = row;
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        var table = CrcTable;
        foreach (var t in data)
            crc = table[(crc ^ t) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
