using System.Globalization;
using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0174: render-resolution scaling. At a render scale of 0.75, TAAU's edges and swaying leaves converge close to native
/// TAA's (both against a 4× supersampled frame), FSR 1's EASU beats a bilinear upscale, the upscaled paths survive resizes
/// and scale changes validation-clean, and they allocate nothing per frame. Every run is validation-clean; the scenes check
/// their own render size and which upscale ran.
/// </summary>
public class TaauTests
{
    private const string Size = "160x120";
    private const int Supersample = 4;

    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static string At(uint frame) => frame.ToString(CultureInfo.InvariantCulture);

    private static string Reference => (HostOptions.CanonicalScale * Supersample).ToString(CultureInfo.InvariantCulture);

    private static void AssertClean(HostResult result)
    {
        Gates.AssertValidationClean(result);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
    }

    [Fact]
    public void TaauConvergesTowardsTheSupersampledEdges()
    {
        // Thin bars and sub-pixel spokes at 0.75: after 40 frames TAAU recovers a good part of native TAA's gain over the
        // aliased native frame and stays within 4 dB of native TAA, against the supersampled frame. Measured on MoltenVK
        // (240 × 180 → 320 × 240): no AA 22.6 dB, native TAA 33.1 dB, TAAU 29.9 dB (70 % of the gain; after 16 frames
        // 31.5 and 28.4); on lavapipe (120 × 90 → 160 × 120): 19.6, 25.7 and 22.3 dB (44 %). A spoke narrower than a
        // render pixel is the worst case.
        const uint frame = 40;
        var taau = HostRunner.Run("taau", Output("taau"), "--size", Size, "--capture", At(frame), "--hidden");
        var taa = HostRunner.Run("taa-edges", Output("taau-native"), "--size", Size, "--capture", At(frame), "--aa", "taa", "--hidden");
        var none = HostRunner.Run("taa-edges", Output("taau-none"), "--size", Size, "--capture", "2", "--aa", "none", "--hidden");
        var reference = HostRunner.Run("taa-edges", Output("taau-reference"), "--size", Size, "--capture", "2", "--aa", "none",
            "--scale", Reference, "--hidden");
        AssertClean(taau);
        AssertClean(taa);
        AssertClean(none);
        AssertClean(reference);

        var truth = SupersampledImage.LinearDownsample(Capture(reference, 2), Supersample);
        var aliased = SupersampledImage.Psnr(Capture(none, 2), truth);
        var native = SupersampledImage.Psnr(Capture(taa, frame), truth);
        var upscaled = SupersampledImage.Psnr(Capture(taau, frame), truth);
        TestContext.Current.SendDiagnosticMessage($"PSNR against the {Supersample}× supersampled frame: no AA {aliased:F2} dB, TAA {native:F2} dB, TAAU 0.75 {upscaled:F2} dB");
        Assert.True(upscaled - aliased > 0.35 * (native - aliased),
            $"TAAU ({upscaled:F2} dB) recovers too little of native TAA's ({native:F2} dB) gain over no AA ({aliased:F2} dB).");
        Assert.True(upscaled > native - 4.0, $"TAAU ({upscaled:F2} dB) is more than 4 dB below native TAA ({native:F2} dB).");
        Gates.AssertMatchesGolden(taau, frame);
    }

    [Fact]
    public void TaauResolvesFoliageInTheWindCloseToNativeTaa()
    {
        // Swaying dithered leaves at 0.75: within 2 dB of native TAA against the supersampled frame (measured: MoltenVK
        // native TAA 28.8 dB, TAAU 27.1 dB, no AA 23.9 dB; lavapipe 25.6 and 24.1 dB).
        const uint frame = 90;
        var taau = HostRunner.Run("taau-foliage", Output("taau-foliage"), "--size", Size, "--capture", At(frame), "--hidden");
        var taa = HostRunner.Run("taa-foliage", Output("taau-foliage-native"), "--size", Size, "--capture", At(frame), "--hidden");
        var reference = HostRunner.Run("taa-foliage", Output("taau-foliage-reference"), "--size", Size, "--capture", At(frame), "--aa", "none",
            "--scale", Reference, "--hidden");
        AssertClean(taau);
        AssertClean(taa);
        AssertClean(reference);

        var truth = SupersampledImage.LinearDownsample(Capture(reference, frame), Supersample);
        var native = SupersampledImage.Psnr(Capture(taa, frame), truth);
        var upscaled = SupersampledImage.Psnr(Capture(taau, frame), truth);
        TestContext.Current.SendDiagnosticMessage($"Foliage PSNR: TAA {native:F2} dB, TAAU 0.75 {upscaled:F2} dB");
        Assert.True(upscaled > native - 2.0, $"TAAU's leaves ({upscaled:F2} dB) are more than 2 dB below native TAA's ({native:F2} dB).");
    }

