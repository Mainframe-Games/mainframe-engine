namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0257: the exposure a view's tonemap applies, which <c>IgnoreExposure</c> labels and panels divide by.</summary>
public sealed class PostExposureTests
{
    [Fact]
    public void TheFrameExposureAloneWithoutAutoExposure()
    {
        var settings = PostProcessSettings.Default;
        Assert.Equal(1.3f, PostExposure.Effective(1.3f, settings, postTonemap: true, adaptedLuminance: 0.2f));
        var auto = settings with { AutoExposureEnabled = true };
        Assert.Equal(1.3f, PostExposure.Effective(1.3f, auto, postTonemap: false, adaptedLuminance: 0.2f)); // the engine tonemap has no auto exposure
        Assert.Equal(1.3f, PostExposure.Effective(1.3f, auto, postTonemap: true, adaptedLuminance: 0f));   // nothing read back yet
    }

    [Fact]
    public void AutoExposureMultipliesByScaleOverTheAdaptedLuminance()
    {
        var auto = PostProcessSettings.Default with { AutoExposureEnabled = true, AutoExposureScale = 0.4f };
        Assert.Equal(1.3f * 0.4f / 0.2f, PostExposure.Effective(1.3f, auto, postTonemap: true, adaptedLuminance: 0.2f), 1e-5f);
        // The tonemap's adapted luminance is clamped to the settings' range; so is this.
        Assert.Equal(1.3f * auto.AutoExposureFor(1e-9f), PostExposure.Effective(1.3f, auto, postTonemap: true, adaptedLuminance: 1e-9f), 1e-5f);
    }
}
