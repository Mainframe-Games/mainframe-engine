using System.Buffers.Binary;
using System.IO.Compression;

namespace MainframeEngine.Tests.Imaging;

public class PngGray16Tests
{
    private static ushort[] Ramp(int width, int height)
    {
        var rng = new Random(7);
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)(i % 3 == 0 ? rng.Next(65536) : i * 257 % 65536);
        return pixels;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(65, 65)]
    [InlineData(257, 9)]
    public void Gray16RoundTripsExactly(int width, int height)
    {
        var pixels = Ramp(width, height);
        using var stream = new MemoryStream();
        Png.WriteGray16(stream, width, height, pixels);

        var bytes = stream.ToArray();
        Assert.Equal(16, bytes[24]); // bit depth
        Assert.Equal(0, bytes[25]);  // grey
        var image = Png.ReadGray16(new MemoryStream(bytes));
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal(pixels, image.Pixels);
    }

    [Fact]
    public void FileRoundTripCreatesTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mfe-png16-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "nested", "height.png");
            var pixels = Ramp(17, 17);
            Png.WriteGray16(path, 17, 17, pixels);
            Assert.Equal(pixels, Png.ReadGray16(path).Pixels);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // A PNG from a different encoder: unfiltered rows (filter 0), written here by hand.
    private static byte[] Handmade(int width, int height, byte bitDepth, byte colorType, byte[] samples, byte interlace = 0)
    {
        var channels = colorType switch { 0 => 1, 4 => 2, 2 => 3, _ => 4 };
        var stride = (width * channels * bitDepth + 7) / 8;
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(samples, y * stride, stride);
        }

        using var z = new MemoryStream();
        using (var zlib = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw.ToArray());

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlace;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
            s.Write(header);
            s.Write(data);
            var crc = Crc(Crc(0xFFFFFFFFu, header.AsSpan(4)), data) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
            s.Write(tail);
        }

        static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return crc;
        }
    }

    [Fact]
    public void ReadsAnUnfilteredSixteenBitFileFromAnotherEncoder()
    {
        var samples = new byte[] { 0x00, 0x00, 0x12, 0x34, 0xFF, 0xFF, 0x80, 0x01 };
        var image = Png.ReadGray16(new MemoryStream(Handmade(2, 2, 16, 0, samples)));
        Assert.Equal(new ushort[] { 0, 0x1234, 0xFFFF, 0x8001 }, image.Pixels);
    }

    [Fact]
    public void EightBitGreyIsWidened()
    {
        var image = Png.ReadGray16(new MemoryStream(Handmade(3, 1, 8, 0, [0, 128, 255])));
        Assert.Equal(new ushort[] { 0, 128 * 257, 65535 }, image.Pixels);
    }

    [Fact]
    public void SixteenBitRgbaReadsItsRedChannel()
    {
        // Two pixels: R = 0xABCD then 0x0102; G, B, A differ.
        var samples = new byte[] { 0xAB, 0xCD, 1, 1, 2, 2, 3, 3, 0x01, 0x02, 9, 9, 9, 9, 9, 9 };
        var image = Png.ReadGray16(new MemoryStream(Handmade(2, 1, 16, 6, samples)));
        Assert.Equal(new ushort[] { 0xABCD, 0x0102 }, image.Pixels);
    }

    [Fact]
    public void EightBitRgbaReadsItsRedChannel()
    {
        var png = Png.EncodeRgba8(2, 1, [10, 20, 30, 255, 200, 0, 0, 0]);
        Assert.Equal(new ushort[] { 10 * 257, 200 * 257 }, Png.ReadGray16(new MemoryStream(png)).Pixels);
    }

    [Fact]
    public void InterlacedAndUnsupportedFormatsAreRefused()
    {
        Assert.Throws<NotSupportedException>(() => Png.ReadGray16(new MemoryStream(Handmade(1, 1, 16, 0, [0, 0], interlace: 1))));
        Assert.Throws<NotSupportedException>(() => Png.ReadGray16(new MemoryStream(Handmade(8, 1, 1, 0, [0]))));
        // The RGBA8 reader still refuses 16-bit images.
        Assert.Throws<NotSupportedException>(() => Png.ReadRgba8(new MemoryStream(Handmade(1, 1, 16, 0, [0, 0]))));
    }

    [Fact]
    public void MismatchedSampleCountIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Png.WriteGray16(new MemoryStream(), 4, 4, new ushort[10]));
    }
}
