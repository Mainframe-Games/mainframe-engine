using System.Numerics;
using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;
using MainframeEngine.Localization;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Mirrors the showcase fixture's per-frame work — Spine, shadows, sky, grid, the RmlUi HUD (data bindings dirtied every frame)
/// the light and axis screen gizmos and the RmlUi dev overlay with every panel expanded — for the steady-state allocation
/// gate. The overlay shows timings, so it is not used for golden images. Its labels are looked up in the Spanish test catalog (Tests/Assets/locale) every frame
/// (M9), so the gate also covers translation lookups. An adaptive-music player (streamed and memory stems, layer curves
/// and filters, transitions and stingers) covers the music runtime in a real frame.
/// </summary>
public sealed class ShowcaseScene(HostOptions host) : SpineScene(host)
{
    private AudioPlayer3D? _orbiting;
    private AdaptiveMusicPlayer? _music;
    private string? _musicFolder;

    protected override void LoadScene()
    {
        base.LoadScene();
        Tr.SetLocale("es");
        Servers.Render!.ShowLightGizmos = true; // the allocation gate covers the screen-gizmo path
        Servers.Render.ShowAxisGizmo = true;
        AddDevOverlay();
    }

    /// <summary>
    /// The dev overlay with every panel expanded, plus a test panel with the labels the Spanish test catalog translates
    /// (a hit, a context hit and a miss), so the gate covers the overlay's refresh path and translation lookups.
    /// </summary>
    private void AddDevOverlay()
    {
        DevOverlayVisible = true;
        var overlay = DevOverlay!;
        overlay.AddPanel("render-test", Tr._("Render test"), """
            <div class="dev-row"><span>{{language}}</span><span class="v">{{locale}}</span></div>
            <div class="dev-row"><span>{{camera}}</span><span class="v">{{x | format(2)}}, {{y | format(2)}}, {{z | format(2)}}</span></div>
            """, p => p.Model!
            .Bind("language", this, static s => Tr._("Language"))              // hit
            .Bind("camera", this, static s => Tr.P("overlay section", "Camera")) // context hit
            .Bind("locale", this, static s => Tr.CurrentLocale)
            .Bind("x", this, static s => s.Camera.GlobalPosition.X)
            .Bind("y", this, static s => s.Camera.GlobalPosition.Y)
            .Bind("z", this, static s => s.Camera.GlobalPosition.Z));
        // G10: the Profiler section only with --profile (expanded, it records: the second allocation gate).
        foreach (var panel in overlay.Panels)
            panel.Expanded = panel.Id != "profiler" || Host.Profile;
    }

    /// <summary>
    /// Adds the showcase's audio (M7) and physics (M6) stacks and the RmlUi HUD (M8), so the allocation gate covers
    /// <c>AudioServer.Process</c> with moving positional voices, physics steps, interpolation and contacts, and the UI.
    /// </summary>
    protected override void AddNodes(Node scene)
    {
        base.AddNodes(scene);
        AddAudio(scene);
        AddAdaptiveMusic(scene);
        AddPhysics(scene);
        scene.AddChild(UiHudScene.CreateHudLayer());
    }

    private void AddAudio(Node scene)
    {
        // As in the showcase fixture: the streamed ambience on a box-like emitter plus an orbiting, looping in-memory tone.
        scene.AddChild(new AudioPlayer3D
        {
            Name = "Ambience",
            Stream = new AudioStream { File = "Assets/Audio/ambient_hum.ogg", LoadMode = AudioLoadMode.Stream, Loop = true, LoopEnd = 6f },
            Bus = "SFX",
            VolumeDb = -18f,
            Autoplay = true,
            UnitSize = 3f,
            MaxDistance = 60f,
            LowPassAtMaxDistance = 1500f,
            Position = new Vector3(3, 1, 0),
        });

        var tone = new float[48000];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.25f * MathF.Sin(2 * MathF.PI * 220 * i / 48000f);
        _orbiting = new AudioPlayer3D
        {
            Name = "Orbiter",
            Stream = AudioStream.FromSamples(tone, 1, 48000, "orbit tone"),
            Loop = true,
            Autoplay = true,
            UnitSize = 2f,
            DopplerTracking = true,
        };
        scene.AddChild(_orbiting);
    }

