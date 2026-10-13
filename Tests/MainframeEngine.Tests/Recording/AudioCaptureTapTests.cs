using System.Reflection;
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

        var drained = tap.Drain(new float[1 << 16]);
        Assert.True(tap.OverflowSamples > 0);
        Assert.Equal(2048 * 2, drained + tap.OverflowSamples);
        Assert.Equal(0, drained % 2); // whole frames only: a partial block would swap L/R
    }

    [Fact]
    public void ABlockThatDoesNotFitIsDroppedWholeNotSplit()
    {
        var tap = new AudioCaptureTap(8);
        var block = new float[] { 1, 2, 3, 4, 5, 6 };

        tap.Write(block, 2);
        tap.Write(block, 2); // 2 slots free: the whole block is refused, not its first two samples

        var drained = new float[16];
        Assert.Equal(6, tap.Drain(drained));
        Assert.Equal(6, tap.OverflowSamples);
    }

    [Fact]
    public void TheTapKeepsReceivingSilenceWhileTheMixIsFaulted()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var tap = new AudioCaptureTap(1 << 16);
        server.BeginCapture(tap);

        // Break the live graph so AfterMix throws on the audio path: the root outputs silence from then on.
        var graph = (AudioGraph)typeof(AudioServer).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var voice = graph.Voices[0];
        graph.Voices[0] = null!;
        try
        {
            server.RenderNullDevice(800);
            server.RenderNullDevice(800);
        }
        finally
        {
            graph.Voices[0] = voice;
        }

        Assert.True(server.Stats.Faulted);
        var buffer = new float[1 << 16];
        var drained = tap.Drain(buffer);
        Assert.Equal(1600 * 2, drained);
        Assert.All(buffer.AsSpan(0, drained).ToArray(), sample => Assert.Equal(0f, sample));
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
