using System.Numerics;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// Sub-viewport textures on 3D materials (ADR 0216): a 2D view's pattern and a 3D view's camera image on albedo, emission
/// and spatial-shader sampler slots, upright, followed across a resize, validation-clean and allocation-free.
/// </summary>
public class ViewportMaterialTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    [Fact]
    public void ScreensShowTheirSubViewportsAndMatchGolden()
    {
        var result = HostRunner.Run("viewport-material", Output("viewport-material"), "--capture", $"{ViewportMaterialScene.CheckFrame}", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, ViewportMaterialScene.CheckFrame);

        var image = Png.ReadRgba8(result.Captures.Single(c => c.Frame == ViewportMaterialScene.CheckFrame).Path);
        // The 2D view, upright: each quadrant of the panel shows its colour (after the resize: the redrawn, larger image).
        AssertHue(image, ViewportMaterialScene.PanelAlbedo, new Vector2(0.25f, 0.25f), Hue.Red, "panel top left");
        AssertHue(image, ViewportMaterialScene.PanelAlbedo, new Vector2(0.75f, 0.25f), Hue.Green, "panel top right");
        AssertHue(image, ViewportMaterialScene.PanelAlbedo, new Vector2(0.25f, 0.75f), Hue.Blue, "panel bottom left");
        AssertHue(image, ViewportMaterialScene.PanelAlbedo, new Vector2(0.75f, 0.75f), Hue.White, "panel bottom right");
        // The spatial shader rotates the channels (rgb → gbr): red → blue, green → red, blue → green.
        AssertHue(image, ViewportMaterialScene.PanelShader, new Vector2(0.25f, 0.25f), Hue.Blue, "shader top left");
        AssertHue(image, ViewportMaterialScene.PanelShader, new Vector2(0.75f, 0.25f), Hue.Red, "shader top right");
        AssertHue(image, ViewportMaterialScene.PanelShader, new Vector2(0.25f, 0.75f), Hue.Green, "shader bottom left");
        AssertHue(image, ViewportMaterialScene.PanelShader, new Vector2(0.75f, 0.75f), Hue.White, "shader bottom right");
        // The 3D view: the orange box up and to the left, teal elsewhere; on an albedo and an emission slot.
        foreach (var (screen, slot) in new[] { (ViewportMaterialScene.ViewAlbedo, "albedo"), (ViewportMaterialScene.ViewEmission, "emission") })
        {
            AssertHue(image, screen, ViewportMaterialScene.BoxUv, Hue.Orange, $"view box ({slot})");
            AssertHue(image, screen, new Vector2(0.75f, 0.75f), Hue.Teal, $"view background ({slot})");
        }
    }

    [Fact]
    public void ViewportMaterialFramesAllocateNothing()
    {
        const int warmup = 20, measured = 90; // after the resize; the 3D view's box spins every frame
        var result = HostRunner.Run("viewport-material", Output("viewport-material-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");

        Gates.AssertValidationClean(result);
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Viewport-texture frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
    }

    private enum Hue
    {
        Red,
        Green,
        Blue,
        White,
        Orange,
        Teal,
    }

    private static void AssertHue(PngImage image, Vector3 screen, Vector2 uv, Hue hue, string what)
    {
        var pixel = ViewportMaterialScene.ToPixel(screen, uv, image.Width, image.Height);
        var i = ((int)pixel.Y * image.Width + (int)pixel.X) * 4;
        int r = image.Pixels[i], g = image.Pixels[i + 1], b = image.Pixels[i + 2];
        var ok = hue switch
        {
            Hue.Red => r > g + 80 && r > b + 80,
            Hue.Green => g > r + 80 && g > b + 60,
            Hue.Blue => b > r + 80 && b > g + 60,
            Hue.White => Math.Min(r, Math.Min(g, b)) > 180,
            Hue.Orange => r > b + 120 && g > b + 40 && r > g + 30,
            Hue.Teal => g > r + 30 && b > r + 30 && Math.Max(g, b) < 200,
            _ => false,
        };
        Assert.True(ok, $"{what} at ({(int)pixel.X}, {(int)pixel.Y}) is ({r}, {g}, {b}), not {hue}");
    }
}
