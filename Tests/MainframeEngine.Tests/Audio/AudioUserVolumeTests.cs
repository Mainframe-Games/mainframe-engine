namespace MainframeEngine.Tests.Audio;

/// <summary>
/// <see cref="AudioBus.UserVolume"/>: the player's volume, a second gain stage on top of the designer's fader
/// (gain = DbToLinear(VolumeDb) · UserVolume²), measured on the null device's output.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AudioUserVolumeTests
{
    private const int Rate = AudioTestUtil.Rate;

    [Fact]
    public void UserVolumeMultipliesTheFaderSquared()
    {
        using var server = AudioTestUtil.CreateServer();
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f), bus: "SFX");
        Assert.Equal(0.5f, AudioTestUtil.Render(server, 512)[1000], 3);

        var sfx = server.GetBus("SFX")!;
        sfx.UserVolume = 0.5f; // 0.5² = 0.25 ≈ −12 dB
        Assert.Equal(0.125f, AudioTestUtil.Render(server, Rate / 4)[^2], 1e-3f);
        Assert.Equal(0.25f, sfx.EffectiveGain, 1e-5f);

        sfx.VolumeDb = -6.0206f; // the designer's fader still applies: 0.5 × 0.25
        Assert.Equal(0.0625f, AudioTestUtil.Render(server, Rate / 4)[^2], 1e-3f);
        Assert.Equal(0.125f, sfx.EffectiveGain, 1e-4f);

        server.Master.UserVolume = 0.5f; // stages multiply down the bus tree
        Assert.Equal(0.015625f, AudioTestUtil.Render(server, Rate / 4)[^2], 1e-3f);
    }

    [Fact]
    public void ZeroIsSilent()
    {
        using var server = AudioTestUtil.CreateServer();
        server.PlayOneShot(AudioTestUtil.Constant(0.5f, 2f), bus: "Music");
        server.PlayOneShot(AudioTestUtil.Constant(0.25f, 2f), bus: "SFX");
        Assert.Equal(0.75f, AudioTestUtil.Render(server, 512)[1000], 3);

        server.GetBus("Music")!.UserVolume = 0f;
        Assert.Equal(0.25f, AudioTestUtil.Render(server, Rate / 10)[^2], 3);
        Assert.Equal(0f, server.GetBus("Music")!.EffectiveGain);

        server.Master.UserVolume = 0f;
        Assert.Equal(0f, AudioTestUtil.Render(server, Rate / 10)[^2], 4);
    }

    [Fact]
    public void ValuesAreClampedToTheSliderRange()
    {
        using var server = AudioTestUtil.CreateServer();
        var bus = server.GetBus("UI")!;
        Assert.Equal(1f, bus.UserVolume); // default
        bus.UserVolume = 3f;
        Assert.Equal(1f, bus.UserVolume);
        bus.UserVolume = -1f;
        Assert.Equal(0f, bus.UserVolume);
        bus.UserVolume = float.NaN;
        Assert.Equal(1f, bus.UserVolume);
    }

    [Fact]
    public void UserVolumeIsNotPartOfTheBusLayout()
    {
        using var server = AudioTestUtil.CreateServer();
        var music = server.GetBus("Music")!;
        music.VolumeDb = -3f;
        music.UserVolume = 0.2f;

        var layout = server.GetBusLayout();
        Assert.Equal(-3f, layout.Buses.Single(b => b.Name == "Music").VolumeDb); // the mix only
        Assert.DoesNotContain(typeof(AudioBusInfo).GetProperties(), p => p.Name.Contains("User", StringComparison.Ordinal));

        // An editor round trip of the layout leaves the player's volume behind.
        server.ApplyBusLayout(layout);
        Assert.Equal(-3f, server.GetBus("Music")!.VolumeDb);
        Assert.Equal(1f, server.GetBus("Music")!.UserVolume);
    }

    [Fact]
    public void ChangingTheUserVolumeDoesNotAllocate()
    {
        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        var loop = AudioTestUtil.Sine(330f, 1f);
        loop.Loop = true;
        var player = new AudioPlayer { Stream = loop, Bus = "Music", Autoplay = true };
        tree.ChangeScene(new Node { Name = "Scene" });
        tree.CurrentScene!.AddChild(player);
        var music = server.GetBus("Music")!;
        var buffer = new float[800 * 2];

        void Frame(int i)
        {
            music.UserVolume = (i % 100) / 100f; // a slider drag: one change per frame
            server.Master.UserVolume = 1f - (i % 7) / 10f;
            tree.Tick(AudioTestUtil.Frame);
            server.RenderNullDevice(800, buffer);
        }

        for (var i = 0; i < 240; i++) // warm-up
            Frame(i);
        var next = 240;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var end = next + 300; next < end; next++)
                Frame(next);
        });

        Assert.Equal(0, allocated);
        Assert.False(server.Stats.Faulted);
        tree.Shutdown();
    }
}
