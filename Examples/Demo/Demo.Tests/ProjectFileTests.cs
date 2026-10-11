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

    [Fact]
    public void TheInputActionsAreLabelledGroupedAndQuitIsLocked()
    {
        var input = Load().Input;
        foreach (var name in (string[])["scene_next", "scene_prev", "settings", "quit", "drop", "rain"])
            Assert.NotNull(input.GetAction(name)?.Label);
        Assert.False(input.GetAction("quit")!.Rebindable);
        Assert.True(input.GetAction("settings")!.Rebindable);
        Assert.Equal("physics", input.GetAction("drop")!.Group);
        Assert.Equal(["key:F10", "pad:Start"], input.GetAction("settings")!.Bindings.Select(b => b.ToString()));
    }

    [Fact]
    public void SavesKeepTwoBackupsWithThumbnailsInAStableFolder()
    {
        var settings = Load();
        Assert.Equal("MainframeDemo", settings.UserDataFolderName);
        Assert.Equal(2, settings.Saves.Backups);
        Assert.True(settings.Saves.Thumbnails);
        Assert.True(settings.ToEngineOptions().EnableFrameCapture);
    }
}
