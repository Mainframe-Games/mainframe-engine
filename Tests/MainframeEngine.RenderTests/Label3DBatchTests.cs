using System.Numerics;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// The batched <see cref="Label3D"/> renderer (ADR 0258): 300 labels in a few draws with picking (the scene's
/// self-check), its golden, 0 B per frame while labels move and the camera orbits; and <c>IgnoreExposure</c> on labels
/// and panels (ADR 0257) against a run at exposure 1.
/// </summary>
public class Label3DBatchTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    [Fact]
    public void ThreeHundredLabelsDrawInAFewDrawsAndPick()
    {
        var result = HostRunner.Run("label3d-batch", Output("label3d-batch"), "--capture", $"{Label3DBatchScene.CheckFrame}", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, Label3DBatchScene.CheckFrame);
    }

    [Fact]
    public void BatchedLabelFramesAllocateNothing()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("label3d-batch", Output("label3d-batch-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");
        Gates.AssertValidationClean(result);
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Batched label frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
    }

    [Fact]
    public void IgnoreExposureKeepsTheExposureOneColour()
    {
        var frame = Label3DExposureScene.ShotFrame;
        var high = HostRunner.Run("label3d-exposure", Output("label3d-exposure"), "--capture", $"{frame}", "--hidden");
        var one = HostRunner.Run("label3d-exposure", Output("label3d-exposure-1"), "--capture", $"{frame}", "--count", "1", "--hidden");
        Gates.AssertValidationClean(high);
        Gates.AssertValidationClean(one);
        var a = Png.ReadRgba8(high.Captures.Single(c => c.Frame == frame).Path);
        var b = Png.ReadRgba8(one.Captures.Single(c => c.Frame == frame).Path);

        // Labels: the brightest text red (glyph interiors) around each label.
        var leftHigh = BrightRed(a, Label3DExposureScene.LeftLabel);
        var leftOne = BrightRed(b, Label3DExposureScene.LeftLabel);
        var rightHigh = BrightRed(a, Label3DExposureScene.RightLabel);
        var rightOne = BrightRed(b, Label3DExposureScene.RightLabel);
        // Panels: the centre pixel's grey.
        var panelLeftHigh = Grey(a, Label3DExposureScene.LeftPanel);
        var panelLeftOne = Grey(b, Label3DExposureScene.LeftPanel);
        var panelRightHigh = Grey(a, Label3DExposureScene.RightPanel);
        var panelRightOne = Grey(b, Label3DExposureScene.RightPanel);
        TestContext.Current.SendDiagnosticMessage(
            $"labels: ignore {leftHigh} (exp 1: {leftOne}), exposed {rightHigh} (exp 1: {rightOne}); panels: ignore {panelLeftHigh} ({panelLeftOne}), exposed {panelRightHigh} ({panelRightOne})");

        Assert.InRange(leftHigh, leftOne - 3, leftOne + 3);
        Assert.InRange(panelLeftHigh, panelLeftOne - 3, panelLeftOne + 3);
        Assert.InRange(rightOne, leftOne - 3, leftOne + 3); // at exposure 1 the flag changes nothing
        Assert.True(rightHigh > rightOne + 25, $"the exposed label did not brighten: {rightHigh} vs {rightOne}");
        Assert.True(panelRightHigh > panelRightOne + 25, $"the exposed panel did not brighten: {panelRightHigh} vs {panelRightOne}");
    }

    private static Vector2 Pixel(Vector3 world, PngImage image) => Label3DExposureScene.ToPixel(world, image.Width, image.Height);

    // The 95th percentile of red within ±30 px of the label's centre.
    private static int BrightRed(PngImage image, Vector3 world)
    {
        var centre = Pixel(world, image);
        var reds = new List<int>();
        for (var y = (int)centre.Y - 30; y <= (int)centre.Y + 30; y++)
            for (var x = (int)centre.X - 30; x <= (int)centre.X + 30; x++)
                if (x >= 0 && y >= 0 && x < image.Width && y < image.Height)
                    reds.Add(image.Pixels[(y * image.Width + x) * 4]);
        reds.Sort();
        return reds[reds.Count * 95 / 100];
    }

    private static int Grey(PngImage image, Vector3 world)
    {
        var p = Pixel(world, image);
        var i = ((int)p.Y * image.Width + (int)p.X) * 4;
        return image.Pixels[i + 1];
    }
}
