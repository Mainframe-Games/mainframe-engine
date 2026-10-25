using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// Spatial shaders (ADR 0205): a procedural world-space ground, swaying stalks with swaying shadows, a dissolve cutout
/// from a <c>.mres</c>, an additive rim as a next pass and an overlay — lit, shadowed, pickable, and under TAA (the depth
/// prepass and its velocity).
/// </summary>
public class SpatialShaderTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static readonly string Frames = $"{SpatialShaderScene.Frame},{SpatialShaderScene.LaterFrame}";

    [Fact]
    public void SpatialShadersDrawLitShadowedAndPickableAndMatchGolden()
    {
        var result = HostRunner.Run("spatial-shaders", Output("spatial-shaders"), "--capture", Frames, "--frames", "52", "--hidden");

        Gates.AssertValidationClean(result);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertMatchesGolden(result, SpatialShaderScene.Frame);
        Assert.True(result.MeshShadowDrawCalls > 0, "nothing was drawn into the shadow maps");

        // The stalks sway (TIME in vertex()): the left of the frame, where they and their shadows are, changes between the
        // two frames; the right (the static dissolve sphere, box and ground) does not.
        var a = Capture(result, SpatialShaderScene.Frame);
        var b = Capture(result, SpatialShaderScene.LaterFrame);
        var left = DifferingPixels(a, b, 0, a.Width * 2 / 5);
        var right = DifferingPixels(a, b, a.Width * 3 / 5, a.Width);
        TestContext.Current.SendDiagnosticMessage($"Pixels changed between frames: left {left}, right {right}");
        Assert.True(left > a.Width * a.Height / 400, $"the swaying stalks changed only {left} px");
        Assert.True(right < left / 10, $"the static right half changed {right} px (the stalks {left})");

        // The dissolve's emissive edge glows orange-red somewhere on the frame.
        Assert.True(EdgeGlowPixels(a) > 50, "no emissive dissolve edge");
    }

    [Fact]
    public void SpatialShadersUnderTaaAreValidationCleanPickableAndMatchGolden()
    {
        var result = HostRunner.Run("spatial-shaders-taa", Output("spatial-shaders-taa"), "--capture", Frames, "--frames", "52", "--hidden");

        Gates.AssertValidationClean(result);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertMatchesGolden(result, SpatialShaderScene.Frame);
    }

    [Fact]
    public void SpatialShadersSteadyStateAllocatesNothing()
    {
        const int warmup = 30, measured = 90; // after the scene's picks (frames 4–16)
        var result = HostRunner.Run("spatial-shaders-taa", Output("spatial-shaders-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Spatial shader frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    private static int DifferingPixels(PngImage a, PngImage b, int x0, int x1)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * a.Width + x) * 4;
                if (Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > 24)
                    count++;
            }

        return count;
    }

    private static int EdgeGlowPixels(PngImage image)
    {
        var count = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            if (p[i] > 230 && p[i + 1] > 90 && p[i + 1] < 220 && p[i + 2] < 120)
                count++;
        return count;
    }
}
