using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>The audio nodes: wiring, the indoor/outdoor mix, the interactables' sounds, the stereo and 0 B per frame.</summary>
[Collection(AudioCollection.Name)]
public sealed class NeonAudioTests
{
    [Fact]
    public void TheBusLayoutHasNeonsBuses()
    {
        var layout = AudioHarness.LoadLayout();
        Assert.Null(layout.Validate());
        Assert.Equal(["Master", "Ambience", "Rain", "Foley", "Bathroom", "Music", "Tv", "SFX", "UI"], layout.Buses.Select(b => b.Name));
        foreach (var bus in NeonAudio.Buses.Skip(1))
        {
            var info = layout.Buses.Single(b => b.Name == bus);
            Assert.Equal("Master", info.Send);
            Assert.InRange(info.VolumeDb, -12f, -4f);
        }

        var bathroom = Assert.IsType<AudioEffectReverb>(Assert.Single(layout.Buses.Single(b => b.Name == "Bathroom").Effects));
        var foley = Assert.IsType<AudioEffectReverb>(Assert.Single(layout.Buses.Single(b => b.Name == "Foley").Effects));
        Assert.True(bathroom.Wet > 2f * foley.Wet, "the tiled bathroom rings more than the rooms");
        Assert.True(layout.Buses.Single(b => b.Name == "Foley").MaxVoices >= NeonAudio.PoolSize + 6);

        var project = ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj"));
        Assert.Equal("Assets/Settings/AudioBusLayout.mres", project.Audio.BusLayout);
    }

    [Fact]
    public void TheNodeBuildsTheBedsThePoolAndTheFootsteps()
    {
        using var h = new AudioHarness();
        var player = h.Controller.AddPlayer();
        var audio = h.AddAudio();
        h.Run(2);

        Assert.Same(audio, NeonAudio.Current);
        Assert.Same(player, audio.Player);
        Assert.True(audio.RainOutdoor!.Playing && audio.RainIndoor!.Playing && audio.CityOutdoor!.Playing && audio.CityIndoor!.Playing);
        Assert.Equal(NeonAudio.RainBus, audio.RainIndoor.Bus);
        Assert.Equal(NeonAudio.AmbienceBus, audio.CityIndoor.Bus);
        Assert.Equal(NeonAudio.PoolSize, audio.Pool.Count);
        Assert.Same(player, audio.Footsteps!.Parent);
        Assert.NotNull(audio.Music);
        Assert.True(audio.FlyBy!.DopplerTracking);

        audio.Free();
        h.Run(1);
        Assert.Null(NeonAudio.Current);
        NeonAudio.PlayAt(NeonSound.SwitchClick, Vector3.Zero); // no audio in the tree: nothing happens
    }

    [Fact]
    public void InsideTheRainAndCityAreMuffledAndTheBalconyIsOpen()
    {
        using var h = new AudioHarness();
        var listener = h.AddListener(new Vector3(0, 1.6f, 0));
        var audio = h.AddAudio(); // no apartment: apartment space is world space
        h.Run(1);
        Assert.Equal(0f, audio.Openness);
        Assert.True(audio.RainIndoor!.VolumeDb > audio.RainOutdoor!.VolumeDb + 40f);
        Assert.True(audio.CityIndoor!.VolumeDb > audio.CityOutdoor!.VolumeDb + 40f);

        // Out onto the balcony: eased over a fraction of a second, then fully open.
        listener.Position = new Vector3(2, 1.6f, 6.8f);
        h.Run(6);
        Assert.InRange(audio.Openness, 0.1f, 0.9f);
        h.RunSeconds(2f);
        Assert.Equal(1f, audio.Openness, 2);
        Assert.True(audio.RainOutdoor.VolumeDb > audio.RainIndoor.VolumeDb + 40f);
        Assert.Equal(audio.RainVolumeDb, audio.RainOutdoor.VolumeDb, 2);

        audio.Update(0f);
        Assert.Equal(1f, audio.OpennessAt(new Vector3(0, 1.6f, 7f)));
        Assert.Equal(0f, audio.OpennessAt(new Vector3(-6.5f, 4.6f, -5f))); // up in bedroom 2
        Assert.Equal(1f, audio.OpennessAt(new Vector3(0, 1.6f, -9f))); // outside the box altogether
    }

