using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;
using static MainframeEngine.Tests.Audio.Music.MusicTestStems;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// The adaptive-music runtime on the manual null device (deterministic): synthetic stems prove that sections, loops,
/// transitions and stingers land on their exact output sample, tails overlap what follows, fades are linear, layer
/// gains ramp without zipper, the clock holds while paused, resampled lanes stay aligned, and a lane that is not ready
/// makes the transition miss and land one quantum later.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AdaptiveMusicRuntimeTests : IDisposable
{
    private const float Tolerance = 1e-5f;

    private readonly MusicTestStems _stems = new();

    public void Dispose() => _stems.Dispose();

    private static MusicParameterInfo Area => new() { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] };

    private static MusicParameterInfo Intensity => new() { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1 };

    // Explore (2 bars, DC 0.25, impulse 1) → Combat (2 bars, DC 0.5, impulse 2) at the next bar once area = Combat.
    private MusicManifest ExploreCombat(float? fadeOutBeats = 0, int combatBars = 2)
    {
        var manifest = Manifest(
            Section("explore", 2, stems: ("base", _stems.Body(2, 0.25f, 1f), null)),
            Section("combat", combatBars, stems: ("base", _stems.Body(combatBars, 0.5f, 2f), null)));
        manifest.Parameters.Add(Area);
        manifest.Transitions.Add(new MusicTransitionInfo
        {
            From = ["explore"],
            To = "combat",
            When = MusicLogicTests.When("area", "Combat"),
            Quantize = MusicQuantize.Bar,
            FadeOutBeats = fadeOutBeats,
        });
        return manifest;
    }

    [Fact]
    public void FirstSectionStartsAtFrameZeroAndLoopsWithItsTailOverTheRestart()
    {
        var music = InMemory(Manifest(Section("a", 1, stems: ("base", _stems.Body(1, 0.25f, 1f), _stems.Tail(1200, 0.5f)))));
        using var rig = new MusicRig(music);
        rig.Instance.Play();
        rig.RunTo(3 * Bar);

        Assert.Equal(1f, rig[0], Tolerance);
        Assert.Equal(1f, rig.Right(0), Tolerance);
        Assert.Equal(0.25f, rig[1], Tolerance);
        Assert.Equal(0.25f, rig[Bar - 1], Tolerance);
        Assert.Equal(1.5f, rig[Bar], Tolerance); // the restart's impulse plus the tail's first frame
        Assert.Equal(0.25f + 0.5f * (1f - 1f / 1200), rig[Bar + 1], Tolerance);
        Assert.Equal(0.25f, rig[Bar + 1200], Tolerance); // the tail has rung out
        Assert.Equal(1.5f, rig[2 * Bar], Tolerance);
        Assert.Equal(["section a"], rig.Signals.Where(s => s.StartsWith("section", StringComparison.Ordinal)));
        Assert.Equal("a", rig.Instance.CurrentSection);
    }

    [Fact]
    public void RuleTransitionLandsExactlyOnTheNextBar()
    {
        using var rig = new MusicRig(InMemory(ExploreCombat()));
        rig.Instance.Play();
        rig.RunTo(2000);
        rig.Instance.SetParameter("area", "Combat");
        rig.RunTo(3 * Bar);

        Assert.Equal(0.25f, rig[Bar - 1], Tolerance);
        Assert.Equal(2f, rig[Bar], Tolerance); // combat's downbeat on the bar line, explore stopped there
        Assert.Equal(0.5f, rig[Bar + 1], Tolerance);
        Assert.Equal(0.5f, rig[2 * Bar], Tolerance); // mid-combat: no stray explore restart
        Assert.Equal("combat", rig.Instance.CurrentSection);
        Assert.Equal(["section explore", "section combat"], rig.Signals.Where(s => s.StartsWith("section", StringComparison.Ordinal)));
        Assert.Equal(0, rig.Server.Stats.LateMusicTransitions);
    }

    [Fact]
    public void OutgoingSectionFadesLinearlyOverTheFadeBeats()
    {
        using var rig = new MusicRig(InMemory(ExploreCombat(fadeOutBeats: 1)));
        rig.Instance.Play();
        rig.RunTo(2000);
        rig.Instance.SetParameter("area", "Combat");
        rig.RunTo(2 * Bar);

        Assert.Equal(2f + 0.25f * (1f - 1f / Beat), rig[Bar], 1e-4f);
        var step = rig[Bar + 2] - rig[Bar + 1];
        Assert.Equal(-0.25f / Beat, step, 1e-6f);
        for (var k = 1; k < Beat - 1; k++)
            Assert.Equal(step, rig[Bar + k + 1] - rig[Bar + k], 1e-6f);
        Assert.Equal(0.5f + 0.25f * 0.5f, rig[Bar + Beat / 2 - 1], 1e-4f);
        Assert.Equal(0.5f, rig[Bar + Beat], Tolerance); // faded out after exactly one beat
    }

    [Fact]
    public void LayerGainsFollowTheirCurvesWithoutZipper()
    {
        var manifest = Manifest(Section("a", 4, stems: [("base", _stems.Body(4, 0.2f), null), ("perc", _stems.Body(4, 0.3f), null)]));
        manifest.Layers.Add(new MusicManifestLayer { Id = "perc", Name = "Perc" });
        manifest.Parameters.Add(Intensity);
        manifest.Curves.Add(new MusicCurveInfo { Layer = "perc", Parameter = "intensity", Points = [new(0, -80), new(1, 0)] });
        using var rig = new MusicRig(InMemory(manifest));
        rig.Instance.Play();
        rig.RunTo(4800);
        Assert.Equal(0.2f, rig[4000], Tolerance); // perc silent at intensity 0

        rig.Instance.SetParameter("intensity", 1f);
        rig.RunTo(4800 + 9600);
        var largest = 0f;
        for (var f = 4800; f < rig.Rendered - 1; f++)
            largest = Math.Max(largest, Math.Abs(rig[f + 1] - rig[f]));
        Assert.True(largest < 0.005f, $"gain step {largest} per sample");
        Assert.Equal(0.5f, rig[rig.Rendered - 1], 1e-3f);
    }

    [Fact]
    public void LayerFilterCutoffFollowsItsCurve()
    {
        var tone = new float[(int)(4 * Bar)];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 5000 * i / Rate);
        var manifest = Manifest(Section("a", 4, stems: ("perc", _stems.Write(tone, 1), null)));
        manifest.Layers = [new MusicManifestLayer { Id = "perc", Name = "Perc", Filter = MusicLayerFilter.LowPass }];
        manifest.Parameters.Add(Intensity);
        manifest.Curves.Add(new MusicCurveInfo
        {
            Layer = "perc",
            Target = MusicCurveTarget.Cutoff,
            Parameter = "intensity",
            Points = [new(0, 200), new(1, 20000)],
        });
        using var rig = new MusicRig(InMemory(manifest));
        rig.Instance.Play();
        rig.RunTo(9600);
        var closed = Rms(rig, 4800, 9600);
        rig.Instance.SetParameter("intensity", 1f);
        rig.RunTo(9600 + 14400);
        var open = Rms(rig, 9600 + 9600, 9600 + 14400);

        Assert.True(closed < 0.01f, $"closed low-pass RMS {closed}");
        Assert.Equal(0.5f / MathF.Sqrt(2), open, 0.03f);
    }

    [Fact]
    public void StingerDownbeatLandsOnTheBeatAfterItsPickup()
    {
        var manifest = Manifest(
            Section("a", 4, stems: ("base", _stems.Body(4, 0f), null)),
            Section("hit", 1, loop: false, kind: MusicSectionKind.Stinger, stems: (MusicManifestSection.MixStem, _stems.Impulse((int)Bar, (int)Beat, 1f), null)));
        manifest.Sections[1].PickupBeats = 1;
        manifest.Sections[1].Quantize = MusicQuantize.Beat;
        using var rig = new MusicRig(InMemory(manifest));
        rig.Instance.Play();
        rig.RunTo(10_000);
        rig.Instance.TriggerStinger("hit");
        rig.RunTo(2 * Bar);

        // Triggered at 10 000: the earliest is 14 800 (100 ms ahead); the pickup must fit after it, so the downbeat is
        // the beat at or after 20 800, frame 24 000, and the stinger starts one beat before.
        Assert.Equal(Bar, rig.FindAbove(0.5f));
        Assert.Equal(1f, rig[24_000], Tolerance);
    }

    [Fact]
    public void ThenContinuesAndTheEndRingsOutItsTailThenFinishes()
    {
        var manifest = Manifest(
            Section("intro", 1, loop: false, then: "main", stems: ("base", _stems.Body(1, 0.1f, 1f), null)),
            Section("main", 1, loop: false, stems: ("base", _stems.Body(1, 0.2f, 2f), _stems.Tail(1200, 0.5f))));
        using var rig = new MusicRig(InMemory(manifest));
        rig.Instance.Play();
        rig.RunTo(3 * Bar);

        Assert.Equal(0.1f, rig[Bar - 1], Tolerance);
        Assert.Equal(2f, rig[Bar], Tolerance);
        Assert.Equal(0.2f, rig[2 * Bar - 1], Tolerance);
        Assert.Equal(0.5f, rig[2 * Bar], Tolerance); // the end: only the tail
        Assert.Equal(0f, rig[2 * Bar + 1200], Tolerance);
        Assert.Equal(["section intro", "section main"], rig.Signals.Where(s => s.StartsWith("section", StringComparison.Ordinal)));
        Assert.Equal("finished", rig.Signals[^1]);
        Assert.False(rig.Instance.Playing);
    }

    [Fact]
    public void BarsBeatsAndMarkersFireOnceEachInOrderAcrossUnevenFrames()
    {
        var manifest = Manifest(Section("a", 2, stems: ("base", _stems.Body(2, 0f), null)));
        manifest.Sections[0].Markers.Add(new MusicManifestMarker { Name = "drop", Frame = 9000 });
        using var rig = new MusicRig(InMemory(manifest));
        rig.Instance.Play();
        foreach (var frames in new[] { 800, 30_000, 100, 13_100, 8000 })
            rig.Frame(frames);
        rig.Server.Process(AudioTestUtil.Frame); // the signals for the last block

        Assert.Equal(52_000, rig.Rendered);
        Assert.Equal(
        [
            "section a", "bar 1", "beat 1.1", "beat 1.2", "marker drop", "beat 1.3", "beat 1.4",
            "bar 2", "beat 2.1", "beat 2.2", "beat 2.3", "beat 2.4",
            "bar 1", "beat 1.1", // the loop restarts the count (no new SectionStarted)
        ], rig.Signals);
    }

    [Fact]
    public void PauseHoldsTheClockAndResumeContinuesWhereItStopped()
    {
        var music = InMemory(Manifest(Section("a", 1, stems: ("base", _stems.Body(1, 0.25f, 1f), null))));
        using var rig = new MusicRig(music);
        rig.Instance.Play();
        rig.RunTo(5000);
        rig.Instance.Paused = true;
        rig.Frame(800);
        rig.Frame(1200);
        Assert.All(Enumerable.Range(5000, 2000), f => Assert.Equal(0f, rig[f]));

        rig.Instance.Paused = false;
        rig.RunTo(2 * Bar);
        Assert.Equal(0.25f, rig[7000], Tolerance); // no restart on resume
        Assert.Equal(Bar + 2000, rig.FindAbove(0.9f, 5000)); // the next downbeat, 2 000 frames later than without the pause
    }

    [Fact]
    public void LayersResampledToTheDeviceRateStayAligned()
    {
        var tone = new float[(int)(2 * Bar)];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
        var inverted = Array.ConvertAll(tone, s => -s);
        var manifest = Manifest(Section("a", 2, stems: [("base", _stems.Write(tone, 1), null), ("perc", _stems.Write(inverted, 1), null)]));
        manifest.Layers.Add(new MusicManifestLayer { Id = "perc", Name = "Perc" });
        using var rig = new MusicRig(InMemory(manifest), sampleRate: 44100);
        rig.Instance.Play();
        rig.RunTo(44100);
        Assert.True(Rms(rig, 0, 44100) < 1e-5f);

        var single = Manifest(Section("a", 2, stems: ("base", _stems.Write(tone, 1), null)));
        using var reference = new MusicRig(InMemory(single), sampleRate: 44100);
        reference.Instance.Play();
        reference.RunTo(44100);
        Assert.Equal(0.4f / MathF.Sqrt(2), Rms(reference, 0, 44100), 0.01f);
    }

    [Fact]
    public void StreamedSectionsLoopSeamlesslyAndTransitionOnTheBar()
    {
        var music = AdaptiveMusic.FromManifest(ExploreCombat());
        Assert.True(music.Preload());
        using var rig = new MusicRig(music) { WaitForStreams = true };
        rig.Instance.Play();
        rig.RunTo(2 * Bar + 2000); // explore loops once
        rig.Instance.SetParameter("area", "Combat");
        rig.RunTo(4 * Bar);

        Assert.Equal(1f, rig[0], Tolerance);
        Assert.Equal(0.25f, rig[2 * Bar - 1], Tolerance);
        Assert.Equal(1f, rig[2 * Bar], Tolerance);
        Assert.Equal(0.25f, rig[3 * Bar - 1], Tolerance);
        Assert.Equal(2f, rig[3 * Bar], Tolerance);
        Assert.Equal(0.5f, rig[3 * Bar + 1], Tolerance);
        Assert.Equal(0, rig.Server.Stats.Underruns);
    }

    [Fact]
    public void ALaneThatIsNotReadyMissesAndTheTransitionLandsOneQuantumLater()
    {
        var music = AdaptiveMusic.FromManifest(ExploreCombat());
        music.PreloadSections.Add("explore"); // combat streams
        Assert.True(music.Preload());
        using var rig = new MusicRig(music);
        rig.Server.HoldStreaming = true;
        rig.Instance.Play();
        rig.RunTo(2000);
        rig.Instance.SetParameter("area", "Combat");
        rig.RunTo(Bar + 1600); // the start at bar 2 finds combat's stream empty
        rig.Frame(); // the miss is reported and rescheduled
        Assert.Equal(1, rig.Server.Stats.LateMusicTransitions);
        Assert.Equal("explore", rig.Instance.CurrentSection);

        rig.Server.HoldStreaming = false;
        rig.WaitForStreams = true;
        rig.RunTo(3 * Bar);
        Assert.Equal(0.25f, rig[Bar], Tolerance); // nothing happened at the missed bar
        Assert.Equal(0.25f, rig[2 * Bar - 1], Tolerance);
        Assert.Equal(2f, rig[2 * Bar], Tolerance); // exactly one quantum later
        Assert.Equal("combat", rig.Instance.CurrentSection);
    }

    [Fact]
    public void ALateCancelDoesNotUndoAnExecutedTransition()
    {
        using var rig = new MusicRig(InMemory(ExploreCombat()));
        rig.Instance.Play();
        rig.RunTo(2000);
        rig.Instance.SetParameter("area", "Combat");
        rig.RunTo(Bar + 4000);
        Assert.Equal("combat", rig.Instance.CurrentSection);

        for (var id = 1; id <= 32; id++)
        {
            rig.Server.EnqueueMusic(new AudioCommand
            {
                Type = AudioCommandType.MusicCancel,
                Index = rig.Instance.VoiceIndex,
                Generation = rig.Instance.VoiceGeneration,
                Music = new MusicCommand { Action = id },
            });
        }

        rig.RunTo(2 * Bar);
        Assert.Equal(0.5f, rig[2 * Bar - 1], Tolerance);
        Assert.Equal("combat", rig.Instance.CurrentSection);
    }

    [Fact]
    public void StopFadesOutWithoutFinished()
    {
        using var rig = new MusicRig(InMemory(ExploreCombat()));
        rig.Instance.Play();
        rig.RunTo(4000);
        rig.Instance.Stop(MusicQuantize.Beat, fadeBeats: 1);
        rig.RunTo(Bar);

        // Stop requested at 4 000: the earliest is 8 800, the next beat 12 000; the fade lasts one beat.
        Assert.Equal(0.25f, rig[11_999], Tolerance);
        Assert.Equal(0.125f, rig[12_000 + Beat / 2 - 1], 1e-4f);
        Assert.Equal(0f, rig[12_000 + Beat], Tolerance);
        Assert.False(rig.Instance.Playing);
        Assert.DoesNotContain("finished", rig.Signals);
    }

    [Fact]
    public void AMissingStemPlaysSilenceAndTheOtherLayersGoOn()
    {
        var manifest = Manifest(Section("a", 1, stems:
            [("base", new MusicStemRef { Path = Path.Combine(_stems.Folder, "nope.wav") }, null), ("perc", _stems.Body(1, 0.3f), null)]));
        manifest.Layers.Add(new MusicManifestLayer { Id = "perc", Name = "Perc" });
        var music = AdaptiveMusic.FromManifest(manifest);
        music.PreloadSections.Add("a");
        Assert.False(music.Preload());
        Assert.Equal(1, music.MissingStems);

        using var rig = new MusicRig(music);
        rig.Instance.Play();
        rig.RunTo(4000);
        Assert.Equal(0.3f, rig[3000], Tolerance);
    }

    [Fact]
    public void PlayFailsLikeARejectedVoiceWhenNoMusicVoiceIsFree()
    {
        var music = InMemory(Manifest(Section("a", 1, stems: ("base", _stems.Body(1, 0.1f), null))));
        using var rig = new MusicRig(music);
        Assert.Equal(AudioBusInfo.DefaultMusicVoices, rig.Server.GetBus("Music")!.MusicVoices);
        Assert.Equal(AudioBusInfo.DefaultMusicVoices, rig.Server.TotalMusicVoices);
        using var second = new AdaptiveMusicInstance(rig.Server, music);
        using var third = new AdaptiveMusicInstance(rig.Server, music);
        rig.Instance.Play();
        second.Play();
        third.Play();

        Assert.True(rig.Instance.Playing && second.Playing);
        Assert.False(third.Playing);
        Assert.Equal(1, rig.Server.Stats.Rejected);
    }

    private static float Rms(MusicRig rig, long from, long to)
    {
        double sum = 0;
        for (var f = from; f < to; f++)
            sum += rig[f] * (double)rig[f];
        return (float)Math.Sqrt(sum / Math.Max(1, to - from));
    }
}
