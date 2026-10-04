using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace MainframeEngine;

/// <summary>An RGBA8 image decoded by <see cref="Png.ReadRgba8(Stream)"/>; rows are top first.</summary>
public sealed record PngImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Minimal in-house PNG codec for screenshots and golden images. Writes 8-bit RGBA, non-interlaced,
/// with per-row adaptive filtering. Reads 8-bit RGB/RGBA, non-interlaced (everything this encoder and
/// common tools emit for screenshots); other formats throw <see cref="NotSupportedException"/>.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const byte ColorTypeRgb = 2;
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

        stream.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;               // bit depth
        ihdr[9] = ColorTypeRgba;
        ihdr[10] = 0;              // compression: deflate
        ihdr[11] = 0;              // filter method: adaptive
        ihdr[12] = 0;              // no interlace
        WriteChunk(stream, "IHDR"u8, ihdr);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, compression, leaveOpen: true))
                WriteFilteredRows(z, rgba, stride, height);
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

    private static void WriteFilteredRows(Stream output, ReadOnlySpan<byte> rgba, int stride, int height)
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
                    var score = Filter(filter, row, prev, dst[1..]);
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
    private static long Filter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, Span<byte> dst)
    {
        const int bpp = 4;
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
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> sig = stackalloc byte[8];
        stream.ReadExactly(sig);
        if (!sig.SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0;
        byte colorType = 0;
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
                var bitDepth = data[8];
                colorType = data[9];
                var interlace = data[12];
                if (width <= 0 || height <= 0)
                    throw new InvalidDataException("PNG has invalid dimensions.");
                if (bitDepth != 8 || (colorType != ColorTypeRgba && colorType != ColorTypeRgb) || interlace != 0)
                    throw new NotSupportedException(
                        $"Only 8-bit RGB/RGBA non-interlaced PNGs are supported (bit depth {bitDepth}, color type {colorType}, interlace {interlace}).");
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

        var bpp = colorType == ColorTypeRgba ? 4 : 3;
        var stride = checked(width * bpp);
        var raw = new byte[checked((stride + 1) * height)];
        compressed.Position = 0;
        using (var z = new ZLibStream(compressed, CompressionMode.Decompress))
            z.ReadExactly(raw);

        Unfilter(raw, stride, height, bpp);

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
