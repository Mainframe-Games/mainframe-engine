using MainframeEngine.Audio.Music;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

/// <summary>
/// Adaptive playback in the <see cref="SongEngine"/> (docs/design/future/adaptive-music.md, "Live preview"): the section
/// program, transitions on the director's frames, layer buses, stingers over the music, edits while playing, and no
/// allocation on steady blocks. The engine runs offline, block by block, with no audio device.
/// </summary>
public sealed class AdaptiveLiveTests
{
    private const long Bar = 3840; // 960 PPQ, 4/4
    private const long BarFrames = Bar * FramesPerTick; // 96 000 frames, 2 s at 120 BPM
    private const int Block = SongEngine.BlockFrames;

    // Intro (1 bar, then Explore), Explore (2 bars, loops), Combat (1 bar, loops), a Hit stinger (1 bar). Explore → Combat
    // when area = Combat at the next bar; Combat → Explore when area = Calm at its end. Pad (Main) plays everywhere; Drums
    // (layer perc, its volume a curve over intensity) only in Combat.
    private static Song Theme()
    {
        var song = NewSong();
        var pad = AddInstrumentTrack(song, "pad");
        AddClip(pad, 0, 5 * Bar, (60, 0, Bar), (62, Bar, 2 * Bar), (64, 3 * Bar, Bar), (67, 4 * Bar, Bar / 2));
        var drums = AddInstrumentTrack(song, "drums");
        drums.Layer = "perc";
        AddClip(drums, 3 * Bar, Bar, (36, 0, 480), (38, 1920, 480));
        song.Adaptive = new SongAdaptive
        {
            Start = "intro",
            Sections =
            [
                new SongSection { Id = "intro", Name = "Intro", Kind = MusicSectionKind.Section, Start = 0, End = Bar, Then = "explore" },
                new SongSection { Id = "explore", Name = "Explore", Kind = MusicSectionKind.Section, Start = Bar, End = 3 * Bar, Loop = true },
                new SongSection { Id = "combat", Name = "Combat", Kind = MusicSectionKind.Section, Start = 3 * Bar, End = 4 * Bar, Loop = true },
                new SongSection { Id = "hit", Name = "Hit", Kind = MusicSectionKind.Stinger, Start = 4 * Bar, End = 5 * Bar, Quantize = MusicQuantize.Beat },
            ],
            Layers = [new SongLayer { Id = "perc", Name = "Perc" }],
            Parameters =
            [
                new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] },
                new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Min = 0, Max = 1, Default = 1 },
            ],
            Curves =
            [
                new MusicCurveInfo { Layer = "perc", Target = MusicCurveTarget.Volume, Parameter = "intensity", Points = [new(0, -80), new(1, 0)] },
            ],
            Transitions =
            [
                new MusicTransitionInfo
                {
                    From = ["explore"], To = "combat", Quantize = MusicQuantize.Bar,
                    When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue("Combat") }] },
                },
                new MusicTransitionInfo
                {
                    From = ["combat"], To = "explore", Quantize = MusicQuantize.SectionEnd,
                    When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue("Calm") }] },
                },
            ],
        };
        return song;
    }

    private static (SongEngine Engine, SongSnapshotBuilder Builder) Engine(Song song, Func<SongTrack, int, IInstrument?>? instruments = null)
    {
        var engine = new SongEngine(Rate);
        var builder = new SongSnapshotBuilder(Rate, instruments ?? ((_, _) => new ToneInstrument()));
        engine.SetSnapshot(builder.Build(song));
        return (engine, builder);
    }

    private static List<(int Section, long Frame)> Starts(SongEngine engine)
    {
        var starts = new List<(int, long)>();
        for (var i = 0; i < engine.SectionStartCount; i++)
            starts.Add(engine.SectionStartAt(i));
        return starts;
    }

    [Fact]
    public void LiveDecisionsMatchTheRuntimeDirector()
    {
        // The same parameter script through the engine (offline, 256-frame blocks) and through a bare MusicDirector updated at
        // the same frames, as a game would with no lookahead: the same sections start on the same frames.
        var song = Theme();
        long[] combatAt = [150_000, 700_000];
        long[] calmAt = [430_000];
        var (engine, _) = Engine(song);
        engine.PlayAdaptive();
        var director = new MusicDirector(SongAdaptiveCompiler.Compile(song, Rate));
        director.Play();
        var actions = new MusicAction[MusicDirector.MaxActionsPerUpdate];
        var expected = new List<(int, long)>();
        var lastSection = -1;
        var lastPass = -1L;
        var block = new float[Block * 2];
        for (long clock = 0; clock < 1_000_000; clock += Block)
        {
            foreach (var at in combatAt)
            {
                if (clock <= at && at < clock + Block)
                {
                    engine.SetParameter("area", 1);
                    director.Parameters.SetTarget(0, 1);
                }
            }

            foreach (var at in calmAt)
            {
                if (clock <= at && at < clock + Block)
                {
                    engine.SetParameter("area", 0);
                    director.Parameters.SetTarget(0, 0);
                }
            }

            engine.Process(block);
            director.Update(clock, clock, actions);
            // The director's prediction after each update: every pass it entered (sections and loop passes) since the last.
            if (director.CurrentSection != lastSection || director.PassStart != lastPass)
            {
                (lastSection, lastPass) = (director.CurrentSection, director.PassStart);
                expected.Add((lastSection, lastPass));
            }
        }

        // The director sees passes once per update, so compare the passes the engine started against the ones it predicted.
        var starts = Starts(engine).Where(s => s.Frame < 1_000_000 - Block).ToList();
        var plan = engine.Current!.Adaptive!.Plan;
        Assert.Equal(expected.Where(e => e.Item2 < 1_000_000 - Block).ToList(), starts);
        var names = starts.Select(s => plan.Sections[s.Section].Id).ToList();
        Assert.Equal(["intro", "explore", "combat"], names.Take(3));
        Assert.Contains("explore", names.Skip(3));
        Assert.Equal(0, engine.DroppedActions);
    }

    [Fact]
    public void SectionsLoopAndContinueOnTheirBarsAndThePlayheadFollows()
    {
        var (engine, _) = Engine(Theme());
        engine.PlayAdaptive();
        Run(engine, (int)(BarFrames * 4 / Block) + 2);
        // Intro at 0, Explore at 1 bar, again at 3 bars (it loops).
        Assert.Equal([(0, 0L), (1, BarFrames), (1, 3 * BarFrames)], Starts(engine));
        var state = engine.AdaptiveState;
        Assert.True(state.Playing);
        Assert.Equal(1, state.Section);
        // The song position is inside Explore's range (bars 2–3), not past it.
        Assert.InRange(engine.PositionFrames, BarFrames, 3 * BarFrames);
    }

    [Fact]
    public void LayerGainsFollowTheirCurvesOnTheLayerBus()
    {
        // Only Drums (perc) sounds in Combat; its volume is a curve over intensity (0: −80 dB, silent).
        var song = Theme();
        song.Adaptive!.Start = "combat";
        song.Tracks[0].Mute = true;
        var (engine, _) = Engine(song);
        engine.SetParameter("intensity", 0);
        engine.PlayAdaptive();
        var silent = Run(engine, 40);
        Assert.Equal(-80, engine.LayerGainDb(1), 1); // layers: Main, then perc
        Assert.Equal(0, Energy(silent));

        engine.SetParameter("intensity", 1);
        Run(engine, 20); // seek speed 0: the value jumps; the gain ramps over one block
        var loud = Run(engine, 40);
        Assert.Equal(0, engine.LayerGainDb(1), 1);
        Assert.True(Energy(loud) > 0);
        Assert.Equal(1, engine.ParameterValue(1));
    }

    [Fact]
    public void AStingerPlaysOverTheMusicOnItsBeat()
    {
        var recorder = new RecordingInstrument();
        var song = Theme();
        var (engine, _) = Engine(song, (track, _) => track.Id == "pad" ? recorder : new ToneInstrument());
        engine.PlayAdaptive();
        Run(engine, 10);
        engine.TriggerStinger("hit");
        Run(engine, (int)(BarFrames / 4 / Block) + 2); // within a beat
        Assert.Equal(3, engine.AdaptiveState.Stinger);
        // The stinger's note (pitch 67) sounded while Intro's (60) still played.
        Assert.Contains(recorder.Events, e => e.Pitch == 67 && e.On);
        Assert.Equal(0, engine.AdaptiveState.Section);
    }

    [Fact]
    public void AnEditWhilePlayingKeepsThePassAndANewSnapshotsDirectorTakesOver()
    {
        var song = Theme();
        var (engine, builder) = Engine(song);
        engine.SetParameter("intensity", 0.5f);
        engine.PlayAdaptive();
        Run(engine, (int)(BarFrames * 1.5 / Block)); // half way into Explore's first pass
        var before = Starts(engine);
        ((MidiClip)song.Tracks[0].Clips[0]).Notes.Add(new MidiNote { Pitch = 70, Start = Bar + 960, Length = 480, Velocity = 100 });
        engine.SetSnapshot(builder.Build(song));
        Run(engine, 4);
        Assert.Equal(before, Starts(engine)); // no restart
        Assert.Equal(1, engine.AdaptiveState.Section);
        Assert.Equal(0.5f, engine.ParameterValue(1), 3); // the parameters moved to the new director

        // A section whose id goes away: the start section plays from here.
        song.Adaptive!.Sections.RemoveAll(s => s.Id == "explore");
        song.Adaptive.Sections.Find(s => s.Id == "intro")!.Then = null;
        engine.SetSnapshot(builder.Build(song));
        Run(engine, 2);
        Assert.Equal(0, engine.AdaptiveState.Section);
    }

    [Fact]
    public void TheMusicEndsWithoutAContinuationAndStopOrSeekReturnsToTheTimeline()
    {
        var song = Theme();
        song.Adaptive!.Sections[0].Then = null; // Intro ends the music
        var (engine, _) = Engine(song);
        engine.PlayAdaptive();
        Run(engine, (int)(BarFrames / Block) + 3);
        Assert.False(engine.AdaptiveState.Playing);
        Assert.False(engine.IsPlaying);

        engine.PlayAdaptive(engine.Current!.Adaptive!.Plan.FindSection("explore"));
        Run(engine, 4);
        Assert.Equal(1, engine.AdaptiveState.Section);
        engine.SeekFrames(0);
        Run(engine, 1);
        Assert.False(engine.IsAdaptive);
    }

    [Fact]
    public void SteadyAdaptiveBlocksAllocateNothing()
    {
        var song = Theme();
        var (engine, _) = Engine(song);
        engine.PlayAdaptive();
        engine.SetParameter("intensity", 0.7f);
        var block = new float[Block * 2];
        for (var i = 0; i < 2000; i++) // past Intro, through Explore's loops, a transition and a stinger
        {
            if (i == 400)
                engine.SetParameter("area", 1);
            engine.Process(block);
        }

        engine.TriggerStinger("hit");
        for (var i = 0; i < 50; i++)
            engine.Process(block);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            engine.Process(block);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
}
