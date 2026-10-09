using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>The audio nodes: wiring, the stream emitters, the bird schedule, footsteps, settings and 0 B per frame.</summary>
[Collection(AudioCollection.Name)]
public sealed class ForestAudioTests
{
    [Fact]
    public void AttachWiresEveryPart()
    {
        using var h = new AudioHarness();
        var river = h.AddRiver();
        var player = h.Controller.AddPlayer(new Vector3(-8, 0, 0));
        var audio = ForestAudio.Attach(h.Scene, river, player, [new Vector3(0, 0, -20)]);
        h.Run(2);

        Assert.NotNull(audio.Ambience);
        Assert.True(audio.Ambience!.Roar!.Playing && audio.Ambience.Rustle!.Playing);
        Assert.Same(river, audio.Stream!.River);
        Assert.True(audio.Stream.Near!.Playing && audio.Stream.Far!.Playing);
        Assert.Single(audio.Stream.Falls);
        Assert.True(audio.Stream.Falls[0].Playing);
        Assert.NotNull(audio.Birds);
        Assert.Same(player, audio.Footsteps!.Parent);
        Assert.Equal(ForestAudio.FoleyBus, audio.Footsteps.Players[0].Bus);
        Assert.Equal(ForestAudio.WaterBus, audio.Stream.Near.Bus);
        Assert.Equal(ForestAudio.AmbienceBus, audio.Ambience.Roar.Bus);
        Assert.Equal(6, audio.Birds!.Pool.Count);
    }

    [Fact]
    public void AForestAudioNodeInASceneFindsTheRiverAndThePlayer()
    {
        using var h = new AudioHarness();
        var river = h.AddRiver();
        var player = h.Controller.AddPlayer(new Vector3(-8, 0, 0));
        var audio = new ForestAudio { Name = "Audio" };
        h.Scene.AddChild(audio);
        Assert.Same(river, audio.River);
        Assert.Same(player, audio.Player);
        Assert.NotNull(audio.Footsteps);
    }

    [Fact]
    public void TheBusLayoutHasTheForestBusesAtCalmLevels()
    {
        var layout = AudioHarness.LoadLayout();
        Assert.Null(layout.Validate());
        var names = layout.Buses.Select(b => b.Name).ToArray();
        Assert.Equal(["Master", "Ambience", "Water", "Foley", "Music", "SFX", "UI"], names);
        foreach (var bus in new[] { "Ambience", "Water", "Foley" })
        {
            var info = layout.Buses.Single(b => b.Name == bus);
            Assert.Equal("Master", info.Send);
            Assert.InRange(info.VolumeDb, -12f, -4f);
        }

        Assert.IsType<AudioEffectReverb>(Assert.Single(layout.Buses.Single(b => b.Name == "Foley").Effects));
    }

