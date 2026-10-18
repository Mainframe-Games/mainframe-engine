using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The wavetable synth in songs: the patch's file format, undo, the snapshot builder, the engine and renders.</summary>
public sealed class SynthSongTests
{
    private static (Song Song, SongTrack Track) SynthSong()
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "pad", InstrumentDescriptor.Wavetable());
        AddClip(track, 0, 960 * 4, (57, 0, 960), (60, 960, 960), (64, 1920, 960));
        return (song, track);
    }

    [Fact]
    public void ThePatchRoundTripsAndKeepsWhatItDoesNotKnow()
    {
        var (song, track) = SynthSong();
        var patch = track.Instrument!.Patch!;
        patch.Params!["a.unison"] = 7;
        patch.Params["fromLater"] = 3;
        patch.Matrix = [new SynthModRoute { Source = "env2", Target = "f1.cutoff", Amount = 0.45f }, new SynthModRoute { Source = "future", Target = "x", Amount = 1, Bipolar = true }];
        patch.LfoShapes = [new SynthLfoShape { Points = [[0, 0, 0], [0.25f, 1, 0.4f], [1, 0, -0.3f]] }, null];
        patch.Fx = [new PluginInsert { Builtin = "chorus", Params = new() { ["mix"] = 30 } }];
        patch.Tables!["b"] = new SynthTableRef { Source = "aud_000000000001", FrameSize = 1024 };
        var json = SongFormat.Write(song);
        var text = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("\"builtin\": \"wavetable\"", text, StringComparison.Ordinal);
        Assert.Contains("\"a.unison\": 7", text, StringComparison.Ordinal);

        var read = SongFormat.Parse(json, "x.msong").Song.Tracks[0].Instrument!;
        Assert.True(read.IsWavetable);
        Assert.Equal(7, read.Patch!.Params!["a.unison"]);
        Assert.Equal(3, read.Patch.Params["fromLater"]);
        Assert.Equal("future", read.Patch.Matrix![1].Source);
        Assert.Equal(0.4f, read.Patch.LfoShapes![0]!.Points[1][2]);
        Assert.Null(read.Patch.LfoShapes[1]);
        Assert.Equal(1024, read.Patch.Tables!["b"].FrameSize);
        Assert.Equal(json, SongFormat.Write(SongFormat.Parse(json, "x.msong").Song));
        Assert.Equal(SynthParams.ProductName, read.DisplayName);
    }

    [Fact]
    public void SynthEditsAreUndoableAndKnobDragsMerge()
    {
        var (song, track) = SynthSong();
        using var document = new SongDocument(song, "/tmp/x.msong");
        for (var c = 1000; c <= 3000; c += 500)
            document.SetSynthParam(track, "f1.cutoff", c, "drag");
        document.History.EndMerge();
        Assert.Equal(3000, track.Instrument!.Patch!.Params!["f1.cutoff"]);
        document.History.Undo();
        Assert.False(track.Instrument.Patch.Params!.ContainsKey("f1.cutoff"));

        document.SetSynthParam(track, "a.unison", 99); // clamped, whole
        Assert.Equal(16, track.Instrument.Patch.Params["a.unison"]);
        document.AddModRoute(track, new SynthModRoute { Source = "lfo1", Target = "a.wt_pos", Amount = 0.2f });
        document.SetModRoute(track, 0, new SynthModRoute { Source = "lfo1", Target = "a.wt_pos", Amount = 0.6f });
        document.SetLfoShape(track, 2, new SynthLfoShape { Points = [[0, 1, 0], [1, 0, 0]] });
        var fx = document.AddSynthFx(track, "reverb");
        document.SetInsertParam(fx, "mix", 50);
        document.SetSynthTable(track, "a", new SynthTableRef { Source = "factory:digital/fm" });
        Assert.Equal(0.6f, track.Instrument.Patch.Matrix![0].Amount);
        Assert.NotNull(track.Instrument.Patch.LfoShapes![2]);
        for (var i = 0; i < 6; i++)
            document.History.Undo();
        Assert.Empty(track.Instrument.Patch.Matrix!);
        Assert.Empty(track.Instrument.Patch.Fx!);
        Assert.Equal(FactoryWavetables.DefaultSource, track.Instrument.Patch.Tables!["a"].Source);
        Assert.Throws<ArgumentException>(() => document.SetSynthParam(track, "nope", 1));
    }

    [Fact]
    public void ASongPlaysTheSynthAndRendersItTheSameTwice()
    {
        var (song, _) = SynthSong();
        float[] Render()
        {
            var builder = new SongSnapshotBuilder(Rate);
            builder.Build(song);
            WavetableCache.WaitForPending();
            var engine = new SongEngine(Rate);
            engine.SetSnapshot(builder.Build(song));
            engine.Play();
            return Run(engine, 200);
        }

        var a = Render();
        Assert.True(Energy(a) > 1, "the synth sounds in the song");
        Assert.Equal(a, Render());
    }

    [Fact]
    public void LiveControllersReachTheTracksInstrument()
    {
        var song = NewSong();
        AddInstrumentTrack(song, "t1");
        var instrument = new RecordingInstrument();
        var builder = new SongSnapshotBuilder(Rate, (_, _) => instrument);
        var engine = new SongEngine(Rate);
        var snapshot = builder.Build(song);
        engine.SetSnapshot(snapshot);
        Run(engine, 1);
        engine.Controller(snapshot.Find("t1")!, NoteEventKind.ModWheel, 64);
        engine.Controller(snapshot.Find("t1")!, NoteEventKind.PitchBend, 12000);
        Run(engine, 1);
        Assert.Equal([(NoteEventKind.ModWheel, 64), (NoteEventKind.PitchBend, 12000)], instrument.Controllers);
        Assert.Empty(instrument.Events); // controllers are not notes
    }

    [Fact]
    public void AnUnknownWavetableShowsOnTheTrackAndPlaysTheDefault()
    {
        var (song, track) = SynthSong();
        track.Instrument!.Patch!.Tables!["a"] = new SynthTableRef { Source = "factory:no/such-table" };
        var builder = new SongSnapshotBuilder(Rate);
        builder.Build(song);
        WavetableCache.WaitForPending();
        Assert.Equal("missing wavetable: factory:no/such-table", builder.Build(song).Find("pad")!.Status);
    }
}
