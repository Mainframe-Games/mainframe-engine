using System.Globalization;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0166: temporal anti-aliasing. Edges converge towards a supersampled reference, a moving object leaves no trail,
/// foliage in the wind resolves without smearing, history resets on cuts and resizes, and the resolve allocates nothing.
/// Every run is validation-clean.
/// </summary>
public class TaaTests
{
    // Small windows keep the 4× supersampled reference cheap: 160 × 120 points (× the canonical scale in pixels).
    private const string Size = "160x120";
    private const int Supersample = 4;

    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static string Invariant(float value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void TaaConvergesTowardsTheSupersampledEdges()
    {
        const uint frame = 40;
        var none = HostRunner.Run("taa-edges", Output("taa-edges-none"), "--size", Size, "--capture", "2", "--aa", "none", "--hidden");
        var taa = HostRunner.Run("taa-edges", Output("taa-edges"), "--size", Size, "--capture", frame.ToString(CultureInfo.InvariantCulture),
            "--aa", "taa", "--hidden");
        var reference = HostRunner.Run("taa-edges", Output("taa-edges-reference"), "--size", Size, "--capture", "2", "--aa", "none",
            "--scale", Invariant(HostOptions.CanonicalScale * Supersample), "--hidden");
        Gates.AssertValidationClean(none);
        Gates.AssertValidationClean(taa);
        Gates.AssertValidationClean(reference);

        var truth = LinearDownsample(Capture(reference, 2), Supersample);
        var aliased = MeanError(Capture(none, 2), truth);
        var resolved = MeanError(Capture(taa, frame), truth);
        TestContext.Current.SendDiagnosticMessage($"Mean error against the {Supersample}× supersampled frame: no AA {aliased:F3}, TAA {resolved:F3}");
        Assert.True(resolved < 0.6 * aliased, $"TAA's edges ({resolved:F3}) are not much closer to the supersampled ones than no AA's ({aliased:F3}).");
        Gates.AssertMatchesGolden(taa, frame);
    }

    [Fact]
    public void TaaReplacesFxaaAndLeavesTheOverlaySharp()
    {
        // The FXAA scene (an oblique white box, a red screen-gizmo square in the overlay pass) with TAA instead: the box's
        // edges are blended, the gizmo — drawn after TAA and its sharpen, like the canvas and UI — is exact.
        const uint frame = 30;
        var at = frame.ToString(CultureInfo.InvariantCulture);
        var taa = HostRunner.Run("fxaa", Output("fxaa-taa"), "--capture", at, "--aa", "taa", "--hidden");
        var none = HostRunner.Run("fxaa", Output("fxaa-taa-off"), "--capture", at, "--aa", "none", "--hidden");
        Gates.AssertValidationClean(taa);
        Gates.AssertValidationClean(none);
        var smooth = Capture(taa, frame);
        var aliased = Capture(none, frame);

        int blended = 0, blendedWithout = 0;
        for (var i = 0; i < smooth.Pixels.Length; i += 4)
        {
            blended += smooth.Pixels[i + 1] is > 40 and < 180 ? 1 : 0;
            blendedWithout += aliased.Pixels[i + 1] is > 40 and < 180 ? 1 : 0;
        }

        TestContext.Current.SendDiagnosticMessage($"Blended edge pixels: TAA {blended}, none {blendedWithout}");
        Assert.True(blended > 200 && blended > 10 * (blendedWithout + 1), $"TAA blended {blended} edge pixels (none: {blendedWithout})");

        var scale = smooth.Width / 320;
        var (min, max) = FxaaScene.GizmoRect;
        int x0 = (int)min.X * scale, y0 = (int)min.Y * scale, x1 = (int)max.X * scale - 1, y1 = (int)max.Y * scale - 1;
        foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1), ((x0 + x1) / 2, (y0 + y1) / 2) })
            Assert.Equal((255, 0, 0), PixelAt(smooth, x, y));
        foreach (var (x, y) in new[] { (x0 - 1, (y0 + y1) / 2), (x1 + 1, (y0 + y1) / 2), ((x0 + x1) / 2, y0 - 1), ((x0 + x1) / 2, y1 + 1) })
            Assert.Equal(PixelAt(aliased, x, y), PixelAt(smooth, x, y));
    }

    private static (int R, int G, int B) PixelAt(PngImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    [Fact]
    public void AMovingObjectLeavesNoTrail()
    {
        // Self-checked in the scene at each capture: the box's trail is the wall, its centre still red.
        var result = HostRunner.Run("taa-ghost", Output("taa-ghost"), "--capture", "30,60", "--aa", "taa", "--hidden");
        Gates.AssertValidationClean(result);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void HistoryResetsOnCutsCameraSwitchesAndResizes()
    {
        var result = HostRunner.Run("taa-ghost", Output("taa-ghost-cuts"), "--count", "1", "--capture", "60", "--aa", "taa",
            "--resize", "300x220@40", "--hidden");
        Gates.AssertValidationClean(result);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
    }

    [Fact]
    public void FoliageInTheWindResolvesWithoutSmearing()
    {
        // Swaying, dithered leaves (and the distant checker floor) at the same moment, with TAA, without anti-aliasing and
        // supersampled 4× without it: if TAA dragged the leaves behind their motion (ghosting, smearing), its frame would
        // be further from the supersampled one than the aliased frame is, not closer. The TAA frame has a golden too.
        const uint frame = 90;
        var at = frame.ToString(CultureInfo.InvariantCulture);
        var taa = HostRunner.Run("taa-foliage", Output("taa-foliage"), "--size", Size, "--capture", at, "--hidden");
        var none = HostRunner.Run("taa-foliage", Output("taa-foliage-none"), "--size", Size, "--capture", at, "--aa", "none", "--hidden");
        var reference = HostRunner.Run("taa-foliage", Output("taa-foliage-reference"), "--size", Size, "--capture", at, "--aa", "none",
            "--scale", Invariant(HostOptions.CanonicalScale * Supersample), "--hidden");
        Gates.AssertValidationClean(taa);
        Gates.AssertValidationClean(none);
        Gates.AssertValidationClean(reference);

        var truth = LinearDownsample(Capture(reference, frame), Supersample);
        var aliased = MeanError(Capture(none, frame), truth);
        var resolved = MeanError(Capture(taa, frame), truth);
        TestContext.Current.SendDiagnosticMessage($"Foliage in the wind against the {Supersample}× supersampled frame: no AA {aliased:F3}, TAA {resolved:F3}");
        Assert.True(resolved < 0.9 * aliased, $"TAA's swaying leaves ({resolved:F3}) are not closer to the supersampled ones than no AA's ({aliased:F3}).");
        Gates.AssertMatchesGolden(taa, frame);
    }

    [Fact]
    public void TaaAllocatesNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("taa-ghost", Output("taa-alloc"), "--aa", "taa", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"TAA allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    // Each output pixel the mean of its factor × factor block in linear light (the captures are sRGB-encoded): what a
    // supersampled renderer resolves to, and what TAA's linear HDR history converges to.
    private static PngImage LinearDownsample(PngImage image, int factor)
    {
        int width = image.Width / factor, height = image.Height / factor;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0f;
                    for (var j = 0; j < factor; j++)
                        for (var i = 0; i < factor; i++)
                            sum += SrgbToLinear(image.Pixels[((y * factor + j) * image.Width + x * factor + i) * 4 + c] / 255f);
                    pixels[(y * width + x) * 4 + c] = (byte)Math.Round(LinearToSrgb(sum / (factor * factor)) * 255f);
                }

                pixels[(y * width + x) * 4 + 3] = 255;
            }
        }

        return new PngImage(width, height, pixels);
    }

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    // Mean absolute difference per colour channel (0–255).
    private static double MeanError(PngImage a, PngImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
            for (var c = 0; c < 3; c++)
                sum += Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]);
        return sum / (3.0 * a.Width * a.Height);
    }

}
