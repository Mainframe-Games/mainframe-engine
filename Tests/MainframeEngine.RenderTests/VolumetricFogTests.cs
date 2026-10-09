using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0171: volumetric fog. A beam of sunlight through a hole in a roof stands in the shadowed fog (self-checked and a
/// golden); still frames are stable once the history has built up; a swinging camera allocates nothing per frame.
/// </summary>
public class VolumetricFogTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void ABeamOfSunlightStandsInTheShadowedFog()
    {
        var on = HostRunner.Run("volumetric-fog", Output("volumetric-fog"), "--capture", "30", "--hidden");
        var off = HostRunner.Run("volumetric-fog", Output("volumetric-fog-off"), "--capture", "30", "--count", "1", "--hidden");

        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        Gates.AssertMatchesGolden(on, 30);

        // A row through the middle of the beam: rays that stay under the roof over the fog's whole length. The beam (the
        // row's brightest pixels) must stand well above the fog to either side, which only the sky's ambient light reaches.
        var fog = RowProfile(Capture(on, 30), 0.5f);
        var none = RowProfile(Capture(off, 30), 0.5f);
        TestContext.Current.SendDiagnosticMessage(
            $"Volumetric fog: beam {fog.Peak:F1} at {fog.PeakAt:F2} of the width, shadowed fog {fog.Edges:F1}; without: {none.Peak:F1} / {none.Edges:F1}");
        Assert.InRange(fog.PeakAt, 0.35f, 0.7f); // the beam leans in from the hole, near the middle
        Assert.True(fog.Peak > 3f * fog.Edges && fog.Peak - fog.Edges > 15f,
            $"the beam ({fog.Peak:F1}) does not stand out from the shadowed fog ({fog.Edges:F1})");
        Assert.True(fog.Edges > none.Edges, "the shadowed fog should still scatter some sky light");
        Assert.True(none.Peak - none.Edges < 4f, $"without volumetric fog the air should be dark everywhere ({none.Peak:F1} vs {none.Edges:F1})");
    }

    [Fact]
    public void StillFramesAreStable()
    {
        // The march's noise moves every frame; the temporal reprojection averages it, so a still view barely changes.
        var result = HostRunner.Run("volumetric-fog", Output("volumetric-fog-stable"), "--capture", "30,60,61,62", "--hidden");
        Gates.AssertValidationClean(result);
        var frames = new[] { Capture(result, 60), Capture(result, 61), Capture(result, 62) };
        for (var i = 1; i < frames.Length; i++)
        {
            var (mean, max) = Difference(frames[i - 1], frames[i]);
            TestContext.Current.SendDiagnosticMessage($"Volumetric fog frames {59 + i}→{60 + i}: mean |Δ| {mean:F3}, max {max}");
            Assert.True(mean < 0.25, $"consecutive still frames differ by {mean:F3} on average");
            Assert.True(max <= 16, $"a pixel of consecutive still frames changed by {max}");
        }

        // Converged: frame 60 is close to frame 30.
        var (drift, _) = Difference(Capture(result, 30), frames[0]);
        Assert.True(drift < 0.5, $"the fog kept changing between frames 30 and 60 ({drift:F3})");
    }

    [Fact]
    public void ASwingingCameraAllocatesNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("volumetric-fog", Output("volumetric-fog-alloc"), "--count", "2", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Volumetric fog with a swinging camera allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    // The mean luminance of five rows around `row` (a fraction of the height), its peak and where it is (fraction of the
    // width), and the mean of the outer eighths.
    private static (float Peak, float PeakAt, float Edges) RowProfile(PngImage image, float row)
    {
        var y0 = (int)(image.Height * row);
        var profile = new float[image.Width];
        for (var y = y0 - 2; y <= y0 + 2; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                profile[x] += (0.2126f * image.Pixels[i] + 0.7152f * image.Pixels[i + 1] + 0.0722f * image.Pixels[i + 2]) / 5f;
            }
        }

        var peak = 0f;
        var at = 0;
        for (var x = 0; x < profile.Length; x++)
        {
            if (profile[x] > peak)
            {
                peak = profile[x];
                at = x;
            }
        }

        var eighth = image.Width / 8;
        var edges = 0f;
        for (var x = 0; x < eighth; x++)
            edges += profile[x] + profile[image.Width - 1 - x];
        return (peak, (float)at / image.Width, edges / (2 * eighth));
    }

    private static (double Mean, int Max) Difference(PngImage a, PngImage b)
    {
        long sum = 0;
        var max = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            if ((i & 3) == 3)
                continue;
            var d = Math.Abs(a.Pixels[i] - b.Pixels[i]);
            sum += d;
            max = Math.Max(max, d);
        }

        return ((double)sum / (a.Pixels.Length / 4 * 3), max);
    }
}