    [Fact]
    public void FsrUpscalesSharperThanBilinear()
    {
        // FSR 1 at 0.75 with TAA off: EASU's edges are closer to the supersampled frame than a bilinear upscale's (measured
        // 23.2 vs 22.5 dB on MoltenVK, 19.2 vs 18.8 dB on lavapipe), then RCAS sharpens under the UI. The scene checks EASU ran and TAA did not.
        const uint frame = 5;
        var fsr = HostRunner.Run("fsr1", Output("fsr1"), "--size", Size, "--capture", At(frame), "--hidden");
        var bilinear = HostRunner.Run("fsr1", Output("fsr1-bilinear"), "--size", Size, "--capture", At(frame), "--scaling", "bilinear", "--hidden");
        var reference = HostRunner.Run("taa-edges", Output("fsr1-reference"), "--size", Size, "--capture", "2", "--aa", "none",
            "--scale", Reference, "--hidden");
        AssertClean(fsr);
        Gates.AssertValidationClean(bilinear);
        AssertClean(reference);

        var truth = SupersampledImage.LinearDownsample(Capture(reference, 2), Supersample);
        var easu = SupersampledImage.Psnr(Capture(fsr, frame), truth);
        var plain = SupersampledImage.Psnr(Capture(bilinear, frame), truth);
        TestContext.Current.SendDiagnosticMessage($"PSNR at 0.75: FSR 1 {easu:F2} dB, bilinear {plain:F2} dB");
        Assert.True(easu > plain, $"FSR 1 ({easu:F2} dB) is not closer to the supersampled frame than bilinear ({plain:F2} dB).");
        Gates.AssertMatchesGolden(fsr, frame);
    }

    [Fact]
    public void TheUpscaledPathsSurviveResizesAndScaleChanges()
    {
        // TAAU from 0.75 to 0.5 at frame 20, a window resize at 40; FSR from 0.75 back to native at 20 and a resize at 40;
        // native TAA to 0.75 TAAU at 20. Validation-clean, every capture at the window's size, the ghost's trail still the wall.
        var taau = HostRunner.Run("taa-ghost", Output("taau-changes"), "--aa", "taa", "--scaling", "taau", "--render-scale", "0.75",
            "--render-scale-at", "0.5@20", "--resize", "300x220@40", "--capture", "30,60", "--hidden");
        var fsr = HostRunner.Run("taa-ghost", Output("fsr1-changes"), "--aa", "none", "--scaling", "fsr", "--render-scale", "0.75",
            "--render-scale-at", "1@20", "--resize", "300x220@40", "--capture", "60", "--hidden");
        var native = HostRunner.Run("taa-ghost", Output("taau-from-native"), "--aa", "none", "--scaling", "taau",
            "--render-scale-at", "0.75@20", "--capture", "60", "--hidden");
        AssertClean(taau);
        AssertClean(native);
        Gates.AssertValidationClean(fsr);
    }

    [Fact]
    public void PickingHitsTheRightNodesWhileUpscaled()
    {
        // The main view's object-ID pass runs at the render size; picks in window pixels are mapped to it.
        var result = HostRunner.Run("picking", Output("picking-upscaled"), "--render-scale", "0.75", "--scaling", "taau", "--aa", "taa",
            "--capture", "20", "--hidden");
        AssertClean(result);
    }

    [Fact]
    public void TheUpscaledPathsAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        foreach (var (name, mode, aa) in new[] { ("taau-alloc", "taau", "taa"), ("fsr1-alloc", "fsr", "none") })
        {
            var result = HostRunner.Run("taa-ghost", Output(name), "--aa", aa, "--scaling", mode, "--render-scale", "0.75",
                "--alloc", $"{warmup}:{measured}", "--hidden");
            Assert.Equal(measured, result.MeasuredFrames);
            Assert.True(result.AllocatedBytes == 0, $"{mode} allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
            Gates.AssertValidationClean(result);
        }
    }
}

/// <summary>Compares captures with a supersampled reference (the TAA and TAAU tests).</summary>
internal static class SupersampledImage
{
    // Each output pixel the mean of its factor × factor block in linear light (the captures are sRGB-encoded).
    public static PngImage LinearDownsample(PngImage image, int factor)
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

    /// <summary>Peak signal-to-noise ratio of the colour channels in dB (8-bit).</summary>
    public static double Psnr(PngImage a, PngImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                double d = a.Pixels[i + c] - b.Pixels[i + c];
                sum += d * d;
            }
        }

        var mse = Math.Max(sum / (3.0 * a.Width * a.Height), 1e-9);
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
}