    [Fact]
    public void CityEventsAndFlyBysAreDistantAndMuffledIndoors()
    {
        using var h = new AudioHarness();
        h.AddListener(new Vector3(0, 1.6f, 0));
        var audio = h.AddAudio(cityEvents: true);
        h.Run(1);
        audio.TriggerCityEvent(new Vector3(0, 1.6f, 0));
        audio.StartFlyBy(new Vector3(0, 1.6f, 0));
        h.Run(2);
        Assert.Equal(1, audio.CityEvents);
        Assert.Equal(1, audio.FlyBys);
        Assert.True(audio.FlyBy!.Playing);
        Assert.InRange(NeonAudio.EffectiveCutoff(audio.FlyBy, Vector3.Distance(audio.FlyBy.GlobalPosition, new Vector3(0, 1.6f, 0))), 790f, 810f); // inside: muffled
        var start = audio.FlyBy.GlobalPosition;
        Assert.InRange(MathF.Abs(start.X), 120f, 136f);
        Assert.InRange(start.Z, 23f, 38f);
        h.RunSeconds(1f);
        Assert.InRange(Vector3.Distance(start, audio.FlyBy.GlobalPosition), 40f, 50f); // 45 m/s

        // Over a few minutes the schedule keeps the city going: about one event in nine seconds, a car in thirty.
        h.RunSeconds(180f);
        Assert.InRange(audio.CityEvents, 10, 40);
        Assert.InRange(audio.FlyBys, 3, 12);
    }

    [Fact]
    public void LoopsAndOneShotsComeFromTheApi()
    {
        using var h = new AudioHarness();
        h.AddListener(new Vector3(0, 1.6f, 0));
        var audio = h.AddAudio();
        var tap = new Node3D { Name = "Tap", Position = new Vector3(2, 1, -3) };
        h.Scene.AddChild(tap);
        var loop = NeonAudio.CreateLoop(NeonSound.Tap, tap, NeonAudio.BathroomBus);
        Assert.Same(tap, loop.Parent);
        Assert.True(loop.Loop);
        Assert.False(loop.Playing);
        Assert.Equal(NeonAudio.BathroomBus, loop.Bus);
        Assert.Equal(NeonSoundBank.Emitter(NeonSound.Tap).MaxDistance, loop.MaxDistance);
        loop.Play();
        h.Run(2);
        Assert.True(loop.Playing);
        loop.Stop();

        var track = new Node3D { Name = "Track", Position = new Vector3(0, -40f, 80f) };
        h.Scene.AddChild(track);
        var train = NeonAudio.CreateLoop(NeonSound.TrainRumble, track);
        Assert.Equal(NeonAudio.AmbienceBus, train.Bus);
        train.Play();
        h.Run(1);
        // Registered as outdoor: its voice's low-pass lands on ≈ 800 Hz at its distance while the listener is inside.
        Assert.InRange(NeonAudio.EffectiveCutoff(train, Vector3.Distance(train.GlobalPosition, new Vector3(0, 1.6f, 0))), 790f, 810f);
        Assert.True(train.MaxDistance > Vector3.Distance(train.GlobalPosition, new Vector3(0, 1.6f, 0)), "never beyond its max distance");

        NeonAudio.PlayAt(NeonSound.KeyClick, new Vector3(1, 1, 1));
        Assert.Equal(NeonSound.KeyClick, audio.LastPlayed.Sound);
        var first = audio.LastPlayed.Player!;
        Assert.True(first.Playing);
        Assert.Equal(NeonAudio.FoleyBus, first.Bus);
        Assert.Equal(new Vector3(1, 1, 1), first.GlobalPosition);
        // Never the same key twice running, and the pool hands out another player while the first still plays.
        var last = first.Stream;
        for (var i = 0; i < 20; i++)
        {
            NeonAudio.PlayAt(NeonSound.KeyClick, new Vector3(1, 1, 1), bus: "SFX");
            Assert.NotSame(last, audio.LastPlayed.Player!.Stream);
            last = audio.LastPlayed.Player.Stream;
        }
    }

