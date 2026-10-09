using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>ADR 0160: screen-space light shafts. Every run is validation-clean; the shafts frame matches its golden.</summary>
public class LightShaftsTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void ShaftsStreamThroughTheGapsBetweenPosts()
    {
        // Different frames, so the shafts-on golden has its own name.
        var on = HostRunner.Run("light-shafts", Output("light-shafts"), "--capture", "6", "--hidden");
        var off = HostRunner.Run("light-shafts", Output("light-shafts-off"), "--capture", "5", "--count", "1", "--hidden");

        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        AssertSelfChecks(on); // the sun is at the centre of the screen, fully faded in
        Gates.AssertMatchesGolden(on, 6);

        var shafts = Capture(on, 6);
        var none = Capture(off, 5);
        var brighter = MeanLuminance(shafts) - MeanLuminance(none);

        // Below the sun (the image centre) a circle crosses the shafts: where a ray reaches the sun through a gap the
        // shafts add a lot, where a post blocks it little. A uniform glow would add about the same everywhere.
        var radius = 0.4f * shafts.Height;
        float least = float.MaxValue, most = float.MinValue;
        for (var degrees = 200; degrees <= 340; degrees += 2)
        {
            var angle = float.DegreesToRadians(degrees);
            var x = (int)(shafts.Width / 2f + radius * MathF.Cos(angle));
            var y = (int)(shafts.Height / 2f - radius * MathF.Sin(angle));
            var added = Luminance(PixelAt(shafts, x, y)) - Luminance(PixelAt(none, x, y));
            least = MathF.Min(least, added);
            most = MathF.Max(most, added);
        }

        TestContext.Current.SendDiagnosticMessage($"Light shafts: mean luminance +{brighter:F1}; added below the sun {least:F1} … {most:F1}");
        Assert.True(brighter > 10f, $"the shafts brightened the frame by only {brighter:F1}");
        Assert.True(most - least > 40f, $"the shafts below the sun are not streaked: they add {least:F1} … {most:F1}");
    }

    [Fact]
    public void ShaftsWithAMovingSunAllocateNothingPerFrame()
    {
        // The sun swings through the screen and out past its edges (the fade and the skipped passes) and back.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("light-shafts", Output("light-shafts-alloc"), "--count", "2", "--alloc", $"{warmup}:{measured}", "--hidden");

        AssertSelfChecks(result);
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Light shafts with a moving sun allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    private static (int R, int G, int B) PixelAt(PngImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    private static float Luminance((int R, int G, int B) p) => 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;

    private static float MeanLuminance(PngImage image)
    {
        double sum = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
            sum += 0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2];
        return (float)(sum / (image.Width * image.Height));
    }
}
