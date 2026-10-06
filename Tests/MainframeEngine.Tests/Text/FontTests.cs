using System.Numerics;

namespace MainframeEngine.Tests.Text;

/// <summary>
/// Canvas text (ADR 0118) on the engine's Lato: the TrueType reader against fontTools' values, FreeType-style metrics,
/// GPOS kerning, coverage and outlines, and DrawString's quads.
/// </summary>
public sealed class FontTests : IDisposable
{
    private static readonly byte[] Lato = File.ReadAllBytes(ContentPaths.Resolve("Content/UI/fonts/LatoLatin-Regular.ttf"));
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;

    public FontTests()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(1000, 1000));
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    [Fact]
    public void TheReaderMatchesFontToolsTables()
    {
        var face = new TrueTypeFont(Lato);
        Assert.Equal((2000, 1974, -426), (face.UnitsPerEm, face.Ascender, face.Descender));
        Assert.Equal(3, face.GlyphIndex('A'));
        Assert.Equal(382, face.GlyphIndex('é'));
        Assert.Equal(1354, face.AdvanceWidth(3));
        Assert.Equal(-244, face.Kerning(face.GlyphIndex('T'), face.GlyphIndex('o')));
        Assert.Equal(0, face.GlyphIndex(0x4E2D)); // not in the font: .notdef
        Assert.NotEmpty(face.Outline(382));          // composite (e + acute)
        Assert.Empty(face.Outline(face.GlyphIndex(' ')));
        var bounds = face.Outline(3).SelectMany(c => c.Points).Aggregate((Min: new Vector2(float.MaxValue), Max: new Vector2(float.MinValue)),
            (b, p) => (Vector2.Min(b.Min, p), Vector2.Max(b.Max, p)));
        Assert.Equal((new Vector2(6, 0), new Vector2(1348, 1433)), bounds); // fontTools: A bbox
    }

    [Fact]
    public void MetricsAreTheHheaValuesScaledAndRounded()
    {
        var font = Font.FromData(Lato);
        Assert.Equal(20, font.GetAscent(20));  // 19.74
        Assert.Equal(4, font.GetDescent(20));  // 4.26
        Assert.Equal(24, font.GetHeight(20));
        // T + o advances with the pair's kerning: (1181 − 244 + 1134) / 100.
        Assert.Equal(new Vector2(20.71f, 24), font.GetStringSize("To", fontSize: 20));
        Assert.Equal(new Vector2(300, 24), font.GetStringSize("To", HorizontalAlignment.Fill, 300, 20));
    }

    [Fact]
    public void AStemIsFullyCoveredInsideAndEmptyOutside()
    {
        var face = new TrueTypeFont(Lato);
        // 'I' at 2000 units = 100 px: the stem spans x 1.83..3.77 px from the pen, y 0..14.33 px up.
        var bitmap = GlyphRasterizer.Rasterize(face.Outline(face.GlyphIndex('I')), 0.01f * 10, 0);
        int Alpha(int x, int up) => bitmap.Alpha[(bitmap.Top - up) * bitmap.Width + x - bitmap.Left];
        Assert.Equal(17, bitmap.Left);     // floor(18.3) − 1 px of padding
        Assert.Equal(255, Alpha(28, 70));  // inside the stem (18.3..37.7 px at scale 0.1)
        Assert.Equal(0, Alpha(17, 70));
        Assert.Equal(0, Alpha(38, 70));
        Assert.Equal(179, Alpha(18, 70));  // 70 % of the pixel 18..19
        var sum = bitmap.Alpha.Sum(a => a / 255.0);
        Assert.Equal((37.7 - 18.3) * 143.3, sum, 0); // covered area = the stem's area, to a pixel
    }

    [Fact]
    public void AnOutlineDilatesTheGlyphByAQuarterOfItsSize()
    {
        var face = new TrueTypeFont(Lato);
        var outline = face.Outline(face.GlyphIndex('I'));
        var plain = GlyphRasterizer.Rasterize(outline, 0.01f, 0);
        var stroked = GlyphRasterizer.Rasterize(outline, 0.01f, 8 / 4f);
        Assert.True(stroked.Width >= plain.Width + 4);
        var row = stroked.Top - 7; // mid-stem, 7 px above the baseline
        // The stem is 1.83..3.77 px; 2 px of stroke reach 5.77 px: pixel 5 is 77 % covered, pixel 6 not at all.
        Assert.InRange(stroked.Alpha[row * stroked.Width + (5 - stroked.Left)], 180, 210);
        Assert.Equal(255, stroked.Alpha[row * stroked.Width + (4 - stroked.Left)]);
        Assert.Equal(0, stroked.Alpha[row * stroked.Width + (6 - stroked.Left)]);
    }

    private sealed class Text : Node2D
    {
        public Font? Font;
        protected override void OnDraw()
        {
            DrawStringOutline(Font!, new Vector2(10, 30), "A b", fontSize: 20, size: 4, modulate: new Vector4(0, 0, 0, 1));
            DrawString(Font!, new Vector2(10, 30), "A b", HorizontalAlignment.Center, 200, 20);
        }
    }

    [Fact]
    public void DrawStringEmitsOneQuadPerVisibleGlyphFromTheAtlas()
    {
        var root = new Text { Name = "Text", Font = Font.FromData(Lato) };
        _tree.ChangeScene(root);
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var frame = _server.Frame;
        var batch = Assert.Single(frame.Batches); // outline and text share the atlas page
        Assert.Equal(4 * 6, batch.IndexCount);   // two glyphs each, the space draws nothing
        Assert.NotNull(batch.Texture);
        // Centred: (200 − advance) / 2 floored, from x 10; the outlined 'A' starts 2 px further left than the text's.
        var advance = root.Font.GetStringSize("A b", fontSize: 20).X;
        var xs = Enumerable.Range(0, (int)batch.IndexCount).Select(i => frame.Vertices[(int)frame.Indices[batch.FirstIndex + i]].Position.X).ToList();
        Assert.Contains(xs, x => x <= 10 + MathF.Floor((200 - advance) / 2));
    }
}
