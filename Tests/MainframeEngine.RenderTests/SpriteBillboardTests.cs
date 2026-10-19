using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary><see cref="Sprite3D"/> billboards and flips (ADR 0199): drawn and shadowed facing the camera.</summary>
public class SpriteBillboardTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void BillboardedSpritesFaceTheCameraCastMatchingShadowsAndMatchGolden()
    {
        var faced = HostRunner.Run("sprite-billboard", Output("sprite-billboard"), "--capture", "10", "--hidden");
        var edgeOn = HostRunner.Run("sprite-billboard", Output("sprite-billboard-off"), "--capture", "10", "--count", "1", "--hidden");

        Gates.AssertValidationClean(faced);
        Gates.AssertValidationClean(edgeOn);
        Gates.AssertMatchesGolden(faced, 10);

        // Turned 90° from the camera, the flags are edge-on (a sliver at most) unless billboarded.
        var facedImage = Capture(faced, 10);
        var edgeOnImage = Capture(edgeOn, 10);
        var flag = FlagPixels(facedImage);
        var sliver = FlagPixels(edgeOnImage);
        // Lit from behind the camera, quads facing it cast full shadows onto the floor; edge-on ones cast slivers.
        var shadow = DarkFloorPixels(facedImage);
        var thinShadow = DarkFloorPixels(edgeOnImage);
        TestContext.Current.SendDiagnosticMessage($"Flag pixels: billboarded {flag}, edge-on {sliver}; shadow pixels: {shadow}, {thinShadow}");
        Assert.True(flag > facedImage.Width * facedImage.Height / 100, $"the billboarded flags cover only {flag} px"); // ≥ 1 % of the frame
        Assert.True(sliver * 3 < flag, $"edge-on flags ({sliver} px) should be a sliver of the billboarded ones ({flag} px)");
        Assert.True(shadow > thinShadow * 2, $"billboard shadows ({shadow} px) should outgrow the edge-on ones ({thinShadow} px)");
    }

    private static int FlagPixels(PngImage image)
    {
        var count = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            if (p[i] > 120 && p[i + 2] > 100 && p[i] - p[i + 1] > 70 && p[i + 2] - p[i + 1] > 50)
                count++;
        return count;
    }

    // Below the horizon (the floor fills the frame from 40 % down).
    private static int DarkFloorPixels(PngImage image)
    {
        var count = 0;
        for (var y = image.Height * 2 / 5; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                if (0.2126f * image.Pixels[i] + 0.7152f * image.Pixels[i + 1] + 0.0722f * image.Pixels[i + 2] < 80f)
                    count++;
            }

        return count;
    }
}
