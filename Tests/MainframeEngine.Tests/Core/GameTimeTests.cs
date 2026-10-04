namespace MainframeEngine.Tests.Core;

public class GameTimeTests
{
    [Fact]
    public void DefaultGameTimeIsZeroed()
    {
        var time = new GameTime();

        Assert.Equal(0u, time.FrameCount);
        Assert.Equal(0f, time.DeltaTime);
        Assert.Equal(0u, time.FramesPerSecond);
        Assert.Equal(0u, time.FramesTimeMs);
    }

    [Fact]
    public void EngineOptionsDefaultsMatchDocumentation()
    {
        var options = new EngineOptions { GameName = "Test" };

        Assert.Equal(RenderingBackend.Vulkan, options.RenderingBackend);
        Assert.Equal(800, options.WindowSize.X);
        Assert.Equal(600, options.WindowSize.Y);
        Assert.True(options.VSync);
        Assert.Equal(EngineOptions.DefaultEnableValidation, options.EnableValidation);
        Assert.True(options.WindowVisible);
        Assert.False(options.EnableFrameCapture);
        Assert.Equal(0, options.MaxFrames);
        Assert.Equal(0f, options.FixedDeltaTime);
    }

    [Fact]
    public void ValidationDefaultsOnInDebugOffInRelease()
    {
        // Test and engine assemblies are built with the same configuration.
#if DEBUG
        Assert.True(EngineOptions.DefaultEnableValidation);
#else
        Assert.False(EngineOptions.DefaultEnableValidation);
#endif
    }
}
