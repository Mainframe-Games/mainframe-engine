using System.Drawing;
using System.Numerics;

namespace MainframeEngine.Tests.Scene;

using Color = System.Drawing.Color;

/// <summary><see cref="Label3D"/> (ADR 0200): glyph quads from the font atlas, alignment, outline, materials and rebuilds.</summary>
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

    [Fact]
    public void NothingIsDrawnWithoutTextAndTheEnginesLatoIsTheDefaultFont()
    {
        Assert.Null(new Label3D { Font = Lato }.GetRenderMesh());
        Assert.Null(Label(" ").GetRenderMesh()); // a space has no glyph

        var fallback = new Label3D { Text = "Hello", OutlineSize = 0 };
        var mesh = fallback.GetRenderMesh()!;
        var withLato = Label("Hello");
        withLato.OutlineSize = 0;
        withLato.PixelSize = fallback.PixelSize;
        Assert.Equal(SurfaceBounds(withLato.GetRenderMesh()!, 0), SurfaceBounds(mesh, 0)); // the same glyphs
        fallback.Free();
        withLato.Free();
    }

    [Fact]
    public void TheShownTextIsTranslatedAndFollowsTheLocale()
    {
        var tree = new SceneTree(new ServerRegistry());
        var label = Label("Hello");
        tree.Root.AddChild(label);
        var before = label.GetRenderMesh()!.Version;
        tree.Root.PropagateLocaleChanged(); // as Tr.SetLocale does
        label.GetRenderMesh();
        Assert.NotEqual(before, label.GetRenderMesh()!.Version); // rebuilt with Atr(Text)
        tree.Shutdown();
    }

    [Fact]
    public void TheOutlineDrawsUnderTheTextAndMaterialsFollowTheLabel()
    {
        var label = Label();
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
            Assert.Equal(ShadingMode.Unshaded, material.ShadingMode);
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

        var centred = SurfaceBounds(label.GetRenderMesh()!, 0);
        Assert.InRange(centred.Center.X, -2 * pixel, 2 * pixel);
        Assert.InRange(centred.Size.X, width * 0.9f, width * 1.05f); // glyph ink inside the advance

        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.VerticalAlignment = VerticalAlignment.Top;
        var topLeft = SurfaceBounds(label.GetRenderMesh()!, 0);
        Assert.InRange(topLeft.Min.X, -pixel, 3 * pixel);   // starts at the node
        Assert.True(topLeft.Max.Y <= pixel, $"{topLeft}"); // hangs below it

        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        var bottomRight = SurfaceBounds(label.GetRenderMesh()!, 0);
        Assert.InRange(bottomRight.Max.X, -3 * pixel, pixel); // ends at the node
        Assert.True(bottomRight.Min.Y >= -pixel, $"{bottomRight}"); // sits on it

        label.Offset = new Vector2(10, 20); // pixels, +Y up
        var moved = SurfaceBounds(label.GetRenderMesh()!, 0);
        Assert.Equal(bottomRight.Min.X + 10 * pixel, moved.Min.X, 4);
        Assert.Equal(bottomRight.Min.Y + 20 * pixel, moved.Min.Y, 4);
        label.Free();
    }

    [Fact]
    public void TheMeshIsRebuiltOnlyWhenTheLabelChanges()
    {
        var label = Label();
        var mesh = label.GetRenderMesh()!;
        var version = mesh.Version;
        Assert.Same(mesh, label.GetRenderMesh());
        Assert.Equal(version, mesh.Version);

        label.Billboard = BillboardMode.FixedY; // turned by the renderer: no rebuild
        label.GetRenderMesh();
        Assert.Equal(version, mesh.Version);
        Assert.Equal(BillboardMode.FixedY, label.RenderBillboard);

        label.Text = "Hattie Jane";
        label.GetRenderMesh();
        Assert.NotEqual(version, mesh.Version);
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
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var copy = (Label3D)PackedScene.Parse(SceneSaver.ToJson(label)).Instantiate();
        Assert.Equal("Dusty Nell", copy.Text);
        Assert.Equal(64, copy.FontSize);
        Assert.Equal(14, copy.OutlineSize);
        Assert.Equal(label.OutlineModulate, copy.OutlineModulate);
        Assert.Equal(BillboardMode.FixedY, copy.Billboard);
        Assert.True(copy.NoDepthTest);
        Assert.Equal(VerticalAlignment.Bottom, copy.VerticalAlignment);
        Assert.Equal(HorizontalAlignment.Center, copy.HorizontalAlignment);
        Assert.Equal(-1, copy.OutlineRenderPriority);
        label.Free();
        copy.Free();
    }
}
