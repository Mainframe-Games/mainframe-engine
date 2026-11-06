namespace MainframeEngine.Tests.Text;

/// <summary>G11.2: the shared line breaking (<see cref="TextLayout"/>) and <see cref="Font.Fallbacks"/>.</summary>
public sealed class TextLayoutTests
{
    private static Font Lato() => Font.FromData(File.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf")));

    private static Font Mono() => Font.FromData(File.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/RobotoMono-Regular.ttf")));

    private static List<string> Break(Font font, string text, float width, AutowrapMode mode)
    {
        var lines = new List<string>();
        TextLayout.BreakLines(font, text, 16, width, mode, lines);
        return lines;
    }

    [Fact]
    public void NewlinesSplitParagraphsWithoutWrapping()
    {
        Assert.Equal(["one", "two", "", "four"], Break(Lato(), "one\ntwo\r\n\nfour", 0, AutowrapMode.Word));
        Assert.Equal(["a very long line that is not wrapped"], Break(Lato(), "a very long line that is not wrapped", 40, AutowrapMode.Off));
        Assert.Empty(Break(Lato(), "", 100, AutowrapMode.Word));
    }

    [Fact]
    public void WordWrapIsGreedyAndMatchesLabel2D()
    {
        var font = Lato();
        const string text = "The quick brown fox jumps over the lazy dog";
        var width = font.GetStringSize("The quick brown", fontSize: 16).X + 1;
        var lines = Break(font, text, width, AutowrapMode.Word);
        Assert.Equal("The quick brown", lines[0]);
        foreach (var line in lines)
            Assert.True(font.GetStringSize(line, fontSize: 16).X <= width, line);

        var label = new Label2D { Font = font, FontSize = 16, Width = width, Autowrap = AutowrapMode.Word, Text = text };
        Assert.Equal(lines, label.GetLines());
    }

    [Fact]
    public void WordSmartBreaksAWordWiderThanTheLineAndWordLetsItOverflow()
    {
        var font = Lato();
        var width = font.GetStringSize("abcd", fontSize: 16).X;
        Assert.Equal(["abcdefghij"], Break(font, "abcdefghij", width, AutowrapMode.Word));
        var smart = Break(font, "abcdefghij", width, AutowrapMode.WordSmart);
        Assert.True(smart.Count >= 3, string.Join('|', smart));
        Assert.Equal("abcdefghij", string.Concat(smart));
        Assert.Equal("abcdefghij", string.Concat(Break(font, "abcdefghij", width, AutowrapMode.Arbitrary)));
    }

    [Fact]
    public void FallbackFontsDrawCharactersThePrimaryFaceLacks()
    {
        var lato = Lato();
        var plain = new List<GlyphQuad>();
        lato.Layout("Ж", default, 32, 0, plain);
        var notdef = Assert.Single(plain).Rect.Size;

        lato.Fallbacks = [Mono()];
        var withFallback = new List<GlyphQuad>();
        lato.Layout("AЖ", default, 32, 0, withFallback);
        Assert.Equal(2, withFallback.Count);
        Assert.NotEqual(notdef, withFallback[1].Rect.Size); // Roboto Mono's Zhe, not Lato's .notdef box
        Assert.Same(withFallback[0].Page, withFallback[1].Page); // fallback glyphs share the primary font's atlas
        Assert.Equal(lato.MeasureLine("AЖ", 32, null), lato.GetStringSize("AЖ", fontSize: 32).X);
        Assert.True(lato.MeasureLine("Ж", 32, null) > 0);

        // Extra fallbacks (the locale's) are searched after the font's own, and give the same glyph.
        var other = Lato();
        var extra = new List<GlyphQuad>();
        other.Layout("AЖ", default, 32, 0, extra, [Mono()]);
        Assert.Equal(withFallback[1].Rect, extra[1].Rect);
    }

    [Fact]
    public void LatinTextPlacesTheSameGlyphsWithFallbacksSet()
    {
        var a = Lato();
        var b = Lato();
        b.Fallbacks = [Mono()];
        var qa = new List<GlyphQuad>();
        var qb = new List<GlyphQuad>();
        a.Layout("Wavy AV text", new System.Numerics.Vector2(3, 20), 24, 0, qa);
        b.Layout("Wavy AV text", new System.Numerics.Vector2(3, 20), 24, 0, qb);
        Assert.Equal(qa.Select(q => (q.Rect, q.Source)), qb.Select(q => (q.Rect, q.Source)));
    }
}
