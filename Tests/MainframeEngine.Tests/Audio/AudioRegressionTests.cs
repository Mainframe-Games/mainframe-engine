using System.Numerics;
using MainframeEngine.Audio;

namespace MainframeEngine.Tests.Audio;

/// <summary>Regression tests for the M7 review findings (reverb, seek/finish race, stream errors, generations, …).</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AudioRegressionTests
{
    private const int Rate = AudioTestUtil.Rate;

    [Fact]
    public void ReverbHasATailAndProcessesWithoutAllocating()
    {
        var reverb = new ReverbProcessor(Rate, roomSize: 0.8f, damp: 0.3f, wet: 0.5f, dry: 1f, width: 1f);
        var block = new float[480 * 2];
        block[0] = block[1] = 1f; // an impulse
        reverb.Process(block, 2);
        Assert.Equal(1f, block[0], 3); // the dry impulse passes through

        // The tail rings on after the impulse, decays, and never goes non-finite.
        float tail = 0f, late = 0f;
        for (var b = 0; b < 400; b++) // 4 s
        {
            Array.Clear(block);
            reverb.Process(block, 2);
            var energy = 0f;
            foreach (var s in block)
            {
                Assert.True(float.IsFinite(s));
                energy += s * s;
            }

            if (b < 50)
                tail += energy;
            if (b >= 350)
                late += energy;
        }

        Assert.True(tail > 1e-4f, $"no reverb tail ({tail})");
        Assert.True(late < tail * 1e-3f, $"tail does not decay ({late} vs {tail})");

        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var b = 0; b < 200; b++)
                reverb.Process(block, 2);
        }));
    }

    [Fact]
    public void BusesWithEveryEffectMixWithoutAllocating()
    {
        var layout = AudioBusLayout.CreateDefault();
        layout.Buses[2].Effects =
        [
            new AudioEffectReverb { RoomSize = 0.7f },
            new AudioEffectLowPass { CutoffHz = 4000 },
            new AudioEffectHighPass { CutoffHz = 80 },
            new AudioEffectCompressor { ThresholdDb = -18 },
        ];
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree, layout);
        var loop = AudioTestUtil.Sine(330f, 1f);
        loop.Loop = true;
        for (var i = 0; i < 6; i++)
        {
            var player = new AudioPlayer3D { Stream = loop, Bus = "SFX", Autoplay = true, UnitSize = 1f, Position = new Vector3(i - 3, 0, -2) };
            tree.Root.AddChild(player);
        }

        var buffer = new float[800 * 2];
        for (var i = 0; i < 120; i++)
        {
            tree.Tick(AudioTestUtil.Frame);
            server.RenderNullDevice(800, buffer);
        }

        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                tree.Tick(AudioTestUtil.Frame);
                server.RenderNullDevice(800, buffer);
            }
        }));
        Assert.True(AudioTestUtil.Peak(buffer, 0) > 0.01f);
        tree.Shutdown();
    }

    [Fact]
    public void SeekingAStreamThatJustEndedDoesNotResurrectItsVoice()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer
        {
            Stream = new AudioStream { File = AudioTestUtil.Asset("stereo440_660.ogg"), LoadMode = AudioLoadMode.Stream },
        };
        var finished = 0;
        player.Finished += () => finished++;
        tree.Root.AddChild(player);
        player.Play();
        server.Flush();
        Assert.True(server.WaitForStreams(30000, TimeSpan.FromSeconds(5)));

        // Play the whole 0.5 s file on the audio side only: its end is reported, but the game has not seen it yet.
        var capture = new float[Rate * 2];
        server.RenderNullDevice(Rate, capture);
        Assert.True(player.Playing);

        player.Seek(0.1f); // races the pending finish
        server.RenderNullDevice(Rate / 10, capture);
        Assert.True(AudioTestUtil.Peak(capture.AsSpan(0, Rate / 5), 0) < 1e-4f, "the ended voice came back");

        tree.Tick(AudioTestUtil.Frame);
        Assert.False(player.Playing);
        Assert.Equal(1, finished);
        Assert.Equal(0, server.Stats.ActiveVoices);
        server.RenderNullDevice(Rate / 10, capture);
        Assert.True(AudioTestUtil.Peak(capture.AsSpan(0, Rate / 5), 0) < 1e-4f);
        tree.Shutdown();
    }

    [Fact]
    public void SeekingAStreamRestartsItThere()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AudioPlayer
        {
            Stream = new AudioStream { File = AudioTestUtil.Asset("sine440_mono16_44k.wav"), LoadMode = AudioLoadMode.Stream },
        };
        tree.Root.AddChild(player);
        player.Play();
        server.Flush();
        Assert.True(server.WaitForStreams(4096, TimeSpan.FromSeconds(5)));
        AudioTestUtil.Render(server, 2048, tree);

        player.Seek(0.3f);
        server.Flush();
        Assert.True(server.WaitForStreams(2048, TimeSpan.FromSeconds(5)));
        var output = AudioTestUtil.Render(server, 2048, tree);
        Assert.True(AudioTestUtil.Peak(output, 0) > 0.4f);
        Assert.InRange(player.GetPlaybackPosition(), 0.33, 0.36);
        tree.Shutdown();
    }

    [Fact]
    public void AStreamThatFailsEndsItsVoiceWithoutFinished()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-audio-fail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "gone.ogg");
            File.Copy(AudioTestUtil.Asset("stereo440_660.ogg"), file);
            var stream = new AudioStream { File = file, LoadMode = AudioLoadMode.Stream };
            Assert.True(stream.Preload()); // probed while the file exists
            File.Delete(file); // ...then it disappears before the streaming thread opens it

            var tree = new SceneTree();
            using var server = AudioTestUtil.CreateServer(tree);
            var player = new AudioPlayer { Stream = stream };
            var finished = 0;
            player.Finished += () => finished++;
            tree.Root.AddChild(player);
            player.Play();
            server.Flush();
            Assert.True(server.WaitForStreams(1, TimeSpan.FromSeconds(5)));
            AudioTestUtil.Render(server, 512, tree);
            tree.Tick(AudioTestUtil.Frame);

            Assert.False(player.Playing);
            Assert.Equal(0, finished);
            Assert.Equal(1, server.Stats.StreamErrors);
            tree.Shutdown();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AConsumerNeverReadsTheNextGenerationsSamples()
    {
        var channel = new AudioStreamChannel(0);
        channel.BeginGeneration(1, 1);
        channel.Write([1f, 1f, 1f, 1f]);
        Assert.Equal(1, channel.TrySync(1));
        Assert.Equal(4, channel.Readable(channel.ReadLimit(1)));
        channel.Advance(1);

        // The producer moves on to generation 2 and writes its samples.
        channel.BeginGeneration(2, 2);
        channel.Write([2f, 2f, 2f, 2f, 2f, 2f]);
        Assert.Equal(3, channel.Readable(channel.ReadLimit(1))); // generation 1 still sees only its own 3 left
        Assert.Equal(0, channel.TrySync(1)); // and cannot sync to it any more
        Assert.Equal(2, channel.TrySync(2));
        Assert.Equal(6, channel.Readable(channel.ReadLimit(2)));
        Assert.Equal(2f, channel.Peek(0));
    }

    [Fact]
    public void NonFinitePitchAndVelocitiesAreSanitized()
    {
        var source = new VoiceSource(Rate) { TargetPitch = float.NaN };
        Assert.Equal(1f, source.TargetPitch);
        source.TargetPitch = -2f;
        Assert.Equal(1f, source.TargetPitch);
        source.TargetPitch = 100f;
        Assert.Equal(16f, source.TargetPitch);

        var nan = new Vector3(float.NaN);
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, nan, new Vector3(0, 0, -5), Vector3.Zero, 343f));
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, nan, Vector3.Zero, 343f));
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -5), new Vector3(float.PositiveInfinity), 343f));
    }

    [Fact]
    public void SwitchingListenersDoesNotShiftDopplerPitch()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var camera = new Camera3D();
        tree.Root.AddChild(camera);
        var emitter = new AudioPlayer3D
        {
            Stream = AudioTestUtil.Sine(1000f, 5f),
            AttenuationModel = AttenuationModel.Disabled,
            DopplerTracking = true,
            Position = new Vector3(0, 0, -50),
        };
        tree.Root.AddChild(emitter);
        tree.Tick(AudioTestUtil.Frame);
        emitter.Play();
        AudioTestUtil.Render(server, Rate / 10, tree);

        // A listener 300 units away becomes current: that is not a listener moving at 18 000 units/s.
        tree.Root.AddChild(new AudioListener3D { Current = true, Position = new Vector3(0, 0, 300) });
        var capture = new List<float>();
        for (var i = 0; i < 12; i++)
            capture.AddRange(AudioTestUtil.Render(server, Rate / 60, tree));
        Assert.InRange(AudioTestUtil.Frequency(capture.ToArray(), 2, 0, Rate), 995f, 1005f);
        Assert.Equal(Vector3.Zero, server.ListenerVelocity);
        tree.Shutdown();
    }

    [Fact]
    public void BatchesArePublishedWholeOrNotAtAll()
    {
        using var server = AudioServer.Create(new AudioOptions
        {
            Device = AudioDeviceMode.NullManual,
            BusLayoutPath = null,
            CommandQueueCapacity = 64,
        });
        server.RenderNullDevice(1); // drain the initial graph swap
        var tone = AudioTestUtil.Constant(0.1f, 1f);
        for (var i = 0; i < 25; i++) // 25 plays + 25 stops = 50 commands
            server.Stop(server.PlayOneShot(tone));
        server.Flush();
        Assert.Equal(0, server.UnsentCommandCount);

        for (var i = 0; i < 15; i++) // 30 more: only 14 slots are free
            server.Stop(server.PlayOneShot(tone));
        server.Flush();
        Assert.Equal(30, server.UnsentCommandCount); // not split: nothing sent

        server.RenderNullDevice(1); // the audio thread drains the ring...
        server.Flush(); // ...and the whole batch then fits
        Assert.Equal(0, server.UnsentCommandCount);
    }

    [Fact]
    public void AGraphWhoseSwapWasNeverSentIsDisposedNotLeaked()
    {
        using var server = AudioServer.Create(new AudioOptions
        {
            Device = AudioDeviceMode.NullManual,
            BusLayoutPath = null,
            CommandQueueCapacity = 64,
        });
        server.RenderNullDevice(1);
        var tone = AudioTestUtil.Constant(0.1f, 1f);
        for (var i = 0; i < 32; i++) // fill the ring (64 commands) without rendering
            server.Stop(server.PlayOneShot(tone));
        server.Flush();

        server.ApplyBusLayout(AudioBusLayout.CreateDefault()); // its swap cannot be sent
        Assert.Equal(1, server.RetiringGraphCount); // the original graph, still in use by the audio thread
        server.ApplyBusLayout(AudioBusLayout.CreateDefault()); // supersedes the unsent one, which is disposed now
        Assert.Equal(1, server.RetiringGraphCount);

        server.RenderNullDevice(1); // old commands drain; the newest swap is published at the next flush
        server.Process(AudioTestUtil.Frame);
        server.RenderNullDevice(1); // swap applied: the original graph is retired
        server.Process(AudioTestUtil.Frame);
        Assert.Equal(0, server.RetiringGraphCount);
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 1f));
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 512)[1000], 3);
    }
}
