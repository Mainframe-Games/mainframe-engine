using System.Text;

namespace MainframeEngine.Tests.Imaging;

/// <summary>SVG through mfsvg (ThorVG as Godot uses it, ADR 0112) and Godot's fix_alpha_edges.</summary>
public sealed class SvgTests
{
    private const string HalfRed = """
        <svg xmlns="http://www.w3.org/2000/svg" width="4" height="2" viewBox="0 0 4 2">
          <rect x="0" y="0" width="2" height="2" fill="#ff0000"/>
        </svg>
        """;

    [Fact]
    public void RasterizesStraightAlphaRgbaAtScale()
    {
        var (rgba, w, h) = Svg.Rasterize(Encoding.UTF8.GetBytes(HalfRed));
        Assert.Equal((4, 2), (w, h));
        Assert.Equal([255, 0, 0, 255], rgba[..4]);
        Assert.Equal(0, rgba[3 * 4 + 3]);
        (_, w, h) = Svg.Rasterize(Encoding.UTF8.GetBytes(HalfRed), 2.5f);
        Assert.Equal((10, 5), (w, h));
        Assert.Equal((10, 5), Svg.PixelSize(Encoding.UTF8.GetBytes(HalfRed), 2.5f));
        Assert.Throws<InvalidDataException>(() => Svg.Rasterize("<svg"u8));
    }

    [Fact]
    public void FixAlphaEdgesCopiesTheNearestOpaqueColourIntoTransparentPixels()
    {
        // 6×1: opaque blue at x = 0, transparent elsewhere; pixels within 4 take blue, x = 5 keeps its colour.
        byte[] rgba = [0, 0, 255, 255, 9, 9, 9, 0, 9, 9, 9, 10, 9, 9, 9, 0, 9, 9, 9, 0, 9, 9, 9, 0];
        ImageOps.FixAlphaEdges(rgba, 6, 1);
        Assert.Equal([0, 0, 255, 0], rgba[4..8]);
        Assert.Equal([0, 0, 255, 10], rgba[8..12]);   // alpha kept
        Assert.Equal([0, 0, 255, 0], rgba[16..20]);   // distance 4
        Assert.Equal([9, 9, 9, 0], rgba[20..24]);     // distance 5: unchanged
    }

    [Fact]
    public void TexturesLoadSvgFilesWithScaleAndAlphaBorder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-svg-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, HalfRed);
        try
        {
            var texture = Texture2D.FromFile(path, TextureImportSettings.Default with { SvgScale = 2f, FixAlphaBorder = true });
            Assert.Equal(8, texture.Width);
            Assert.Equal(4, texture.Height);
            var (rgba, w, _) = texture.DecodePixels();
            var transparent = (0 * w + 6) * 4;
            Assert.Equal([255, 0, 0, 0], rgba[transparent..(transparent + 4)]); // red bled into the transparent half
        }
        finally
        {
            File.Delete(path);
        }
    }
}
