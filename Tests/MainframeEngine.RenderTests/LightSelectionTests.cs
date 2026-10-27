using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0217: with more point lights than the lights UBO holds, the renderer keeps the ones nearest the camera. 24 lamps in
/// red, green and blue groups of eight; the camera looks at the red group, then at the blue one (added last).
/// </summary>
public class LightSelectionTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void TheLightsNearestTheCameraAreRendered()
    {
        var result = HostRunner.Run("light-selection", Output("light-selection"), "--capture",
            $"{LightSelectionScene.RedFrame},{LightSelectionScene.BlueFrame}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, LightSelectionScene.RedFrame);
        Gates.AssertMatchesGolden(result, LightSelectionScene.BlueFrame);

        // Each view shows its own group's pools of light: the blue group was dropped whatever the camera before.
        var red = Capture(result, LightSelectionScene.RedFrame);
        var blue = Capture(result, LightSelectionScene.BlueFrame);
        var total = red.Width * red.Height;
        var (redInRed, blueInRed) = TintedPixels(red);
        var (redInBlue, blueInBlue) = TintedPixels(blue);
        TestContext.Current.SendDiagnosticMessage($"Tinted pixels: red view {redInRed} red / {blueInRed} blue, blue view {redInBlue} red / {blueInBlue} blue");
        Assert.True(redInRed > total / 20, $"the red view has {redInRed} red-lit pixels of {total}");
        Assert.True(blueInBlue > total / 20, $"the blue view has {blueInBlue} blue-lit pixels of {total}: its lamps were dropped");
        Assert.True(blueInRed < total / 200 && redInBlue < total / 200, "a view shows the other group's light");
    }

    // Pixels clearly lit red (r over g and b) and blue (b over r and g).
    private static (int Red, int Blue) TintedPixels(PngImage image)
    {
        int red = 0, blue = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            float r = image.Pixels[i], g = image.Pixels[i + 1], b = image.Pixels[i + 2];
            if (r > 60 && r > 1.4f * g && r > 1.4f * b)
                red++;
            else if (b > 60 && b > 1.4f * r && b > 1.2f * g)
                blue++;
        }

        return (red, blue);
    }
}
