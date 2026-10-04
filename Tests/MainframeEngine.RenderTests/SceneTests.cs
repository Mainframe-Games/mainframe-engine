using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)] // one GPU, one window at a time

namespace MainframeEngine.RenderTests;

public class SceneTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void LitShapesRenderCleanlyAndMatchGoldens()
    {
        var result = HostRunner.Run("lit-shapes", Output("lit-shapes"), "--capture", "1,60", "--hidden");

        Assert.Equal(2, result.Captures.Count);
        Assert.All(result.Captures, c => Assert.Equal(c.Width * 3, c.Height * 4)); // 4:3 window, any DPI scale
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 1);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void SpineRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("spine", Output("spine"), "--capture", "60", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void MultipleShadowCastingLightsEachUseTheirOwnMatrix()
    {
        // Directional + spot + point (6 cube faces): 8 shadow sub-passes in one frame.
        var result = HostRunner.Run("multi-light", Output("multi-light"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);

        // 15 shadow maps, render targets, UBO rings, staging: a handful of 64 MiB blocks, not one
        // vkAllocateMemory per resource (drivers cap the count, often at 4096).
        Assert.InRange(result.GpuDeviceMemoryCount, 1, 16);
        Assert.True(result.GpuAllocationCount > 2 * result.GpuDeviceMemoryCount,
            $"{result.GpuAllocationCount} allocations in {result.GpuDeviceMemoryCount} device memories: expected sub-allocation.");
    }

    [Fact]
    public void SpineRendersWithoutAShadowSystem()
    {
        var result = HostRunner.Run("spine-no-shadows", Output("spine-no-shadows"), "--capture", "60", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 60);
    }

    [Fact]
    public void PhysicsSceneRendersCleanlyAndMatchesGoldens()
    {
        // Boxes in flight (frame 30) and settled on the floor and ramp (frame 150).
        var result = HostRunner.Run("physics", Output("physics"), "--capture", "30,150", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
        Gates.AssertMatchesGolden(result, 150);
    }

    [Fact]
    public void PhysicsDebugDrawRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("physics-debug", Output("physics-debug"), "--capture", "150", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 150);
    }

    [Fact]
    public void PhysicsCapturesAreDeterministicAcrossRuns()
    {
        var first = HostRunner.Run("physics", Output("physics-determinism-a"), "--capture", "90", "--hidden");
        var second = HostRunner.Run("physics", Output("physics-determinism-b"), "--capture", "90", "--hidden");

        var comparison = ImageComparison.Compare(Png.ReadRgba8(first.Captures[0].Path), Png.ReadRgba8(second.Captures[0].Path), channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs of the physics scene differ in {comparison.DifferingPixels} pixels (single-threaded fixed steps should be reproducible).");
    }

    [Fact]
    public void SwapchainRecreationOnResizeAndVSyncToggleIsClean()
    {
        var result = HostRunner.Run("lit-shapes", Output("recreate"),
            "--resize", "400x300@10", "--toggle-vsync", "20", "--capture", "5,40", "--hidden");

        var before = result.Captures.Single(c => c.Frame == 5);
        var after = result.Captures.Single(c => c.Frame == 40);
        Assert.Equal(before.Width * 400 / 320, after.Width); // same DPI scale, new size
        Assert.Equal(before.Height * 300 / 240, after.Height);
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void QuitWithErrorReturnsErrorExitCode()
    {
        var result = HostRunner.RunExpectingExit((int)ExitCode.Error, "lit-shapes", Output("exit-code"),
            "--quit-error", "3", "--frames", "100", "--hidden");

        Assert.Equal((int)ExitCode.Error, result.ExitCode);
        Assert.InRange(result.RenderedFrames, 2, 3); // closes after the render of the update that quit
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void SandboxSteadyStateAllocatesNothing()
    {
        const int warmup = 120, measured = 300;
        var result = HostRunner.Run("sandbox", Output("sandbox"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures)); // audio really plays
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Steady-state frames allocated {result.AllocatedBytes} managed bytes over {measured} frames " +
            $"(~{result.AllocatedBytes / (double)measured:0.#} B/frame); per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void PipelineCacheIsPersistedAndReloaded()
    {
        var cacheDir = Path.Combine(RenderTestEnvironment.ArtifactsDirectory, "pipeline-cache-roundtrip");
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        var cold = HostRunner.Run("lit-shapes", Output("pipeline-cache-cold"), "--frames", "3", "--hidden", "--pipeline-cache", cacheDir);
        var warm = HostRunner.Run("lit-shapes", Output("pipeline-cache-warm"), "--frames", "3", "--hidden", "--pipeline-cache", cacheDir);

        Assert.Equal(0, cold.PipelineCacheLoadedBytes);
        Assert.True(warm.PipelineCacheLoadedBytes > 0, "The second run did not load the pipeline cache written by the first.");
        Assert.Single(Directory.GetFiles(cacheDir, "pipelines-*.bin"));
        Gates.AssertValidationClean(warm);
    }

    [Fact]
    public void HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath()
    {
        var result = HostRunner.Run("color-pipeline", Output("color-pipeline"), "--capture", "4,12", "--hidden");
        Gates.AssertValidationClean(result);

        foreach (var (frame, exposure) in new[] { (4u, IVulkanContext.DefaultExposure), (12u, ColorPipelineScene.SecondExposure) })
        {
            var image = Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);
            var scale = image.Width / 320f; // HiDPI: ImGui works in points, the capture is in pixels

            // Scene: sRGB texture → linear (sampler) → × exposure → ACES → sRGB (swapchain view or shader).
            var c = ColorPipelineScene.SkyColor;
            var linear = ColorSpace.SrgbToLinear(new System.Numerics.Vector3(c[0], c[1], c[2]) / 255f);
            var expected = ColorSpace.LinearToSrgb(ColorSpace.AcesFitted(linear * exposure)) * 255f;
            AssertPixel(image, image.Width / 2, image.Height * 3 / 4, expected, 2, $"scene, exposure {exposure}");

            // Overlay: written as authored, blended in sRGB space like before the HDR pipeline.
            var o = ColorPipelineScene.OverlayColor;
            AssertPixel(image, (int)(35 * scale), (int)(35 * scale), new System.Numerics.Vector3(o.X, o.Y, o.Z) * 255f, 1, "opaque ImGui colour");
            AssertPixel(image, (int)(95 * scale), (int)(35 * scale), new System.Numerics.Vector3(127.5f), 2, "50% white over black (sRGB blend)");
        }
    }

    private static void AssertPixel(PngImage image, int x, int y, System.Numerics.Vector3 expected, float tolerance, string what)
    {
        var i = (y * image.Width + x) * 4;
        var actual = new System.Numerics.Vector3(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
        var delta = System.Numerics.Vector3.Abs(actual - expected);
        Assert.True(delta.X <= tolerance && delta.Y <= tolerance && delta.Z <= tolerance,
            $"{what}: pixel ({x},{y}) is {actual}, expected {expected} ±{tolerance}.");
    }

    [Fact]
    public void CapturesAreDeterministicAcrossRuns()
    {
        var first = HostRunner.Run("lit-shapes", Output("determinism-a"), "--capture", "30", "--hidden");
        var second = HostRunner.Run("lit-shapes", Output("determinism-b"), "--capture", "30", "--hidden");

        var a = Png.ReadRgba8(first.Captures[0].Path);
        var b = Png.ReadRgba8(second.Captures[0].Path);
        var comparison = ImageComparison.Compare(a, b, channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs of the same frame differ in {comparison.DifferingPixels} pixels (fixed timestep should make them identical).");
    }
}
