using MainframeEngine;

namespace Demo.Tests;

public sealed class ProjectFileTests
{
    private static ProjectSettings Load() => ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj"));

    [Fact]
    public void ProjectFileUsesCanvasItemsStretch()
    {
        var settings = Load();
        Assert.Equal(ContentScaleMode.CanvasItems, settings.Window.StretchMode);
        Assert.Equal((1280, 720), (settings.Window.Width, settings.Window.Height));
    }

    [Fact]
    public void TheUiScalesWithTheScreenFromTheDemosOwn720pBase()
    {
        // ADR 0181: the Demo's panels and nav bar are authored for its 1280×720 window, the 2D canvas's stretch base too,
        // so both grow together with the window; the default window looks as before.
        var settings = Load();
        Assert.Equal(UiScalingMode.ScaleWithScreenSize, settings.Ui.ScaleMode);
        Assert.Equal(new Vector2I(settings.Window.Width, settings.Window.Height), settings.Ui.ReferenceResolution);
    }

    [Fact]
    public void MainSceneAndIconExist()
    {
        var settings = Load();
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.MainScene!)));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.Window.Icon!)));
    }
}
