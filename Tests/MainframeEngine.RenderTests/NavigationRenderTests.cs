namespace MainframeEngine.RenderTests;

/// <summary>
/// G3a: the navigation debug draw (grid outline and cells, baked solid cells, dashed mud, agent paths, next positions and
/// targets) over the collision-shape draw, through a <see cref="Camera2D"/>; golden and the validation gate.
/// </summary>
public class NavigationRenderTests
{
    [Fact]
    public void NavigationDebugDrawMatchesGolden()
    {
        var output = Path.Combine(RenderTestEnvironment.ArtifactsDirectory, "navigation");
        var result = HostRunner.Run("navigation", output, "--capture", "30", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);

        // The camera shows the 320 × 240 px grid exactly. A solid cell of the first wall, (6, 2), shows its red cross at its
        // centre; an open cell, (3, 6), the background there.
        var image = Png.ReadRgba8(result.Captures.Single().Path);
        var scale = image.Width / 320f;
        int At(float x, float y) => ((int)(y * scale) * image.Width + (int)(x * scale)) * 4;
        var solid = At(6.5f * 16, 2.5f * 16);
        var open = At(3.5f * 16, 6.5f * 16);
        Assert.True(image.Pixels[solid] > image.Pixels[solid + 2] + 40, "a solid cell's cross is red");
        Assert.True(Math.Abs(image.Pixels[open] - image.Pixels[open + 2]) < 30, "an open cell shows the background");
    }
}