    [Fact]
    public void TheApartmentsDoorsSwitchesAndMachinesSound()
    {
        using var h = new AudioHarness(floor: false);
        var apartment = h.AddApartment();
        var player = h.Controller.AddPlayer(NeonScene.Spawn);
        var audio = h.AddAudio();
        h.Run(2);

        Assert.True(audio.Fridge!.Playing);
        Assert.Equal(2, audio.PcFans.Count);
        Assert.All(audio.PcFans, f => Assert.True(f.Playing));
        Assert.InRange(audio.Fridge.GlobalPosition.Z, -6f, -3f); // the kitchen's back wall

        var bath = (Door)apartment.Doors["Door_Bath"];
        bath.Toggle();
        Assert.Equal(NeonSound.DoorOpen, audio.LastPlayed.Sound);
        Assert.True(Vector3.Distance(bath.GlobalPosition, audio.LastPlayed.Player!.GlobalPosition) < 1.5f);
        h.RunSeconds(1f);
        bath.Toggle();
        h.RunSeconds(1f);
        Assert.Equal(NeonSound.DoorClose, audio.LastPlayed.Sound); // when it reaches the frame

        var slider = (SlidingDoor)apartment.Doors["Door_Balcony"];
        slider.Toggle();
        Assert.Equal(NeonSound.SliderOpen, audio.LastPlayed.Sound);
        h.RunSeconds(1.2f);
        slider.Toggle();
        Assert.Equal(NeonSound.SliderClose, audio.LastPlayed.Sound);

        apartment.FrontDoor!.Interact();
        Assert.Equal(NeonSound.LockedBuzz, audio.LastPlayed.Sound);

        apartment.Switches["Living"].Interact();
        Assert.Equal(NeonSound.SwitchClick, audio.LastPlayed.Sound);
        apartment.Switches["Living"].SetOn(true); // code (the terminal) does not click
        Assert.Equal(NeonSound.SwitchClick, audio.LastPlayed.Sound);
        Assert.NotNull(player);
    }

    [Fact]
    public void RoomsSetTheFootstepsBusAndTheStereosMuffle()
    {
        using var h = new AudioHarness(floor: false);
        var apartment = h.AddApartment();
        var listener = h.AddListener(new Vector3(4, 1.6f, 0));
        var audio = h.AddAudio();
        h.Run(2);
        Assert.Equal("Living", audio.Room);
        Assert.Equal(0f, audio.Music!.Muffle);
        Assert.True(Vector3.Distance(audio.Music.StereoPosition, apartment.Sockets["Stereo"].GlobalPosition) < 1e-3f);

        listener.Position = new Vector3(-4.3f, 1.6f, -3.5f);
        h.Run(1);
        Assert.Equal("Bath", audio.Room);
        Assert.Equal(0.9f, audio.Music.Muffle); // the bathroom door is shut
        ((Door)apartment.Doors["Door_Bath"]).Toggle();
        h.RunSeconds(0.5f);
        Assert.Equal(0.5f, audio.Music.Muffle);

        listener.Position = new Vector3(-4.3f, 1.6f, 2f);
        h.Run(1);
        Assert.Equal("Computer", audio.Room);
        listener.Position = new Vector3(0, 1.6f, 6.8f);
        h.Run(1);
        Assert.Equal("Balcony", audio.Room);
        listener.Position = new Vector3(-4.3f, 4.6f, 2f);
        h.Run(1);
        Assert.Equal("Loft", audio.Room);
        Assert.Equal(0.25f, audio.Music.Muffle);
    }

    [Fact]
    public void FootstepsInTheBathroomRing()
    {
        using var h = new AudioHarness(floor: false);
        h.AddApartment();
        var player = h.Controller.AddPlayer(new Vector3(-4.3f, 0f, -3.5f));
        var audio = h.AddAudio();
        h.RunSeconds(0.3f);
        Assert.Equal("Bath", audio.Room);
        audio.Footsteps!.OnFootstep("tile");
        Assert.Equal(NeonAudio.BathroomBus, audio.Footsteps.Players.First(p => p.Playing).Bus);
        Assert.Equal("tile", player.ResolveSurface());
    }

