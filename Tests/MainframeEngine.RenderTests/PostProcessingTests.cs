using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0163: the depth prepass, motion vectors, the projection jitter and the post-processing stages. Every run is
/// validation-clean.
/// </summary>
public class PostProcessingTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    /// <summary>
    /// With the prepass forced on, a scene matches its golden (recorded without it) within the usual tolerance (±4 on at
    /// most 0.5 % of the pixels). Not bit-exact: where two surfaces meet at equal depth the later one wins (the scene pass
    /// tests LESS_OR_EQUAL), a cutout's edge pixels follow the prepass's alpha test, and the prepassed pipelines' shading
    /// differs by a code value here and there (measured on MoltenVK: at most 0.12 % of pixels beyond ±4, tree-forest).
    /// </summary>
    [Theory]
    [InlineData("materials", 20u)]
    [InlineData("foliage-wind", 90u)]
    [InlineData("tree-realistic", 90u)]
    [InlineData("tree-forest", 30u)]
    [InlineData("terrain-splat", 10u)]
    [InlineData("terrain-foliage", 30u)]
    [InlineData("pbr", 10u)]
    [InlineData("multimesh", 20u)]
    [InlineData("gltf", 20u)]
    [InlineData("water", 30u)]
    public void ThePrepassLeavesTheImageAsItWas(string scene, uint frame)
    {
        var result = HostRunner.Run(scene, Output($"{scene}-prepass"), "--capture", frame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--prepass", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, frame, goldenFile: $"{scene}_frame{frame:D4}.png");
    }

    [Fact]
    public void ARotatingCameraAndAMovingBodyGetTheirMotionVectors()
    {
        // Self-checked against the camera's matrices: every static pixel (the floor, the posts, the sky) moves with the
        // camera's rotation, the body's pixels by that plus their own motion.
        var result = HostRunner.Run("velocity", Output("velocity"), "--capture", "30", "--velocity-view", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
    }

    [Fact]
    public void TheProjectionJitterLeavesStillPixelsStill()
    {
        // A still camera with the Halton jitter on: the motion vectors are unjittered, so every pixel is exactly still.
        var result = HostRunner.Run("velocity", Output("velocity-jitter"), "--capture", "30", "--count", "1", "--velocity-view", "--jitter", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
        var image = Capture(result, 30);
        for (var i = 0; i < image.Pixels.Length; i += 4)
            Assert.True(image.Pixels[i] == 128 && image.Pixels[i + 1] == 128, $"pixel {i / 4} moves: ({image.Pixels[i]}, {image.Pixels[i + 1]})");
    }

    [Fact]
    public void FoliageInTheWindHasMotionVectors()
    {
        var windy = HostRunner.Run("foliage-wind", Output("foliage-wind-velocity"), "--capture", "90", "--velocity-view", "--hidden");
        var still = HostRunner.Run("foliage-wind", Output("foliage-still-velocity"), "--capture", "90", "--count", "1", "--velocity-view", "--hidden");
        Gates.AssertValidationClean(windy);
        Gates.AssertValidationClean(still);

        // The camera is still: only the swaying leaves move (by more than a step), and without wind nothing does.
        var moving = CountMoving(Capture(windy, 90), steps: 2);
        var calm = CountMoving(Capture(still, 90), steps: 1);
        TestContext.Current.SendDiagnosticMessage($"Foliage velocity: {moving} moving pixels in the wind, {calm} without");
        Assert.True(moving > 500, $"only {moving} pixels move in the wind");
        Assert.Equal(0, calm);
    }

    [Fact]
    public void MotionVectorsWithJitterAllocateNothingPerFrame()
    {
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("velocity", Output("velocity-alloc"), "--count", "2", "--velocity-view", "--jitter", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"The prepass, motion vectors and jitter allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void ABeforeTonemapEffectWritesTheSceneColour()
    {
        // A test effect clears a pooled ping-pong target to a linear colour and copies it over the scene: the frame is that
        // colour, tonemapped, everywhere.
        var result = HostRunner.Run("post-copy", Output("post-copy"), "--capture", "10", "--hidden");
        Gates.AssertValidationClean(result);
        AssertSelfChecks(result);
        var image = Capture(result, 10);
        var expected = ColorSpace.LinearToSrgb(ColorSpace.AcesFitted(PostCopyScene.Color * IVulkanContext.DefaultExposure)) * 255f;
        foreach (var (x, y) in new[] { (5, 5), (image.Width / 2, image.Height / 2), (image.Width - 5, image.Height - 5) })
        {
            var i = (y * image.Width + x) * 4;
            Assert.InRange(image.Pixels[i], expected.X - 2, expected.X + 2);
            Assert.InRange(image.Pixels[i + 1], expected.Y - 2, expected.Y + 2);
            Assert.InRange(image.Pixels[i + 2], expected.Z - 2, expected.Z + 2);
        }
    }

    // Pixels of the velocity view whose motion exceeds `steps` 8-bit steps in x or y.
    private static int CountMoving(PngImage image, int steps)
    {
        var moving = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
            if (Math.Abs(image.Pixels[i] - 128) > steps || Math.Abs(image.Pixels[i + 1] - 128) > steps)
                moving++;
        return moving;
    }
}
