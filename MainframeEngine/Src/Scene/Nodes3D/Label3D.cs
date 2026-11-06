using DrawingColor = System.Drawing.Color;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Text in the 3D world (Godot's <c>Label3D</c>, ADRs 0200 and 0251): glyph quads from <see cref="Font"/>'s atlas at
/// <see cref="FontSize"/>, one world unit per <see cref="PixelSize"/>⁻¹ pixels, aligned around the node. Lines break at
/// <c>\n</c> and, with <see cref="Autowrap"/>, inside <see cref="Width"/>; characters the font lacks come from its
/// fallbacks, then from the project's <see cref="Localization.FontFallbackTable"/> for the current locale (CJK text).
/// <see cref="FixedSize"/> keeps one label pixel one screen pixel at any distance. The outline
/// (<see cref="OutlineSize"/>, <see cref="OutlineModulate"/>) draws first, at <see cref="OutlineRenderPriority"/>.
/// Unshaded, blended and double-sided by default, like Godot; <see cref="Billboard"/> turns it toward the camera and
/// <see cref="NoDepthTest"/> keeps it visible through geometry (name tags). Without a <see cref="Font"/> it uses the
/// engine's Lato Latin, like Godot's default font. <see cref="Text"/> is shown translated (<see cref="Node.Atr"/>). The mesh
/// is rebuilt only when a property or the locale changes. For menus and HUDs use the UI (<see cref="UiDocument"/>).
/// </summary>
[EditorIcon("letter-t")]
public class Label3D : GeometryInstance3D
{
    /// <summary>The font used when <see cref="Font"/> is null.</summary>
    public const string DefaultFontPath = "Assets/UI/fonts/LatoLatin-Regular.ttf";

    private static Font? _defaultFont;
    private readonly ArrayMesh _mesh = new();
    private readonly List<(bool Outline, Texture2D Page, StandardMaterial3D Material)> _materials = [];
    private readonly List<GlyphQuad> _quads = [];
    private readonly List<string> _lines = [];
    private int _builtStamp = -1;
    private string? _builtLocale;
    private int _builtFallbacks = -1;
    private bool _builtBatched;
    private TextGlyph[] _glyphs = [];
    private int _glyphCount;
    private Aabb _bounds;

    [Export(Translatable = true)]
    public string Text
    {
        get;
        set => Changed(ref field, value ?? "");
    } = "";

    /// <summary>The font; null uses <see cref="DefaultFontPath"/>.</summary>
    [Export]
    public Font? Font
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            RenderStamp++;
        }
    }

    /// <summary>Glyph size in pixels (Godot's default 32).</summary>
    [Export(Range = "1,256,1")]
    public int FontSize
    {
        get;
        set => Changed(ref field, Math.Max(1, value));
    } = 32;

    /// <summary>World units per pixel (Godot's default 0.005).</summary>
    [Export(Range = "0.0001,1,0.0001")]
    public float PixelSize
    {
        get;
        set => Changed(ref field, value);
    } = 0.005f;

    /// <summary>The text's colour (sRGB, alpha blends).</summary>
    [Export]
    public DrawingColor Modulate
    {
        get;
        set => Changed(ref field, value);
    } = DrawingColor.White;

    /// <summary>Outline thickness in pixels (Godot's default 12; 0 = none).</summary>
    [Export(Range = "0,127,1")]
    public int OutlineSize
    {
        get;
        set => Changed(ref field, Math.Max(0, value));
    } = 12;

    /// <summary>The outline's colour (sRGB, alpha blends); fully transparent draws none.</summary>
    [Export]
    public DrawingColor OutlineModulate
    {
        get;
        set => Changed(ref field, value);
    } = DrawingColor.Black;

    /// <summary>Where the line sits across the node: its left edge, centre or right edge on it (Fill = Left).</summary>
    [Export]
    public HorizontalAlignment HorizontalAlignment
    {
        get;
        set => Changed(ref field, value);
    } = HorizontalAlignment.Center;

    /// <summary>Where the line (the font's height) sits against the node.</summary>
    [Export]
    public VerticalAlignment VerticalAlignment
    {
        get;
        set => Changed(ref field, value);
    } = VerticalAlignment.Center;

    /// <summary>The width in pixels that <see cref="Autowrap"/> wraps in and the lines align across (Godot's default 500).</summary>
    [Export(Range = "0,4096,1")]
    public float Width
    {
        get;
        set => Changed(ref field, MathF.Max(0f, value));
    } = 500f;

    /// <summary>How lines wider than <see cref="Width"/> break (Godot's <c>autowrap_mode</c>; off by default).</summary>
    [Export]
    public AutowrapMode Autowrap
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>Extra pixels between lines (Godot's <c>line_spacing</c>).</summary>
    [Export(Range = "-64,256,1")]
    public float LineSpacing
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>Shows the text in upper case (Godot's <c>uppercase</c>; the current culture's rules).</summary>
    [Export]
    public bool Uppercase
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>
    /// Keeps the label's size on screen constant (Godot's <c>fixed_size</c>): one label pixel is one framebuffer pixel
    /// at any distance, for name tags and debug text. Scaled on the CPU like the billboard, per view.
    /// </summary>
    [Export]
    public bool FixedSize
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>
    /// <see cref="SpriteAlphaCut.Discard"/> draws the glyphs as an alpha-tested cutout (depth-writing, sorted with opaque
    /// geometry, hard edges); the default blends them.
    /// </summary>
    [Export]
    public SpriteAlphaCut AlphaCut
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>The text's draw order among transparent surfaces (Godot's <c>render_priority</c>, default 0).</summary>
    [Export(Range = "-128,127,1")]
    public int TextRenderPriority
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>Shifts the text in pixels (+Y up).</summary>
    [Export]
    public Vector2 Offset
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>Faces the camera (Godot's <c>billboard</c>; see <see cref="Sprite3D.Billboard"/>).</summary>
    [Export]
    public BillboardMode Billboard { get; set; }

    /// <summary>Draws over geometry in front of it (Godot's <c>no_depth_test</c>; <see cref="StandardMaterial3D.NoDepthTest"/>).</summary>
    [Export]
    public bool NoDepthTest
    {
        get;
        set => Changed(ref field, value);
    }

    /// <summary>Lit by the scene's lights instead of unshaded.</summary>
    [Export]
    public bool Shaded
    {
        get;
        set => Changed(ref field, value);
    }

    [Export]
    public bool DoubleSided
    {
        get;
        set => Changed(ref field, value);
    } = true;

    /// <summary>
    /// The outline's draw order among transparent surfaces (Godot's default −1). The text draws at 0, so it lands on its
    /// outline. (<see cref="VisualInstance3D.RenderPriority"/> is node order, not Godot's material <c>render_priority</c>.)
    /// </summary>
    [Export(Range = "-128,127,1")]
    public int OutlineRenderPriority
    {
        get;
        set => Changed(ref field, value);
    } = -1;

    /// <summary>
    /// Divides the text's colour by the view's exposure (manual × the world's × auto exposure; ADR 0257), so a sign keeps
    /// its authored brightness while the camera adapts. The tonemap (ACES) still applies. Off by default (Godot tonemaps
    /// labels); only unshaded labels drawn by the batched renderer use it (<see cref="IsBatchedText"/>).
    /// </summary>
    [Export]
    public bool IgnoreExposure { get; set; }

    internal override BillboardMode RenderBillboard => Billboard;

    internal override float RenderFixedPixelSize => FixedSize ? PixelSize : 0f;

    /// <summary>
    /// Drawn by the batched <see cref="Text3DRenderer"/> (ADR 0258): every unshaded label, except a cutout that casts
    /// shadows. Lit labels and shadow-casting cutouts stay a mesh on the mesh batcher (ADR 0251).
    /// </summary>
    internal bool IsBatchedText => !Shaded && !(AlphaCut == SpriteAlphaCut.Discard && CastShadows);

    /// <summary>The label's local bounds as built (empty without text), for editor focus and culling.</summary>
    public Aabb GetAabb()
    {
        if (Text.Length == 0)
            return default;
        Sync(DefaultOr(Font), IsBatchedText);
        return _bounds;
    }

    /// <summary>The lines as last laid out (translated, wrapped).</summary>
    public IReadOnlyList<string> GetLines()
    {
        if (Text.Length > 0)
            Sync(DefaultOr(Font), IsBatchedText);
        else
            _lines.Clear();
        return _lines;
    }

    internal override Mesh? GetRenderMesh()
    {
        if (Text.Length == 0 || IsBatchedText)
            return null;
        Sync(DefaultOr(Font), batched: false);
        return _mesh.SurfaceCount > 0 ? _mesh : null;
    }

    // ── Batched glyph run (ADR 0258) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The glyph quads of a batched label (label space, world units), outline glyphs first.</summary>
    internal ReadOnlySpan<TextGlyph> Glyphs => new(_glyphs, 0, _glyphCount);

    /// <summary>The local bounds of the run last built (<see cref="SyncGlyphs"/>), without rebuilding.</summary>
    internal Aabb RunBounds => _bounds;

    /// <summary>Bumped whenever <see cref="Glyphs"/> is rebuilt.</summary>
    internal int GlyphsVersion { get; private set; }

    /// <summary>The renderer's per-label state (page slots), owned by <see cref="Text3DRenderer"/>.</summary>
    internal Text3DRenderer.LabelState? TextState;

    /// <summary>Rebuilds the glyph run when the text, a property or the locale changed (batched labels only).</summary>
    internal void SyncGlyphs()
    {
        if (Text.Length == 0)
        {
            if (_glyphCount != 0)
            {
                _glyphCount = 0;
                _bounds = default;
                GlyphsVersion++;
            }

            return;
        }

        Sync(DefaultOr(Font), batched: true);
    }

    protected override void OnLocaleChanged() => RenderStamp++;

    protected override void OnExitTree()
    {
        RenderServer?.Text3D?.Release(this);
        base.OnExitTree();
    }

    protected override void ReleaseRenderResources()
    {
        RenderServer?.Text3D?.Release(this);
        base.ReleaseRenderResources();
    }

    private static Font DefaultOr(Font? font) =>
        font ?? (_defaultFont ??= Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve(DefaultFontPath))));

    private void Sync(Font font, bool batched)
    {
        var fallbacks = LocaleFallbackFonts.Current;
        if (_builtStamp == RenderStamp && _builtBatched == batched && _builtFallbacks == fallbacks.Count &&
            ReferenceEquals(_builtLocale, Localization.Tr.CurrentLocale))
            return;
        _builtStamp = RenderStamp;
        _builtBatched = batched;
        _builtFallbacks = fallbacks.Count;
        _builtLocale = Localization.Tr.CurrentLocale;
        _mesh.ClearSurfaces();
        _glyphCount = 0;
        _bounds = default;
        GlyphsVersion++;

        var text = IsInsideTree ? Atr(Text) : Text;
        if (Uppercase)
            text = text.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        _lines.Clear();
        TextLayout.BreakLines(font, text, FontSize, Width, Autowrap, _lines, fallbacks);

        // The block: the widest line (or Width when wrapping), the lines' height with their spacing.
        var blockWidth = 0f;
        foreach (var line in _lines)
            blockWidth = MathF.Max(blockWidth, font.MeasureLine(line, FontSize, fallbacks));
        if (Autowrap != AutowrapMode.Off && Width > 0f)
            blockWidth = Width;
        var lineHeight = font.GetHeight(FontSize);
        var blockHeight = _lines.Count * lineHeight + Math.Max(0, _lines.Count - 1) * LineSpacing;
        var left = HorizontalAlignment switch
        {
            HorizontalAlignment.Center => -MathF.Round(blockWidth / 2),
            HorizontalAlignment.Right => -blockWidth,
            _ => 0f,
        };
        var top = VerticalAlignment switch
        {
            VerticalAlignment.Center => -MathF.Round(blockHeight / 2),
            VerticalAlignment.Bottom => -blockHeight,
            _ => 0f,
        };

        var bounds = Aabb.Empty;
        if (OutlineSize > 0 && OutlineModulate.A > 0)
            AddRun(font, left, top, lineHeight, blockWidth, fallbacks, OutlineSize, outline: true, batched, ref bounds);
        AddRun(font, left, top, lineHeight, blockWidth, fallbacks, 0, outline: false, batched, ref bounds);
        _bounds = bounds.IsEmpty ? default : bounds;
    }

    // Lays the lines out at outlineSize and adds them: as glyphs (batched) or as one mesh surface per atlas page.
    private void AddRun(Font font, float left, float top, float lineHeight, float blockWidth, IReadOnlyList<Font> fallbacks, int outlineSize,
        bool outline, bool batched, ref Aabb bounds)
    {
        _quads.Clear();
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Length == 0)
                continue;
            var width = font.MeasureLine(line, FontSize, fallbacks);
            var x = HorizontalAlignment switch
            {
                HorizontalAlignment.Center => MathF.Round((blockWidth - width) / 2),
                HorizontalAlignment.Right => blockWidth - width,
                _ => 0f,
            };
            // Pixels, Y down from the node; the offset is +Y up like Sprite3D's.
            var baseline = new Vector2(left + x + Offset.X, top + i * (lineHeight + LineSpacing) + font.GetAscent(FontSize) - Offset.Y);
            font.Layout(line, baseline, FontSize, outlineSize, _quads, fallbacks);
        }

        var atlas = 1f / Font.AtlasPageSize;
        foreach (var quad in _quads)
        {
            var r = quad.Rect;
            bounds = bounds.Merge(new Aabb(new Vector3(r.Position.X, -r.End.Y, 0) * PixelSize, new Vector3(r.End.X, -r.Position.Y, 0) * PixelSize));
        }

        if (batched)
        {
            if (_glyphs.Length < _glyphCount + _quads.Count)
                Array.Resize(ref _glyphs, Math.Max(_glyphs.Length * 2, _glyphCount + _quads.Count));
            foreach (var quad in _quads)
            {
                var r = quad.Rect;
                var s = quad.Source;
                // Pixel Y down → label space Y up (x0, bottom, x1, top); UVs top-left (u0, v0) to bottom-right (u1, v1).
                _glyphs[_glyphCount++] = new TextGlyph(
                    new Vector4(r.Position.X, -r.End.Y, r.End.X, -r.Position.Y) * PixelSize,
                    new Vector4(s.Position.X, s.Position.Y, s.End.X, s.End.Y) * atlas,
                    quad.Page, outline);
            }

            _quads.Clear();
            return;
        }

        for (var first = 0; first < _quads.Count; first++)
        {
            var page = _quads[first].Page;
            if (IndexOfPage(page, first) != first)
                continue; // done with an earlier quad on the same page
            var count = 0;
            for (var i = first; i < _quads.Count; i++)
                if (ReferenceEquals(_quads[i].Page, page))
                    count++;

            var positions = new Vector3[count * 4];
            var normals = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var indices = new int[count * 6];
            var q = 0;
            for (var i = first; i < _quads.Count; i++)
            {
                var quad = _quads[i];
                if (!ReferenceEquals(quad.Page, page))
                    continue;
                var r = quad.Rect;
                var s = quad.Source;
                var v = q * 4;
                // Bottom-left, bottom-right, top-right, top-left facing +Z (QuadMesh's order); pixel Y down → world Y up.
                positions[v] = new Vector3(r.Position.X, -r.End.Y, 0) * PixelSize;
                positions[v + 1] = new Vector3(r.End.X, -r.End.Y, 0) * PixelSize;
                positions[v + 2] = new Vector3(r.End.X, -r.Position.Y, 0) * PixelSize;
                positions[v + 3] = new Vector3(r.Position.X, -r.Position.Y, 0) * PixelSize;
                uvs[v] = new Vector2(s.Position.X, s.End.Y) * atlas;
                uvs[v + 1] = new Vector2(s.End.X, s.End.Y) * atlas;
                uvs[v + 2] = new Vector2(s.End.X, s.Position.Y) * atlas;
                uvs[v + 3] = new Vector2(s.Position.X, s.Position.Y) * atlas;
                for (var n = 0; n < 4; n++)
                    normals[v + n] = Vector3.UnitZ;
                var t = q * 6;
                (indices[t], indices[t + 1], indices[t + 2]) = (v, v + 1, v + 2);
                (indices[t + 3], indices[t + 4], indices[t + 5]) = (v, v + 2, v + 3);
                q++;
            }

            _mesh.AddSurface(positions, normals, uvs, indices, MaterialFor(outline, page));
        }

        _quads.Clear();
    }

    private int IndexOfPage(Texture2D page, int before)
    {
        for (var i = 0; i < before; i++)
            if (ReferenceEquals(_quads[i].Page, page))
                return i;
        return before;
    }

    private StandardMaterial3D MaterialFor(bool outline, Texture2D page)
    {
        StandardMaterial3D? material = null;
        foreach (var entry in _materials)
            if (entry.Outline == outline && ReferenceEquals(entry.Page, page))
                material = entry.Material;
        if (material is null)
        {
            material = new StandardMaterial3D { AlbedoTexture = page, Transparency = AlphaMode.Blend };
            _materials.Add((outline, page, material));
        }

        material.AlbedoColor = outline ? OutlineModulate : Modulate;
        material.ShadingMode = Shaded ? ShadingMode.BlinnPhong : ShadingMode.Unshaded;
        material.DoubleSided = DoubleSided;
        material.NoDepthTest = NoDepthTest;
        material.Transparency = AlphaCut == SpriteAlphaCut.Discard ? AlphaMode.Cutout : AlphaMode.Blend;
        material.RenderPriority = outline ? OutlineRenderPriority : TextRenderPriority;
        return material;
    }

    private void Changed<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        RenderStamp++;
    }
}

/// <summary>One glyph quad of a batched <see cref="Label3D"/> (ADR 0258), in label space.</summary>
/// <param name="Rect">x0, y0 (bottom), x1, y1 (top) in world units, +Y up.</param>
/// <param name="Uv">u0, v0 (top-left), u1, v1 (bottom-right) in the atlas page.</param>
/// <param name="Page">The font atlas page.</param>
/// <param name="Outline">An outline glyph (drawn in the outline colour).</param>
internal readonly record struct TextGlyph(Vector4 Rect, Vector4 Uv, Texture2D Page, bool Outline);
