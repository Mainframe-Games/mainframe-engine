using MainframeEngine.Audio.Music;
using static MainframeEngine.Tests.Audio.Music.MusicTestStems;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// The adaptive-music allocation gate: an <see cref="AdaptiveMusicPlayer"/> with four layers (streamed sections, a
/// memory transition segment and boss section, tails, a tail point, filters) goes through transitions, stingers, parameter sweeps,
/// misses (the streaming thread held) and tree pause/resume, and a steady-state frame allocates nothing on the game
/// thread (tree tick + <see cref="AudioServer.Process"/>) or the audio thread (here the same thread, rendering the null
/// device).
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AdaptiveMusicAllocationTests
{
    private static readonly string[] Layers = ["base", "perc", "combat", "pad"];

    [Fact]
    public void AdaptiveMusicFramesDoNotAllocate()
    {
        using var stems = new MusicTestStems();
        var music = AdaptiveMusic.FromManifest(BuildManifest(stems));
        music.PreloadSections.Add("boss");
        Assert.True(music.Preload());
        Assert.Empty(music.Plan.Problems);

        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AdaptiveMusicPlayer { Music = music, Autoplay = true };
        tree.ChangeScene(player);
        var signals = 0;
        player.Beat += (_, _) => signals++;
        player.SectionStarted += _ => signals++;
        var warmingUp = true;
        var sections = new HashSet<string>();
        player.SectionStarted += s =>
        {
            if (warmingUp)
                sections.Add(s);
        };
        player.Marker += _ => signals++;
        player.TransitionScheduled += (_, _) => signals++;
        string[] areas = ["Calm", "Combat", "Boss", "Combat"];
        var buffer = new float[800 * 2];

        void Frame(int i)
        {
            player.SetParameter("intensity", 0.5f + 0.5f * MathF.Sin(i * 0.05f)); // parameter sweeps
            if (i % 70 == 0)
                player.SetParameter("area", areas[i / 70 % areas.Length]); // transitions, a segment, a stinger on the way back
            if (i % 45 == 0)
                player.TriggerStinger("hit");
            if (i % 110 == 100)
                tree.Paused = true;
            if (i % 110 == 104)
                tree.Paused = false;
            server.HoldStreaming = i % 280 is >= 200 and < 230; // starts into streamed sections miss
            tree.Tick(AudioTestUtil.Frame);
            server.RenderNullDevice(800, buffer);
        }

        for (var i = 0; i < 840; i++) // warm-up: JIT, every lane group, both kinds of start, misses
        {
            Frame(i);
            if (i % 10 == 0)
                Thread.Sleep(1); // let the streaming thread keep up
        }

        Assert.True(player.Playing);
        Assert.Equal(["boss", "combat", "explore", "toboss"], sections.Order(StringComparer.Ordinal));
        warmingUp = false;
        var lateBefore = server.Stats.LateMusicTransitions;
        var next = 840;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var end = next + 280; next < end; next++)
                Frame(next);
        });

        server.HoldStreaming = false;
        Assert.Equal(0, allocated);
        Assert.True(player.Playing);
        Assert.True(signals > 100);
        Assert.True(lateBefore > 0, "the warm-up should include a miss");
        Assert.False(server.Stats.Faulted);
        tree.Shutdown();
    }

    private static MusicManifest BuildManifest(MusicTestStems stems)
    {
        (string, MusicStemRef?, MusicStemRef?)[] Layered(int bars, float dc) =>
            [.. Layers.Select((layer, i) => (layer, (MusicStemRef?)stems.Body(bars, dc * (i + 1), 0.5f, channels: 2), (MusicStemRef?)stems.Tail(2400, 0.2f)))];

        var manifest = Manifest(
            Section("explore", 2, stems: Layered(2, 0.02f)),
            Section("combat", 2, stems: Layered(2, 0.03f)),
            Section("toboss", 1, loop: false, kind: MusicSectionKind.Transition, stems: Layered(1, 0.01f)),
            Section("boss", 1, stems: Layered(1, 0.04f)),
            Section("hit", 1, loop: false, kind: MusicSectionKind.Stinger, stems: (MusicManifestSection.MixStem, stems.Impulse((int)Bar, (int)Beat, 0.3f), null)));
        manifest.Sections[1].Markers.Add(new MusicManifestMarker
        {
            Name = "drop",
            Frame = Bar,
            Exit = true,
            Tails = Layers.ToDictionary(layer => layer, MusicStemRef? (_) => stems.Tail(1800, 0.1f)), // a tail point: combat → boss leaves here
        });
        manifest.Sections[4].PickupBeats = 1;
        manifest.Layers =
        [
            new MusicManifestLayer { Id = "base" },
            new MusicManifestLayer { Id = "perc" },
            new MusicManifestLayer { Id = "combat", Filter = MusicLayerFilter.LowPass },
            new MusicManifestLayer { Id = "pad", Filter = MusicLayerFilter.HighPass },
        ];
        manifest.Parameters =
        [
            new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1, SeekSpeed = 2 },
            new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat", "Boss"] },
        ];
        manifest.Curves =
        [
            new MusicCurveInfo { Layer = "perc", Parameter = "intensity", Shape = MusicCurveShape.Smooth, Points = [new(0, -80), new(0.5f, 0)] },
            new MusicCurveInfo { Layer = "combat", Parameter = "intensity", Points = [new(0.3f, -80), new(1, 0)] },
            new MusicCurveInfo { Layer = "combat", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 300), new(1, 20000)] },
            new MusicCurveInfo { Layer = "pad", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 20), new(1, 2000)] },
        ];
        manifest.Transitions =
        [
            new MusicTransitionInfo { From = ["explore"], To = "combat", When = MusicLogicTests.When("area", "Combat"), FadeOutBeats = 1 },
            new MusicTransitionInfo
            {
                From = ["combat"], To = "boss", When = MusicLogicTests.When("area", "Boss"), Quantize = MusicQuantize.ExitMarker, Segment = "toboss",
            },
            new MusicTransitionInfo { From = ["boss"], To = "combat", When = MusicLogicTests.When("area", "Combat"), Quantize = MusicQuantize.Beat },
            new MusicTransitionInfo
            {
                From = ["*"], To = "explore", When = MusicLogicTests.When("area", "Calm"), Stinger = "hit", FadeOutBeats = 0.5f, FadeInBeats = 0.5f,
            },
        ];
        return manifest;
    }
}
