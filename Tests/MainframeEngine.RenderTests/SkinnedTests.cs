using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0207: compute pre-skinned characters render posed, cast cascaded and point shadows, keep their outline overlay on
/// the pose and are picked where the pose is; a crowd of them stays allocation-free (with the prepass, TAA and skinned
/// motion vectors) and inside a 120 fps frame.
/// </summary>
public class SkinnedTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void ASkinnedPosedShadowedCharacterMatchesGolden()
    {
        var result = HostRunner.Run("skinned", Output("skinned"), "--capture", "20", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);

        // The raised arm is drawn (and outlined) where the rest pose has nothing: the upper left of the frame, above the
        // heads, holds the left character's white outline and dark suit, not just the wall.
        var image = Capture(result, 20);
        int outline = 0, suit = 0;
        for (var y = image.Height / 6; y < image.Height / 4; y++)
            for (var x = image.Width / 6; x < image.Width / 3; x++)
            {
                var i = (y * image.Width + x) * 4;
                var (r, g, b) = (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
                if (r > 200 && Math.Abs(r - g) < 8 && Math.Abs(r - b) < 8) // the white outline after the tonemap
                    outline++;
                else if (b > r + 25 && r < 90)
                    suit++;
            }

        Assert.True(outline > 0 && suit > 0, $"the raised arm is missing above the head (outline {outline} px, suit {suit} px)");
    }

    [Fact]
    public void ASkinnedCrowdWithThePrepassAndTaaAllocatesNothingPerFrame()
    {
        const int warmup = 90, measured = 120;
        var result = HostRunner.Run("skinned-crowd", Output("skinned-crowd-alloc"), "--alloc", $"{warmup}:{measured}", "--prepass", "--aa", "taa",
            "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Skinned crowd frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TwelveSkinnedCharactersFitA120FpsFrame()
    {
        // 12 characters of about 6 000 triangles, animated and crossfading, with sun shadows. Reports the frame; on a GPU
        // device in Release the frame must stay within 120 fps. A CPU device (lavapipe) only reports.
        var result = HostRunner.Run("skinned-crowd", Output("skinned-crowd-perf"), "--perf", "60:240", "--perf-on-cpu", "20:60",
            "--no-validation", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        TestContext.Current.SendDiagnosticMessage(
            $"skinned-crowd 12 ({result.Configuration}, {result.DeviceName}): frame {result.AverageFrameMs:0.00} ms " +
            $"(p95 {result.P95FrameMs:0.00}), CPU {result.AverageCpuFrameMs:0.00} ms, shadow GPU {result.ShadowGpuMs:0.00} ms; " +
            $"{result.MeshDrawCalls} draws, {result.MeshShadowDrawCalls} shadow draws");
        if (result.Configuration == "Release" && !result.IsCpuDevice)
            Assert.True(result.AverageFrameMs < 1000.0 / 120 + 0.5, $"12 skinned characters take {result.AverageFrameMs:0.00} ms per frame.");
    }
}
