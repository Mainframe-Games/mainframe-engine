using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// The 3D view's resolution (Editor Settings › 3D view): Auto renders one pixel per point, so a 2× display renders a
/// quarter of its pixels; picking, gizmos and icons stay in view pixels; 2D tabs render every pixel.
/// </summary>
public sealed class ViewResolutionSettingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-view-resolution").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(ViewResolution.Auto, 1f, 1f)]
    [InlineData(ViewResolution.Auto, 2f, 0.5f)]
    [InlineData(ViewResolution.Auto, 1.5f, 1f / 1.5f)]
    [InlineData(ViewResolution.Full, 2f, 1f)]
    [InlineData(ViewResolution.ThreeQuarters, 2f, 0.75f)]
    [InlineData(ViewResolution.Half, 1f, 0.5f)]
    public void TheScaleFollowsTheChoiceAndTheDisplay(ViewResolution resolution, float pixelScale, float expected) =>
        Assert.Equal(expected, EditorSettings.ViewResolutionScale(resolution, pixelScale), 5);

    [Fact]
    public void AutoIsTheDefaultAndOtherChoicesAreSavedLoadedAndCloned()
    {
        Assert.Equal(ViewResolution.Auto, new EditorSettings().ViewResolution);
        var path = Path.Combine(_directory, "editor_settings.json");
        new EditorSettings().Save(path);
        Assert.Contains("\"viewResolution\": null", File.ReadAllText(path), StringComparison.Ordinal); // Auto: the default
        Assert.Equal(ViewResolution.Auto, EditorSettings.Load(path).ViewResolution);

        new EditorSettings { ViewResolution = ViewResolution.ThreeQuarters }.Save(path);
        Assert.Contains("\"viewResolution\": \"threeQuarters\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(ViewResolution.ThreeQuarters, EditorSettings.Load(path).ViewResolution);
        Assert.Equal(ViewResolution.Half, new EditorSettings { ViewResolution = ViewResolution.Half }.Clone().ViewResolution);

        File.WriteAllText(path, """{ "format": 1, "viewResolution": "huge" }""");
        Assert.Equal(ViewResolution.Auto, EditorSettings.Load(path).ViewResolution);
    }
}

[Collection(nameof(SerialEditor))]
public sealed class ViewResolutionViewportTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    [Fact]
    public void TheViewRendersAtTheChosenResolutionAndMapsPointsToItsPixels()
    {
        _editor.Host.PixelScale = 2f; // a Retina display
        _editor.Tick(2);
        var rect = W.Viewport.ViewRect;
        var viewport = _editor.Scene.Viewport;

        // Auto: one pixel per point.
        Assert.Equal(1f, W.Viewport.ViewScale);
        Assert.Equal(new Vector2(MathF.Round(rect.Width), MathF.Round(rect.Height)), W.Viewport.ViewPixels);
        Assert.Equal((int)MathF.Round(rect.Width), viewport.Width);
        Assert.Equal((int)MathF.Round(rect.Height), viewport.Height);
        Assert.Equal(1f, W.Gizmo.PixelScale); // gizmo handles keep their size in points
        Assert.Equal(new Vector2(10, 20), W.Viewport.LocalPixel(new Vector2(rect.X + 10, rect.Y + 20)));

        // Full: every display pixel.
        W.Settings.ViewResolution = ViewResolution.Full;
        _editor.Tick(2);
        Assert.Equal(2f, W.Viewport.ViewScale);
        Assert.Equal((int)MathF.Round(rect.Width * 2), viewport.Width);
        Assert.Equal(2f, W.Gizmo.PixelScale);
        Assert.Equal(new Vector2(20, 40), W.Viewport.LocalPixel(new Vector2(rect.X + 10, rect.Y + 20)));

        W.Settings.ViewResolution = ViewResolution.Half;
        _editor.Tick(2);
        Assert.Equal((int)MathF.Round(rect.Width), viewport.Width);

        // 2D tabs render every pixel, whatever the setting.
        W.Commands.Execute("view.2d");
        _editor.Tick(2);
        Assert.Equal(2f, W.Viewport.ViewScale);
        Assert.Equal((int)MathF.Round(rect.Width * 2), viewport.Width);
    }

    [Fact]
    public void TheDialogAppliesTheViewResolution()
    {
        W.EditorSettingsDialog.Open();
        _editor.Tick();
        Assert.False(W.EditorSettingsDialog.Document.GetElementById("es-view-resolution").IsNull);
        W.EditorSettingsDialog.Working.ViewResolution = ViewResolution.ThreeQuarters;
        W.EditorSettingsDialog.Apply();
        Assert.Equal(ViewResolution.ThreeQuarters, W.Settings.ViewResolution);
        Assert.Empty(_editor.RmlMessages);
    }
}
