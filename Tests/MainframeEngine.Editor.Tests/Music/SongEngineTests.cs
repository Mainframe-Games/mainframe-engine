using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class SongEngineTests
{
    private const int Block = SongEngine.BlockFrames;

    private static (SongEngine Engine, RecordingInstrument Instrument) Start(Song song, long startFrame = 0)
    {
        var instrument = new RecordingInstrument();
        var builder = new SongSnapshotBuilder(Rate, (_, _) => instrument);
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(builder.Build(song));
        engine.SeekFrames(startFrame);
        engine.Play();
        return (engine, instrument);
    }

    [Fact]
    public void NotesLandOnExactSampleOffsetsAcrossBlocks()
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "t1");
        AddClip(track, 0, 4000, (60, 10, 20), (64, 100, 1)); // frames 250–750 and 2500–2525
        var (engine, rec) = Start(song);
        Run(engine, 12);

        Assert.Equal(
        [
            (0L, 250, 60, true),
            (2L, 750 - 2 * Block, 60, false),
            (9L, 2500 - 9 * Block, 64, true),
            (9L, 2525 - 9 * Block, 64, false),
        ], rec.Events);
        Assert.Equal(12 * Block, engine.PositionFrames);
        Assert.Equal(12.0 * Block / FramesPerTick, engine.PositionTicks, 6);
    }

    [Fact]
    public void LoopWrapEndsHeldNotesAtTheWrapAndRestartsFromTheLoopStart()
    {
        var song = NewSong();
        song.Loop = new SongLoop { Enabled = true, Start = 0, End = 1000 }; // 25 000 frames: wraps in block 97 at 168
        var track = AddInstrumentTrack(song, "t1");
        AddClip(track, 0, 2000, (60, 0, 10), (72, 990, 100)); // the second note crosses the loop end
        var (engine, rec) = Start(song);
        Run(engine, 99);

        var wrapBlock = 25000 / Block;
        var wrapOffset = 25000 - wrapBlock * Block;
        Assert.Equal(
        [
            (0L, 0, 60, true),
            (0L, 250, 60, false),
            (96L, 24750 - 96 * Block, 72, true),
            ((long)wrapBlock, wrapOffset, 72, false), // note-off at the wrap, before the restart
            ((long)wrapBlock, wrapOffset, 60, true),
            ((long)wrapBlock + 1, wrapOffset + 250 - Block, 60, false),
        ], rec.Events);
        Assert.Equal(99L * Block - 25000, engine.PositionFrames);
    }

    [Fact]
    public void StopAndSeekSendAllNotesOff()
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "t1");
        AddClip(track, 0, 4000, (60, 0, 3000));
        var (engine, rec) = Start(song);
        Run(engine, 2);
        var initial = rec.AllNotesOffCalls; // the start's seek
        engine.Stop();
        Run(engine, 1);
        Assert.Equal(initial + 1, rec.AllNotesOffCalls);
        Assert.False(engine.IsPlaying);
        var stoppedAt = engine.PositionFrames;
        Run(engine, 3);
        Assert.Equal(stoppedAt, engine.PositionFrames); // stopped: the position holds

        engine.SeekFrames(0);
        engine.Play();
        Run(engine, 1);
        Assert.Equal(initial + 2, rec.AllNotesOffCalls);
        Assert.Equal(2, rec.Events.Count(e => e.On)); // the note restarted from the seek
    }

    [Fact]
    public void ZzfxNotesSoundAndStopReleasesThem()
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "t1", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,,1,.1])"));
        AddClip(track, 0, 8000, (69, 0, 4000));
        var builder = new SongSnapshotBuilder(Rate);
        var snapshot = builder.Build(song);
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(snapshot);
        engine.Play();
        var playing = Run(engine, 8);
        Assert.True(Energy(playing) > 1f);
        Assert.Equal(1, ((ZzfxInstrument)snapshot.Find("t1")!.Instrument!).ActiveVoices);
        Assert.True(engine.TakeTrackPeak("t1") > 0.1f);
        Assert.Equal(0f, engine.TakeTrackPeak("t1")); // taken
        engine.Stop();
        Run(engine, 2); // 5 ms panic fade
        Assert.Equal(0f, Energy(Run(engine, 1)));
        Assert.Equal(0, ((ZzfxInstrument)snapshot.Find("t1")!.Instrument!).ActiveVoices);
    }

    [Fact]
    public void PreviewPlaysAStoppedTracksInstrument()
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "t1", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,,1,.1])"));
        var builder = new SongSnapshotBuilder(Rate);
        var snapshot = builder.Build(song);
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(snapshot);
        Assert.Equal(0f, Energy(Run(engine, 1)));
        builder.EnsurePitch(track, 60);
        engine.PreviewNote(snapshot.Find("t1")!, 60, 100);
        Assert.True(Energy(Run(engine, 4)) > 0.1f);
        engine.PreviewNote(snapshot.Find("t1")!, 60, 0);
        Run(engine, 7); // 30 ms release
        Assert.Equal(0f, Energy(Run(engine, 1)));
    }

    [Fact]
    public void MuteSoloVolumeAndPanFollowTheMixerRules()
    {
        float[] Mix(Action<Song> edit)
        {
            var song = NewSong();
            foreach (var (id, file) in new[] { ("a", "dc_a"), ("b", "dc_b") })
            {
                var t = new SongTrack { Id = id, Name = id, Kind = SongTrackKind.Audio };
                t.Clips.Add(new AudioClip { File = file, Start = 0, Length = 1000 });
                song.Tracks.Add(t);
            }

            edit(song);
            var builder = new SongSnapshotBuilder(Rate, clipLoader: f => Constant(f == "dc_a" ? 0.5f : 0.25f));
            var engine = new SongEngine(Rate);
            engine.SetSnapshot(builder.Build(song));
            engine.Play();
            return Run(engine, 1)[^2..];
        }

        void Expect(float left, float right, float[] actual)
        {
            Assert.Equal(left, actual[0], 5);
            Assert.Equal(right, actual[1], 5);
        }

        Expect(0.75f, 0.75f, Mix(_ => { }));
        Expect(0.25f, 0.25f, Mix(s => s.Tracks[1].Solo = true));
        Expect(0f, 0f, Mix(s => (s.Tracks[1].Solo, s.Tracks[1].Mute) = (true, true)));
        Expect(0.5f, 0.5f, Mix(s => s.Tracks[1].Mute = true));
        Expect(0.25f, 0f, Mix(s => (s.Tracks[0].Mute, s.Tracks[1].Pan) = (true, -1f)));

        var halfRight = Mix(s => (s.Tracks[1].Mute, s.Tracks[0].Pan) = (true, 0.5f));
        AudioMath.PanGains(0.5f, out var left, out var right);
        Assert.Equal(0.5f * left, halfRight[0], 6);
        Assert.Equal(0.5f * right, halfRight[1], 6);
        Assert.Equal(1f, right, 6); // equal power, normalized: the near side stays at unity
        Assert.Equal(MathF.Sqrt(2f) * MathF.Cos(1.5f * MathF.PI / 4f), left, 5);

        var quieter = Mix(s => (s.Tracks[1].Mute, s.Tracks[0].VolumeDb, s.Master.VolumeDb) = (true, -6.0206f, -6.0206f));
        Assert.Equal(0.125f, quieter[0], 4);
    }

    [Fact]
    public void AudioClipsPlayFromTheirOffsetAndStopAtTheirEnd()
    {
        var samples = new float[Rate];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = i / (float)Rate;
        var song = NewSong();
        var t = new SongTrack { Id = "a", Name = "a", Kind = SongTrackKind.Audio };
        t.Clips.Add(new AudioClip { File = "ramp", Start = 4, Length = 8, Offset = 2 }); // frames 100–300, from source frame 50
        song.Tracks.Add(t);
        var builder = new SongSnapshotBuilder(Rate, clipLoader: _ => AudioStream.FromSamples(samples, 1, Rate));
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(builder.Build(song));
        engine.Play();
        var output = Run(engine, 2);
        Assert.Equal(0f, output[2 * 99]);
        Assert.Equal(50f / Rate, output[2 * 100], 6);
        Assert.Equal(249f / Rate, output[2 * 299], 6);
        Assert.Equal(0f, output[2 * 300]);
    }

    [Fact]
    public void SteadyStatePlaybackDoesNotAllocate()
    {
        var song = NewSong();
        song.Loop = new SongLoop { Enabled = true, Start = 0, End = 960 * 2 };
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,.01,.05,.1])"));
        var notes = Enumerable.Range(0, 16).Select(i => (60 + i % 12, (long)i * 120, 100L)).ToArray();
        AddClip(lead, 0, 960 * 2, notes);
        var bass = AddInstrumentTrack(song, "bass", InstrumentDescriptor.Zzfx("zzfx(...[,0,110,,.2,.1,1])"));
        AddClip(bass, 0, 960 * 2, (36, 0, 960), (43, 960, 1200)); // the second crosses the loop end
        var rain = new SongTrack { Id = "rain", Name = "rain", Kind = SongTrackKind.Audio, Pan = 0.3f };
        rain.Clips.Add(new AudioClip { File = "dc", Start = 0, Length = 960 * 3 });
        song.Tracks.Add(rain);

        var builder = new SongSnapshotBuilder(Rate, clipLoader: _ => Constant(0.1f, 3f));
        var engine = new SongEngine(Rate);
        var first = builder.Build(song);
        engine.SetSnapshot(first);
        builder.EnsurePitch(lead, 80);
        engine.Play();
        var block = new float[Block * 2];
        for (var i = 0; i < 400; i++) // warm up: several loop wraps, every code path jitted
        {
            engine.Process(block);
            if (i == 100)
                engine.PreviewNote(first.Find("lead")!, 80, 90);
        }

        song.Tracks[0].VolumeDb = -3; // an edit: a new snapshot swapped in at a block boundary
        var second = builder.Build(song);

        // Like the engine's AllocationGate: a late tier-up or type load can allocate once on this thread, so the
        // window may run up to three times (swapping the snapshots each time) and the smallest run must be zero.
        var allocated = long.MaxValue;
        for (var attempt = 0; attempt < 3 && allocated != 0; attempt++)
        {
            var snapshot = attempt % 2 == 0 ? second : first;
            var snapshotLead = snapshot.Find("lead")!;
            var before = GC.GetAllocatedBytesForCurrentThread();
            engine.SetSnapshot(snapshot);
            for (var i = 0; i < 1000; i++)
            {
                engine.Process(block);
                if (i == 300)
                    engine.PreviewNote(snapshotLead, 80, 90);
                if (i == 320)
                    engine.PreviewNote(snapshotLead, 80, 0);
                if (i == 500)
                    engine.SeekFrames(1000);
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, allocated);
        Assert.Equal(0, engine.DroppedEvents);
    }
}
