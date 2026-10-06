using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>ADR 0124: Godot 4.7's glow and tonemap. Each run is validation-clean; the glow is measured and matches a golden.</summary>
public class GlowTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void GlowBleedsAroundBrightAreasOnlyAboveTheThreshold()
    {
        var on = HostRunner.Run("glow", Output("glow"), "--capture", "6", "--hidden");
        var off = HostRunner.Run("glow", Output("glow-off"), "--capture", "6", "--count", "1", "--hidden");
        var dim = HostRunner.Run("glow", Output("glow-dim"), "--capture", "6", "--count", "2", "--hidden");

        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        Gates.AssertValidationClean(dim);
        Gates.AssertMatchesGolden(on, 6);

        var glow = Capture(on, 6);
        var plain = Capture(off, 6);
        int w = glow.Width, h = glow.Height;

        // Just outside the emitter (centred, about 13% of the height across): brighter with glow.
        var near = (w / 2 + h / 10, h / 2);
        var nearGain = Luminance(glow, near) - Luminance(plain, near);
        // A corner, far from the emitter: unchanged.
        var far = (w / 20, h / 20);
        var farDiff = MathF.Abs(Luminance(glow, far) - Luminance(plain, far));
        TestContext.Current.SendDiagnosticMessage($"Glow gain next to the emitter {nearGain:F1}, far away {farDiff:F1}");
        Assert.True(nearGain > 8f, $"no glow next to the emitter (gain {nearGain})");
        Assert.True(farDiff <= 2f, $"glow reached the far corner ({farDiff})");

        // Below the threshold glow adds nothing: the dim square looks the same with glow on as the bright scene's
        // background does without it, everywhere but the square itself.
        var dimImage = Capture(dim, 6);
        Assert.True(MathF.Abs(Luminance(dimImage, near) - Luminance(plain, near)) <= 2f, "glow below the threshold changed the image");
    }

    private static float Luminance(PngImage image, (int X, int Y) p)
    {
        var i = (p.Y * image.Width + p.X) * 4;
        return 0.2126f * image.Pixels[i] + 0.7152f * image.Pixels[i + 1] + 0.0722f * image.Pixels[i + 2];
    }
}
