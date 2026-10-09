using System.Drawing;
using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Tests.UI;

/// <summary>The scale math of <see cref="UiScaling"/> (ADR 0181): Unity's "Scale With Screen Size" from framebuffer pixels.</summary>
public sealed class UiScalingMathTests
{
    private static readonly UiScaling Height1080 = UiScaling.ScaleWithScreenSize;

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(2560, 1440, 4f / 3f)]
    [InlineData(3840, 2160, 2f)]
    [InlineData(1280, 720, 2f / 3f)]
    [InlineData(3440, 1440, 4f / 3f)] // ultrawide: the height decides, the layout gets 2580 dp of width
    [InlineData(1600, 1200, 10f / 9f)] // 4:3
    public void ScaleWithScreenSizeFollowsTheHeightByDefault(int width, int height, float expected) =>
        Assert.Equal(expected, Height1080.ComputeDpRatio(new Vector2(width, height), 1f), 5);

    [Fact]
    public void TheMatchBlendsWidthAndHeightInLogSpace()
    {
        var ultrawide = new Vector2(3440, 1440);
        var width = Height1080 with { MatchWidthOrHeight = 0f };
        var half = Height1080 with { MatchWidthOrHeight = 0.5f };
        Assert.Equal(3440f / 1920f, width.ComputeDpRatio(ultrawide, 1f), 5);
        Assert.Equal(MathF.Sqrt(3440f / 1920f * (1440f / 1080f)), half.ComputeDpRatio(ultrawide, 1f), 4); // geometric mean (Unity)
        Assert.Equal(4f / 3f, Height1080.ComputeDpRatio(ultrawide, 1f), 5);

        // On the reference aspect every match gives the same scale.
        foreach (var match in (float[])[0f, 0.25f, 0.5f, 1f])
            Assert.Equal(2f, (Height1080 with { MatchWidthOrHeight = match }).ComputeDpRatio(new Vector2(3840, 2160), 1f), 4);
    }

    [Fact]
    public void ClampsLimitTheScale()
    {
        var clamped = Height1080 with { MinScale = 1f, MaxScale = 1.5f };
        Assert.Equal(1f, clamped.ComputeDpRatio(new Vector2(1280, 720), 1f)); // would be 0.667
        Assert.Equal(1.5f, clamped.ComputeDpRatio(new Vector2(3840, 2160), 1f)); // would be 2
        Assert.Equal(4f / 3f, clamped.ComputeDpRatio(new Vector2(2560, 1440), 1f), 5);
    }

    [Fact]
    public void HiDpiDisplaysScaleFromPixelsNotPoints()
    {
        // A 1920×1080-point window on a 2× Retina display is 3840×2160 pixels: the UI is 2× in pixels, the same physical
        // size as 1080p at 1×. The display's pixels per point does not enter the scale.
        Assert.Equal(2f, Height1080.ComputeDpRatio(new Vector2(3840, 2160), contentScale: 2f));
        Assert.Equal(1f, Height1080.ComputeDpRatio(new Vector2(1920, 1080), contentScale: 2f)); // contentScale 1 project on Retina

        // Constant pixel size is the content scale, whatever the window.
        Assert.Equal(2f, UiScaling.ConstantPixelSize.ComputeDpRatio(new Vector2(3840, 2160), 2f));
        Assert.Equal(1f, UiScaling.ConstantPixelSize.ComputeDpRatio(new Vector2(640, 360), 1f));
    }

    [Fact]
    public void InvalidSettingsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiScaling { ReferenceResolution = new Vector2(0, 1080) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiScaling { ReferenceResolution = new Vector2(float.NaN, 1080) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiScaling { MatchWidthOrHeight = 1.5f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiScaling { MinScale = -1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiScaling { MaxScale = float.PositiveInfinity });
        Assert.Equal(1f, UiScaling.ScaleWithScreen(Vector2.Zero, UiScaling.DefaultReferenceResolution, 1f)); // a zero-size window
    }

    [Fact]
    public void LayersUseTheProjectScaleUnlessTheyOverrideIt()
    {
        var size = new Vector2(2560, 1440);
        var layer = new UiLayer();
        Assert.Equal(UiScaleMode.Project, layer.ScaleMode); // the default
        Assert.Equal(2f, layer.ComputeDpRatio(size, 2f)); // outside a tree: constant pixel size = the content scale
        Assert.Equal(4f / 3f, layer.ComputeDpRatio(size, 2f, Height1080), 5);

        layer.Scaling = UiScaling.ConstantPixelSize; // per-layer override (the editor's UI preview)
        Assert.Equal(2f, layer.ComputeDpRatio(size, 2f, Height1080));
        layer.Scaling = null;

        layer.ScaleMode = UiScaleMode.Dpi; // explicit modes ignore the project's scale
        Assert.Equal(2f, layer.ComputeDpRatio(size, 2f, Height1080));
        layer.ScaleMode = UiScaleMode.Pixels;
        Assert.Equal(1f, layer.ComputeDpRatio(size, 2f, Height1080));
    }
}

/// <summary>The UI server applying <see cref="UiServer.Scaling"/> to its layers, the overlay and input (ADR 0181).</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class UiScalingServerTests
{
    private static string BoxPage(UiTestTree ui) => ui.Write("UI/scaled.rml", """
        <rml><head><style>
        body { font-family: LatoLatin; width: 100%; height: 100%; pointer-events: none; }
        #box { display: block; position: absolute; left: 100dp; top: 50dp; width: 200dp; height: 100dp; pointer-events: auto; }
        </style></head><body><div id="box"/></body></rml>
        """);

    [Fact]
    public void LayersFollowTheServerScaleAndRelayoutWhenItChanges()
    {
        using var ui = new UiTestTree(new UiServerOptions { Scaling = UiScaling.ScaleWithScreenSize }, viewport: new Vector2(2560, 1440));
        var doc = new UiDocument { Name = "Scaled", Source = BoxPage(ui) };
        var layer = ui.AddLayer(0, doc);
        ui.Tick(2);

        Assert.Equal(4f / 3f, layer.Context!.DensityIndependentPixelRatio, 5);
        AssertRect(doc.GetElementById("box")!.Bounds, 133.333f, 66.667f, 266.667f, 133.333f);

        // A new scale (a settings menu, the editor's project settings) re-lays out on the next frame.
        ui.Server.Scaling = UiScaling.ScaleWithScreenSize with { ReferenceResolution = new Vector2(1280, 720) };
        ui.Tick();
        Assert.Equal(2f, layer.Context.DensityIndependentPixelRatio);
        AssertRect(doc.GetElementById("box")!.Bounds, 200, 100, 400, 200);

        ui.Server.Scaling = UiScaling.ConstantPixelSize;
        ui.Tick();
        Assert.Equal(1f, layer.Context.DensityIndependentPixelRatio);
        AssertRect(doc.GetElementById("box")!.Bounds, 100, 50, 200, 100);
    }

    [Fact]
    public void TheScaleFollowsTheLayoutSize()
    {
        // A resize reaches the layer as a new layout size (here its region, which the framebuffer stands in for
        // headless): the dp ratio and the layout follow on the next frame.
        using var ui = new UiTestTree(new UiServerOptions { Scaling = UiScaling.ScaleWithScreenSize }, viewport: new Vector2(3840, 2160));
        var doc = new UiDocument { Name = "Scaled", Source = BoxPage(ui) };
        var layer = ui.AddLayer(0, doc);
        ui.Tick(2);
        Assert.Equal(2f, layer.Context!.DensityIndependentPixelRatio);
        Assert.Equal(400f, doc.GetElementById("box")!.Bounds.Width, 2);

        layer.Region = new Rectangle(0, 0, 1280, 720);
        ui.Tick();
        Assert.Equal(2f / 3f, layer.Context.DensityIndependentPixelRatio, 5);
        Assert.Equal(133.333f, doc.GetElementById("box")!.Bounds.Width, 2);
    }

    [Fact]
    public void ExplicitLayerModesAndTheDevOverlayLayer()
    {
        using var ui = new UiTestTree(new UiServerOptions { Scaling = UiScaling.ScaleWithScreenSize, ContentScale = 2f }, viewport: new Vector2(3840, 2160));
        var project = ui.AddLayer(0);
        var dpi = ui.AddLayer(1);
        dpi.ScaleMode = UiScaleMode.Dpi;
        var overlay = new DevOverlay(ui.Tree) { Visible = true }; // an RmlUi layer of its own, in Project mode
        ui.Servers.Register(overlay);
        ui.Tick(2);

        Assert.Equal(2f, project.Context!.DensityIndependentPixelRatio); // 2160 / 1080
        Assert.Equal(2f, dpi.Context!.DensityIndependentPixelRatio); // the content scale
        var overlayLayer = ui.Server.Layers.Single(l => l.Name == "DevOverlay");
        Assert.Equal(UiScaleMode.Project, overlayLayer.ScaleMode);

        ui.Server.Scaling = UiScaling.ScaleWithScreenSize with { ReferenceResolution = new Vector2(1280, 720) };
        ui.Tick();
        Assert.Equal(3f, project.Context.DensityIndependentPixelRatio);
        Assert.Equal(3f, overlayLayer.Context!.DensityIndependentPixelRatio);
        Assert.Equal(2f, dpi.Context.DensityIndependentPixelRatio);
    }

    [Fact]
    public void MouseInputHitsScaledElements()
    {
        // RmlUi contexts are laid out in pixels and the mouse arrives in pixels: scaling moves the elements, and the
        // mouse finds them where they are drawn.
        using var ui = new UiTestTree(new UiServerOptions { Scaling = UiScaling.ScaleWithScreenSize }, viewport: new Vector2(3840, 2160));
        var doc = new UiDocument { Name = "Scaled", Source = BoxPage(ui) };
        ui.AddLayer(0, doc);
        ui.Tick(2);
        var clicks = 0;
        doc.GetElementById("box")!.Click += _ => clicks++;

        // (100, 50) dp → (200, 100) px; the box spans 200..600 × 100..300 px.
        Assert.False(ui.Button(Silk.NET.Input.MouseButton.Left, true, 150, 80)); // outside at 2×, inside at 1×
        ui.Button(Silk.NET.Input.MouseButton.Left, false, 150, 80);
        Assert.Equal(0, clicks);
        Assert.True(ui.Button(Silk.NET.Input.MouseButton.Left, true, 590, 290));
        Assert.True(ui.Button(Silk.NET.Input.MouseButton.Left, false, 590, 290));
        ui.Tick();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ScaledFramesDoNotAllocate()
    {
        using var ui = new UiTestTree(new UiServerOptions { Scaling = UiScaling.ScaleWithScreenSize with { MatchWidthOrHeight = 0.5f, MinScale = 0.5f } },
            viewport: new Vector2(2560, 1080));
        var doc = new UiDocument { Name = "Scaled", Source = BoxPage(ui) };
        ui.AddLayer(0, doc);
        ui.Tick(30);
        Assert.Equal(0, AllocationGate.SmallestWindow(() => ui.Tick(200)));
    }

    private static void AssertRect(RmlRect actual, float x, float y, float width, float height) =>
        Assert.True(MathF.Abs(actual.X - x) < 0.01f && MathF.Abs(actual.Y - y) < 0.01f && MathF.Abs(actual.Width - width) < 0.01f &&
                    MathF.Abs(actual.Height - height) < 0.01f, $"{actual.X}, {actual.Y}, {actual.Width}×{actual.Height}; expected {x}, {y}, {width}×{height}");
}
