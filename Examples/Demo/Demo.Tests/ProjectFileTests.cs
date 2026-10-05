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
    public void MainSceneAndIconExist()
    {
        var settings = Load();
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.MainScene!)));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.Window.Icon!)));
    }
}