    /// <summary>
    /// Adaptive music from generated stems (written to a temporary folder): two streamed looping sections with three
    /// layers (one through a low-pass), a memory-loaded stinger, and rules on an <c>area</c> parameter. The stems are at
    /// 24 kHz, so the lanes are resampled to the device rate too.
    /// </summary>
    private void AddAdaptiveMusic(Node scene)
    {
        const int rate = 24000;
        const int bar = rate * 2; // 120 BPM, 4/4
        _musicFolder = Path.Combine(Path.GetTempPath(), $"mf-showcase-music-{System.Environment.ProcessId}");
        Directory.CreateDirectory(_musicFolder);
        MusicStemRef Stem(string name, float frequency, float amplitude, int frames)
        {
            var samples = new float[frames];
            for (var i = 0; i < frames; i++)
                samples[i] = amplitude * MathF.Sin(2 * MathF.PI * frequency * i / rate) * (1f - (float)i / frames);
            var path = Path.Combine(_musicFolder, name + ".wav");
            WavWriter.Write(path, samples, 1, rate, WavSampleFormat.Pcm16);
            return new MusicStemRef { Path = path };
        }

        var manifest = new MusicManifest
        {
            SampleRate = rate,
            Tempo = 120,
            Start = "explore",
            Parameters =
            [
                new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1, SeekSpeed = 1 },
                new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] },
            ],
            Layers =
            [
                new MusicManifestLayer { Id = "base" },
                new MusicManifestLayer { Id = "perc" },
                new MusicManifestLayer { Id = "lead", Filter = MusicLayerFilter.LowPass },
            ],
            Curves =
            [
                new MusicCurveInfo { Layer = "perc", Parameter = "intensity", Points = [new(0, -80), new(0.5f, 0)] },
                new MusicCurveInfo { Layer = "lead", Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 400), new(1, 12000)] },
            ],
            Transitions =
            [
                new MusicTransitionInfo { From = ["explore"], To = "combat", When = When("Combat"), Quantize = MusicQuantize.Beat, FadeOutBeats = 1 },
                new MusicTransitionInfo { From = ["combat"], To = "explore", When = When("Calm"), Quantize = MusicQuantize.Beat, Stinger = "hit" },
            ],
        };
        foreach (var (id, root) in new[] { ("explore", 220f), ("combat", 330f) })
        {
            var section = new MusicManifestSection { Id = id, Bars = 1, Loop = true };
            section.Stems["base"] = new MusicStemSet { Body = Stem(id + ".base", root, 0.05f, bar), Tail = Stem(id + ".base.tail", root, 0.02f, rate / 2) };
            section.Stems["perc"] = new MusicStemSet { Body = Stem(id + ".perc", root * 4, 0.03f, bar) };
            section.Stems["lead"] = new MusicStemSet { Body = Stem(id + ".lead", root * 2, 0.04f, bar) };
            manifest.Sections.Add(section);
        }

        var hit = new MusicManifestSection { Id = "hit", Kind = MusicSectionKind.Stinger, Bars = 1, PickupBeats = 1 };
        hit.Stems[MusicManifestSection.MixStem] = new MusicStemSet { Body = Stem("hit", 880, 0.05f, bar) };
        manifest.Sections.Add(hit);

        _music = new AdaptiveMusicPlayer { Name = "Music", Music = AdaptiveMusic.FromManifest(manifest, "Showcase music"), VolumeDb = -12f, Autoplay = true };
        scene.AddChild(_music);

        static MusicConditionGroup When(string area) =>
            new() { All = [new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue(area) }] };
    }

    /// <summary>A static floor collider and a tower of crates (the showcase's physics stack).</summary>
    private void AddPhysics(Node scene)
    {
        // Single-threaded: Jitter2's worker pool allocates 56 B about once per 5 000 multi-threaded steps (inside the
        // library), which would make this 300-frame zero-byte gate flaky. The engine's own code is the same either way.
        Servers.GetRequired<PhysicsServer3D>().Settings.MultiThreaded = false;
        var floor = new StaticBody3D { Name = "FloorCollider", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(10, 1, 10) } });
        scene.AddChild(floor);
        var cube = new BoxShape3D { Size = new Vector3(0.5f) };
        var crateMesh = new BoxMesh { Size = new Vector3(0.5f) }; // shared: the crates batch into one instanced draw
        // Two neat columns, so the stack is at rest (no new contact pairs) by the end of the warm-up.
        for (var i = 0; i < 12; i++)
        {
            var crate = new RigidBody3D
            {
                Name = $"Crate{i}",
                Position = new Vector3(-3 + i % 2 * 0.6f, 0.251f + i / 2 * 0.501f, 1.5f),
                ContactMonitor = i == 0,
                CanSleep = i % 3 != 0, // some keep simulating through the measured window
            };
            crate.AddChild(new CollisionShape3D { Shape = cube });
            crate.AddChild(new MeshInstance3D { Mesh = crateMesh });
            scene.AddChild(crate);
        }
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        base.UpdateScene(gameTime);
        if (_orbiting is not null)
        {
            var angle = gameTime.FrameCount * 0.02f;
            _orbiting.Position = new Vector3(MathF.Cos(angle) * 6f, 1f, MathF.Sin(angle) * 6f);
        }

        if (_music is not null)
        {
            // Parameter sweeps every frame and a transition every 40 frames, each way (the way back fires the stinger), so
            // the warm-up has been through both before the gate measures.
            _music.SetParameter("intensity", 0.5f + 0.5f * MathF.Sin(gameTime.FrameCount * 0.03f));
            if (gameTime.FrameCount % 40 == 0)
                _music.SetParameter("area", gameTime.FrameCount / 40 % 2 == 0 ? "Calm" : "Combat");
        }

        // Self-check (before the measured window): both voices really play, so the gate measures live audio.
        if (gameTime.FrameCount == 60)
        {
            var audio = Servers.Get<AudioServer>();
            if (audio is null)
                Fail("No AudioServer registered.");
            else if (audio.Stats.ActiveVoices < 2 || _orbiting?.Playing != true)
                Fail($"Expected the ambience and the orbiter to play; {audio.Stats.ActiveVoices} active voices.");
            else if (audio.Stats.StreamErrors > 0 || audio.Stats.Faulted)
                Fail("Audio stream errors or a mixer fault.");
            else if (_music is not { Playing: true, CurrentSection: not null })
                Fail("Expected the adaptive music to play.");
        }
    }

    protected override void DisposeScene()
    {
        base.DisposeScene();
        if (_musicFolder is null)
            return;
        try
        {
            Directory.Delete(_musicFolder, recursive: true);
        }
        catch (IOException)
        {
            // still open on the streaming thread: the OS cleans its temporary folder
        }
    }
}
