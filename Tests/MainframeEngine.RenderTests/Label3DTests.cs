using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary><see cref="Label3D"/> name tags (ADR 0200): outlined, billboarded, and drawn through geometry with NoDepthTest.</summary>
public class Label3DTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void NameTagsRenderOutlinedAndNoDepthTestShowsThroughTheWall()
    {
        var through = HostRunner.Run("label3d", Output("label3d"), "--capture", "10", "--hidden");
        var hidden = HostRunner.Run("label3d", Output("label3d-depth"), "--capture", "10", "--count", "1", "--hidden");

        Gates.AssertValidationClean(through);
        Gates.AssertValidationClean(hidden);
        Gates.AssertMatchesGolden(through, 10);

        // Only the left tag's NoDepthTest differs: its lower half shows through the wall in the first run. The right half of
        // the frame (the depth-tested tag) is the same in both.
        var a = Capture(through, 10);
        var b = Capture(hidden, 10);
        var left = ChangedPixels(a, b, 0, a.Width / 2);
        var right = ChangedPixels(a, b, a.Width / 2, a.Width);
        TestContext.Current.SendDiagnosticMessage($"Changed pixels: left {left}, right {right}");
        Assert.True(left > a.Width * a.Height / 1000, $"the NoDepthTest tag shows through the wall in only {left} px");
        Assert.True(right <= a.Width * a.Height / 2000, $"the depth-tested tag changed {right} px");
    }

    private static int ChangedPixels(PngImage a, PngImage b, int fromX, int toX)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = fromX; x < toX; x++)
            {
                var i = (y * a.Width + x) * 4;
                if (Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > 24)
                    count++;
            }

        return count;
    }
}
