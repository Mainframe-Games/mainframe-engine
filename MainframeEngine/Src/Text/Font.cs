using System.Numerics;

namespace MainframeEngine;

/// <summary>How text sits in its width (Godot's <c>HorizontalAlignment</c>).</summary>
public enum HorizontalAlignment : byte
{
    Left,
    Center,
    Right,
    Fill,
}

/// <summary>Where text sits vertically against its anchor (Godot's <c>VerticalAlignment</c>, without <c>Fill</c>).</summary>
public enum VerticalAlignment : byte
{
    Top,
    Center,
    Bottom,
}

/// <summary>How a text node breaks lines that do not fit its width (Godot's <c>TextServer.AutowrapMode</c>).</summary>
public enum AutowrapMode : byte
{
    /// <summary>No wrapping: only newlines break lines.</summary>
    Off,

    /// <summary>Breaks between any two characters.</summary>
    Arbitrary,

    /// <summary>Breaks between words; a word wider than the line overflows it.</summary>
    Word,

    /// <summary>Like <see cref="Word"/>, but a word wider than the line is broken between characters.</summary>
    WordSmart,
}

/// <summary>
/// A TrueType font for canvas text (Godot's <c>FontFile</c> as a 2D font, ADR 0118): <c>.ttf</c> files load through
/// <see cref="ResourceLoader"/>. Glyphs are rasterised in managed code at the requested pixel size (unhinted, greyscale,
/// pen positions rounded to whole pixels) into shared atlas pages, once per size and outline width; GPOS/kern pair
/// kerning is applied. Metrics follow FreeType's rounding: ascent and descent are the <c>hhea</c> values scaled and
/// rounded. A character the face lacks comes from the first of <see cref="Fallbacks"/> that has it (G11.2); its glyphs
/// share this font's atlas pages. No shaping beyond kerning; one line per call (multi-line layout is the nodes', through
/// <c>TextLayout</c>).
/// </summary>
[EditorIcon("typography")]
public sealed class Font : Resource
{
    private const int PageSize = 512;
    private TrueTypeFont? _face;
    private readonly Dictionary<(int Size, int Outline), Dictionary<int, GlyphEntry>> _glyphs = [];
    private readonly List<TrueTypeFont> _faceIds = []; // faces whose glyphs this font's atlas holds; the index is in the cache key
    private readonly List<AtlasPage> _pages = [];
    private readonly List<GlyphQuad> _drawScratch = [];

    /// <summary>The width and height of an atlas page in pixels (glyph regions are in page pixels).</summary>
    internal const int AtlasPageSize = PageSize;

    /// <summary>The font file (project path).</summary>
    [Export(File = "*.ttf")]
    public string FontPath { get; set; } = "";

    /// <summary>
    /// Fonts searched in order for characters this face lacks (Godot's <c>fallbacks</c>), for example Noto Sans JP for
    /// Japanese text. Their metrics (ascent, descent) are not used: lines keep this font's.
    /// </summary>
    [Export]
    public Font[] Fallbacks { get; set; } = [];

    private sealed class AtlasPage
    {
        public readonly byte[] Rgba = new byte[PageSize * PageSize * 4];
        public readonly Texture2D Texture;
        public int ShelfX, ShelfY, ShelfHeight;
        public bool Dirty;

        public AtlasPage()
        {
            for (var i = 0; i < Rgba.Length; i += 4)
                (Rgba[i], Rgba[i + 1], Rgba[i + 2]) = (255, 255, 255);
            Texture = Texture2D.FromPixels(PageSize, PageSize, Rgba);
        }
    }

    private readonly record struct GlyphEntry(int Page, Rect2 Source, int Left, int Top);

    /// <summary>A font from a <c>.ttf</c> file's bytes (no path; not saved with scenes).</summary>
    public static Font FromData(byte[] data) => new() { _face = new TrueTypeFont(data) };

