using System.Drawing;
using System.Numerics;

namespace MainframeEngine.Tests.Scene;

using Color = System.Drawing.Color;

/// <summary>
/// <see cref="Label3D"/> (ADRs 0200, 0251, 0258): glyph quads from the font atlas — a glyph run for the batched renderer
/// (unshaded labels), a mesh for lit labels and shadow-casting cutouts — alignment, outline, materials and rebuilds.
/// </summary>
public sealed class Label3DTests
{
    private static readonly Font Lato = Font.FromData(File.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf")));

    private static Label3D Label(string text = "Dusty Nell") => new() { Text = text, Font = Lato, FontSize = 32, PixelSize = 0.01f };

    private static Aabb SurfaceBounds(Mesh mesh, int surface)
    {
        var positions = mesh.GetSurface(surface).Positions;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new Aabb(min, max);
    }

    // The bounds of a batched label's text (or outline) glyphs, in label space.
    private static Aabb GlyphBounds(Label3D label, bool outline = false)
    {
        label.SyncGlyphs();
        var bounds = Aabb.Empty;
        foreach (var glyph in label.Glyphs)
            if (glyph.Outline == outline)
                bounds = bounds.Merge(new Aabb(new Vector3(glyph.Rect.X, glyph.Rect.Y, 0), new Vector3(glyph.Rect.Z, glyph.Rect.W, 0)));
        return bounds;
    }

    [Fact]
    public void NothingIsDrawnWithoutTextAndTheEnginesLatoIsTheDefaultFont()
    {
        var empty = new Label3D { Font = Lato };
        empty.SyncGlyphs();
        Assert.True(empty.Glyphs.IsEmpty);
        Assert.Null(empty.GetRenderMesh());
        var space = Label(" "); // a space has no glyph
        space.SyncGlyphs();
        Assert.True(space.Glyphs.IsEmpty);

        var fallback = new Label3D { Text = "Hello", OutlineSize = 0 };
        var withLato = Label("Hello");
        withLato.OutlineSize = 0;
        withLato.PixelSize = fallback.PixelSize;
        Assert.Equal(GlyphBounds(withLato), GlyphBounds(fallback)); // the same glyphs
        fallback.Free();
        withLato.Free();
    }

    [Fact]
    public void UnshadedLabelsAreBatchedLitOnesAndShadowCastingCutoutsAreMeshes()
    {
        var label = Label();
        Assert.True(label.IsBatchedText);
        Assert.Null(label.GetRenderMesh()); // the batched renderer draws it
        label.SyncGlyphs();
        Assert.False(label.Glyphs.IsEmpty);

        label.AlphaCut = SpriteAlphaCut.Discard; // a cutout casts shadows (CastShadows defaults on): the mesh batcher
        Assert.False(label.IsBatchedText);
        Assert.NotNull(label.GetRenderMesh());
        label.CastShadows = false;
        Assert.True(label.IsBatchedText);
        Assert.Null(label.GetRenderMesh());

        label.AlphaCut = SpriteAlphaCut.Disabled;
        label.Shaded = true; // lit: the mesh batcher
        Assert.False(label.IsBatchedText);
        Assert.NotNull(label.GetRenderMesh());
        label.Free();
    }

    [Fact]
    public void TheShownTextIsTranslatedAndFollowsTheLocale()
    {
        var tree = new SceneTree(new ServerRegistry());
        var label = Label("Hello");
        tree.Root.AddChild(label);
        label.SyncGlyphs();
        var before = label.GlyphsVersion;
        tree.Root.PropagateLocaleChanged(); // as Tr.SetLocale does
        label.SyncGlyphs();
        Assert.NotEqual(before, label.GlyphsVersion); // rebuilt with Atr(Text)
        tree.Shutdown();
    }

    [Fact]
    public void TheOutlineGlyphsComeFirstAndSurroundTheText()
    {
        var label = Label();
        label.OutlineSize = 14;
        label.SyncGlyphs();
        var glyphs = label.Glyphs.ToArray();
        var firstText = Array.FindIndex(glyphs, g => !g.Outline);
        Assert.True(firstText > 0, "outline glyphs first");
        Assert.All(glyphs[firstText..], g => Assert.False(g.Outline));
        var text = GlyphBounds(label);
        var outline = GlyphBounds(label, outline: true);
        Assert.True(outline.Min.X < text.Min.X && outline.Max.X > text.Max.X);
        Assert.True(outline.Min.Y < text.Min.Y && outline.Max.Y > text.Max.Y);
        Assert.Equal(text.Merge(outline), label.GetAabb());

        label.OutlineModulate = Color.Transparent; // no outline glyphs at all
        label.SyncGlyphs();
        Assert.All(label.Glyphs.ToArray(), g => Assert.False(g.Outline));
        label.Free();
    }

    [Fact]
    public void ALitLabelsMeshHasTheOutlineUnderTheTextAndMaterialsFollowTheLabel()
    {
        var label = Label();
        label.Shaded = true;
        label.Modulate = Color.FromArgb(255, 43, 33, 24);
        label.OutlineModulate = Color.FromArgb(200, 254, 244, 216);
        label.OutlineSize = 14;
        label.NoDepthTest = true;
        var mesh = label.GetRenderMesh()!;

        Assert.Equal(2, mesh.SurfaceCount); // one atlas page: the outline's surface, then the text's
        var outline = (StandardMaterial3D)label.GetRenderMaterial(mesh, 0);
        var text = (StandardMaterial3D)label.GetRenderMaterial(mesh, 1);
        Assert.Equal(label.OutlineModulate, outline.AlbedoColor);
        Assert.Equal(label.Modulate, text.AlbedoColor);
        Assert.Equal(-1, outline.RenderPriority);
        Assert.Equal(0, text.RenderPriority);
        foreach (var material in new[] { outline, text })
        {
            Assert.Equal(AlphaMode.Blend, material.Transparency);
            Assert.Equal(ShadingMode.BlinnPhong, material.ShadingMode);
            Assert.True(material.DoubleSided);
            Assert.True(material.RenderState.NoDepthTest);
            Assert.True(material.RenderState.IsTransparent);
        }

        // The stroke surrounds the glyphs.
        var textBounds = SurfaceBounds(mesh, 1);
        var outlineBounds = SurfaceBounds(mesh, 0);
        Assert.True(outlineBounds.Min.X < textBounds.Min.X && outlineBounds.Max.X > textBounds.Max.X);
        Assert.True(outlineBounds.Min.Y < textBounds.Min.Y && outlineBounds.Max.Y > textBounds.Max.Y);

        label.OutlineModulate = Color.Transparent;
        Assert.Equal(1, label.GetRenderMesh()!.SurfaceCount);
        label.Free();
    }

    [Fact]
    public void CentredByDefaultAndAlignedAroundTheNode()
    {
        var label = Label();
        label.OutlineSize = 0;
        var width = Lato.GetStringSize(label.Text, fontSize: 32).X * label.PixelSize;
        var pixel = label.PixelSize;

        var centred = GlyphBounds(label);
        Assert.InRange(centred.Center.X, -2 * pixel, 2 * pixel);
        Assert.InRange(centred.Size.X, width * 0.9f, width * 1.05f); // glyph ink inside the advance

        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.VerticalAlignment = VerticalAlignment.Top;
        var topLeft = GlyphBounds(label);
        Assert.InRange(topLeft.Min.X, -pixel, 3 * pixel);   // starts at the node
        Assert.True(topLeft.Max.Y <= pixel, $"{topLeft}"); // hangs below it

        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        var bottomRight = GlyphBounds(label);
        Assert.InRange(bottomRight.Max.X, -3 * pixel, pixel); // ends at the node
        Assert.True(bottomRight.Min.Y >= -pixel, $"{bottomRight}"); // sits on it

        label.Offset = new Vector2(10, 20); // pixels, +Y up
        var moved = GlyphBounds(label);
        Assert.Equal(bottomRight.Min.X + 10 * pixel, moved.Min.X, 4);
        Assert.Equal(bottomRight.Min.Y + 20 * pixel, moved.Min.Y, 4);

        label.Shaded = true; // the mesh path lays out the same quads
        Assert.Equal(moved.Min.X, SurfaceBounds(label.GetRenderMesh()!, 0).Min.X, 4);
        label.Free();
    }

    [Fact]
    public void TheGlyphsAreRebuiltOnlyWhenTheLabelChanges()
    {
        var label = Label();
        label.SyncGlyphs();
        var version = label.GlyphsVersion;
        label.SyncGlyphs();
        Assert.Equal(version, label.GlyphsVersion);

        label.Billboard = BillboardMode.FixedY; // turned by the renderer: no rebuild
        label.IgnoreExposure = true;          // a colour scale in the renderer: no rebuild
        label.SyncGlyphs();
        Assert.Equal(version, label.GlyphsVersion);
        Assert.Equal(BillboardMode.FixedY, label.RenderBillboard);

        label.Text = "Hattie Jane";
        label.SyncGlyphs();
        Assert.NotEqual(version, label.GlyphsVersion);

        label.Shaded = true; // the mesh path keeps its own rebuild rule
        var mesh = label.GetRenderMesh()!;
        var meshVersion = mesh.Version;
        Assert.Same(mesh, label.GetRenderMesh());
        Assert.Equal(meshVersion, mesh.Version);
        label.Free();
    }

    [Fact]
    public void PropertiesRoundTripThroughScenes()
    {
        var label = new Label3D
        {
            Name = "NameLabel",
            Text = "Dusty Nell",
            FontSize = 64,
            OutlineSize = 14,
            OutlineModulate = Color.FromArgb(200, 254, 244, 216),
            Billboard = BillboardMode.FixedY,
            NoDepthTest = true,
            IgnoreExposure = true,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var copy = (Label3D)PackedScene.Parse(SceneSaver.ToJson(label)).Instantiate();
        Assert.Equal("Dusty Nell", copy.Text);
        Assert.Equal(64, copy.FontSize);
        Assert.Equal(14, copy.OutlineSize);
        Assert.Equal(label.OutlineModulate, copy.OutlineModulate);
        Assert.Equal(BillboardMode.FixedY, copy.Billboard);
        Assert.True(copy.NoDepthTest);
        Assert.True(copy.IgnoreExposure);
        Assert.False(new Label3D().IgnoreExposure); // off by default (ADR 0257)
        Assert.Equal(VerticalAlignment.Bottom, copy.VerticalAlignment);
        Assert.Equal(HorizontalAlignment.Center, copy.HorizontalAlignment);
        Assert.Equal(-1, copy.OutlineRenderPriority);
        label.Free();
        copy.Free();
    }

    [Fact]
    public void LinesStackDownwardsAndAlignAcrossTheWidestLine()
    {
        var one = Label("Wide line here");
        one.OutlineSize = 0;
        var two = Label("Wide line here\nab");
        two.OutlineSize = 0;
        two.LineSpacing = 4;
        Assert.Equal(["Wide line here", "ab"], two.GetLines());

        var b1 = one.GetAabb();
        var b2 = two.GetAabb();
        var lineHeight = Lato.GetHeight(32) * 0.01f;
        Assert.Equal(b1.Size.X, b2.Size.X, 3);                       // the block is as wide as its widest line
        Assert.True(b2.Size.Y > b1.Size.Y + lineHeight * 0.6f, $"{b2.Size.Y} vs {b1.Size.Y}");
        Assert.Equal(0f, b2.Center.X, 1);                             // centred around the node, line by line

        two.HorizontalAlignment = HorizontalAlignment.Left;
        var left = two.GetAabb();
        Assert.True(left.Min.X >= -0.02f && left.Min.X < 0.05f, left.ToString()); // a pixel of side bearing
    }

    [Fact]
    public void AutowrapBreaksInsideWidthAndUppercaseApplies()
    {
        var label = Label("one two three four five six");
        label.Autowrap = AutowrapMode.Word;
        label.Width = Lato.GetStringSize("one two three", fontSize: 32).X + 1;
        Assert.Equal(["one two three", "four five six"], label.GetLines());

        label.Uppercase = true; // wider capitals wrap again
        Assert.All(label.GetLines(), line => Assert.Equal(line.ToUpperInvariant(), line));
        Assert.Equal("ONE TWO THREE FOUR FIVE SIX", string.Join(' ', label.GetLines()));

        label.Autowrap = AutowrapMode.Off;
        Assert.Single(label.GetLines());
    }

    [Fact]
    public void FixedSizeAndAlphaCutReachTheRendererAndMaterials()
    {
        var label = Label("Tag");
        Assert.Equal(0f, label.RenderFixedPixelSize);
        label.FixedSize = true;
        Assert.Equal(0.01f, label.RenderFixedPixelSize);

        label.AlphaCut = SpriteAlphaCut.Discard;
        label.TextRenderPriority = 3;
        var mesh = label.GetRenderMesh()!;
        var text = (StandardMaterial3D)mesh.GetSurface(mesh.SurfaceCount - 1).Material!;
        Assert.Equal(AlphaMode.Cutout, text.Transparency);
        Assert.Equal(3, text.RenderPriority);
    }

    [Fact]
    public void FixedSizeScalesTheModelSoOneMeshPixelIsOneScreenPixel()
    {
        // A 90° vertical field over 200 px: at depth 10 one pixel is 2·10·tan(45°)/200 = 0.1 world units.
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1f, 0.1f, 100f);
        var pixelScale = LodSelection.PixelScale(projection, 200);
        var model = Matrix4x4.CreateTranslation(0, 0, -10);
        var scaled = Billboard.FixedSize(model, 0.005f, Matrix4x4.Identity, pixelScale, perspective: true);
        Assert.Equal(0.1f / 0.005f, scaled.M11, 3);
        Assert.Equal(new Vector3(0, 0, -10), scaled.Translation);

        // Twice as far, twice the scale; orthographic views ignore the depth.
        var far = Billboard.FixedSize(Matrix4x4.CreateTranslation(0, 0, -20), 0.005f, Matrix4x4.Identity, pixelScale, perspective: true);
        Assert.Equal(2 * scaled.M11, far.M11, 3);
        var ortho = Billboard.FixedSize(model, 0.005f, Matrix4x4.Identity, 20f, perspective: false);
        Assert.Equal(1f / 20f / 0.005f, ortho.M11, 3);
    }
}
