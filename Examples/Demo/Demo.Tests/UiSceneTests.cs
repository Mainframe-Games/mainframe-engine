using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class UiSceneTests
{
    private static (ServerRegistry Servers, SceneTree Tree, UiServer Ui) Create(bool hotReload)
    {
        var servers = new ServerRegistry();
        var ui = new UiServer(options: new UiServerOptions { HotReload = hotReload });
        servers.Register(ui);
        var tree = new SceneTree(servers);
        tree.ChangeScene(UiScene.Build());
        return (servers, tree, ui);
    }

    [Fact]
    public void TheSceneHoldsTheShowcaseDocument()
    {
        var (servers, tree, _) = Create(hotReload: false);
        try
        {
            var showcase = tree.CurrentScene!.GetNode<UiShowcase>("Ui/Showcase");
            Assert.Equal("Content/UI/showcase.rml", showcase.Source);
        }
        finally
        {
            tree.Shutdown();
            servers.Dispose();
        }
    }

    [Theory]
    [InlineData(false, "hot-off", "hot-on")]
    [InlineData(true, "hot-on", "hot-off")]
    public void TheHotFlagPicksWhichHintShows(bool hotReload, string shown, string hidden)
    {
        var (servers, tree, _) = Create(hotReload);
        try
        {
            uint frame = 0;
            for (var i = 0; i < 3; i++)
                TestTime.Tick(tree, ref frame);
            var showcase = tree.CurrentScene!.GetNode<UiShowcase>("Ui/Showcase");
            Assert.True(showcase.GetElementById(shown)!.Bounds.Height > 0);
            Assert.True(showcase.GetElementById(hidden)!.Bounds.Height == 0);
        }
        finally
        {
            tree.Shutdown();
            servers.Dispose();
        }
    }

    [Fact]
    public void AStylesheetReloadIsCounted()
    {
        var (servers, tree, ui) = Create(hotReload: false);
        try
        {
            var showcase = tree.CurrentScene!.GetNode<UiShowcase>("Ui/Showcase");
            Assert.Equal(0, showcase.Reloads);
            ui.Reload(UiReloadKind.StyleSheets);
            ui.Reload(UiReloadKind.StyleSheets);
            Assert.Equal(2, showcase.Reloads);
        }
        finally
        {
            tree.Shutdown();
            servers.Dispose();
        }
    }

    [Fact]
    public void LeavingTheTreeStopsCounting()
    {
        var (servers, tree, ui) = Create(hotReload: false);
        try
        {
            var showcase = tree.CurrentScene!.GetNode<UiShowcase>("Ui/Showcase");
            tree.CurrentScene.GetNode("Ui").RemoveChild(showcase);
            ui.Reload(UiReloadKind.StyleSheets);
            Assert.Equal(0, showcase.Reloads);
            showcase.Free();
        }
        finally
        {
            tree.Shutdown();
            servers.Dispose();
        }
    }
}