    [Fact]
    public void SettingsScaleTheBusesOverTheLayout()
    {
        using var h = new AudioHarness();
        var audio = ForestAudio.Attach(h.Scene, null, null, settings: new ForestAudioSettings());
        var ambience = h.Server.GetBus("Ambience")!;
        var layoutDb = ambience.VolumeDb;
        audio.ApplySettings(new ForestAudioSettings { AmbienceVolume = 0.5f, FoleyVolume = 0f });
        Assert.Equal(layoutDb - 6.02f, ambience.VolumeDb, 2);
        Assert.True(h.Server.GetBus("Foley")!.Mute);
        audio.ApplySettings(new ForestAudioSettings());
        Assert.Equal(layoutDb, ambience.VolumeDb, 4);
        Assert.False(h.Server.GetBus("Foley")!.Mute);

        // Saved with the player's other settings.
        var path = Path.Combine(Path.GetTempPath(), $"forest-settings-{Guid.NewGuid():N}.json");
        try
        {
            new ForestSettings { Audio = { WaterVolume = 0.25f } }.Save(path);
            Assert.Equal(0.25f, ForestSettings.LoadOrDefault(path).Audio.WaterVolume);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheWindFollowsTheEnvironment()
    {
        using var h = new AudioHarness();
        var environment = new WorldEnvironment { WindStrength = 0f };
        h.Scene.AddChild(environment);
        var audio = ForestAudio.Attach(h.Scene, null, null);
        h.Run(1);
        var ambience = audio.Ambience!;
        var calmRoar = ambience.Roar!.VolumeDb;
        var calmRustle = ambience.Rustle!.VolumeDb;

        environment.WindStrength = 1f;
        h.RunSeconds(20f);
        Assert.InRange(ambience.WindStrength, 0.98f, 1f);
        Assert.Equal(calmRoar + ambience.CalmRoarDropDb, ambience.Roar.VolumeDb, 1);
        Assert.Equal(calmRustle + ambience.CalmRustleDropDb, ambience.Rustle.VolumeDb, 1);
        Assert.True(ambience.Roar.PitchScale > 1f);

        // Eased, not stepped: one second after a drop it is still on its way.
        environment.WindStrength = 0f;
        h.RunSeconds(1f);
        Assert.InRange(ambience.WindStrength, 0.4f, 0.8f);
    }

    [Fact]
    public void TheBrookFollowsTheNearestPointOfTheRiver()
    {
        using var h = new AudioHarness();
        var river = h.AddRiver();
        var audio = ForestAudio.Attach(h.Scene, river, null);
        h.Run(1);
        var stream = audio.Stream!;

        // Standing 10 m east of the river at z = 5: the near emitter is on the east bank (3 m out), at z ≈ 5.
        stream.UpdateEmitters(new Vector3(10, 1.6f, 5), 0f);
        var near = stream.Near!.GlobalPosition;
        Assert.Equal(3f, near.X, 2);
        Assert.Equal(5f, near.Z, 1);
        Assert.InRange(near.Y, -1f, 0f); // on the surface
        Assert.Equal(20f, stream.NearOffset, 1);
        // The far emitter is 12 m further along, towards the longer end.
        var farCentre = river.Curve!.SampleBaked(32f) with { Y = river.SurfaceHeightAtOffset(32f) };
        Assert.InRange(Vector3.Distance(farCentre, stream.Far!.GlobalPosition), 2.95f, 3.05f);

        // Standing on the centreline: the emitter is under the listener.
        stream.UpdateEmitters(new Vector3(0, 1.6f, -10), 0f);
        Assert.Equal(0f, stream.Near.GlobalPosition.X, 3);
        Assert.Equal(-10f, stream.Near.GlobalPosition.Z, 1);
    }

    [Fact]
    public void TheEmitterGlidesAtMostThirtyMetresPerSecond()
    {
        using var h = new AudioHarness();
        var river = h.AddRiver(length: 200f);
        var audio = ForestAudio.Attach(h.Scene, river, null);
        h.Run(1);
        var stream = audio.Stream!;
        stream.UpdateEmitters(new Vector3(5, 0, 90), 0f);
        var start = stream.Near!.GlobalPosition;

        // The listener jumps 150 m downstream: the sound follows at 30 m/s, not at once.
        const float dt = 1f / 60f;
        stream.UpdateEmitters(new Vector3(5, 0, -60), dt);
        Assert.Equal(StreamAudio.MaxEmitterSpeed * dt, Vector3.Distance(start, stream.Near.GlobalPosition), 3);
        for (var i = 0; i < 400; i++)
            stream.UpdateEmitters(new Vector3(5, 0, -60), dt);
        Assert.Equal(-60f, stream.Near.GlobalPosition.Z, 1);
    }

    [Fact]
    public void FasterWaterIsLouder()
    {
        float LevelFor(float drop)
        {
            using var h = new AudioHarness();
            var river = h.AddRiver(drop);
            var audio = ForestAudio.Attach(h.Scene, river, null);
            h.Run(1);
            audio.Stream!.UpdateEmitters(new Vector3(5, 0, 0), 0f);
            Assert.InRange(audio.Stream.Near!.VolumeDb, audio.Stream.StreamVolumeDb - 6f, audio.Stream.StreamVolumeDb + 6f);
            return audio.Stream.Near.VolumeDb;
        }

        Assert.True(LevelFor(6f) > LevelFor(0.2f) + 3f);
    }

    [Fact]
    public void SteepStretchesAreFound()
    {
        using var h = new AudioHarness();
        // Flat for 20 m, a 3 m drop over 4 m, flat again.
        var curve = new Curve3D { BakeInterval = 0.25f };
        curve.AddPoint(new Vector3(0, 0, 0));
        curve.AddPoint(new Vector3(0, 0, -20));
        curve.AddPoint(new Vector3(0, -3, -24));
        curve.AddPoint(new Vector3(0, -3, -44));
        for (var i = 0; i < curve.PointCount; i++)
            curve.SetPointWidth(i, 4f);
        var river = new River3D { Curve = curve };
        h.Scene.AddChild(river);
        var points = StreamAudio.FindSteepPoints(river);
        var fall = Assert.Single(points);
        Assert.InRange(fall.Z, -24.5f, -19.5f);
    }

    [Fact]
    public void BirdsSingFromTheCanopyOnAPoissonSchedule()
    {
        using var h = new AudioHarness();
        var listener = new Vector3(3, 1.6f, -2);
        h.AddListener(listener);
        var audio = ForestAudio.Attach(h.Scene, null, null);
        var birds = audio.Birds!;
        BirdSpecies? previous = null;
        var counts = new int[BirdSynth.AllSpecies.Length];
        var seen = 0;
        h.RunSeconds(240f, _ =>
        {
            if (birds.Triggered == seen)
                return;
            seen = birds.Triggered;
            var species = birds.LastSpecies!.Value;
            Assert.NotEqual(previous, species); // never the same species twice in a row
            previous = species;
            counts[(int)species]++;
            var offset = birds.LastPosition - listener;
            Assert.InRange(new Vector2(offset.X, offset.Z).Length(), birds.MinDistance - 0.01f, birds.MaxDistance + 0.01f);
            Assert.InRange(offset.Y, birds.MinHeight, birds.MaxHeight);
        });

        // ≈ 240 s / 4 s, give or take the Poisson spread; songs are short, so the pool of six is rarely full.
        Assert.InRange(birds.Triggered + birds.Skipped, 40, 85);
        Assert.True(birds.Skipped <= 2, $"{birds.Skipped} skipped");
        Assert.All(counts, c => Assert.True(c > 0, string.Join(", ", counts)));
        Assert.True(counts[(int)BirdSpecies.Woodpecker] < counts[(int)BirdSpecies.Warbler]); // rare outside the pines
    }

    [Fact]
    public void BirdScheduleIsDeterministic()
    {
        static string Run()
        {
            using var h = new AudioHarness();
            var birds = new BirdSongs { Seed = 99 };
            h.Scene.AddChild(birds);
            var log = new System.Text.StringBuilder();
            for (var i = 0; i < 60 * 60; i++)
            {
                var before = birds.Triggered + birds.Skipped;
                birds.Advance(1f / 60f, Vector3.Zero);
                if (birds.Triggered + birds.Skipped != before)
                    log.Append(i).Append(':').Append(birds.LastSpecies).Append(' ');
            }

            return log.ToString();
        }

        var a = Run();
        Assert.Equal(a, Run());
        Assert.True(a.Length > 40);
    }

    [Fact]
    public void DensePinesQuietenSongbirdsAndBringTheWoodpecker()
    {
        using var h = new AudioHarness();
        var birds = new BirdSongs { PineDensity = _ => 1f, VolumeSpreadDb = 0f, MeanInterval = 1f };
        h.Scene.AddChild(birds);
        var woodpeckers = 0;
        for (var i = 0; i < 200; i++)
        {
            foreach (var p in birds.Pool)
                p.Stop();
            birds.Trigger(Vector3.Zero);
            var player = birds.Pool.First(p => p.Playing);
            if (birds.LastSpecies == BirdSpecies.Woodpecker)
            {
                woodpeckers++;
                Assert.Equal(birds.VolumeDb, player.VolumeDb, 3);
            }
            else
            {
                Assert.Equal(birds.VolumeDb - birds.PineQuietDb, player.VolumeDb, 3);
            }
        }

        // Weight 2 of 5 (it never follows itself): about a third of the birds.
        Assert.InRange(woodpeckers, 40, 100);
    }

    [Fact]
    public void FootstepsPlayTheSurfaceSetWithoutRepeats()
    {
        using var h = new AudioHarness();
        var player = h.Controller.AddPlayer();
        var audio = ForestAudio.Attach(h.Scene, null, player);
        h.Run(1);
        var steps = audio.Footsteps!;
        var bank = ForestSoundBank.Shared;
        AudioStream? last = null;
        foreach (var surface in ForestSynth.FootstepSurfaces.Append("leaf_litter").Append("unknown"))
        {
            for (var i = 0; i < 12; i++)
            {
                steps.OnFootstep(surface);
                Assert.Contains(steps.LastStream!, bank.Footsteps(surface));
                Assert.NotSame(last, steps.LastStream);
                last = steps.LastStream;
                h.Frame();
            }
        }

        // The controller's signal drives it: walk a few strides.
        var before = (steps.Played, player.FootstepCount);
        h.Controller.Hold("move_forward");
        h.RunSeconds(3f);
        Assert.Equal(player.FootstepCount - before.FootstepCount, steps.Played - before.Played);
        Assert.True(steps.Played - before.Played >= 8);
    }

    [Fact]
    public void FramesAllocateNothing()
    {
        using var h = new AudioHarness();
        var river = h.AddRiver(length: 120f);
        h.Scene.AddChild(new WorldEnvironment { WindStrength = 0.6f });
        var player = h.Controller.AddPlayer(new Vector3(-6, 0, 40));
        var audio = ForestAudio.Attach(h.Scene, river, player, [new Vector3(0, -0.5f, 0)]);
        audio.Birds!.MeanInterval = 0.5f; // plenty of triggers in the window
        ForestSoundBank.Shared.Preload();

        void Walk(int tick)
        {
            h.Controller.Hold("move_forward");
            if (tick % 90 == 0)
                player.AddLook(25f, 0f);
            if (tick % 20 == 0)
                audio.Footsteps!.OnFootstep(ForestSynth.FootstepSurfaces[tick / 20 % ForestSynth.FootstepSurfaces.Length]);
        }

        Action<int> walk = Walk;
        h.Run(240, walk); // warm up: every voice, set and pooled player used once
        var triggered = audio.Birds.Triggered;
        var played = audio.Footsteps!.Played;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        h.Run(600, walk);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.True(audio.Birds.Triggered - triggered > 5);
        Assert.True(audio.Footsteps.Played - played > 30);
        Assert.Equal(0, allocated);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioCollection
{
    public const string Name = "Audio";
}
