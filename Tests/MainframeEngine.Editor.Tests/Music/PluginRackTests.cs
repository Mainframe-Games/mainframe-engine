using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// The plugin rack against <see cref="FakePluginHost"/> (real shared memory, simulated plugins): plugin delay
/// compensation, the block deadline, crash recovery with state reload, repeat offenders and state capture.
/// </summary>
public sealed class PluginRackTests : IDisposable
{
    private static readonly FakePlugin Synth = new("S1", "Synth", Instrument: true);
    private static readonly FakePlugin Delay = new("D1", "Delay", Latency: 100);
    private static readonly FakePlugin Half = new("H1", "Half", Gain: 0.5f);
    private static readonly FakePlugin Crasher = new("C1", "Crasher");

    private readonly FakePluginHost _host = new();
    private readonly PluginRack _rack;
    private readonly string _root = Directory.CreateTempSubdirectory("mf-rack-").FullName;

    public PluginRackTests()
    {
        foreach (var p in new[] { Synth, Delay, Half, Crasher })
            _host.Plugins[p.ClassId] = p;
        _rack = new PluginRack(() => _host, Rate, id => "/fake/" + id + ".vst3");
    }

    public void Dispose()
    {
        _rack.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static SongTrack PluginTrack(Song song, string id, params FakePlugin[] inserts)
    {
        var track = AddInstrumentTrack(song, id, new InstrumentDescriptor { Plugin = Synth.Descriptor });
        foreach (var fx in inserts)
            track.Inserts.Add(new PluginInsert { Plugin = fx.Descriptor });
        AddClip(track, 0, 960, (60, 0, 480));
        return track;
    }

    private (SongEngine Engine, SongSnapshotBuilder Builder) Start(Song song)
    {
        var builder = new SongSnapshotBuilder(Rate, plugins: _rack);
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(builder.Build(song));
        engine.Play();
        return (engine, builder);
    }

    [Fact]
    public void TracksAreDelayedToTheSlowestChain()
    {
        var song = NewSong();
        PluginTrack(song, "slow", Delay);
        PluginTrack(song, "fast");
        var (engine, _) = Start(song);
        var output = Run(engine, 2);
        Assert.Equal(100, engine.Current!.MaxTrackLatency);
        // Both impulses (frame 0) land on frame 100: the plugin delays one, compensation the other.
        Assert.Equal(0f, output[0]);
        Assert.True(output[2 * 100] > 1.2f, $"both tracks at frame 100: {output[200]}");
        for (var f = 0; f < output.Length / 2; f++)
        {
            if (f != 100)
                Assert.Equal(0f, output[2 * f]);
        }

        Assert.Equal(2, _host.ProcessCalls); // one round trip per block for every track's chain
    }

    [Fact]
    public void AMissedDeadlinePlaysSilenceAndCountsAnXrun()
    {
        var song = NewSong();
        PluginTrack(song, "lead", Half);
        var (engine, _) = Start(song);
        _host.MissProcesses = 1;
        var first = Run(engine, 1);
        Assert.All(first, s => Assert.Equal(0f, s));
        Assert.Equal(1, _rack.Xruns);

        engine.SeekFrames(0);
        var second = Run(engine, 1);
        Assert.True(Energy(second) > 0, "the next block plays");
    }

    [Fact]
    public void ACrashRestartsTheHostAndReloadsEveryPluginWithItsState()
    {
        var song = NewSong();
        var track = PluginTrack(song, "lead", Crasher);
        track.Inserts[0].State = Convert.ToBase64String(new byte[] { 9, 9 });
        var (engine, _) = Start(song);
        Assert.All(_rack.Instances, i => Assert.True(i.IsLoaded));

        _host.CrashOnProcessClass = Crasher.ClassId;
        Run(engine, 1);
        Assert.All(_rack.Instances, i => Assert.False(i.IsLoaded));

        _host.CrashOnProcessClass = null;
        _rack.Maintain(null);
        Assert.Equal(2, _host.Starts);
        Assert.All(_rack.Instances, i => Assert.True(i.IsLoaded));
        var crasher = _rack.Instances.Single(i => ReferenceEquals(i.Owner, track.Inserts[0]));
        Assert.Equal(new byte[] { 9, 9 }, _host.StateOf(crasher.HelperId));
        Assert.True(Energy(Run(engine, 1)) >= 0); // processing again
    }

    [Fact]
    public void APluginThatCrashesTheHostTwiceInAMinuteIsDisabled()
    {
        var song = NewSong();
        var track = PluginTrack(song, "lead", Crasher);
        var (engine, builder) = Start(song);
        _host.CrashOnProcessClass = Crasher.ClassId;

        Run(engine, 1);
        _rack.Maintain(null);
        engine.SetSnapshot(builder.Build(song));
        Run(engine, 1);
        _rack.Maintain(null);

        var crasher = _rack.Instances.Single(i => ReferenceEquals(i.Owner, track.Inserts[0]));
        Assert.True(crasher.Disabled);
        Assert.False(crasher.IsLoaded);
        var synth = _rack.Instances.Single(i => i.IsInstrument);
        Assert.True(synth.IsLoaded, "the other plugins come back");

        var snapshot = builder.Build(song);
        engine.SetSnapshot(snapshot);
        Assert.StartsWith("insert disabled: Crasher", snapshot.Find("lead")!.Status);
        Run(engine, 1); // no crash: the disabled insert is skipped
        Assert.True(_host.IsRunning);
    }

    [Fact]
    public void StatesAreCapturedOnSaveAndOnEditorCloseUndoablyAndPushedBackOnUndo()
    {
        var song = NewSong();
        var track = PluginTrack(song, "lead", Half);
        var path = Path.Combine(_root, "song.msong");
        using var document = new SongDocument(song, path);
        using var player = new SongPlayer(null, plugins: _ => _rack);
        player.Update(document);
        var synth = _rack.Instances.Single(i => i.IsInstrument);

        // The user turns a knob in the plugin's window and closes it.
        _host.SetPluginState(synth.HelperId, new byte[] { 7, 7 });
        _host.CloseEditorByUser(synth.HelperId);
        player.Update(document);
        Assert.Equal(Convert.ToBase64String(new byte[] { 7, 7 }), track.Instrument!.State);
        Assert.True(document.IsDirty);

        // Undo puts the old state back into the model and the plugin.
        document.History.Undo();
        player.Update(document);
        Assert.Null(track.Instrument.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, _host.StateOf(synth.HelperId)); // FakePlugin.DefaultState

        // Save captures every plugin.
        _host.SetPluginState(_rack.Instances.Single(i => !i.IsInstrument).HelperId, new byte[] { 5 });
        player.CapturePluginStates(document);
        document.Save();
        var loaded = SongFormat.Load(path).Song;
        Assert.Equal(Convert.ToBase64String(new byte[] { 5 }), loaded.Tracks[0].Inserts[0].State);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), loaded.Tracks[0].Instrument!.State);
    }

    [Fact]
    public void InsertEditsAreUndoableAndAMissingPluginShowsOnTheTrack()
    {
        var song = NewSong();
        var track = PluginTrack(song, "lead");
        using var document = new SongDocument(song, Path.Combine(_root, "s.msong"));
        var a = document.AddInsert(track, Half.Descriptor);
        var b = document.AddInsert(track, Delay.Descriptor);
        document.MoveInsert(track, b, 0);
        Assert.Equal([b, a], track.Inserts);
        document.SetInsertBypass(a, true);
        document.RemoveInsert(track, b);
        Assert.Equal([a], track.Inserts);
        var master = document.AddInsert(null, Half.Descriptor);
        Assert.Same(master, song.Master.Inserts[0]);
        for (var i = 0; i < 6; i++)
            document.History.Undo();
        Assert.Empty(track.Inserts);
        Assert.Empty(song.Master.Inserts);

        track.Inserts.Add(new PluginInsert { Plugin = new PluginDescriptor { ClassId = "NOPE", Name = "Gone", Vendor = "Nobody" } });
        var builder = new SongSnapshotBuilder(Rate, plugins: new PluginRack(() => _host, Rate, _ => null));
        Assert.Equal("missing plugin: Synth (Fake)", builder.Build(song).Find("lead")!.Status);
    }

    [Fact]
    public void TheBatchedBlockDoesNotAllocate()
    {
        var song = NewSong();
        PluginTrack(song, "a", Half, Delay);
        PluginTrack(song, "b");
        song.Master.Inserts.Add(new PluginInsert { Plugin = Half.Descriptor });
        var (engine, _) = Start(song);
        var block = new float[SongEngine.BlockFrames * 2];
        for (var i = 0; i < 8; i++)
            engine.Process(block); // warm up (delay rings, first calls)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++)
            engine.Process(block);
        // The fake host itself does not allocate per block either; the rack and engine must not.
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
