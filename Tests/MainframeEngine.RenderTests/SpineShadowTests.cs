using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// <see cref="SpineNode"/> alpha-tested shadows and billboards (ADR 0204): the attachments cast their silhouettes into
/// cascades, spot atlas tiles and point cubes, and a billboarded skeleton faces the camera and casts from that pose.
/// </summary>
public class SpineShadowTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void SpineBoyCastsHisSilhouetteUnderALowSun()
    {
        var silhouette = HostRunner.Run("spine-shadow", Output("spine-shadow"), "--capture", "10", "--hidden");
        var quads = HostRunner.Run("spine-shadow-quads", Output("spine-shadow-quads"), "--capture", "10", "--hidden");

        Gates.AssertValidationClean(silhouette);
        Gates.AssertValidationClean(quads);
        Gates.AssertMatchesGolden(silhouette, 10);
        Gates.AssertMatchesGolden(quads, 10);
        AssertSilhouetteSmallerThanQuads(Capture(silhouette, 10), Capture(quads, 10), "sun");
    }

    [Theory]
    [InlineData("spine-shadow-spot")]
    [InlineData("spine-shadow-omni")]
    public void SpineBoyCastsHisSilhouetteFromSpotAndOmniLights(string scene)
    {
        var silhouette = HostRunner.Run(scene, Output(scene), "--capture", "10", "--hidden");
        var quads = HostRunner.Run(scene, Output(scene + "-quads"), "--capture", "10", "--count", "1", "--hidden");

        Gates.AssertValidationClean(silhouette);
        Gates.AssertValidationClean(quads);
        Gates.AssertMatchesGolden(silhouette, 10);
        AssertSilhouetteSmallerThanQuads(Capture(silhouette, 10), Capture(quads, 10), scene);
    }

    [Theory]
    [InlineData("spine-billboard")]
    [InlineData("spine-billboard-yaw")]
    public void BillboardedSpineBoysFaceTheCameraFromEitherYaw(string scene)
    {
        var faced = HostRunner.Run(scene, Output(scene), "--capture", "10", "--hidden");
        var edgeOn = HostRunner.Run(scene, Output(scene + "-off"), "--capture", "10", "--count", "1", "--hidden");

        Gates.AssertValidationClean(faced);
        Gates.AssertValidationClean(edgeOn);
        Gates.AssertMatchesGolden(faced, 10);

        // Turned 90° from the camera, the skeletons are edge-on (a sliver) unless billboarded: count their red parts.
        var red = RedPixels(Capture(faced, 10));
        var sliver = RedPixels(Capture(edgeOn, 10));
        TestContext.Current.SendDiagnosticMessage($"{scene}: red pixels billboarded {red}, edge-on {sliver}");
        Assert.True(red > 600, $"the billboarded skeletons show only {red} red pixels");
        Assert.True(sliver * 3 < red, $"edge-on skeletons ({sliver} red px) should be a sliver of the billboarded ones ({red} px)");
    }

    // The same pose and camera: the quads add the transparent corners of every region attachment to the shadow, so the
    // quad run is darker in many pixels (the corners) and the alpha-tested run darker in almost none.
    private static void AssertSilhouetteSmallerThanQuads(PngImage silhouette, PngImage quads, string what)
    {
        int cornersOnly = 0, silhouetteOnly = 0;
        var s = silhouette.Pixels;
        var q = quads.Pixels;
        for (var i = 0; i < s.Length; i += 4)
        {
            var delta = Luminance(s, i) - Luminance(q, i);
            if (delta > 25f)
                cornersOnly++;
            else if (delta < -25f)
                silhouetteOnly++;
        }

        TestContext.Current.SendDiagnosticMessage($"{what}: pixels shadowed by the quads only {cornersOnly}, by the silhouette only {silhouetteOnly}");
        Assert.True(cornersOnly > silhouette.Width * silhouette.Height / 200,
            $"the quad shadow should cover the attachments' transparent corners ({cornersOnly} px darker than the silhouette)");
        Assert.True(silhouetteOnly * 20 < cornersOnly, $"the alpha-tested shadow darkens {silhouetteOnly} px the quad shadow does not");
    }

    private static float Luminance(byte[] p, int i) => 0.2126f * p[i] + 0.7152f * p[i + 1] + 0.0722f * p[i + 2];

    // SpineBoy's boots, gloves and straps.
    private static int RedPixels(PngImage image)
    {
        var count = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            if (p[i] > 140 && p[i + 1] < 70 && p[i + 2] < 80)
                count++;
        return count;
    }
}
