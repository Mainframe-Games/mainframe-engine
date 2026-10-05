using MainframeEngine.Tests.UI;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Debugging;

[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayTests
{
    private static (UiTestTree Ui, DevOverlay Overlay) Create()
    {
        var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        return (ui, overlay);
    }

    private static UiDocument PanelsDocument(UiTestTree ui) =>
        (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);

    private static GameTime Frame(float delta = 1f / 60f) => new() { DeltaTime = delta };

    [Fact]
    public void PanelsBindAndShowWhenVisible()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        var value = 7;
        overlay.AddPanel("test", "Test", "<p id='v'>{{v}}</p>", p => p.Model!.Bind("v", () => value));
        overlay.Visible = true;
        ui.Tick(3);
        Assert.Equal("7", PanelsDocument(ui).GetElementById("v")!.InnerRml);
    }

    [Fact]
    public void PanelAddedAfterLoadIsBound()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.Visible = true;
        ui.Tick(3);
        overlay.AddPanel("late", "Late", "<p id='late'>{{x}}</p>", p => p.Model!.Bind("x", () => "ok"));
        ui.Tick(3);
        Assert.Equal("ok", PanelsDocument(ui).GetElementById("late")!.InnerRml);
    }

    [Fact]
    public void ClickingTheTitleTogglesThePanel()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        var panel = overlay.AddPanel("t", "Toggle me", "<p id='body'>x</p>");
        overlay.Visible = true;
        ui.Tick(3);
        Assert.True(panel.Expanded);
        var b = PanelsDocument(ui).QuerySelector(".dev-title")!.Bounds;
        var (x, y) = (b.X + 5, b.Y + 5);
        Assert.True(ui.Move(x, y));
        Assert.True(ui.Button(MouseButton.Left, true, x, y));
        Assert.True(ui.Button(MouseButton.Left, false, x, y));
        Assert.False(panel.Expanded);
    }

    [Fact]
    public void RemovedPanelDisappearsAndItsIdCanBeReused()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.AddPanel("a", "A", "<p id='a'>{{x}}</p>", p => p.Model!.Bind("x", () => "one"));
        overlay.Visible = true;
        ui.Tick(3);
        Assert.True(overlay.RemovePanel("a"));
        Assert.False(overlay.RemovePanel("a"));
        ui.Tick(3);
        Assert.Null(PanelsDocument(ui).GetElementById("a"));
        overlay.AddPanel("a", "A", "<p id='a'>{{x}}</p>", p => p.Model!.Bind("x", () => "two"));
        ui.Tick(3);
        Assert.Equal("two", PanelsDocument(ui).GetElementById("a")!.InnerRml);
    }

    [Fact]
    public void HiddenOverlayDoesNotRefresh()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        var refreshes = 0;
        var frames = 0;
        overlay.Refreshed += () => refreshes++;
        overlay.Frame += _ => frames++;
        for (var i = 0; i < 60; i++)
            overlay.Process(Frame());
        Assert.Equal(0, refreshes);
        Assert.Equal(0, frames);
        overlay.Visible = true;
        for (var i = 0; i < 60; i++)
            overlay.Process(Frame());
        Assert.InRange(refreshes, 3, 5); // ~4 Hz over one second
        Assert.Equal(60, frames);
    }

    [Fact]
    public void LayerSurvivesSceneChangesAndFollowsVisible()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.AddPanel("t", "T", "<p id='v'>{{v}}</p>", p => p.Model!.Bind("v", () => 3));
        overlay.Visible = true;
        ui.Tick(3);
        var layer = ui.Tree.Root.GetNode<UiLayer>("DevOverlay");
        Assert.True(layer.Visible);

        ui.Tree.ChangeScene(new Node { Name = "SceneA" });
        ui.Tick(2);
        ui.Tree.ChangeScene(new Node { Name = "SceneB" });
        ui.Tick(2);
        Assert.Same(layer, ui.Tree.Root.GetNode<UiLayer>("DevOverlay"));
        Assert.True(layer.Visible);
        Assert.Equal("3", PanelsDocument(ui).GetElementById("v")!.InnerRml);

        overlay.Visible = false;
        Assert.False(layer.Visible);
        overlay.Visible = true;
        Assert.True(layer.Visible);
    }

    [Fact]
    public void DuplicatePanelIdsAreRejected()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.AddPanel("a", "A", "<p/>");
        Assert.Throws<ArgumentException>(() => overlay.AddPanel("a", "A", "<p/>"));
    }
}
