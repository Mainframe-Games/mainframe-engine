using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Recording;

/// <summary>The recorder's copy of the master mix: exact sample counts, overflow accounting, detach.</summary>
public sealed class AudioCaptureTapTests
{
    [Fact]
    public void EveryRenderedSampleReachesTheTap()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1 << 16);
        server.BeginCapture(tap);

        server.RenderNullDevice(800);
        server.RenderNullDevice(800);

        var buffer = new float[1 << 16];
        Assert.Equal(1600 * 2, tap.Drain(buffer));
        Assert.Equal(2, tap.Channels);
        Assert.NotEqual(0, tap.FirstBlockTimestamp);
    }

    [Fact]
    public void AFullRingCountsOverflowInsteadOfBlocking()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1024);
        server.BeginCapture(tap);

        server.RenderNullDevice(2048);

        Assert.True(tap.OverflowSamples > 0);
    }

    [Fact]
    public void EndCaptureDetaches()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1 << 16);
        server.BeginCapture(tap);
        server.EndCapture();

        server.RenderNullDevice(800);

        Assert.Equal(0, tap.Drain(new float[4096]));
    }
}
