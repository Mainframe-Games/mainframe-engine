using System.Numerics;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// World-space UI panels (G11.5/G11.6): the <c>ui-panel3d</c> golden (an angled terminal with a gradient, a clip mask and
/// a blur; a swatch panel; a sub-viewport monitor), the same-frame check (a panel changed in frame N shows in frame N's
/// image), the validation gate and 0 B per frame with a panel updating every frame.
/// </summary>
public class UiPanel3DTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void PanelsRenderBeforeTheSceneAndShowTheSameFramesUi()
    {
        var before = UiPanel3DScene.CheckFrame - 1;
        var result = HostRunner.Run("ui-panel3d", Output("ui-panel3d"), "--capture", $"{before},{UiPanel3DScene.CheckFrame}", "--hidden");
        Gates.AssertValidationClean(result);

        // The swatch is red until CheckFrame, when its document turns green before the frame's UI update.
        var red = Png.ReadRgba8(result.Captures.Single(c => c.Frame == before).Path);
        var green = Png.ReadRgba8(result.Captures.Single(c => c.Frame == UiPanel3DScene.CheckFrame).Path);
        var (r0, g0, b0) = Sample(red, UiPanel3DScene.SwatchCentre);
        var (r1, g1, b1) = Sample(green, UiPanel3DScene.SwatchCentre);
        TestContext.Current.SendDiagnosticMessage($"swatch before ({r0},{g0},{b0}), at the check frame ({r1},{g1},{b1})");
        Assert.True(r0 > 150 && g0 < 80 && b0 < 80, $"the swatch before the change is ({r0},{g0},{b0}), expected red");
        Assert.True(g1 > 150 && g1 > r1 + 60 && g1 > b1 + 60, $"the swatch in the frame it changed is ({r1},{g1},{b1}), expected green (same frame)");

        // The monitor shows the sub-viewport: teal in its lower right, orange near the box.
        var (mr, mg, mb) = Sample(green, UiPanel3DScene.MonitorCentre + new Vector3(UiPanel3DScene.MonitorSize.X * 0.3f, -UiPanel3DScene.MonitorSize.Y * 0.3f, 0));
        Assert.True(mg > 70 && mb > 70 && mr < 80, $"monitor background ({mr},{mg},{mb}), expected teal");

        Gates.AssertMatchesGolden(result, UiPanel3DScene.CheckFrame);
    }

    [Fact]
    public void PanelFramesAllocateNothing()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("ui-panel3d", Output("ui-panel3d-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");
        Gates.AssertValidationClean(result);
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Panel frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
    }

    private static (int R, int G, int B) Sample(PngImage image, Vector3 point)
    {
        var pixel = UiPanel3DScene.ToPixel(point, image.Width, image.Height);
        var i = ((int)pixel.Y * image.Width + (int)pixel.X) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }
}
