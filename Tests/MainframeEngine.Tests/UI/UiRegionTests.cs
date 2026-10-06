using System.Drawing;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary><see cref="UiLayer.Region"/>: a layer laid out in, and taking the mouse from, a rectangle of the window.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class UiRegionTests
{
    private const string FullBody = """
        <rml><head><style>
        body { font-family: LatoLatin; font-size: 16px; width: 100%; height: 100%; pointer-events: none; }
        #root { display: block; width: 100%; height: 100%; }
        button { display: block; width: 100px; height: 30px; pointer-events: auto; }
        </style></head><body data-model='hud'><div id='root'><button id='btn'>Go</button></div></body></rml>
        """;

    [Fact]
    public void ARegionLayerIsLaidOutAtTheRegionSize()
    {
        using var ui = new UiTestTree(viewport: new System.Numerics.Vector2(800, 600));
        var document = new UiDocument { Name = "Doc", Rml = FullBody };
        var layer = ui.AddLayer(0, document);
        layer.ScaleMode = UiScaleMode.Pixels;
        ui.Tick();
        Assert.Equal(800, document.GetElementById("root")!.Bounds.Width, 1);

        layer.Region = new Rectangle(100, 50, 300, 200);
        ui.Tick();
        var body = document.GetElementById("root")!.Bounds;
        Assert.Equal(300, body.Width, 1);
        Assert.Equal(200, body.Height, 1);
        Assert.Equal(0, body.X, 1); // RmlUi lays the region out from (0, 0); the renderer moves it to the region

        layer.Region = null;
        ui.Tick();
        Assert.Equal(600, document.GetElementById("root")!.Bounds.Height, 1);
    }

    [Fact]
    public void TheMouseIsTranslatedIntoTheRegionAndIgnoredOutsideIt()
    {
        using var ui = new UiTestTree(viewport: new System.Numerics.Vector2(800, 600));
        var document = new HudTestDocument { Name = "Hud", Rml = FullBody };
        var layer = ui.AddLayer(0, document);
        layer.ScaleMode = UiScaleMode.Pixels;
        layer.Region = new Rectangle(300, 200, 400, 300);
        ui.Tick();
        var b = document.GetElementById("btn")!.Bounds;
        var (bx, by) = (b.X + 5, b.Y + 5);

        // The button's own coordinates are outside the region: the game gets the mouse.
        Assert.False(ui.Move(bx, by));
        Assert.False(ui.Button(MouseButton.Left, true, bx, by));
        Assert.False(ui.Button(MouseButton.Left, false, bx, by));
        Assert.Equal(0, document.Clicks);

        // Offset by the region's origin it is over the button.
        Assert.True(ui.Move(300 + bx, 200 + by));
        Assert.True(ui.Server.IsPointerOverUi);
        Assert.True(ui.Button(MouseButton.Left, true, 300 + bx, 200 + by));
        Assert.True(ui.Button(MouseButton.Left, false, 300 + bx, 200 + by));
        Assert.Equal(1, document.Clicks);

        // Leaving the region drops the hover (the layer saw no move out there).
        Assert.False(ui.Move(10, 10));
        Assert.False(ui.Server.IsPointerOverUi);
        Assert.False(ui.Tree.PushInput(new InputEventMouseWheel { Delta = new System.Numerics.Vector2(0, 1) }));
    }

    [Fact]
    public void ReloadingOneDocumentLeavesTheOthersAlone()
    {
        using var ui = new UiTestTree();
        var a = new UiDocument { Name = "A", Source = ui.Write("UI/a.rml", UiTestTree.Page("<p id='t'>a</p>")) };
        var b = new UiDocument { Name = "B", Source = ui.Write("UI/b.rml", UiTestTree.Page("<p id='t'>b</p>")) };
        ui.AddLayer(0, a);
        ui.AddLayer(1, b);
        ui.Tick();
        var reloadedA = 0;
        var reloadedB = 0;
        a.Reloaded += () => reloadedA++;
        b.Reloaded += () => reloadedB++;

        ui.Write("UI/a.rml", UiTestTree.Page("<p id='t'>changed</p>"));
        ui.Write("UI/b.rml", UiTestTree.Page("<p id='t'>changed</p>"));
        ui.Server.Reload(a, UiReloadKind.Documents);

        Assert.Equal(1, reloadedA);
        Assert.Equal(0, reloadedB);
        Assert.Equal("changed", a.GetElementById("t")!.InnerRml);
        Assert.Equal("b", b.GetElementById("t")!.InnerRml);

        // Style sheets only: the DOM (and its state) stays.
        ui.Server.Reload(a, UiReloadKind.StyleSheets);
        Assert.Equal(1, reloadedA);
    }
}
