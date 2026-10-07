using System.Numerics;

namespace MainframeEngine.Tests.Audio;

/// <summary>
/// The audio server end to end on the manual null device: the real SoundFlow mixer graph renders into a capture
/// buffer, so levels, buses, voices and timing are measured on actual output samples.
/// </summary>
[Collection(nameof(Scene.SerialResources))] // the layout tests swap AssetDatabase.Current and the loader cache
public sealed class AudioServerTests
{
    private const int Rate = AudioTestUtil.Rate;

    [Fact]
    public void NullDeviceStartsWithTheDefaultBusLayout()
    {
        using var server = AudioTestUtil.CreateServer();
        Assert.True(server.IsNullDevice);
        Assert.Equal(Rate, server.SampleRate);
        Assert.Equal(["Master", "Music", "SFX", "UI", "Voice"], server.Buses.Select(b => b.Name));
        Assert.Equal([16, 4, 32, 8, 8], server.Buses.Select(b => b.MaxVoices));
        Assert.Equal(68, server.TotalVoices);
        Assert.All(server.Buses.Skip(1), bus => Assert.Same(server.Master, bus.Parent));
    }

    [Fact]
    public void RealtimeNullDeviceAdvancesOnItsOwn()
    {
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.Null, BusLayoutPath = null });
        Assert.True(server.IsNullDevice);
        var handle = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 0.05f));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.IsPlaying(handle) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            server.Process(AudioTestUtil.Frame);
        }

        Assert.False(server.IsPlaying(handle)); // finished in real time without anyone rendering
        Assert.True(server.Stats.RenderedFrames > 0);
    }

    [Fact]
    public void ABrokenBusLayoutFileFallsBackToTheDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-bad-layout-{Guid.NewGuid():N}.mres");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = path });
            Assert.Equal(5, server.Buses.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheMixChainHasUnityGain()
    {
        using var server = AudioTestUtil.CreateServer();
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 1f), bus: "SFX");
        var output = AudioTestUtil.Render(server, 2048);
        Assert.Equal(0.5f, output[2000], 4);
        Assert.Equal(0.5f, output[2001], 4);
        Assert.Equal(0.5f, AudioTestUtil.Peak(output, 0), 4); // starts at level (no fade-in on play)
    }

    [Fact]
    public void VoiceAndBusVolumesMultiply()
    {
        using var server = AudioTestUtil.CreateServer();
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f), bus: "SFX", volumeDb: -6.0206f);
        Assert.Equal(0.25f, AudioTestUtil.Render(server, 512)[1000], 3);

        server.GetBus("SFX")!.VolumeDb = -6.0206f;
        server.Master.VolumeDb = -6.0206f;
        var output = AudioTestUtil.Render(server, Rate / 4); // past the 10 ms fader ramp
        Assert.Equal(0.0625f, output[^2], 1e-3f);
        Assert.Equal(0.25f, server.GetBus("SFX")!.EffectiveGain * server.Master.EffectiveGain, 1e-3f);
    }

    [Fact]
    public void MuteAndSoloSilenceTheRightBuses()
    {
        using var server = AudioTestUtil.CreateServer();
        server.PlayOneShot(AudioTestUtil.Constant(0.25f, 2f), bus: "SFX");
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f), bus: "Music");
        Assert.Equal(0.75f, AudioTestUtil.Render(server, 512)[1000], 3);

        server.GetBus("SFX")!.Mute = true;
        Assert.Equal(0.5f, AudioTestUtil.Render(server, Rate / 10)[^2], 3);
        server.GetBus("SFX")!.Mute = false;

        server.GetBus("SFX")!.Solo = true; // only SFX (and Master, which it passes through) is heard
        Assert.Equal(0.25f, AudioTestUtil.Render(server, Rate / 10)[^2], 3);
        Assert.True(server.Master.DirectAudible is false);
        Assert.Equal(1f, server.Master.EffectiveGain);
        Assert.Equal(0f, server.GetBus("Music")!.EffectiveGain);

        server.GetBus("SFX")!.Solo = false;
        Assert.Equal(0.75f, AudioTestUtil.Render(server, Rate / 10)[^2], 3);
        server.Master.Mute = true;
        Assert.Equal(0f, AudioTestUtil.Render(server, Rate / 10)[^2], 4);
    }

    [Fact]
    public void SoloOfANestedBusSilencesItsParentsOwnVoices()
    {
        var layout = AudioBusLayout.CreateDefault();
        layout.Buses.Add(new AudioBusInfo { Name = "Footsteps", Send = "SFX", MaxVoices = 4 });
        using var server = AudioTestUtil.CreateServer(layout: layout);
        server.PlayOneShot(AudioTestUtil.Constant(0.25f, 2f), bus: "SFX");
        server.PlayOneShot(AudioTestUtil.Constant(0.125f, 2f), bus: "Footsteps");
        Assert.Equal(0.375f, AudioTestUtil.Render(server, 512)[1000], 3);

        server.GetBus("Footsteps")!.Solo = true;
        var output = AudioTestUtil.Render(server, Rate / 10);
        Assert.Equal(0.125f, output[^2], 3); // SFX passes Footsteps through but its own voice is silenced
        Assert.False(server.GetBus("SFX")!.DirectAudible);
        Assert.Equal(1f, server.GetBus("SFX")!.EffectiveGain);
    }

    [Fact]
    public void OneShotsFinishAndFreeTheirVoice()
    {
        using var server = AudioTestUtil.CreateServer();
        var handle = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 0.1f));
        Assert.True(handle.IsValid);
        Assert.True(server.IsPlaying(handle));
        Assert.Equal(1, server.Stats.ActiveVoices);

        var output = AudioTestUtil.Render(server, Rate / 5); // 0.2 s
        Assert.Equal(0.5f, output[2 * (Rate / 20)], 3); // 50 ms: playing
        Assert.Equal(0f, output[2 * (Rate / 10 + 100)]); // after 0.1 s: silence
        server.Process(AudioTestUtil.Frame); // the finished event reaches the game thread
        Assert.False(server.IsPlaying(handle));
        Assert.Equal(0, server.Stats.ActiveVoices);
    }

    [Fact]
    public void StopFadesOutWithoutAClick()
    {
        using var server = AudioTestUtil.CreateServer();
        var handle = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f));
        AudioTestUtil.Render(server, 512);
        server.Stop(handle);
        Assert.False(server.IsPlaying(handle)); // freed on the game thread immediately
        var output = AudioTestUtil.Render(server, Rate / 10);
        Assert.True(output[0] > 0.3f); // still fading at the start of the block
        var maxStep = 0f;
        for (var i = 2; i < output.Length; i += 2)
            maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 2]));
        Assert.True(maxStep < 0.01f, $"step {maxStep}");
        Assert.Equal(0f, output[^2], 4);
    }

    [Fact]
    public void PlayAndStopInTheSameFrameNeverSounds()
    {
        using var server = AudioTestUtil.CreateServer();
        var handle = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f));
        server.Stop(handle);
        var output = AudioTestUtil.Render(server, Rate / 20);
        Assert.True(AudioTestUtil.Peak(output.AsSpan(2 * 600), 0) < 1e-3f); // commands applied in order: play, then stop (fade)
        Assert.Equal(0, server.Stats.ActiveVoices);
    }

    [Fact]
    public void AFullBusStealsTheLowestPriorityThenTheQuietestThenTheOldest()
    {
        var layout = AudioBusLayout.CreateDefault();
        layout.Buses[2].MaxVoices = 2; // SFX
        using var server = AudioTestUtil.CreateServer(layout: layout);
        var tone = AudioTestUtil.Constant(0.1f, 5f);

        // Equal priority and gain: the oldest goes.
        var a = server.PlayOneShot(tone);
        var b = server.PlayOneShot(tone);
        var c = server.PlayOneShot(tone);
        Assert.False(server.IsPlaying(a));
        Assert.True(server.IsPlaying(b) && server.IsPlaying(c));
        Assert.Equal(1, server.Stats.Steals);

        // Lower priority than everything playing: rejected, nothing stolen.
        var d = server.PlayOneShot(tone, priority: -1);
        Assert.False(d.IsValid);
        Assert.Equal(1, server.Stats.Rejected);
        Assert.True(server.IsPlaying(b) && server.IsPlaying(c));

        // Equal priority: the quietest goes, even when it is the newer one.
        server.Stop(b);
        server.Stop(c);
        var loud = server.PlayOneShot(tone);
        var quiet = server.PlayOneShot(tone, volumeDb: -20f);
        var e = server.PlayOneShot(tone);
        Assert.True(server.IsPlaying(loud));
        Assert.False(server.IsPlaying(quiet));
        Assert.True(server.IsPlaying(e));

        // Lower priority goes before quieter.
        server.Stop(loud);
        server.Stop(e);
        var important = server.PlayOneShot(tone, volumeDb: -30f, priority: 5);
        var normal = server.PlayOneShot(tone);
        var medium = server.PlayOneShot(tone, priority: 1);
        Assert.True(server.IsPlaying(important));
        Assert.False(server.IsPlaying(normal));
        Assert.True(server.IsPlaying(medium));
    }

    [Fact]
    public void NodePolyphonyRestartsTheOldestVoice()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Constant(0.1f, 5f), MaxPolyphony = 2, Bus = "SFX" };
        tree.Root.AddChild(player);
        player.Play();
        player.Play();
        Assert.Equal(2, server.Stats.ActiveVoices);
        player.Play();
        Assert.Equal(2, server.Stats.ActiveVoices);
        Assert.Equal(0.2f, AudioTestUtil.Render(server, 512, tree)[1000], 3);
        player.Stop();
        Assert.False(player.Playing);
        Assert.Equal(0, server.Stats.ActiveVoices);
        tree.Shutdown();
    }

    [Fact]
    public void FinishedIsEmittedForNaturalEndsOnly()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Constant(0.5f, 0.05f) };
        var finished = 0;
        player.Finished += () => finished++;
        tree.Root.AddChild(player);

        player.Play();
        AudioTestUtil.Render(server, Rate / 10, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(1, finished);
        Assert.False(player.Playing);

        player.Play();
        player.Stop();
        AudioTestUtil.Render(server, Rate / 10, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(1, finished); // stopping does not emit

        player.Loop = true;
        player.Play();
        AudioTestUtil.Render(server, Rate / 5, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.True(player.Playing); // looping never finishes
        Assert.Equal(1, finished);
        tree.Shutdown();
    }

    [Fact]
    public void AutoplayStartsOnReadyAndLeavingTheTreeStops()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Constant(0.5f, 5f), Autoplay = true };
        tree.Root.AddChild(player);
        Assert.True(player.Playing);
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 256, tree)[100], 3);
        tree.Root.RemoveChild(player);
        Assert.False(player.Playing);
        Assert.Equal(0, server.Stats.ActiveVoices);
        player.Free();
        tree.Shutdown();
    }

    [Fact]
    public void EditModeKeepsAudioNodesSilent()
    {
        var tree = new SceneTree { EditMode = true };
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Constant(0.5f, 5f), Autoplay = true };
        var player3D = new AudioPlayer3D { Stream = AudioTestUtil.Constant(0.5f, 5f), Autoplay = true };
        var listener = new AudioListener3D { Current = true };
        tree.Root.AddChild(player);
        tree.Root.AddChild(player3D);
        tree.Root.AddChild(listener);
        player.Play();
        Assert.False(player.Playing);
        Assert.False(player3D.Playing);
        Assert.False(listener.IsCurrent);
        Assert.Equal(0, server.Stats.ActiveVoices);
        tree.Shutdown();
    }

    [Fact]
    public void PauseFollowsTheTreeAndProcessMode()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        AudioPlayer Add(ProcessMode mode)
        {
            var p = new AudioPlayer { Stream = AudioTestUtil.Sine(440, 5f), ProcessMode = mode, Loop = true };
            tree.Root.AddChild(p);
            p.Play();
            return p;
        }

        var pausable = Add(ProcessMode.Inherit); // the root is Pausable
        var always = Add(ProcessMode.Always);
        var whenPaused = Add(ProcessMode.WhenPaused);
        var disabled = Add(ProcessMode.Disabled);

        AudioTestUtil.Render(server, Rate / 2, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(0.5, pausable.GetPlaybackPosition(), 2);
        Assert.Equal(0.5, always.GetPlaybackPosition(), 2);
        Assert.Equal(0.0, whenPaused.GetPlaybackPosition(), 2);
        Assert.Equal(0.0, disabled.GetPlaybackPosition(), 2);

        tree.Paused = true;
        AudioTestUtil.Render(server, Rate / 2, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(0.5, pausable.GetPlaybackPosition(), 2); // held
        Assert.Equal(1.0, always.GetPlaybackPosition(), 2);
        Assert.Equal(0.5, whenPaused.GetPlaybackPosition(), 2);
        Assert.Equal(0.0, disabled.GetPlaybackPosition(), 2);

        tree.Paused = false;
        pausable.StreamPaused = true; // a manual pause holds it even unpaused
        AudioTestUtil.Render(server, Rate / 2, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(0.5, pausable.GetPlaybackPosition(), 2);
        Assert.Equal(1.5, always.GetPlaybackPosition(), 2);
        Assert.Equal(0.5, whenPaused.GetPlaybackPosition(), 2);

        pausable.StreamPaused = false;
        AudioTestUtil.Render(server, Rate / 2, tree);
        tree.Tick(AudioTestUtil.Frame);
        Assert.Equal(1.0, pausable.GetPlaybackPosition(), 2); // resumed where it stopped
        Assert.True(pausable.Playing && disabled.Playing); // paused voices still hold their slot
        tree.Shutdown();
    }

    [Fact]
    public void OneShotsUseTheirProcessModeAgainstTreePause()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        server.PlayOneShot(AudioTestUtil.Constant(0.25f, 5f), bus: "SFX");
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 5f), bus: "UI", processMode: ProcessMode.Always);
        Assert.Equal(0.75f, AudioTestUtil.Render(server, 512, tree)[1000], 3);
        tree.Paused = true;
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 512, tree)[1000], 3); // the menu click keeps playing

        // A pausable sound started while paused waits (silently) for the unpause.
        server.PlayOneShot(AudioTestUtil.Constant(0.125f, 5f), bus: "SFX");
        server.Flush();
        var capture = new float[1024];
        server.RenderNullDevice(512, capture);
        Assert.Equal(0.5f, capture[1], 3);
        Assert.Equal(0.5f, capture[1000], 3);
        tree.Paused = false;
        Assert.Equal(0.875f, AudioTestUtil.Render(server, 512, tree)[1000], 3);
        tree.Shutdown();
    }

    [Fact]
    public void ClipsAreResampledToTheDeviceRateAndPitchScaled()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Sine(1000f, 2f, rate: 22050) };
        tree.Root.AddChild(player);
        player.Play();
        var output = AudioTestUtil.Render(server, Rate / 2, tree);
        Assert.InRange(AudioTestUtil.Frequency(output, 2, 0, Rate), 998f, 1002f);

        player.PitchScale = 2f;
        AudioTestUtil.Render(server, 4096, tree); // pitch ramps over one block
        output = AudioTestUtil.Render(server, Rate / 4, tree);
        Assert.InRange(AudioTestUtil.Frequency(output, 2, 0, Rate), 1995f, 2005f);
        Assert.Equal(0.5f, AudioTestUtil.Peak(output, 0), 2);
        tree.Shutdown();
    }

    [Fact]
    public void SeekAndPlaybackPositionWorkOnClips()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Sine(440, 4f) };
        tree.Root.AddChild(player);
        player.Play(fromSeconds: 1f);
        AudioTestUtil.Render(server, Rate / 4, tree);
        Assert.Equal(1.25, player.GetPlaybackPosition(), 2);
        player.Seek(3f);
        AudioTestUtil.Render(server, Rate / 4, tree);
        Assert.Equal(3.25, player.GetPlaybackPosition(), 2);
        tree.Shutdown();
    }

    [Fact]
    public void PositionalVoicesPanAttenuateAndSmooth()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var listener = new AudioListener3D { Current = true };
        tree.Root.AddChild(listener);
        var emitter = new AudioPlayer3D
        {
            Stream = AudioTestUtil.Constant(0.5f, 10f),
            UnitSize = 1f,
            Position = new Vector3(5, 0, 0), // to the right, 5 units: inverse → 0.2
        };
        tree.Root.AddChild(emitter);
        Assert.True(listener.IsCurrent);

        tree.Tick(AudioTestUtil.Frame); // listener resolved before the first play
        emitter.Play();
        var output = AudioTestUtil.Render(server, 2048, tree);
        Assert.Equal(0f, output[2000], 3); // left
        Assert.Equal(0.1f, output[2001], 3); // right: 0.5 × 0.2

        emitter.Position = new Vector3(0, 0, -2); // in front, 2 units: 0.5 × 0.5 on both channels
        output = AudioTestUtil.Render(server, Rate / 10, tree);
        Assert.Equal(0.25f, output[^2], 3);
        Assert.Equal(0.25f, output[^1], 3);

        // Jumping to the far left ramps instead of stepping (no zipper noise).
        emitter.Position = new Vector3(-1, 0, 0);
        output = AudioTestUtil.Render(server, Rate / 10, tree);
        var maxStep = 0f;
        for (var i = 2; i < output.Length; i++)
            maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 2]));
        Assert.True(maxStep < 0.005f, $"largest per-sample change {maxStep}");
        Assert.Equal(0.5f, output[^2], 3);
        Assert.Equal(0f, output[^1], 3);

        // Turning the listener around swaps the sides.
        listener.RotationDegrees = new Vector3(0, 180, 0);
        output = AudioTestUtil.Render(server, Rate / 10, tree);
        Assert.Equal(0f, output[^2], 3);
        Assert.Equal(0.5f, output[^1], 3);
        tree.Shutdown();
    }

    [Fact]
    public void TheActiveCameraIsTheListenerWhenNoListenerIsCurrent()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var camera = new Camera3D { Position = new Vector3(100, 0, 0) };
        tree.Root.AddChild(camera);
        var emitter = new AudioPlayer3D { Stream = AudioTestUtil.Constant(0.5f, 5f), UnitSize = 1f, Position = new Vector3(100, 0, -1) };
        tree.Root.AddChild(emitter);
        tree.Tick(AudioTestUtil.Frame);
        emitter.Play();
        var output = AudioTestUtil.Render(server, 1024, tree);
        Assert.Equal(0.5f, output[1000], 3);
        Assert.Equal(0.5f, output[1001], 3);
        Assert.Equal(camera.GlobalPosition, server.Listener3D.Origin);
        tree.Shutdown();
    }

    [Fact]
    public void DistanceLowPassMufflesFarSounds()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var emitter = new AudioPlayer3D
        {
            Stream = AudioTestUtil.Sine(6000f, 5f),
            AttenuationModel = AttenuationModel.Disabled,
            UnitSize = 1f,
            MaxDistance = 100f,
            LowPassAtMaxDistance = 300f,
            Position = new Vector3(0, 0, -0.5f),
        };
        tree.Root.AddChild(emitter);
        tree.Tick(AudioTestUtil.Frame);
        emitter.Play();
        var near = AudioTestUtil.Rms(AudioTestUtil.Render(server, Rate / 10, tree), 0);
        emitter.Position = new Vector3(0, 0, -99f);
        AudioTestUtil.Render(server, Rate / 10, tree); // let the cutoff ramp
        var far = AudioTestUtil.Rms(AudioTestUtil.Render(server, Rate / 10, tree), 0);
        Assert.Equal(0.3536f, near, 2);
        Assert.True(far < near * 0.15f, $"far {far} vs near {near}");
        tree.Shutdown();
    }

    [Fact]
    public void DopplerTrackingShiftsPitchForMovingEmitters()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var emitter = new AudioPlayer3D
        {
            Stream = AudioTestUtil.Sine(1000f, 5f),
            AttenuationModel = AttenuationModel.Disabled,
            DopplerTracking = true,
            Position = new Vector3(0, 0, -100),
        };
        tree.Root.AddChild(emitter);
        tree.Tick(AudioTestUtil.Frame);
        emitter.Play();

        // Approach at 34.3 units/s (1/10 of the speed of sound): pitch × c / (c − v) = 1.111.
        var capture = new List<float>();
        for (var frame = 0; frame < 40; frame++)
        {
            emitter.Position += new Vector3(0, 0, 34.3f / 60f);
            var block = AudioTestUtil.Render(server, Rate / 60, tree);
            if (frame >= 10)
                capture.AddRange(block);
        }

        Assert.InRange(AudioTestUtil.Frequency(capture.ToArray(), 2, 0, Rate), 1100f, 1122f);
        tree.Shutdown();
    }

    [Fact]
    public void StreamedSoundsPlayLoopAndFinish()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var stream = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Stream, Loop = true };
        var player = new AudioPlayer { Stream = stream };
        tree.Root.AddChild(player);
        player.Play();
        Assert.True(stream.IsStreamed);

        // The streaming thread decodes asynchronously: wait until sound comes out.
        var output = WaitForSound(server, tree);
        var capture = new List<float>(output);
        for (var i = 0; i < 30; i++) // 1.5 s: three times round the 0.5 s loop
        {
            Assert.True(server.WaitForStreams(Rate / 20 + 64, TimeSpan.FromSeconds(5)));
            capture.AddRange(AudioTestUtil.Render(server, Rate / 20, tree));
        }

        var samples = capture.ToArray();
        Assert.InRange(AudioTestUtil.Frequency(samples, 2, 0, Rate), 431f, 449f);
        Assert.InRange(AudioTestUtil.Frequency(samples, 2, 1, Rate), 646f, 674f);
        Assert.True(MinWindowRms(samples, 0, 480) > 0.2f, "a gap (underrun or loop seam) in the streamed audio");
        Assert.Equal(0, server.Stats.Underruns);
        Assert.True(player.Playing);

        // Without looping the stream ends and frees its voice.
        var finished = 0;
        player.Finished += () => finished++;
        stream.Loop = false;
        player.Play();
        WaitForSound(server, tree);
        for (var i = 0; i < 40 && player.Playing; i++)
        {
            server.WaitForStreams(Rate / 20 + 64, TimeSpan.FromSeconds(5));
            AudioTestUtil.Render(server, Rate / 20, tree);
        }

        tree.Tick(AudioTestUtil.Frame);
        Assert.False(player.Playing);
        Assert.Equal(1, finished);
        tree.Shutdown();
    }

    [Fact]
    public void StreamedAndMemoryPlaybackProduceTheSameSamples()
    {
        using var server = AudioTestUtil.CreateServer();
        const string file = "sine440_mono16_44k.wav"; // 44.1 kHz: both paths resample to 48 kHz
        var memory = new AudioStream { File = AudioTestUtil.Asset(file), LoadMode = AudioLoadMode.Memory };
        var streamed = new AudioStream { File = AudioTestUtil.Asset(file), LoadMode = AudioLoadMode.Stream };

        var handle = server.PlayOneShot(memory);
        var fromMemory = AudioTestUtil.Render(server, 8192);
        server.Stop(handle);
        AudioTestUtil.Render(server, 1024); // let the stop fade finish

        server.PlayOneShot(streamed);
        server.Flush();
        Assert.True(server.WaitForStreams(8192 + 64, TimeSpan.FromSeconds(5))); // decoded ahead of the first block
        var fromStream = AudioTestUtil.Render(server, 8192);

        Assert.True(AudioTestUtil.Peak(fromStream, 0) > 0.45f);
        for (var i = 0; i < fromMemory.Length; i++)
            Assert.True(Math.Abs(fromMemory[i] - fromStream[i]) < 1e-3f, $"sample {i}: {fromMemory[i]} vs {fromStream[i]}");
    }

    [Fact]
    public void BusLayoutsSaveLoadAndApply()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-audio-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        var previous = AssetDatabase.Current;
        try
        {
            AssetDatabase.Current = new AssetDatabase(project);
            ResourceLoader.ClearCache();
            using (var server = AudioTestUtil.CreateServer())
            {
                server.GetBus("Music")!.VolumeDb = -12f;
                server.GetBus("UI")!.Mute = true;
                var layout = server.GetBusLayout();
                layout.Buses.Add(new AudioBusInfo
                {
                    Name = "Reverb",
                    Send = "SFX",
                    MaxVoices = 2,
                    Effects = [new AudioEffectReverb { RoomSize = 0.8f }, new AudioEffectLowPass { CutoffHz = 3000 }],
                });
                server.ApplyBusLayout(layout);
                Assert.Equal(6, server.Buses.Count);
                Assert.Equal(2, server.GetBus("Reverb")!.Effects.Count);
                server.SaveBusLayout("Content/Settings/AudioBusLayout.mres");
            }

            Assert.True(File.Exists(Path.Combine(project, "Content", "Settings", "AudioBusLayout.mres")));
            ResourceLoader.ClearCache();
            using (var reloaded = AudioServer.Create(new AudioOptions
            {
                Device = AudioDeviceMode.NullManual,
                BusLayoutPath = "Content/Settings/AudioBusLayout.mres",
            }))
            {
                Assert.Equal(-12f, reloaded.GetBus("Music")!.VolumeDb);
                Assert.True(reloaded.GetBus("UI")!.Mute);
                var reverb = reloaded.GetBus("Reverb")!;
                Assert.Equal("SFX", reverb.Send);
                Assert.Equal(0.8f, Assert.IsType<AudioEffectReverb>(reverb.Effects[0]).RoomSize);
                Assert.Equal(3000f, Assert.IsType<AudioEffectLowPass>(reverb.Effects[1]).CutoffHz);

                // The effect chain processes the bus: a voice on it still plays (through reverb + low-pass).
                reloaded.PlayOneShot(AudioTestUtil.Sine(200f, 1f), bus: "Reverb");
                Assert.True(AudioTestUtil.Peak(AudioTestUtil.Render(reloaded, Rate / 10), 0) > 0.05f);
            }
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void ApplyingALayoutStopsVoicesAndRetiresTheOldGraph()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer { Stream = AudioTestUtil.Constant(0.5f, 5f), Bus = "SFX" };
        var finished = 0;
        player.Finished += () => finished++;
        tree.Root.AddChild(player);
        player.Play();
        AudioTestUtil.Render(server, 512, tree);

        var layout = AudioBusLayout.CreateDefault();
        layout.Buses.RemoveAt(4); // drop Voice
        server.ApplyBusLayout(layout);
        Assert.False(player.Playing);
        Assert.Equal(4, server.Buses.Count);
        Assert.Equal(0f, AudioTestUtil.Render(server, 512, tree)[1000]);
        Assert.Equal(0, finished);

        player.Play(); // plays on the new graph
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 512, tree)[1000], 3);
        Assert.Throws<ArgumentException>(() => server.ApplyBusLayout(new AudioBusLayout()));
        tree.Shutdown();
    }

    [Fact]
    public void UnknownBusesFallBackToMasterAndInvalidStreamsAreIgnored()
    {
        using var server = AudioTestUtil.CreateServer();
        var handle = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 1f), bus: "Nope");
        Assert.True(handle.IsValid);
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 512)[1000], 3);
        Assert.Equal(1, server.Master.ActiveVoices);

        var broken = new AudioStream { File = "Content/Audio/does-not-exist.ogg" };
        Assert.False(server.PlayOneShot(broken).IsValid);
    }

    private static float[] WaitForSound(AudioServer server, SceneTree tree)
    {
        server.Flush();
        Assert.True(server.WaitForStreams(4096, TimeSpan.FromSeconds(5)), "the streaming thread did not decode in time");
        var block = AudioTestUtil.Render(server, 256, tree);
        Assert.True(AudioTestUtil.Peak(block, 0) > 0.05f, "no sound from the streamed voice");
        return block;
    }

    // Smallest RMS over sliding windows after the first: a dropout shows as a near-silent window.
    private static float MinWindowRms(float[] stereo, int channel, int windowFrames)
    {
        var min = float.MaxValue;
        for (var start = windowFrames * 2; start + windowFrames * 2 <= stereo.Length - windowFrames * 2; start += windowFrames)
            min = Math.Min(min, AudioTestUtil.Rms(stereo.AsSpan(start, windowFrames * 2), channel));
        return min;
    }
}