    /// <summary>A font from a <c>.ttf</c> file (path resolved by <see cref="AssetDatabase.Current"/>).</summary>
    public static Font FromFile(string path)
    {
        var font = new Font { FontPath = path };
        font._face = new TrueTypeFont(ContentFiles.ReadAllBytes(AssetDatabase.Current.ToAbsolutePath(path)));
        return font;
    }

    private TrueTypeFont Face => _face ??= new TrueTypeFont(ContentFiles.ReadAllBytes(AssetDatabase.Current.ToAbsolutePath(FontPath)));

    private float Scale(int size) => (float)size / Face.UnitsPerEm;

    /// <summary>Pixels above the baseline at <paramref name="fontSize"/>.</summary>
    public float GetAscent(int fontSize = 16) => MathF.Round(Face.Ascender * Scale(fontSize));

    /// <summary>Pixels below the baseline at <paramref name="fontSize"/>.</summary>
    public float GetDescent(int fontSize = 16) => MathF.Round(-Face.Descender * Scale(fontSize));

    /// <summary>Ascent plus descent (Godot's <c>get_height</c>).</summary>
    public float GetHeight(int fontSize = 16) => GetAscent(fontSize) + GetDescent(fontSize);

    /// <summary>The size of one line of <paramref name="text"/>: its advance width (or <paramref name="width"/> when filling), and the font height.</summary>
    public Vector2 GetStringSize(string text, HorizontalAlignment alignment = HorizontalAlignment.Left, float width = -1, int fontSize = 16)
    {
        ArgumentNullException.ThrowIfNull(text);
        var advance = Advance(text, fontSize);
        return new Vector2(alignment == HorizontalAlignment.Fill && width > 0 ? width : advance, GetHeight(fontSize));
    }

    private float Advance(string text, int size, IReadOnlyList<Font>? extraFallbacks = null)
    {
        var pen = 0f;
        var previous = -1;
        TrueTypeFont? previousFace = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var (face, glyph) = Resolve(rune.Value, extraFallbacks);
            var scale = (float)size / face.UnitsPerEm;
            if (previous >= 0 && ReferenceEquals(face, previousFace))
                pen += face.Kerning(previous, glyph) * scale;
            pen += face.AdvanceWidth(glyph) * scale;
            previous = glyph;
            previousFace = face;
        }

