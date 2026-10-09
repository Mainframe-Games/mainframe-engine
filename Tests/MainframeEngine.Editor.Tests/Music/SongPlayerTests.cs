using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class SongPlayerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-player-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ThePlayheadIsSteadyWhileStoppedAndNeverStepsBackWhilePlaying()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var song = NewSong();
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,,1,.1])"));
        AddClip(lead, 0, 960 * 16, (69, 0, 960 * 16));
        using var doc = new SongDocument(song, Path.Combine(_root, "Player.msong"));
        using var player = new SongPlayer(server);
        player.Update(doc);
        var chunk = new float[SongEngine.BlockFrames * 2];

        player.Seek(960); // stopped at beat 2
        WaitFor(server, chunk, () => player.Engine.PositionFrames > 0);
        player.Play();
        WaitFor(server, chunk, () => player.IsPlaying);
        var previous = player.PositionTicks;
        Assert.True(previous >= 960);
        for (var i = 0; i < 200; i++) // the device and the render thread race; the heard position only moves forward
        {
            if (i % 2 == 0)
                server.RenderNullDevice(SongEngine.BlockFrames / 2, chunk);
            var now = player.PositionTicks;
            Assert.True(now >= previous, $"read {i}: {now} after {previous}");
            previous = now;
        }

        player.Stop();
        WaitFor(server, chunk, () => !player.IsPlaying);
        var stopped = player.PositionTicks;
        for (var i = 0; i < 100; i++) // the render thread keeps queueing silence and the device keeps draining it
        {
            server.RenderNullDevice(SongEngine.BlockFrames / 2, chunk);
            Assert.Equal(stopped, player.PositionTicks);
        }
    }

    [Fact]
    public void ThePlayheadNeverStepsBackWhileARenderedBlockIsBeingQueued()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var song = NewSong();
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,,1,.1])"));
        AddClip(lead, 0, 960 * 16, (69, 0, 960 * 16));
        using var doc = new SongDocument(song, Path.Combine(_root, "Player.msong"));
        using var player = new SongPlayer(server);
        player.Update(doc);
        var chunk = new float[SongEngine.BlockFrames * 2];
        player.Seek(960);
        WaitFor(server, chunk, () => player.Engine.PositionFrames > 0);
        player.Play();
        WaitFor(server, chunk, () => player.IsPlaying);

        // A reader spinning on the playhead lands inside the render thread's push often enough to catch a torn read.
        string? failure = null;
        var done = false;
        var reader = new Thread(() =>
        {
            var previous = player.PositionTicks;
            while (!Volatile.Read(ref done) && failure is null)
            {
                var now = player.PositionTicks;
                if (now < previous)
                    failure = $"{now} after {previous}";
                previous = now;
            }
        });
        reader.Start();
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline && player.PositionTicks < 960 * 12 && Volatile.Read(ref failure) is null)
            server.RenderNullDevice(SongEngine.BlockFrames / 2, chunk);
        Volatile.Write(ref done, true);
        reader.Join();
        Assert.Null(failure);
    }

    // The manual null device only drains when rendered, and the render thread only processes (commands included) when
    // the generator has room, so keep the device pulling while waiting.
    private static void WaitFor(AudioServer server, float[] chunk, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            server.RenderNullDevice(SongEngine.BlockFrames / 2, chunk);
            Thread.Sleep(1);
        }

        Assert.True(condition());
    }
}
