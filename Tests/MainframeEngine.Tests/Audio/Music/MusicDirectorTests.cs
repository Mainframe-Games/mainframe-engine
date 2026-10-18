using MainframeEngine.Audio.Music;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// The director at 120 BPM, 4/4, 48 kHz: a beat is 24 000 frames, a bar 96 000. Sections: intro (2 bars, then explore),
/// explore (4 bars, loops), combat (4 bars, loops, exit marker at bar 3), toboss (1-bar transition), boss (4 bars,
/// loops), victory (1-bar stinger, 1 beat pickup).
/// </summary>
public sealed class MusicDirectorTests
{
    private const long Beat = 24000;
    private const long Bar = 96000;
    private const long Lookahead = 4800; // 100 ms

    private readonly MusicAction[] _actions = new MusicAction[MusicDirector.MaxActionsPerUpdate];

    private static MusicManifest Manifest(params MusicTransitionInfo[] transitions) => new()
    {
        SampleRate = 48000,
        Tempo = 120,
        Start = "intro",
        Parameters =
        [
            new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat", "Boss"] },
            new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1, SeekSpeed = 0.5f },
        ],
        Layers = [new MusicManifestLayer { Id = "base" }, new MusicManifestLayer { Id = "perc" }],
        Curves =
        [
            new MusicCurveInfo { Layer = "perc", Parameter = "intensity", Points = [new(0, -80), new(1, 0)] },
        ],
        Sections =
        [
            new MusicManifestSection { Id = "intro", Bars = 2, Then = "explore" },
            new MusicManifestSection { Id = "explore", Bars = 4, Loop = true },
            new MusicManifestSection { Id = "combat", Bars = 4, Loop = true, Markers = [new MusicManifestMarker { Name = "drop", Frame = 2 * Bar, Exit = true }] },
            new MusicManifestSection { Id = "toboss", Bars = 1, Kind = MusicSectionKind.Transition },
            new MusicManifestSection { Id = "boss", Bars = 4, Loop = true },
            new MusicManifestSection { Id = "victory", Bars = 1, Kind = MusicSectionKind.Stinger, PickupBeats = 1 },
        ],
        Transitions = [.. transitions],
    };

    private static MusicTransitionInfo Rule(string from, string to, string label, MusicQuantize quantize = MusicQuantize.Bar) =>
        new() { From = [from], To = to, When = MusicLogicTests.When("area", label), Quantize = quantize };

    private static MusicDirector Director(params MusicTransitionInfo[] transitions)
    {
        var plan = MusicPlan.Compile(Manifest(transitions));
        Assert.Empty(plan.Problems);
        return new MusicDirector(plan);
    }

    private List<MusicAction> Update(MusicDirector d, long now, long lookahead = Lookahead)
    {
        var n = d.Update(now, now + lookahead, _actions);
        return [.. _actions.AsSpan(0, n).ToArray()];
    }

    private static int Section(MusicDirector d, string id) => d.Plan.FindSection(id);

    [Fact]
    public void PlayStartsAtTheStartAndFollowsAuthoredContinuationsAndLoops()
    {
        var d = Director();
        d.Play();
        var start = Assert.Single(Update(d, 0));
        Assert.Equal((MusicActionKind.StartSection, 0L, Section(d, "intro"), Section(d, "explore")), (start.Kind, start.Frame, start.Section, start.Then));

        Assert.Empty(Update(d, 2 * Bar)); // continuations need no action: the player follows them itself
        Assert.Equal(Section(d, "explore"), d.CurrentSection);
        Assert.Equal(2 * Bar, d.PassStart);

        Update(d, 2 * Bar + 4 * Bar * 1000 + 5);
        Assert.Equal(Section(d, "explore"), d.CurrentSection);
        Assert.Equal(2 * Bar + 4 * Bar * 1000, d.PassStart); // whole passes: no drift
    }

    [Fact]
    public void ARuleSchedulesTheFirstBoundaryAfterTheLookahead()
    {
        var d = Director(WithFade(Rule("explore", "combat", "Combat"), 1));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        var a = Assert.Single(Update(d, 30000));
        Assert.Equal(MusicActionKind.StartSection, a.Kind);
        Assert.Equal(Bar, a.Frame);
        Assert.Equal(Section(d, "combat"), a.Section);
        Assert.Equal(Section(d, "combat"), a.Then); // combat loops
        Assert.Equal(Beat, a.FadeOutFrames);
        Assert.False(a.Tails); // mid-section
        Assert.Equal((Section(d, "combat"), Bar), (d.PendingSection, d.PendingFrame));

        Assert.Empty(Update(d, 40000)); // still wanted: nothing new
        Update(d, Bar);
        Assert.Equal(Section(d, "combat"), d.CurrentSection);
        Assert.Equal(Bar, d.PassStart);
        Assert.Equal(-1, d.PendingSection);
    }

    [Fact]
    public void ABoundaryInsideTheLookaheadMovesToTheNextQuantum()
    {
        var d = Director(Rule("explore", "combat", "Combat"));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        var a = Assert.Single(Update(d, Bar - 1000));
        Assert.Equal(2 * Bar, a.Frame);
        Assert.Equal(Beat / 2, a.FadeOutFrames); // the mid-section default: half a beat
    }

    [Fact]
    public void APendingTransitionIsCancelledWhenItsRuleStopsMatchingUnlessCommitted()
    {
        var d = Director(Rule("explore", "combat", "Combat"));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        var scheduled = Assert.Single(Update(d, 10000));
        d.Parameters.SetLabel("area", "Calm");
        var cancel = Assert.Single(Update(d, 20000));
        Assert.Equal((MusicActionKind.Cancel, scheduled.Id), (cancel.Kind, cancel.CancelId));
        Assert.Equal(-1, d.PendingSection);

        // Committed: the boundary is before the earliest schedulable frame, so a change of mind comes too late.
        d.Parameters.SetLabel("area", "Combat");
        var again = Assert.Single(Update(d, 30000));
        d.Parameters.SetLabel("area", "Calm");
        Assert.Empty(Update(d, again.Frame - 100));
        Update(d, again.Frame);
        Assert.Equal(Section(d, "combat"), d.CurrentSection);
    }

    [Fact]
    public void AnEarlierRuleReplacesAPendingLaterOne()
    {
        var toBoss = Rule("explore", "boss", "Boss", MusicQuantize.SectionEnd);
        var toCombat = new MusicTransitionInfo
        {
            From = ["explore"],
            To = "combat",
            Quantize = MusicQuantize.Beat,
            When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "intensity", Op = MusicCompare.Greater, Value = new MusicValue(0.5f) }] },
        };
        var d = Director(toCombat, toBoss);
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Boss");
        var boss = Assert.Single(Update(d, 1000));
        Assert.Equal((Section(d, "boss"), 4 * Bar, true), (boss.Section, boss.Frame, boss.Tails)); // section end: tails, no fade
        Assert.Equal(0, boss.FadeOutFrames);
        Assert.Equal(1, d.PendingRule);

        d.Parameters.SetTarget(1, 0.8f, immediate: true);
        var actions = Update(d, 2000);
        Assert.Equal(2, actions.Count);
        Assert.Equal((MusicActionKind.Cancel, boss.Id), (actions[0].Kind, actions[0].CancelId));
        Assert.Equal((MusicActionKind.StartSection, Section(d, "combat"), Beat), (actions[1].Kind, actions[1].Section, actions[1].Frame));
        Assert.Equal(0, d.PendingRule);
    }

    [Fact]
    public void ExitMarkerWithASegmentPlaysTheSegmentThenTheDestination()
    {
        var rule = Rule("combat", "boss", "Boss", MusicQuantize.ExitMarker);
        rule.Segment = "toboss";
        var d = Director(rule);
        d.Play(Section(d, "combat"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Boss");
        var a = Assert.Single(Update(d, 10000));
        Assert.Equal((Section(d, "toboss"), 2 * Bar, Section(d, "boss"), false), (a.Section, a.Frame, a.Then, a.Tails));
        Assert.Equal(0, a.FadeOutFrames); // an exit marker's default

        Update(d, 2 * Bar);
        Assert.Equal(Section(d, "toboss"), d.CurrentSection);
        Update(d, 3 * Bar);
        Assert.Equal(Section(d, "boss"), d.CurrentSection);
        Assert.Equal(3 * Bar, d.PassStart);
        Assert.True(d.Plan.Sections[d.CurrentSection].Loops);
        Assert.Equal(d.CurrentSection, d.CurrentThen);
    }

    [Fact]
    public void AStingerOnARuleLandsItsDownbeatOnTheBoundary()
    {
        var rule = Rule("combat", "explore", "Calm");
        rule.Stinger = "victory";
        var d = Director(rule);
        d.Parameters.SetLabel("area", "Combat"); // the default (Calm) would leave combat at once
        d.Play(Section(d, "combat"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Calm");

        // At 80 000 the next bar (96 000) leaves no room for the beat of pickup after the lookahead: bar 2 it is.
        var actions = Update(d, 80000);
        var start = actions.Single(a => a.Kind == MusicActionKind.StartSection);
        var stinger = actions.Single(a => a.Kind == MusicActionKind.Stinger);
        Assert.Equal(2 * Bar, start.Frame);
        Assert.Equal((Section(d, "victory"), 2 * Bar - Beat), (stinger.Section, stinger.Frame));

        // Cancelling the transition cancels its stinger too.
        d.Parameters.SetLabel("area", "Combat");
        var cancels = Update(d, 90000);
        Assert.Equal([start.Id, stinger.Id], cancels.Select(c => c.CancelId).Order());
    }

    [Fact]
    public void TriggerStingerQuantizesToItsBeatWithPickup()
    {
        var d = Director();
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.TriggerStinger(Section(d, "victory"));
        var s = Assert.Single(Update(d, 30000));
        // Earliest 34 800 + 24 000 pickup = 58 800 → beat 3 (72 000), so the stinger starts a beat earlier.
        Assert.Equal((MusicActionKind.Stinger, 48000L), (s.Kind, s.Frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.TriggerStinger(Section(d, "explore")));
    }

    [Fact]
    public void SyncEntryKeepsTheOffsetIntoTheSection()
    {
        var rule = Rule("explore", "boss", "Boss");
        rule.Entry = MusicEntry.Sync;
        var d = Director(rule);
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Boss");
        var a = Assert.Single(Update(d, Bar + 5000));
        Assert.Equal((2 * Bar, 2 * Bar), (a.Frame, a.EntryOffset));
        Update(d, 2 * Bar);
        Assert.Equal(0, d.PassStart); // boss bar 3 plays at music frame 2 bars: its pass started at 0
    }

    [Fact]
    public void ALoopingSectionSchedulesIntoItsNextPass()
    {
        var d = Director(Rule("explore", "combat", "Combat"));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        var a = Assert.Single(Update(d, 4 * Bar - 100));
        Assert.Equal(4 * Bar + Bar, a.Frame); // the end is inside the lookahead: the next pass's first bar
    }

    [Fact]
    public void ANonLoopingSectionLeavesRulesToItsSuccessorPastItsEnd()
    {
        var d = Director(Rule("intro", "combat", "Combat"));
        d.Play();
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        Assert.Empty(Update(d, 2 * Bar - 100)); // the intro's end is inside the lookahead and nothing follows in it
        Assert.Equal(-1, d.PendingSection);
        Update(d, 2 * Bar);
        Assert.Equal(Section(d, "explore"), d.CurrentSection);
    }

    [Fact]
    public void AMissedTransitionRollsBackAndIsScheduledAgain()
    {
        var d = Director(Rule("explore", "combat", "Combat"));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.Parameters.SetLabel("area", "Combat");
        var a = Assert.Single(Update(d, 10000));
        Update(d, Bar + 100); // predicted: combat started at bar 1
        Assert.Equal(Section(d, "combat"), d.CurrentSection);

        d.OnMissed(a.Id); // the audio thread says it did not happen
        Assert.Equal(Section(d, "explore"), d.CurrentSection);
        var again = Assert.Single(Update(d, Bar + 200));
        Assert.Equal(2 * Bar, again.Frame);
        Assert.NotEqual(a.Id, again.Id);
    }

    [Fact]
    public void GoToSectionOverridesRulesAndStopEndsTheMusic()
    {
        var d = Director(Rule("explore", "combat", "Combat"));
        d.Play(Section(d, "explore"));
        Update(d, 0);
        d.GoToSection(Section(d, "boss"), MusicQuantize.Beat, fadeOutBeats: 2);
        d.Parameters.SetLabel("area", "Combat");
        var go = Assert.Single(Update(d, 1000));
        Assert.Equal((Section(d, "boss"), Beat, 2 * Beat), (go.Section, go.Frame, go.FadeOutFrames));
        Assert.Empty(Update(d, 2000)); // the matching rule does not replace a GoToSection

        d.Stop(MusicQuantize.Bar, fadeBeats: 1);
        var actions = Update(d, 3000);
        Assert.Equal(MusicActionKind.Cancel, actions[0].Kind);
        Assert.Equal((MusicActionKind.Stop, Bar, Beat), (actions[1].Kind, actions[1].Frame, actions[1].FadeOutFrames));
        Update(d, Bar);
        Assert.False(d.IsPlaying);
        Assert.True(d.IsFinished);
    }

    [Fact]
    public void SeekMovesLayerGainsOverTime()
    {
        var d = Director();
        d.Play(Section(d, "explore"));
        Update(d, 0);
        Assert.Equal(-80f, d.LayerGainDb(1));
        var version = d.LayerVersion;
        d.Parameters.SetTarget(1, 1);
        Update(d, 48000); // one second at 0.5 per second
        Assert.Equal(0.5f, d.Parameters.GetValue(1), 4);
        Assert.Equal(-40f, d.LayerGainDb(1), 3);
        Assert.Equal(0f, d.LayerGainDb(0));
        Assert.True(d.LayerVersion > version);
    }

    [Fact]
    public void UpdateGranularityDoesNotChangeTheDecisions()
    {
        // The editor's preview updates every 256-frame block with no lookahead; a game every 800-frame frame. With the
        // same parameter script, both start the same sections on the same frames.
        static List<(long, int)> Run(long step)
        {
            var d = Director(Rule("explore", "combat", "Combat"), Rule("combat", "explore", "Calm", MusicQuantize.SectionEnd));
            var buffer = new MusicAction[MusicDirector.MaxActionsPerUpdate];
            var starts = new List<(long, int)>();
            d.Play(d.Plan.FindSection("explore"));
            for (long now = 0; now < 30 * Bar; now += step)
            {
                if (now == 76800 * 3)
                    d.Parameters.SetLabel("area", "Combat");
                if (now == 76800 * 20)
                    d.Parameters.SetLabel("area", "Calm");
                var n = d.Update(now, now, buffer);
                foreach (var a in buffer.AsSpan(0, n))
                {
                    if (a.Kind == MusicActionKind.StartSection)
                        starts.Add((a.Frame, a.Section));
                }
            }

            return starts;
        }

        var block = Run(256);
        Assert.Equal(3, block.Count);
        Assert.Equal(block, Run(800));
    }

    [Fact]
    public void UpdatesDoNotAllocate()
    {
        var d = Director(Rule("explore", "combat", "Combat"), Rule("combat", "explore", "Calm"));
        d.Play(Section(d, "explore"));
        long now = 0;
        void Window()
        {
            for (var i = 0; i < 2000; i++)
            {
                now += 800;
                if (i % 50 == 0)
                    d.Parameters.SetLabel("area", (i / 50) % 2 == 0 ? "Combat" : "Calm");
                d.Parameters.SetTarget(1, i % 7 / 7f);
                if (i % 333 == 0)
                    d.TriggerStinger(5);
                d.Update(now, now + Lookahead, _actions);
            }
        }

        Window(); // warm up
        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
    }

    private static MusicTransitionInfo WithFade(MusicTransitionInfo rule, float beats)
    {
        rule.FadeOutBeats = beats;
        return rule;
    }
}
