using System.Numerics;
using System.Text;

namespace MainframeEngine.Tests.Canvas;

/// <summary>The 2D project settings: window stretch (Godot spellings accepted) and the canvas clear colour.</summary>
public sealed class CanvasProjectSettingsTests
{
    [Fact]
    public void StretchAndCanvasClearColourRoundTrip()
    {
        const string json = """
            {
              "format": 1, "name": "Game", "engineVersion": "0.0.0-dev",
              "window": { "width": 1920, "height": 1080, "stretchMode": "canvas_items", "stretchAspect": "expand", "stretchScale": 1.5, "stretchScaleMode": "integer" },
              "rendering": { "canvasClearColor": [0.3, 0.3, 0.3, 1] }
            }
            """;
        var s = ProjectSettings.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(ContentScaleMode.CanvasItems, s.Window.StretchMode);
        Assert.Equal(ContentScaleAspect.Expand, s.Window.StretchAspect);
        Assert.Equal(1.5f, s.Window.StretchScale);
        Assert.Equal(ContentScaleStretch.Integer, s.Window.StretchScaleMode);
        Assert.Equal(new Vector4(0.3f, 0.3f, 0.3f, 1), s.Rendering.CanvasClearColor);

        var back = ProjectSettings.Parse(s.ToJson());
        Assert.Equal(s.Window.StretchMode, back.Window.StretchMode);
        Assert.Equal(s.Window.StretchAspect, back.Window.StretchAspect);
        Assert.Equal(s.Window.StretchScale, back.Window.StretchScale);
        Assert.Equal(s.Window.StretchScaleMode, back.Window.StretchScaleMode);
        Assert.Equal(s.Rendering.CanvasClearColor, back.Rendering.CanvasClearColor);

        var defaults = ProjectSettings.Parse(new ProjectSettings { Name = "Game" }.ToJson());
        Assert.Equal(ContentScaleMode.Disabled, defaults.Window.StretchMode);
        Assert.Null(defaults.Rendering.CanvasClearColor);
    }

    [Fact]
    public void AnUnknownStretchModeIsAnError()
    {
        var json = """{ "format": 1, "name": "Game", "engineVersion": "0", "window": { "stretchMode": "sideways" } }""";
        var e = Assert.Throws<InvalidDataException>(() => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("window.stretchMode", e.Message, StringComparison.Ordinal);
    }
}
