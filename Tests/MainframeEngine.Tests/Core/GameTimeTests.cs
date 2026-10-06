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
    public void AFixedDeltaAppliesToEveryUpdateEvenTheDiscardedStartUpFrame()
    {
        // --fixed-fps: every update gets 1/n s, the first included (Godot's first frame steps physics too; ADR 0135).
        Assert.Equal(1f / 60f, Engine.FrameDelta(1f / 60f, 0.3, discard: true));
        Assert.Equal(1f / 60f, Engine.FrameDelta(1f / 60f, 0.005, discard: false));
        // Real time: the measured delta, and zero for the start-up frame (its delta is the start-up time).
        Assert.Equal(0.02f, Engine.FrameDelta(0f, 0.02, discard: false));
        Assert.Equal(0f, Engine.FrameDelta(0f, 0.3, discard: true));
    }

    [Fact]
    public void EngineOptionsDefaultsMatchDocumentation()
    {
        var options = new EngineOptions { GameName = "Test" };

        Assert.Equal(RenderingBackend.Vulkan, options.RenderingBackend);
        Assert.Equal(800, options.WindowSize.X);
        Assert.Equal(600, options.WindowSize.Y);
        Assert.Equal(0f, options.ContentScale); // follows the display
        Assert.True(options.VSync);
        Assert.Equal(EngineOptions.DefaultEnableValidation, options.EnableValidation);
        Assert.True(options.WindowVisible);
        Assert.False(options.EnableFrameCapture);
        Assert.Equal(0, options.MaxFrames);
        Assert.Equal(0f, options.FixedDeltaTime);
        Assert.Equal(60, options.PhysicsTicksPerSecond);
        Assert.Equal(new System.Numerics.Vector3(0, -9.81f, 0), options.Physics3D.Gravity);
        Assert.True(options.Physics3D.MultiThreaded);
        Assert.Equal(100f, options.Physics2D.PixelsPerMeter);
        Assert.Equal(new System.Numerics.Vector2(0, 980f), options.Physics2D.Gravity); // 2D is Y-down
        Assert.Equal(4, options.Physics2D.SubstepCount);
        Assert.False(options.DebugCollisionShapes);
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
