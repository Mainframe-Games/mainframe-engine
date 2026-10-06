using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>Godot-style <c>title</c> tooltips in a document's <c>#tooltip</c> element (ADR 0121).</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class UiTooltipsTests
{
    private const string Body = """
        <button id='btn' title='Plant &amp; grow'><span id='label'>Go</span></button>
        <p id='plain'>no tip</p>
        <div id='tooltip' style='position: absolute; pointer-events: none;'></div>
        """;

    private static (UiTestTree Ui, UiDocument Doc) Open(string body = Body, double delay = 0)
    {
        var ui = new UiTestTree();
        ui.Server.TooltipDelaySeconds = delay;
        var doc = new UiDocument { Name = "Tips", Rml = UiTestTree.Page(body) };
        ui.AddLayer(0, doc);
        ui.Tick();
        return (ui, doc);
    }

    [Fact]
    public void ATitleShowsAtTheMouseAfterTheDelay()
    {
        var (ui, doc) = Open();
        using (ui)
        {
            var b = doc.GetElementById("label")!.Bounds; // a child of the titled button
            ui.Move(b.X + 2, b.Y + 2);
            ui.Tick();

            Assert.Equal("Plant & grow", doc.ShownTooltip);
            var tip = doc.GetElementById("tooltip")!;
            Assert.Equal("Plant &amp; grow", tip.InnerRml);
            ui.Tick(); // laid out: placed at the mouse + (10, 10)
            Assert.Equal(b.X + 12, tip.Bounds.X, 0.5f);
            Assert.Equal(b.Y + 12, tip.Bounds.Y, 0.5f);
        }
    }

    [Fact]
    public void LeavingOrPressingHidesIt()
    {
        var (ui, doc) = Open();
        using (ui)
        {
            var b = doc.GetElementById("btn")!.Bounds;
            ui.Move(b.X + 2, b.Y + 2);
            ui.Tick();
            Assert.NotNull(doc.ShownTooltip);

            var p = doc.GetElementById("plain")!.Bounds;
            ui.Move(p.X + 2, p.Y + 2);
            ui.Tick();
            Assert.Null(doc.ShownTooltip);

            ui.Move(b.X + 3, b.Y + 3);
            ui.Tick();
            Assert.NotNull(doc.ShownTooltip);
            ui.Button(MouseButton.Left, true, b.X + 3, b.Y + 3);
            ui.Tick();
            Assert.Null(doc.ShownTooltip);
        }
    }

    [Fact]
    public void NothingShowsBeforeTheDelay()
    {
        var (ui, doc) = Open(delay: 60);
        using (ui)
        {
            var b = doc.GetElementById("btn")!.Bounds;
            ui.Move(b.X + 2, b.Y + 2);
            ui.Tick(3);
            Assert.Null(doc.ShownTooltip);
        }
    }

    [Fact]
    public void ADocumentWithoutATooltipElementHasNone()
    {
        var (ui, doc) = Open("<button id='btn' title='x'>Go</button>");
        using (ui)
        {
            var b = doc.GetElementById("btn")!.Bounds;
            ui.Move(b.X + 2, b.Y + 2);
            ui.Tick();
            Assert.Null(doc.ShownTooltip);
        }
    }
}
