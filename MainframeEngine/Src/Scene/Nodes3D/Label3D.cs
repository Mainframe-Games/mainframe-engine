using DrawingColor = System.Drawing.Color;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// One line of text in the 3D world (Godot's <c>Label3D</c>, ADR 0200): glyph quads from <see cref="Font"/>'s atlas at
/// <see cref="FontSize"/>, one world unit per <see cref="PixelSize"/>⁻¹ pixels, aligned around the node. The outline
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
    private int _builtStamp = -1;

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

    internal override BillboardMode RenderBillboard => Billboard;

    internal override Mesh? GetRenderMesh()
    {
        if (Text.Length == 0)
            return null;
        Sync(Font ?? (_defaultFont ??= Font.FromData(File.ReadAllBytes(ContentPaths.Resolve(DefaultFontPath)))));
        return _mesh.SurfaceCount > 0 ? _mesh : null;
    }

    protected override void OnLocaleChanged() => RenderStamp++;

    private void Sync(Font font)
    {
        if (_builtStamp == RenderStamp)
            return;
        _builtStamp = RenderStamp;
        _mesh.ClearSurfaces();

        var text = IsInsideTree ? Atr(Text) : Text;
        var width = font.GetStringSize(text, fontSize: FontSize).X;
        var height = font.GetHeight(FontSize);
        var left = HorizontalAlignment switch
        {
            HorizontalAlignment.Center => -MathF.Round(width / 2),
            HorizontalAlignment.Right => -width,
            _ => 0f,
        };
        var top = VerticalAlignment switch
        {
            VerticalAlignment.Center => -MathF.Round(height / 2),
            VerticalAlignment.Bottom => -height,
            _ => 0f,
        };
        // Pixels, Y down from the node; the offset is +Y up like Sprite3D's.
        var baseline = new Vector2(left + Offset.X, top + font.GetAscent(FontSize) - Offset.Y);

        if (OutlineSize > 0 && OutlineModulate.A > 0)
            AddSurfaces(font, text, baseline, OutlineSize, outline: true);
        AddSurfaces(font, text, baseline, 0, outline: false);
    }

    // One surface per atlas page the line's glyphs land on.
    private void AddSurfaces(Font font, string text, Vector2 baseline, int outlineSize, bool outline)
    {
        _quads.Clear();
        font.Layout(text, baseline, FontSize, outlineSize, _quads);
        var atlas = 1f / Font.AtlasPageSize;
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
        material.RenderPriority = outline ? OutlineRenderPriority : 0;
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
