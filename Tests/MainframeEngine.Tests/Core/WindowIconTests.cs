namespace MainframeEngine.Tests.Core;

public sealed class WindowIconTests
{
    [Fact]
    public void ReorderedPixelsReadBackAsTheOriginalColoursThroughSilksSurfaceMasks()
    {
        // Four test pixels: pure red, green, blue and fully transparent black, plus the logo's amber core.
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0, 245, 158, 11, 255];
        var expected = new (byte, byte, byte, byte)[] { (255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (0, 0, 0, 0), (245, 158, 11, 255) };

        // As decoded (no reordering), the masks swap channels: red reads as alpha, amber turns pink.
        Assert.Equal((255, 11, 158, 245), WindowIcon.ReadAsSdl(rgba.AsSpan(16, 4)));

        WindowIcon.ToSdlByteOrder(rgba);

        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], WindowIcon.ReadAsSdl(rgba.AsSpan(i * 4, 4)));
    }

    [Fact]
    public unsafe void SdlReadsTheReorderedPixelsAsTheOriginalColours()
    {
        // The exact surface Silk's SdlWindow.SetWindowIcon creates (same masks), read back through SDL itself.
        SilkNativeResolver.Install();
        var sdl = Silk.NET.SDL.Sdl.GetApi();
        byte[] rgba = [255, 0, 0, 255, 0, 0, 0, 0, 245, 158, 11, 255, 23, 37, 84, 128];
        WindowIcon.ToSdlByteOrder(rgba);
        fixed (byte* pixels = rgba)
        {
            var surface = sdl.CreateRGBSurfaceFrom(pixels, 4, 1, 32, 16, 0xFF000000u, 0x00FF0000u, 0x0000FF00u, 0x000000FFu);
            Assert.True(surface is not null, sdl.GetErrorS());
            try
            {
                var colours = new (byte, byte, byte, byte)[4];
                for (var i = 0; i < 4; i++)
                {
                    byte r, g, b, a;
                    sdl.GetRGBA(((uint*)surface->Pixels)[i], surface->Format, &r, &g, &b, &a);
                    colours[i] = (r, g, b, a);
                }

                Assert.Equal([(255, 0, 0, 255), (0, 0, 0, 0), (245, 158, 11, 255), (23, 37, 84, 128)], colours);
            }
            finally
            {
                sdl.FreeSurface(surface);
            }
        }
    }

    [Fact]
    public void PartialPixelsAreRejected() =>
        Assert.Throws<ArgumentException>(() => WindowIcon.ToSdlByteOrder(new byte[6]));
}
