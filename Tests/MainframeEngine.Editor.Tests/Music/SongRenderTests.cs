using System.Security.Cryptography;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

[Collection(nameof(SerialEditor))]
public sealed class SongRenderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-render-").FullName;
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(_root, recursive: true);
    }

    // Two bars at 120 BPM (4 s) of a ZzFX melody; the last note ends exactly at the loop end.
    private static Song Melody(bool loop, bool seamless = true)
    {
        var song = NewSong();
        song.Loop = new SongLoop { Enabled = loop, Start = 0, End = 960 * 8 };
        song.Render.SeamlessLoop = seamless;
        song.Render.TailSeconds = 0.5;
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,.01,.3,.3,2])"));
        AddClip(lead, 0, 960 * 8, (60, 480, 480), (64, 960, 480), (67, 1920, 960), (72, 960 * 7, 960));
        var bass = AddInstrumentTrack(song, "bass", InstrumentDescriptor.Zzfx("zzfx(...[,0,110,,.5,.2,1])"));
        bass.Pan = -0.5f;
        bass.VolumeDb = -4;
        AddClip(bass, 0, 960 * 8, (36, 480, 960 * 3), (43, 960 * 5, 960 * 3));
        return song;
    }

    [Fact]
    public void OfflineRenderIsDeterministic()
    {
        var samples = SongRenderer.RenderToBuffer(Melody(loop: false), out var plan, cancellation: Ct);
        Assert.False(plan.Loop);
        Assert.Equal(960L * 8 * FramesPerTick + Rate / 2, plan.OutputFrames); // last clip end + 0.5 s tail
        Assert.True(Energy(samples) > 10f);

        // Quantised to 1/4096 so libm differences in the last bit across platforms cannot change the hash.
        var quantised = samples.Select(s => (short)Math.Round(s * 4096)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(quantised.AsSpan())));
        Assert.True(hash == PinnedHash, $"render hash {hash}");
        Assert.Equal(samples, SongRenderer.RenderToBuffer(Melody(loop: false), out _, cancellation: Ct));
    }

    private const string PinnedHash = "CC6C83D6E786032B11C3F468B671E5C016B179450E787675688EE7649CD35F11";

    [Fact]
    public void SeamlessLoopFoldsTheTailIntoTheStart()
    {
        var plain = SongRenderer.RenderToBuffer(Melody(loop: true, seamless: false), out var plainPlan, cancellation: Ct);
        var seamless = SongRenderer.RenderToBuffer(Melody(loop: true), out var seamlessPlan, cancellation: Ct);
        Assert.Equal(960L * 8 * FramesPerTick, seamlessPlan.OutputFrames); // exactly the loop
        Assert.Equal(plainPlan.OutputFrames, seamlessPlan.OutputFrames);
        Assert.Equal((0d, 0d), (seamlessPlan.LoopStart, seamlessPlan.LoopEnd));
        Assert.Equal(4.0, plainPlan.LoopEnd, 6);

        var head = 2 * Rate / 50; // the first 20 ms: nothing starts there
        Assert.Equal(0f, Energy(plain.AsSpan(0, head)));
        Assert.True(Energy(seamless.AsSpan(0, head)) > 0.01f); // the last notes' release, from the first pass
        Assert.Equal(plain[(Rate * 2)..], seamless[(Rate * 2)..]); // past the tails, identical
    }

    [Fact]
    public void RenderWritesTheWavAndItsLoopImportSettings()
    {
        var db = new AssetDatabase(_root);
        var song = Melody(loop: true);
        song.Render.Output = "Content/Music/Theme.ogg"; // no encoder yet: written as .wav
        var progress = new List<double>();
        var result = SongRenderer.Render(song, "Theme", db, progress: new SyncProgress(progress.Add), cancellation: Ct);

        Assert.Equal("Content/Music/Theme.wav", result.OutputPath);
        Assert.True(File.Exists(Path.Combine(_root, "Content", "Music", "Theme.wav")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Content", "Music"), "*.tmp"));
        Assert.Equal(1.0, progress[^1]);
        Assert.True(progress.Count > 10);

        var meta = db.ReadOrCreateMeta(result.OutputPath, create: false)!;
        Assert.Equal("audio", meta.Importer);
        Assert.StartsWith("aud_", meta.Uid, StringComparison.Ordinal);
        Assert.Equal(result.OutputPath, db.GetPath(meta.Uid));
        Assert.Equal("Stream", meta.Settings!["loadMode"].GetString());
        Assert.True(meta.Settings["loop"].GetBoolean());
        Assert.Equal(0, meta.Settings["loopStart"].GetDouble());
        Assert.Equal(0, meta.Settings["loopEnd"].GetDouble());

        // A second render keeps the UID.
        song.Render.SeamlessLoop = false;
        SongRenderer.Render(song, "Theme", db, cancellation: Ct);
        var again = db.ReadOrCreateMeta(result.OutputPath, create: false)!;
        Assert.Equal(meta.Uid, again.Uid);
        Assert.Equal(4.0, again.Settings!["loopEnd"].GetDouble(), 6);
    }

    [Fact]
    public void CancellingLeavesThePreviousRenderIntact()
    {
        var db = new AssetDatabase(_root);
        var song = Melody(loop: false);
        var first = SongRenderer.Render(song, "Theme", db, cancellation: Ct);
        var bytes = File.ReadAllBytes(Path.Combine(_root, first.OutputPath));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SongRenderer.Render(song, "Theme", db, cancellation: cancel.Token));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, first.OutputPath)));
    }

    [Fact]
    public void TheRenderPlaysAndLoopsThroughAnAudioStream()
    {
        var db = new AssetDatabase(_root);
        AssetDatabase.Current = db;
        var song = Melody(loop: true);
        song.Loop.End = 960 * 2; // a one-second loop
        var result = SongRenderer.Render(song, "Loop", db, cancellation: Ct);

        var stream = AudioStream.Load(result.OutputPath);
        Assert.True(stream.Loop);
        Assert.Equal(AudioLoadMode.Stream, stream.LoadMode);
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var handle = server.PlayOneShot(stream, bus: "Music");
        Assert.True(handle.IsValid);
        Assert.Equal(1.0, stream.Length, 3);

        var chunk = new float[4800 * 2];
        var late = 0f;
        for (var i = 0; i < 30; i++) // three seconds: twice around the loop
        {
            server.WaitForStreams(4800, TimeSpan.FromSeconds(5));
            server.RenderNullDevice(4800, chunk);
            if (i >= 20)
                late += Energy(chunk);
        }

        Assert.True(server.IsPlaying(handle));
        Assert.True(late > 1f);
    }

    [Fact]
    public void ThePlayerStreamsTheSongThroughAGeneratorVoice()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var song = NewSong();
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,,1,.1])"));
        AddClip(lead, 0, 960 * 4, (69, 0, 960 * 4));
        using var doc = new SongDocument(song, Path.Combine(_root, "Live.msong"));
        using var player = new SongPlayer(server);
        player.Update(doc);
        player.Play();
        var chunk = new float[SongEngine.BlockFrames * 2];
        var energy = 0f;
        for (var i = 0; i < 40; i++)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (player.Engine.Blocks <= i + 1 && DateTime.UtcNow < deadline)
                Thread.Sleep(1);
            server.RenderNullDevice(SongEngine.BlockFrames, chunk);
            energy += Energy(chunk);
        }

        Assert.True(player.IsPlaying);
        Assert.True(energy > 1f);
        Assert.True(player.PositionTicks > 0);
        Assert.True(player.TakeTrackPeak("lead") > 0f);

        doc.SetTrackMute(lead, true); // edits reach the engine as a new snapshot
        player.Update(doc);
        var deadline2 = DateTime.UtcNow.AddSeconds(2);
        while (player.Engine.Current?.Find("lead")?.Mute != true && DateTime.UtcNow < deadline2)
        {
            server.RenderNullDevice(SongEngine.BlockFrames, chunk);
            Thread.Sleep(1);
        }

        Assert.True(player.Engine.Current!.Find("lead")!.Mute);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