        return pen;
    }

    /// <summary>The advance width of one line in pixels, with <paramref name="extraFallbacks"/> searched after <see cref="Fallbacks"/>.</summary>
    internal float MeasureLine(string text, int fontSize, IReadOnlyList<Font>? extraFallbacks) =>
        string.IsNullOrEmpty(text) ? 0f : Advance(text, fontSize, extraFallbacks);

    // The face and glyph that draw a character: this face, then Fallbacks, then the caller's extra fonts; this face's
    // .notdef (glyph 0) when none has it.
    private (TrueTypeFont Face, int Glyph) Resolve(int codepoint, IReadOnlyList<Font>? extraFallbacks)
    {
        var face = Face;
        var glyph = face.GlyphIndex(codepoint);
        if (glyph != 0)
            return (face, glyph);
        foreach (var fallback in Fallbacks)
            if (TryFallback(fallback, codepoint, out var found))
                return found;
        if (extraFallbacks is not null)
            for (var i = 0; i < extraFallbacks.Count; i++)
                if (TryFallback(extraFallbacks[i], codepoint, out var found))
                    return found;
        return (face, 0);
    }

    private bool TryFallback(Font? fallback, int codepoint, out (TrueTypeFont Face, int Glyph) found)
    {
        found = default;
        if (fallback is null || ReferenceEquals(fallback, this))
            return false;
        var face = fallback.Face;
        var glyph = face.GlyphIndex(codepoint);
        if (glyph == 0)
            return false;
        found = (face, glyph);
        return true;
    }

    private int FaceId(TrueTypeFont face)
    {
        for (var i = 0; i < _faceIds.Count; i++)
            if (ReferenceEquals(_faceIds[i], face))
                return i;
        _faceIds.Add(face);
        return _faceIds.Count - 1;
    }

    /// <summary>
    /// Rasterises one line of <paramref name="text"/> on the CPU into an alpha bitmap (no atlas, no GPU): what the editor's
    /// video titles are drawn from, so a headless run makes the same pixels. <paramref name="outlineRadius"/> &gt; 0
    /// returns the outline's stroke (the glyphs dilated by it) instead of the fill. The bitmap's
    /// <see cref="TextBitmap.Baseline"/> is the baseline's row, and the pen starts at column
    /// <see cref="TextBitmap.Origin"/>; both bitmaps of a line (fill and outline) share the same layout when given the same
    /// <paramref name="padding"/>.
    /// </summary>
    public TextBitmap RenderLine(string text, int fontSize, float outlineRadius = 0, int padding = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(fontSize, 1);
        var ascent = (int)GetAscent(fontSize);
        var descent = (int)GetDescent(fontSize);
        var pad = Math.Max(0, padding) + 1 + (int)MathF.Ceiling(outlineRadius);
        var width = (int)MathF.Ceiling(Advance(text, fontSize)) + 2 * pad;
        var height = ascent + descent + 2 * pad;
        var alpha = new byte[Math.Max(1, width) * Math.Max(1, height)];
        var baseline = pad + ascent;
        var pen = 0f;
        var previous = -1;
        TrueTypeFont? previousFace = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var (face, glyph) = Resolve(rune.Value, null);
            var scale = (float)fontSize / face.UnitsPerEm;
            if (previous >= 0 && ReferenceEquals(face, previousFace))
                pen += face.Kerning(previous, glyph) * scale;
            var bitmap = GlyphRasterizer.Rasterize(face.Outline(glyph), scale, outlineRadius);
            var x0 = pad + (int)MathF.Round(pen) + bitmap.Left;
            var y0 = baseline - bitmap.Top;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var ty = y0 + y;
                if ((uint)ty >= (uint)height)
                    continue;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var tx = x0 + x;
                    if ((uint)tx >= (uint)width)
                        continue;
                    ref var target = ref alpha[ty * width + tx];
                    target = Math.Max(target, bitmap.Alpha[y * bitmap.Width + x]);
                }
            }

            pen += face.AdvanceWidth(glyph) * scale;
            previous = glyph;
            previousFace = face;
        }

        return new TextBitmap(alpha, width, height, baseline, pad);
    }

    /// <summary>
    /// Draws one line on <paramref name="item"/> with its baseline starting at <paramref name="position"/> (Godot's
    /// <c>draw_string</c>; <paramref name="outline"/> &gt; 0 draws <c>draw_string_outline</c>'s stroke instead).
    /// </summary>
    internal void Draw(CanvasItem item, Vector2 position, string text, HorizontalAlignment alignment, float width, int fontSize, int outline, Vector4 modulate)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
            return;
        var x = position.X;
        if (width > 0)
        {
            var advance = Advance(text, fontSize);
            x += alignment switch
            {
                HorizontalAlignment.Center => MathF.Floor((width - advance) / 2),
                HorizontalAlignment.Right => width - advance,
                _ => 0,
            };
        }

        _drawScratch.Clear();
        Layout(text, new Vector2(x, position.Y), fontSize, outline, _drawScratch);
        foreach (var quad in _drawScratch)
            item.DrawTextureRectRegion(quad.Page, quad.Rect, quad.Source, modulate);
        _drawScratch.Clear();
    }

    /// <summary>
    /// Places one line's glyphs with the pen's baseline starting at <paramref name="position"/> (pixels, Y down; pen
    /// positions rounded to whole pixels, as <c>draw_string</c> does), appending each visible glyph's rectangle and its
    /// region of an atlas page (<see cref="AtlasPageSize"/> px square) to <paramref name="quads"/>. <paramref name="outline"/>
    /// &gt; 0 places the outline's stroke glyphs. Rasterizes new glyphs and uploads the pages they changed.
    /// </summary>
    internal void Layout(string text, Vector2 position, int fontSize, int outline, List<GlyphQuad> quads, IReadOnlyList<Font>? extraFallbacks = null)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
            return;
        var key = (fontSize, outline);
        if (!_glyphs.TryGetValue(key, out var cache))
            _glyphs[key] = cache = [];
        var pen = 0f;
        var previous = -1;
        TrueTypeFont? previousFace = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var (face, glyph) = Resolve(rune.Value, extraFallbacks);
            var scale = (float)fontSize / face.UnitsPerEm;
            if (previous >= 0 && ReferenceEquals(face, previousFace))
                pen += face.Kerning(previous, glyph) * scale;
            var cacheKey = glyph | (FaceId(face) << 24); // glyph ids are 16-bit
            if (!cache.TryGetValue(cacheKey, out var entry))
                cache[cacheKey] = entry = Rasterize(face, glyph, scale, outline / 4f);
            if (entry.Page >= 0)
            {
                var origin = new Vector2(MathF.Round(position.X + pen) + entry.Left, MathF.Round(position.Y) - entry.Top);
                quads.Add(new GlyphQuad(_pages[entry.Page].Texture, new Rect2(origin, entry.Source.Size), entry.Source));
            }

            pen += face.AdvanceWidth(glyph) * scale;
            previous = glyph;
            previousFace = face;
        }

        foreach (var page in _pages)
            if (page.Dirty)
            {
                page.Texture.SetPixels(page.Rgba);
                page.Dirty = false;
            }
    }

    private GlyphEntry Rasterize(TrueTypeFont face, int glyph, float scale, float outlineRadius)
    {
        var bitmap = GlyphRasterizer.Rasterize(face.Outline(glyph), scale, outlineRadius);
        if (bitmap.Width == 0)
            return new GlyphEntry(-1, default, 0, 0);
        if (bitmap.Width + 1 > PageSize || bitmap.Height + 1 > PageSize)
        {
            Log.Warning($"[Font] '{ResourcePath ?? FontPath}': glyph {glyph} ({bitmap.Width}×{bitmap.Height} px) does not fit a {PageSize} px atlas page.");
            return new GlyphEntry(-1, default, 0, 0);
        }

        var pageIndex = _pages.Count - 1;
        var page = pageIndex >= 0 ? _pages[pageIndex] : null;
        if (page is not null && page.ShelfX + bitmap.Width + 1 > PageSize)
        {
            page.ShelfY += page.ShelfHeight + 1;
            page.ShelfX = 0;
            page.ShelfHeight = 0;
        }

        if (page is null || page.ShelfY + bitmap.Height + 1 > PageSize)
        {
            page = new AtlasPage();
            _pages.Add(page);
            pageIndex = _pages.Count - 1;
        }

        int px = page.ShelfX, py = page.ShelfY;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                page.Rgba[((py + y) * PageSize + px + x) * 4 + 3] = bitmap.Alpha[y * bitmap.Width + x];
        page.ShelfX += bitmap.Width + 1;
        page.ShelfHeight = Math.Max(page.ShelfHeight, bitmap.Height);
        page.Dirty = true;
        return new GlyphEntry(pageIndex, new Rect2(px, py, bitmap.Width, bitmap.Height), bitmap.Left, bitmap.Top);
    }
}

/// <summary>One line rasterised by <see cref="Font.RenderLine"/>: coverage bytes (row-major, Y down), the baseline's row and the pen's first column.</summary>
public readonly record struct TextBitmap(byte[] Alpha, int Width, int Height, int Baseline, int Origin);

/// <summary>A glyph placed by <see cref="Font.Layout"/>: its rectangle (pixels, Y down) and its region of an atlas page.</summary>
internal readonly record struct GlyphQuad(Texture2D Page, Rect2 Rect, Rect2 Source);

/// <summary>Imports <c>.ttf</c> files as <see cref="Font"/>s.</summary>
public sealed class FontImporter : IAssetImporter
{
    public string Name => "font";

    public IReadOnlyList<string> Extensions { get; } = [".ttf"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => Font.FromFile(projectPath);
}
