using System.Buffers.Binary;

namespace MainframeEngine.Tests.Imaging;

public class PngTests
{
    private static byte[] Pattern(int width, int height, int seed = 1)
    {
        var rng = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                // Gradients exercise Sub/Up/Average/Paeth; noise exercises None.
                pixels[i] = (byte)(x * 7);
                pixels[i + 1] = (byte)(y * 13);
                pixels[i + 2] = (byte)rng.Next(256);
                pixels[i + 3] = (byte)(255 - x);
            }

        return pixels;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(64, 48)]
    [InlineData(257, 31)]
    public void EncodeDecodeRoundTripsExactly(int width, int height)
    {
        var pixels = Pattern(width, height);

        var png = Png.EncodeRgba8(width, height, pixels);
        var image = Png.ReadRgba8(new MemoryStream(png));

        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(pixels, image.Pixels);
    }

    [Fact]
    public void OutputStartsWithSignatureAndHeader()
    {
        var png = Png.EncodeRgba8(5, 4, new byte[5 * 4 * 4]);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(6, png[25]); // RGBA
    }

    [Fact]
    public void CorruptedChunkIsDetected()
    {
        var png = Png.EncodeRgba8(8, 8, Pattern(8, 8));
        png[40] ^= 0xFF; // inside the IDAT payload

        Assert.Throws<InvalidDataException>(() => Png.ReadRgba8(new MemoryStream(png)));
    }

    [Fact]
    public void NonPngIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Png.ReadRgba8(new MemoryStream(new byte[16])));
    }

    [Fact]
    public void MismatchedPixelBufferIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Png.EncodeRgba8(4, 4, new byte[10]));
    }

    [Fact]
    public void FileRoundTripCreatesTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mfe-png-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "image.png");
        try
        {
            var capture = new FrameCapture(16, 9, Pattern(16, 9));
            capture.SavePng(path);

            var image = Png.ReadRgba8(path);
            Assert.Equal(capture.Pixels, image.Pixels);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FrameCaptureValidatesItsBuffer()
    {
        Assert.Throws<ArgumentException>(() => new FrameCapture(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameCapture(0, 2, []));
    }
}