    [Fact]
    public void FootstepsPlayTheSurfaceSetWithoutRepeats()
    {
        using var h = new AudioHarness();
        var player = h.Controller.AddPlayer();
        var audio = h.AddAudio();
        h.Run(1);
        var steps = audio.Footsteps!;
        var bank = NeonSoundBank.Shared;
        AudioStream? last = null;
        foreach (var surface in NeonSynth.FootstepSurfaces.Append("grate").Append("unknown"))
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

        // The controller's signal drives it.
        var before = (steps.Played, player.FootstepCount);
        h.Controller.Hold("move_forward");
        h.RunSeconds(3f);
        Assert.Equal(player.FootstepCount - before.FootstepCount, steps.Played - before.Played);
        Assert.True(steps.Played - before.Played >= 8);
    }

    [Fact]
    public void TheStereoPlaysNextAndStops()
    {
        using var h = new AudioHarness();
        var listener = h.AddListener(new Vector3(0, 1.6f, 0));
        var audio = h.AddAudio();
        h.Run(1);
        var stereo = audio.Music!;
        stereo.StereoPosition = new Vector3(0, 1f, -2f);
        var changes = new List<int>();
        stereo.SongChanged += changes.Add;
        stereo.Play();
        h.Run(30);
        Assert.Equal(0, stereo.Current);
        Assert.True(stereo.GetPlayer(0)!.Playing);
        Assert.Equal(stereo.VolumeDb, stereo.GetPlayer(0)!.VolumeDb, 2); // within the unit size
        stereo.Next();
        h.Run(60);
        Assert.Equal(1, stereo.Current);
        Assert.True(stereo.GetPlayer(1)!.Playing);
        Assert.False(stereo.GetPlayer(0)!.Playing);
        stereo.Stop();
        Assert.False(stereo.IsPlaying);
        Assert.Equal([0, 1, -1], changes);

        // Further away, quieter: 12 m is four unit sizes, −12 dB.
        listener.Position = new Vector3(0, 1f, 10f);
        Assert.Equal(stereo.VolumeDb - 12.04f, stereo.LevelAt(listener.Position), 1);
    }

    [Fact]
    public void FramesAllocateNothing()
    {
        using var h = new AudioHarness(floor: false);
        var apartment = h.AddApartment();
        var player = h.Controller.AddPlayer(NeonScene.Spawn + new Vector3(-1, 0, 2));
        var audio = h.AddAudio(cityEvents: true, configure: a =>
        {
            a.CityEventInterval = 0.8f; // plenty of events in the window
            a.FlyByInterval = 4f;
        });
        NeonSoundBank.Shared.Preload();
        h.Run(2);
        audio.Music!.Play();
        var doors = apartment.Doors.Values.OfType<DoorBase>().ToArray();
        var switches = apartment.Switches.Values.ToArray();

        void Walk(int tick)
        {
            h.Controller.Hold("move_forward");
            if (tick % 90 == 0)
                player.AddLook(40f, 0f);
            if (tick % 20 == 0)
                audio.Footsteps!.OnFootstep(NeonSynth.FootstepSurfaces[tick / 20 % NeonSynth.FootstepSurfaces.Length]);
            if (tick % 45 == 0)
                doors[tick / 45 % doors.Length].Toggle();
            if (tick % 70 == 0)
                switches[tick / 70 % switches.Length].Interact();
            if (tick % 33 == 0)
                NeonAudio.PlayAt(NeonSound.KeyClick, player.GlobalPosition);
        }

        Action<int> walk = Walk;
        h.Run(600, walk); // warm up: every voice, set and pooled player used once
        var events = audio.CityEvents;
        var played = audio.Footsteps!.Played;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        h.Run(600, walk);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.True(audio.CityEvents - events > 3);
        Assert.True(audio.Footsteps.Played - played > 25);
        Assert.True(audio.Music.IsPlaying);
        Assert.Equal(0, allocated);
    }
}
