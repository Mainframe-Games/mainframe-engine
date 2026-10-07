namespace MainframeEngine.Tests.Audio;

[Collection(nameof(Scene.SerialResources))]
public sealed class AudioStreamGeneratorTests
{
    private const int Rate = AudioTestUtil.Rate;

    [Fact]
    public void PushedFramesPlayExactlyAtTheDeviceRate()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new AudioStreamGenerator { MixRate = Rate, BufferSeconds = 0.1f };
        var handle = server.PlayOneShot(stream, bus: "SFX");
        var playback = server.GetGeneratorPlayback(handle);
        Assert.NotNull(playback);
        Assert.Equal(stream.BufferFrames, playback.FramesAvailable);

        var frames = new float[1024 * 2];
        for (var i = 0; i < 1024; i++)
        {
            frames[2 * i] = i / 1024f;
            frames[2 * i + 1] = -0.25f;
        }

        Assert.True(playback.PushFrames(frames));
        Assert.Equal(stream.BufferFrames - 1024, playback.FramesAvailable);
        var output = AudioTestUtil.Render(server, 1000);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(i / 1024f, output[2 * i], 5);
            Assert.Equal(-0.25f, output[2 * i + 1], 5);
        }

        Assert.Equal(0, playback.Underruns);
    }

    [Fact]
    public void UnderrunPlaysSilenceAndCounts()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new AudioStreamGenerator { MixRate = Rate };
        var handle = server.PlayOneShot(stream, bus: "SFX");
        var playback = server.GetGeneratorPlayback(handle)!;
        AudioTestUtil.Render(server, 256); // nothing pushed yet: silence, not an underrun
        Assert.Equal(0, playback.Underruns);

        var block = new float[256 * 2];
        Array.Fill(block, 0.5f);
        playback.PushFrames(block);
        var output = AudioTestUtil.Render(server, 1024);
        Assert.Equal(0.5f, output[2], 4);
        Assert.Equal(0f, output[^2]);
        Assert.True(playback.Underruns > 0);
        Assert.True(server.IsPlaying(handle)); // generators never end on their own
    }

    [Fact]
    public void PushIsAllOrNothingAndClearDropsQueuedFrames()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new AudioStreamGenerator { MixRate = Rate, BufferSeconds = 0.01f };
        var handle = server.PlayOneShot(stream, bus: "SFX");
        var playback = server.GetGeneratorPlayback(handle)!;
        var tooMany = new float[(playback.FramesAvailable + 1) * 2];
        Assert.False(playback.PushFrames(tooMany));
        Assert.False(playback.PushFrames(new float[3]));
        Assert.Equal(playback.CapacityFrames, playback.FramesAvailable);

        var ones = new float[200 * 2];
        Array.Fill(ones, 1f);
        Assert.True(playback.PushFrames(ones));
        playback.ClearBuffer();
        var output = AudioTestUtil.Render(server, 128);
        Assert.Equal(0f, AudioTestUtil.Peak(output, 0));
        Assert.Equal(playback.CapacityFrames, playback.FramesAvailable);
    }

    [Fact]
    public void OtherVoicesHaveNoGeneratorPlaybackAndEachPlayHasItsOwnRing()
    {
        using var server = AudioTestUtil.CreateServer();
        var tone = server.PlayOneShot(AudioTestUtil.Constant(0.5f, 1f));
        Assert.Null(server.GetGeneratorPlayback(tone));
        var stream = new AudioStreamGenerator();
        var a = server.GetGeneratorPlayback(server.PlayOneShot(stream));
        var b = server.GetGeneratorPlayback(server.PlayOneShot(stream));
        Assert.NotNull(a);
        Assert.NotSame(a, b);
        Assert.Equal(44100, a.SampleRate);
    }

    [Fact]
    public void ProducerOnAnotherThreadStreamsWithoutLoss()
    {
        using var server = AudioTestUtil.CreateServer();
        var stream = new AudioStreamGenerator { MixRate = Rate, BufferSeconds = 0.05f };
        var handle = server.PlayOneShot(stream, bus: "SFX");
        var playback = server.GetGeneratorPlayback(handle)!;
        const int total = 48000;
        var producer = new Thread(() =>
        {
            var frame = new float[2];
            for (var i = 0; i < total;)
            {
                frame[0] = frame[1] = (i % 100) / 100f;
                if (playback.PushFrames(frame))
                    i++;
                else
                    Thread.Yield();
            }
        });
        producer.Start();
        var next = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var capture = new float[256 * 2];
        while (playback.FramesConsumed < total - 2 && DateTime.UtcNow < deadline)
        {
            if (playback.FramesQueued < 512 && producer.IsAlive)
            {
                Thread.Sleep(1);
                continue;
            }

            server.RenderNullDevice(256, capture);
            for (var i = 0; i < 256; i++)
            {
                if (capture[2 * i] == 0f && next % 100 != 0)
                    continue; // underrun silence near the end
                Assert.Equal((next % 100) / 100f, capture[2 * i], 5);
                next++;
            }
        }

        producer.Join();
        Assert.True(next >= total - 2);
    }
}
