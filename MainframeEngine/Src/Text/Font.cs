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

/// <summary>
/// A TrueType font for canvas text (Godot's <c>FontFile</c> as a 2D font, ADR 0118): <c>.ttf</c> files load through
/// <see cref="ResourceLoader"/>. Glyphs are rasterised in managed code at the requested pixel size (unhinted, greyscale,
/// pen positions rounded to whole pixels) into shared atlas pages, once per size and outline width; GPOS/kern pair
/// kerning is applied. Metrics follow FreeType's rounding: ascent and descent are the <c>hhea</c> values scaled and
/// rounded. No shaping beyond kerning, no fallback fonts, single line.
/// </summary>
[EditorIcon("typography")]
public sealed class Font : Resource
{
    private const int PageSize = 512;
    private TrueTypeFont? _face;
    private readonly Dictionary<(int Size, int Outline), Dictionary<int, GlyphEntry>> _glyphs = [];
    private readonly List<AtlasPage> _pages = [];

    /// <summary>The font file (project path).</summary>
    [Export(File = "*.ttf")]
    public string FontPath { get; set; } = "";

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
        font._face = new TrueTypeFont(File.ReadAllBytes(AssetDatabase.Current.ToAbsolutePath(path)));
        return font;
    }

    private TrueTypeFont Face => _face ??= new TrueTypeFont(File.ReadAllBytes(AssetDatabase.Current.ToAbsolutePath(FontPath)));

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

    private float Advance(string text, int size)
    {
        var face = Face;
        var scale = Scale(size);
        var pen = 0f;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = face.GlyphIndex(rune.Value);
            if (previous >= 0)
                pen += face.Kerning(previous, glyph) * scale;
            pen += face.AdvanceWidth(glyph) * scale;
            previous = glyph;
        }

        return pen;
    }

    /// <summary>
    /// Draws one line on <paramref name="item"/> with its baseline starting at <paramref name="position"/> (Godot's
    /// <c>draw_string</c>; <paramref name="outline"/> &gt; 0 draws <c>draw_string_outline</c>'s stroke instead).
    /// </summary>
    internal void Draw(CanvasItem item, Vector2 position, string text, HorizontalAlignment alignment, float width, int fontSize, int outline, Vector4 modulate)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
            return;
        var face = Face;
        var scale = Scale(fontSize);
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

        var key = (fontSize, outline);
        if (!_glyphs.TryGetValue(key, out var cache))
            _glyphs[key] = cache = [];
        var pen = 0f;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = face.GlyphIndex(rune.Value);
            if (previous >= 0)
                pen += face.Kerning(previous, glyph) * scale;
            if (!cache.TryGetValue(glyph, out var entry))
                cache[glyph] = entry = Rasterize(glyph, scale, outline / 4f);
            if (entry.Page >= 0)
            {
                var origin = new Vector2(MathF.Round(x + pen) + entry.Left, MathF.Round(position.Y) - entry.Top);
                item.DrawTextureRectRegion(_pages[entry.Page].Texture, new Rect2(origin, entry.Source.Size), entry.Source, modulate);
            }

            pen += face.AdvanceWidth(glyph) * scale;
            previous = glyph;
        }

        foreach (var page in _pages)
            if (page.Dirty)
            {
                page.Texture.SetPixels(page.Rgba);
                page.Dirty = false;
            }
    }

    private GlyphEntry Rasterize(int glyph, float scale, float outlineRadius)
    {
        var bitmap = GlyphRasterizer.Rasterize(Face.Outline(glyph), scale, outlineRadius);
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

/// <summary>Imports <c>.ttf</c> files as <see cref="Font"/>s.</summary>
public sealed class FontImporter : IAssetImporter
{
    public string Name => "font";

    public IReadOnlyList<string> Extensions { get; } = [".ttf"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => Font.FromFile(projectPath);
}
