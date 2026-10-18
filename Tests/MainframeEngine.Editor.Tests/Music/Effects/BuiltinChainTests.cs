using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>Built-in inserts in songs: the file format, undo, the snapshot builder and the engine (no plugin host).</summary>
public sealed class BuiltinChainTests
{
    private static (Song Song, SongTrack Track) DcSong(float level = 0.1f)
    {
        var song = NewSong();
        var track = new SongTrack { Id = "dc", Name = "dc", Kind = SongTrackKind.Audio };
        track.Clips.Add(new AudioClip { File = "dc", Start = 0, Length = 960 * 8 });
        song.Tracks.Add(track);
        return (song, track);
    }

    private static SongSnapshotBuilder Builder() => new(Rate, clipLoader: _ => Constant(0.1f, 3f));

    private static SongEngine Start(SongSnapshotBuilder builder, Song song)
    {
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(builder.Build(song));
        engine.Play();
        return engine;
    }

    [Fact]
    public void BuiltinInsertsPlayWithoutAPluginHost()
    {
        var (song, track) = DcSong();
        track.Inserts.Add(new PluginInsert { Builtin = "utility", Params = new() { ["gain"] = -6 } });
        song.Master.Inserts.Add(new PluginInsert { Builtin = "utility", Params = new() { ["invertLeft"] = 1 } });
        var output = Run(Start(Builder(), song), 4);
        Assert.Equal(-0.1f * Dsp.DbToGain(-6), output[^2], 4);
        Assert.Equal(0.1f * Dsp.DbToGain(-6), output[^1], 4);
    }

    [Fact]
    public void RebuildsKeepTheInstanceAndPushParametersWithoutAReset()
    {
        var (song, track) = DcSong();
        var insert = new PluginInsert { Builtin = "utility" };
        track.Inserts.Add(insert);
        var builder = Builder();
        var engine = Start(builder, song);
        Run(engine, 2);
        var effect = builder.FindBuiltin(insert);
        Assert.NotNull(effect);

        insert.Params = new() { ["gain"] = 6 };
        engine.SetSnapshot(builder.Build(song));
        var output = Run(engine, 2);
        Assert.Same(effect, builder.FindBuiltin(insert));
        Assert.Equal(0.1f * Dsp.DbToGain(6), output[^1], 4);

        track.Inserts.Clear();
        builder.Build(song);
        Assert.Null(builder.FindBuiltin(insert)); // removed with its insert
    }

    [Fact]
    public void AnUnknownBuiltinPassesThroughAndShowsOnTheTrack()
    {
        var (song, track) = DcSong();
        track.Inserts.Add(new PluginInsert { Builtin = "from-the-future", Params = new() { ["x"] = 1 } });
        var builder = Builder();
        Assert.Equal("unknown built-in effect: from-the-future", builder.Build(song).Find("dc")!.Status);
        var output = Run(Start(builder, song), 2);
        Assert.Equal(0.1f, output[^1], 5);
    }

    [Fact]
    public void BuiltinsInTheEngineAllocateNothing()
    {
        var (song, track) = DcSong();
        track.Inserts.Add(PluginInsert.ForBuiltin(BuiltinEffects.Find("eq")!));
        track.Inserts.Add(PluginInsert.ForBuiltin(BuiltinEffects.Find("compressor")!));
        foreach (var info in BuiltinEffects.All)
            song.Master.Inserts.Add(PluginInsert.ForBuiltin(info));
        var builder = Builder();
        var engine = Start(builder, song);
        var block = new float[SongEngine.BlockFrames * 2];
        for (var i = 0; i < 300; i++)
            engine.Process(block);

        track.Inserts[0].Params!["peak2Gain"] = 9;
        var edited = builder.Build(song);
        var allocated = long.MaxValue;
        for (var attempt = 0; attempt < 3 && allocated != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            engine.SetSnapshot(edited);
            for (var i = 0; i < 1000; i++)
            {
                engine.Process(block);
                if (i == 500)
                    engine.SeekFrames(1000);
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void BuiltinInsertsRoundTripAndKeepWhatTheyDoNotKnow()
    {
        var (song, track) = DcSong();
        track.Inserts.Add(new PluginInsert { Builtin = "eq", Params = new() { ["peak2Gain"] = -3.5f, ["fromLater"] = 2 } });
        track.Inserts.Add(new PluginInsert { Builtin = "future-fx", Bypass = true });
        var json = SongFormat.Write(song);
        var text = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("\"builtin\": \"eq\"", text, StringComparison.Ordinal);
        Assert.Contains("\"peak2Gain\": -3.5", text, StringComparison.Ordinal);

        var read = SongFormat.Parse(json, "x.msong").Song;
        var inserts = read.Tracks[0].Inserts;
        Assert.Equal("eq", inserts[0].Builtin);
        Assert.Equal(-3.5f, inserts[0].Params!["peak2Gain"]);
        Assert.Equal(2, inserts[0].Params!["fromLater"]);
        Assert.Equal("future-fx", inserts[1].Builtin);
        Assert.True(inserts[1].Bypass);
        Assert.Equal(json, SongFormat.Write(read));
        Assert.Equal("Channel EQ", inserts[0].DisplayName);
        Assert.Equal("Unknown effect (future-fx)", inserts[1].DisplayName);
    }

    [Fact]
    public void BuiltinEditsAreUndoableAndDragsMergeIntoOneEntry()
    {
        var (song, track) = DcSong();
        using var document = new SongDocument(song, "/tmp/x.msong");
        var eq = document.AddBuiltinInsert(track, "eq");
        Assert.Equal(0, eq.Params!["peak2Gain"]);

        for (var g = 1; g <= 6; g++)
            document.SetInsertParam(eq, "peak2Gain", g, "drag");
        document.History.EndMerge();
        Assert.Equal(6, eq.Params["peak2Gain"]);
        Assert.Equal("Channel EQ: Gain", document.History.UndoAction?.Name);
        document.History.Undo();
        Assert.Equal(0, eq.Params["peak2Gain"]);
        document.History.Redo();
        Assert.Equal(6, eq.Params["peak2Gain"]);

        document.SetInsertParam(eq, "peak2Gain", 99); // clamped
        Assert.Equal(24, eq.Params["peak2Gain"]);

        document.SetInsertParams(eq, new Dictionary<string, float> { ["peak3Freq"] = 3000, ["peak3Gain"] = -4 }, "EQ Band 3");
        Assert.Equal(3000, eq.Params["peak3Freq"]);
        document.History.Undo();
        Assert.Equal(2500, eq.Params["peak3Freq"]);

        var comp = PluginInsert.ForBuiltin(BuiltinEffects.Find("compressor")!);
        document.ReplaceInsert(track, eq, comp);
        Assert.Same(comp, track.Inserts[0]);
        document.History.Undo();
        Assert.Same(eq, track.Inserts[0]);

        document.History.Undo(); // the clamped set
        document.History.Undo(); // the drag
        document.History.Undo(); // the add
        Assert.Empty(track.Inserts);
    }

    [Fact]
    public void AParameterAbsentFromTheFileIsRemovedAgainOnUndo()
    {
        var (song, track) = DcSong();
        var insert = new PluginInsert { Builtin = "utility" };
        track.Inserts.Add(insert);
        using var document = new SongDocument(song, "/tmp/x.msong");
        document.SetInsertParam(insert, "gain", -3);
        Assert.Equal(-3, insert.Params!["gain"]);
        document.History.Undo();
        Assert.False(insert.Params.ContainsKey("gain"));
    }
}
