using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)] // one GPU, one window at a time

namespace MainframeEngine.RenderTests;

public class SceneTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    /// <summary>Layout points → capture pixels at the run's fixed content scale.</summary>
    private static int Px(HostResult result, int points) => (int)MathF.Round(points * result.ContentScale);

    [Fact]
    public void LitShapesRenderCleanlyAndMatchGoldens()
    {
        var result = HostRunner.Run("lit-shapes", Output("lit-shapes"), "--capture", "1,60", "--hidden");

        Assert.Equal(2, result.Captures.Count);
        // 320×240 layout points at the canonical scale (2 on macOS, 1 elsewhere), whatever the display's backing scale.
        Assert.Equal(HostOptions.CanonicalScale, result.ContentScale);
        Assert.All(result.Captures, c => Assert.Equal((Px(result, 320), Px(result, 240)), (c.Width, c.Height)));
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
    public void SpineUnderCamera2DIsUprightAndFrontFacing()
    {
        var result = HostRunner.Run("spine-2d", Output("spine-2d"), "--capture", "10", "--hidden");

        Assert.Empty(result.SceneCheckFailures);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void SpineAtASmallSpineScaleRendersTheSamePose()
    {
        // Same world size as "spine" with a 4x smaller SpineScale (0.005): the pose must not depend on it. SpineBoy is
        // small on screen, so the default 0.5% would accept the crumpled legs (~0.27%); identical poses differ by 0 pixels.
        var result = HostRunner.Run("spine-small-scale", Output("spine-small-scale"), "--capture", "60", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 60, maxDifferingPercent: 0.02, goldenFile: "spine_frame0060.png");
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
        Assert.Equal((Px(result, 320), Px(result, 240)), (before.Width, before.Height));
        Assert.Equal((Px(result, 400), Px(result, 300)), (after.Width, after.Height)); // same scale, new size
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void MinimiseAndRestoreIsCleanAndResumesRendering()
    {
        // The window is minimised on frame 10 and restored ~1.5 s later (the engine blocks on events meanwhile, a wake timer
        // pushes SDL events so the restore can run); the host checks nothing was rendered while minimised.
        var result = HostRunner.Run("lit-shapes", Output("minimize"),
            "--minimize", "10", "--capture", "5,40", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        var before = result.Captures.Single(c => c.Frame == 5);
        var after = result.Captures.Single(c => c.Frame == 40); // rendered after the restore
        Assert.Equal((Px(result, 320), Px(result, 240)), (before.Width, before.Height));
        Assert.Equal((Px(result, 320), Px(result, 240)), (after.Width, after.Height));
        Assert.True(File.Exists(after.Path));
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void ASyntheticRightDragLooksAroundThroughTheInputPath()
    {
        // SDL events pushed by the host (button down, motion, button up) reach a node's OnInput through Silk's IMouse and
        // the InputRouter; the mouse-look scene fails its own check unless the camera turned and then stopped looking.
        var result = HostRunner.Run("mouse-look", Output("mouse-look"),
            "--input", "5", "--frames", "40", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")] // no Mac or CI display's backing scale: the window is always resized to reach it
    public void FixedContentScaleCapturesAtTheExactPixelSize(string scale)
    {
        // EngineOptions.ContentScale: the swapchain is the layout size × the scale in pixels on any display (1× monitor,
        // 2× Retina) — at start-up and after Engine.ResizeWindow.
        var result = HostRunner.Run("lit-shapes", Output($"fixed-scale-{scale}"),
            "--size", "200x150", "--scale", scale, "--resize", "100x80@4", "--capture", "2,8", "--hidden");

        var s = int.Parse(scale, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(s, result.ContentScale);
        var first = result.Captures.Single(c => c.Frame == 2);
        var resized = result.Captures.Single(c => c.Frame == 8);
        Assert.Equal((200 * s, 150 * s), (first.Width, first.Height));
        Assert.Equal((100 * s, 80 * s), (resized.Width, resized.Height));
        var image = Png.ReadRgba8(first.Path);
        Assert.Equal((200 * s, 150 * s), (image.Width, image.Height));
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
    public void ShowcaseSteadyStateAllocatesNothing()
    {
        const int warmup = 120, measured = 300;
        // Without tiered compilation: the gate is about the code a steady-state frame runs, which is optimized code.
        // With tiering, some generic BCL code starts at tier 0 — notably the interpolated-string handlers' AppendFormatted<T>,
        // which boxes each formatted value (24 B) until the background JIT promotes it after ~30 calls — and when that
        // promotion lands is timing-dependent (the call-counting delay restarts on every tier-0 JIT), so ~1 run in 3
        // measured a few dozen frames of dev overlay formatting before it. Fully optimized code from the start measures
        // the steady state deterministically.
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "showcase", Output("showcase"), "--alloc", $"{warmup}:{measured}", "--hidden");

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
            var scale = image.Width / 320f; // HiDPI: the gizmo rects are drawn at host scale (points × scale), the capture is in pixels

            // Scene: sRGB texture → linear (sampler) → × exposure → ACES → sRGB (swapchain view or shader).
            var c = ColorPipelineScene.SkyColor;
            var linear = ColorSpace.SrgbToLinear(new System.Numerics.Vector3(c[0], c[1], c[2]) / 255f);
            var expected = ColorSpace.LinearToSrgb(ColorSpace.AcesFitted(linear * exposure)) * 255f;
            AssertPixel(image, image.Width / 2, image.Height * 3 / 4, expected, 2, $"scene, exposure {exposure}");

            // Overlay: written as authored, blended in sRGB space like before the HDR pipeline.
            var o = ColorPipelineScene.OverlayColor;
            AssertPixel(image, (int)(35 * scale), (int)(35 * scale), new System.Numerics.Vector3(o.X, o.Y, o.Z) * 255f, 1, "opaque gizmo colour");
            AssertPixel(image, (int)(95 * scale), (int)(35 * scale), new System.Numerics.Vector3(127.5f), 2, "50% white over black (sRGB blend)");
        }
    }

    [Fact]
    public void ProceduralSkyAndGridMatchTheReferenceMath()
    {
        var result = HostRunner.Run("sky-grid", Output("sky-grid"), "--capture", "3", "--hidden");
        Gates.AssertValidationClean(result);

        var capture = result.Captures.Single();
        var image = Png.ReadRgba8(capture.Path);
        int w = image.Width, h = image.Height;
        var camera = SkyGridScene.CreateCamera((float)w / h);
        var frame = FrameData.From(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position,
            new Silk.NET.Vulkan.Extent2D((uint)w, (uint)h), 0f, IVulkanContext.DefaultExposure);
        var lines = SkyGridReference.ProjectGridLines(frame.ViewProjection, SkyGridScene.GridSize, w, h, margin: 3f);
        var nearLine = SkyGridReference.LineMask(lines, w, h, radius: 2f);
        var sky = new System.Numerics.Vector3[w * h];
        var rays = new System.Numerics.Vector3[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                rays[y * w + x] = SkyGridReference.Ray(frame, x, y, w, h);
                sky[y * w + x] = SkyGridReference.SkyPixel(SkyGridScene.SkySettings, rays[y * w + x], IVulkanContext.DefaultExposure);
            }
        }

        System.Numerics.Vector3 Actual(int i) => new(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]);
        var groundY = -1f / SkyGridScene.SkySettings.HorizonSharpness; // below this the sky is the plain ground colour

        // 1. Every pixel away from a grid line is sky: above the horizon, the horizon-to-ground gradient, and the
        // ground colour between lines. A wrong ray, gradient, NaN, uniform layout or colour encoding changes these;
        // a stray line fragment (e.g. a line clipped badly by the rasterizer) shows where no line projects.
        const float tolerance = 3f;
        int checkedPixels = 0, above = 0, ground = 0, bad = 0;
        var failures = new System.Text.StringBuilder();
        var diff = (byte[])image.Pixels.Clone();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (nearLine[i]) continue;
                var ray = rays[i];
                var expected = sky[i];
                var actual = Actual(i);
                checkedPixels++;
                if (ray.Y > 0.05f) above++;
                if (ray.Y < groundY) ground++;

                var delta = System.Numerics.Vector3.Abs(actual - expected);
                if (delta.X <= tolerance && delta.Y <= tolerance && delta.Z <= tolerance) continue;
                diff[i * 4] = 255;
                diff[i * 4 + 1] = diff[i * 4 + 2] = 0;
                if (bad++ < 8)
                    failures.AppendLine(System.FormattableString.Invariant($"  ({x},{y}) ray.y {ray.Y:0.###}: {actual}, expected {expected}"));
            }
        }

        if (bad > 0)
            Png.WriteRgba8(Path.ChangeExtension(capture.Path, ".mismatch.png"), w, h, diff);
        Assert.True(checkedPixels > w * h / 3, $"Only {checkedPixels} of {w * h} pixels are clear of grid lines.");
        Assert.True(above > w * h / 10 && ground > w * h / 10, $"Sky pixels checked above the horizon: {above}, in the ground: {ground}.");
        Assert.True(bad == 0,
            $"{bad} of {checkedPixels} sky pixels away from any grid line differ from the reference by more than " +
            $"±{tolerance} (red in {Path.ChangeExtension(capture.Path, ".mismatch.png")}):\n{failures}");

        // 2. Every grid line over the plain ground is drawn: sample each projected line every 3 px; some pixel next to
        // the sample must differ visibly from the sky (white at α 0.1 over the ground, or an opaque axis colour).
        // Lines whose endpoints project far off-screen must not be dropped either.
        int sampledLines = 0, missingLines = 0;
        var missing = new System.Text.StringBuilder();
        foreach (var (start, end) in lines)
        {
            var steps = (int)(System.Numerics.Vector2.Distance(start, end) / 3f);
            int samples = 0, drawn = 0;
            for (var s = 0; s <= steps; s++)
            {
                var p = System.Numerics.Vector2.Lerp(start, end, steps == 0 ? 0f : s / (float)steps);
                int px = (int)p.X, py = (int)p.Y;
                if (px < 2 || py < 2 || px >= w - 2 || py >= h - 2 || rays[py * w + px].Y >= groundY) continue;
                samples++;
                var visible = false;
                for (var y = py - 1; y <= py + 1 && !visible; y++)
                {
                    for (var x = px - 1; x <= px + 1 && !visible; x++)
                    {
                        var d = System.Numerics.Vector3.Abs(Actual(y * w + x) - sky[y * w + x]);
                        visible = MathF.Max(d.X, MathF.Max(d.Y, d.Z)) >= 8f;
                    }
                }

                if (visible) drawn++;
            }

            if (samples < 5) continue;
            sampledLines++;
            if (drawn >= samples * 3 / 4) continue;
            if (missingLines++ < 8)
                missing.AppendLine(System.FormattableString.Invariant($"  ({start.X:0},{start.Y:0})→({end.X:0},{end.Y:0}): {drawn} of {samples} samples drawn"));
        }

        Assert.True(sampledLines >= 20, $"Only {sampledLines} grid lines cross the ground region.");
        Assert.True(missingLines == 0, $"{missingLines} of {sampledLines} grid lines over the ground are missing or broken:\n{missing}");
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
    public void OverlaysAndOutlinesDrawOverTheirSurfacesOnlyAndMatchGolden()
    {
        // A cyan overlay + 7 px white outline (Godot's hover highlight) on a sphere and a box, a red next-pass outline on
        // a capsule, a box behind them that the outlines must not bleed through (ADR 0132).
        var result = HostRunner.Run("outline", Output("outline"), "--capture", "20", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void MaterialFeaturesRenderCleanlyAndMatchGolden()
    {
        // Textured box, alpha-cutout quad, normal-mapped sphere, emissive unshaded capsule, mirrored box, blended glass.
        var result = HostRunner.Run("materials", Output("materials"), "--capture", "20", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void ImportedGltfModelRendersAndMatchesGolden()
    {
        var result = HostRunner.Run("gltf", Output("gltf"), "--capture", "20", "--hidden");

        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void ThousandInstancesBatchIntoAFewDrawsAndMatchGolden()
    {
        var result = HostRunner.Run("instances", Output("instances"), "--capture", "20", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(1001, result.MeshInstances);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
    }

    [Fact]
    public void TenThousandInstancesAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("instances", Output("instances-10k-alloc"), "--count", "10000", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"10k-instance frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TenThousandInstancesRenderAtSixtyFps()
    {
        // VSync and validation off; the 60 fps bar (16.7 ms) is enforced for Release builds (the shipping
        // configuration); Debug runs report the numbers only. The engine's CPU cost per frame (update, draw-list build,
        // command recording and submit, without waiting for the GPU or the swapchain) must fit the bar on every device.
        // The wall-clock frame time must too, except on a CPU device (lavapipe): there the "GPU" is a software
        // rasterizer sharing the CPU, ~50 ms per frame for this scene, which measures the runner, not the engine.
        var result = HostRunner.Run("instances", Output("instances-10k-perf"), "--count", "10000", "--perf", "60:300", "--no-validation", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(300, result.PerfMeasuredFrames);
        TestContext.Current.SendDiagnosticMessage(
            $"10k instances ({result.Configuration}, {result.DeviceName}, {result.DeviceType}): wall clock " +
            $"{result.AverageFrameMs:0.00} ms average, {result.P95FrameMs:0.00} ms p95; CPU {result.AverageCpuFrameMs:0.00} ms " +
            $"average, {result.P95CpuFrameMs:0.00} ms p95; {result.MeshDrawCalls} draws");
        Assert.True(result.AverageCpuFrameMs > 0, "The CPU frame time was not measured.");
        Assert.True(result.AverageCpuFrameMs <= result.AverageFrameMs, "CPU time per frame exceeds the wall-clock frame time.");
        if (result.Configuration != "Release")
            return;

        Assert.True(result.AverageCpuFrameMs < 1000.0 / 60,
            $"10k instances cost {result.AverageCpuFrameMs:0.00} ms of CPU per frame on {result.DeviceName} (< 16.67 ms required).");
        if (!result.IsCpuDevice)
            Assert.True(result.AverageFrameMs < 1000.0 / 60, $"10k instances average {result.AverageFrameMs:0.00} ms per frame (< 16.67 ms required).");
    }

    [Fact]
    public void ObjectIdPickingAndSubViewportsWork()
    {
        var result = HostRunner.Run("picking", Output("picking"), "--capture", "20", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 20);
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
