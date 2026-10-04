using System.Numerics;

namespace MainframeEngine.Tests.Audio;

/// <summary>
/// The audio allocation gate: with sounds playing — positional and not, moving, panning, looping, streamed, paused and
/// resumed, voices finishing and restarting — a steady-state frame allocates nothing, on the game thread (scene tree
/// tick + <see cref="AudioServer.Process"/>) or on the audio thread (here the same thread, rendering the null device).
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AudioAllocationTests
{
    [Fact]
    public void SteadyStateFramesWithPlayingSoundsDoNotAllocate()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var scene = new Node3D { Name = "Scene" };
        tree.ChangeScene(scene);

        var listener = new AudioListener3D { Current = true };
        scene.AddChild(listener);

        var loop = AudioTestUtil.Sine(220f, 1f);
        loop.Loop = true;
        var shortSound = AudioTestUtil.Sine(880f, 0.05f);
        var emitters = new AudioPlayer3D[8];
        for (var i = 0; i < emitters.Length; i++)
        {
            emitters[i] = new AudioPlayer3D
            {
                Stream = loop,
                Bus = "SFX",
                UnitSize = 1f,
                MaxDistance = 50f,
                LowPassAtMaxDistance = i % 2 == 0 ? 800f : 0f,
                DopplerTracking = i % 3 == 0,
                AttenuationModel = (AttenuationModel)(1 + i % 5),
                Autoplay = true,
            };
            scene.AddChild(emitters[i]);
        }

        var music = new AudioPlayer
        {
            Stream = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Stream, Loop = true },
            Bus = "Music",
            Autoplay = true,
        };
        scene.AddChild(music);
        var ui = new AudioPlayer { Stream = shortSound, Bus = "UI", ProcessMode = ProcessMode.Always, MaxPolyphony = 4 };
        scene.AddChild(ui);
        var sprite = new Node2D();
        scene.AddChild(sprite);
        var hud = new AudioPlayer2D { Stream = loop, Bus = "UI", Autoplay = true };
        sprite.AddChild(hud);

        var finished = 0;
        ui.Finished += () => finished++;
        var buffer = new float[800 * 2];

        void Frame(int i)
        {
            for (var e = 0; e < emitters.Length; e++)
            {
                var angle = i * 0.05f + e;
                emitters[e].Position = new Vector3(MathF.Cos(angle) * (5 + e), 0, MathF.Sin(angle) * (5 + e));
            }

            listener.RotationDegrees = new Vector3(0, i % 360, 0);
            sprite.Position = new Vector2(MathF.Sin(i * 0.1f) * 500f, 0);
            if (i % 20 == 0)
                ui.Play(); // one-shots finish and restart all the time
            if (i % 50 == 0)
                server.PlayOneShot(shortSound, bus: "SFX", position: new Vector3(1, 0, 0));
            if (i % 90 == 0)
                tree.Paused = !tree.Paused; // pause transitions
            server.GetBus("SFX")!.VolumeDb = -(i % 12); // bus fader changes
            tree.Tick(AudioTestUtil.Frame);
            server.RenderNullDevice(800, buffer); // ≈ one 60 Hz frame of audio
        }

        for (var i = 0; i < 240; i++) // warm-up: JIT, handle lists, pools, stream ring, ArrayPool buckets
        {
            Frame(i);
            if (i % 10 == 0)
                Thread.Sleep(1); // let the streaming thread start and fill
        }

        var next      = 240;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var end = next + 300; next < end; next++)
                Frame(next);
        });

        Assert.Equal(0, allocated);
        Assert.True(finished > 5);
        Assert.True(server.Stats.ActiveVoices >= 10);
        Assert.False(server.Stats.Faulted);
        tree.Shutdown();
    }
}
