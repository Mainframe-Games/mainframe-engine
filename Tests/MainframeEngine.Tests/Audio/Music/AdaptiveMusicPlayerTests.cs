using System.Text;
using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;
using static MainframeEngine.Tests.Audio.Music.MusicTestStems;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// <c>.mmusic</c> through <see cref="ResourceLoader"/> (path and the file's own <c>mus_</c> UID), scenes referencing it, and
/// the <see cref="AdaptiveMusicPlayer"/> node in a tree: autoplay, forwarded signals, tree pause, leaving the tree, edit
/// mode; and the bus layout's <see cref="AudioBusInfo.MusicVoices"/>.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AdaptiveMusicPlayerTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-mmusic-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;
    private readonly string _uid = AssetUid.Generate(AssetUid.MusicPrefix);

    public AdaptiveMusicPlayerTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "Assets", "Music", "Theme.stems"));
        WriteStem("explore.base.wav", 2, 0.25f, 1f);
        WriteStem("combat.base.wav", 2, 0.5f, 2f);
        var manifest = Manifest(
            Section("explore", 2, stems: ("base", new MusicStemRef { Uid = "aud_unknown", Path = "Assets/Music/Theme.stems/explore.base.wav" }, null)),
            Section("combat", 2, stems: ("base", new MusicStemRef { Path = "Assets/Music/Theme.stems/combat.base.wav" }, null)));
        manifest.Uid = _uid;
        File.WriteAllBytes(Path.Combine(_project, "Assets", "Music", "Theme.mmusic"), MusicManifestFormat.Write(manifest));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AudioClipCache.Clear();
        AssetDatabase.Current = _previous;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
            // a stem may still be open on the streaming thread
        }
    }

    [Fact]
    public void MmusicLoadsThroughTheImporterWithItsOwnUid()
    {
        var music = ResourceLoader.Load<AdaptiveMusic>("Assets/Music/Theme.mmusic");
        Assert.Equal(_uid, music.Uid);
        Assert.Equal("Assets/Music/Theme.mmusic", music.ResourcePath);
        Assert.Equal(["explore", "combat"], music.Plan.Sections.Select(s => s.Id));
        Assert.Same(music, ResourceLoader.Load<AdaptiveMusic>(_uid));
        Assert.True(music.Preload()); // an unknown stem UID falls back to the path
        Assert.IsType<AdaptiveMusicImporter>(AssetImporters.Find("x.mmusic"));
        music.Release();
    }

    [Fact]
    public void ScenesReferenceTheMusicByUidAndThePlayerPlaysIt()
    {
        var music = ResourceLoader.Load<AdaptiveMusic>("Assets/Music/Theme.mmusic");
        var root = new Node { Name = "Level" };
        var player = new AdaptiveMusicPlayer
        {
            Name = "Music",
            Music = music,
            Autoplay = true,
            VolumeDb = -6f,
            StartSection = "combat",
            ScheduleAhead = 0.2f,
        };
        root.AddChild(player);
        player.Owner = root;
        var json = SceneSaver.ToJson(root, "scn_00000000a0e1");
        Assert.Contains(_uid, Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        root.Free();

        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        tree.ChangeScene(PackedScene.Parse(json).Instantiate());
        var loaded = tree.CurrentScene!.GetNode<AdaptiveMusicPlayer>("Music");
        Assert.Equal(_uid, loaded.Music!.Uid);
        Assert.Equal((-6f, "combat", 0.2f), (loaded.VolumeDb, loaded.StartSection, loaded.ScheduleAhead));
        Assert.True(loaded.Playing);

        var signals = new List<string>();
        loaded.SectionStarted += s => signals.Add("section " + s);
        loaded.Bar += b => signals.Add("bar " + b);
        server.Flush();
        Assert.True(server.WaitForStreams(4096, TimeSpan.FromSeconds(5)));
        var output = AudioTestUtil.Render(server, 4096, tree);
        Assert.Equal(2f * AudioMath.DbToLinear(-6f), output[0], 1e-4f); // combat's downbeat at −6 dB
        AudioTestUtil.Render(server, 800, tree);
        Assert.Equal(["section combat", "bar 1"], signals);
        Assert.Equal("combat", loaded.CurrentSection);
        Assert.Equal(new MusicPosition("combat", 1, 1, 0f, 0), loaded.GetPosition() with { BeatFraction = 0f, Seconds = 0 });

        tree.Paused = true; // Pausable: the clock holds and the music is silent
        AudioTestUtil.Render(server, 800, tree);
        Assert.Equal(0f, AudioTestUtil.Peak(AudioTestUtil.Render(server, 800, tree), 0));
        tree.Paused = false;
        AudioTestUtil.Render(server, 800, tree);
        Assert.True(AudioTestUtil.Peak(AudioTestUtil.Render(server, 800, tree), 0) > 0.2f);

        tree.CurrentScene!.RemoveChild(loaded); // leaving the tree stops it
        Assert.False(loaded.Playing);
        Assert.Null(loaded.Instance);
        AudioTestUtil.Render(server, 2400, tree);
        Assert.Equal(0f, AudioTestUtil.Peak(AudioTestUtil.Render(server, 800, tree), 0));
        loaded.Free();
        tree.Shutdown();
    }

    [Fact]
    public void ThePlayerIsInertInTheEditorsEditMode()
    {
        var tree = new SceneTree { EditMode = true };
        using var server = AudioTestUtil.CreateServer(tree);
        var player = new AdaptiveMusicPlayer { Music = ResourceLoader.Load<AdaptiveMusic>(_uid), Autoplay = true };
        tree.ChangeScene(player);
        Assert.Null(player.Instance);
        Assert.False(player.Playing);
        player.Play();
        Assert.False(player.Playing);
        tree.Shutdown();
    }

    [Fact]
    public void MusicVoicesAreTwoOnTheMusicBusByDefaultAndRoundTripTheLayout()
    {
        using var server = AudioTestUtil.CreateServer();
        Assert.Equal(2, server.TotalMusicVoices);
        Assert.Equal(0, server.GetBus("SFX")!.MusicVoices);
        Assert.All(server.GetBusLayout().Buses, b => Assert.Equal(AudioBusInfo.AutoMusicVoices, b.MusicVoices));

        var layout = AudioBusLayout.CreateDefault();
        layout.Buses[1].MusicVoices = 0;
        layout.Buses[2].MusicVoices = 3;
        server.ApplyBusLayout(layout);
        Assert.Equal(3, server.TotalMusicVoices);
        Assert.Equal(3, server.GetBus("SFX")!.MusicVoices);
        Assert.Equal(3, server.GetBusLayout().Buses[2].MusicVoices);

        layout.Buses[2].MusicVoices = 17;
        Assert.NotNull(layout.Validate());
    }

    [Fact]
    public void ALayoutSwapStopsThePlayingMusicWithoutFinished()
    {
        using var server = AudioTestUtil.CreateServer();
        var music = ResourceLoader.Load<AdaptiveMusic>(_uid);
        music.PreloadSections.AddRange(["explore", "combat"]);
        using var instance = new AdaptiveMusicInstance(server, music);
        var finished = false;
        instance.Finished += () => finished = true;
        instance.Play();
        AudioTestUtil.Render(server, 800);
        Assert.True(instance.Playing);

        server.ApplyBusLayout(AudioBusLayout.CreateDefault());
        Assert.False(instance.Playing);
        AudioTestUtil.Render(server, 800);
        instance.Play(); // a voice of the new graph
        Assert.True(instance.Playing);
        Assert.Equal(1f, AudioTestUtil.Render(server, 800)[0], 1e-5f);
        Assert.False(finished);
    }

    private void WriteStem(string name, int bars, float dc, float impulse)
    {
        var samples = new float[bars * Bar];
        Array.Fill(samples, dc);
        samples[0] = impulse;
        WavWriter.Write(Path.Combine(_project, "Assets", "Music", "Theme.stems", name), samples, 1, Rate, WavSampleFormat.IeeeFloat);
    }
}
