using System.Numerics;
using System.Text;

namespace MainframeEngine.Tests.Audio;

/// <summary>Audio nodes and their <see cref="AudioStream"/> resources round-trip through <c>.mscene</c> files.</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class AudioSerializationTests
{
    [Fact]
    public void AudioNodesRoundTripThroughSceneFiles()
    {
        var music = new AudioStream
        {
            File = "Content/Audio/theme.ogg",
            LoadMode = AudioLoadMode.Stream,
            Loop = true,
            LoopStart = 1.5f,
            LoopEnd = 30f,
        };
        var root = new Node3D { Name = "Level" };
        Own(root, root, new AudioPlayer
        {
            Name = "Music",
            Stream = music,
            Bus = "Music",
            VolumeDb = -6f,
            PitchScale = 1.25f,
            Autoplay = true,
            Loop = true,
            MaxPolyphony = 2,
            Priority = 10,
            ProcessMode = ProcessMode.Always,
        });
        var hud = Own(root, root, new Node2D { Name = "Hud" });
        Own(root, hud, new AudioPlayer2D
        {
            Name = "Coin",
            Stream = new AudioStream { File = "Content/Audio/coin.wav" },
            Bus = "UI",
            Position = new Vector2(100, 50),
            MaxDistance = 800f,
            Attenuation = 2f,
            PanningStrength = 0.5f,
        });
        Own(root, root, new AudioPlayer3D
        {
            Name = "Engine",
            Stream = music, // shared: written once
            Bus = "SFX",
            Position = new Vector3(1, 2, 3),
            AttenuationModel = AttenuationModel.Custom,
            UnitSize = 2.5f,
            MaxDistance = 40f,
            RolloffFactor = 1.5f,
            CustomAttenuationCurve = [1f, 0.6f, 0.2f, 0f],
            LowPassAtMaxDistance = 1200f,
            PanningStrength = 0.75f,
            DopplerTracking = true,
        });
        Own(root, root, new AudioListener3D { Name = "Ears", Current = true, Position = new Vector3(0, 1.7f, 0) });

        var json = SceneSaver.ToJson(root, "scn_00000000a0d1");
        var text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"type\": \"AudioPlayer3D\"", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "theme.ogg")); // the shared stream is one table entry

        var loaded = PackedScene.Parse(json).Instantiate();
        var player = loaded.GetNode<AudioPlayer>("Music");
        Assert.Equal("Music", player.Bus);
        Assert.Equal(-6f, player.VolumeDb);
        Assert.Equal(1.25f, player.PitchScale);
        Assert.True(player.Autoplay && player.Loop);
        Assert.Equal(2, player.MaxPolyphony);
        Assert.Equal(10, player.Priority);
        Assert.Equal(ProcessMode.Always, player.ProcessMode);
        var stream = player.Stream!;
        Assert.Equal("Content/Audio/theme.ogg", stream.File);
        Assert.Equal(AudioLoadMode.Stream, stream.LoadMode);
        Assert.True(stream.Loop);
        Assert.Equal(1.5f, stream.LoopStart);
        Assert.Equal(30f, stream.LoopEnd);

        var coin = loaded.GetNode<AudioPlayer2D>("Hud/Coin");
        Assert.Equal(new Vector2(100, 50), coin.Position);
        Assert.Equal(800f, coin.MaxDistance);
        Assert.Equal(2f, coin.Attenuation);
        Assert.Equal(0.5f, coin.PanningStrength);
        Assert.Equal("Content/Audio/coin.wav", coin.Stream!.File);

        var engine = loaded.GetNode<AudioPlayer3D>("Engine");
        Assert.Same(stream, engine.Stream);
        Assert.Equal(AttenuationModel.Custom, engine.AttenuationModel);
        Assert.Equal(2.5f, engine.UnitSize);
        Assert.Equal(40f, engine.MaxDistance);
        Assert.Equal(1.5f, engine.RolloffFactor);
        Assert.Equal([1f, 0.6f, 0.2f, 0f], engine.CustomAttenuationCurve ?? []);
        Assert.Equal(1200f, engine.LowPassAtMaxDistance);
        Assert.Equal(0.75f, engine.PanningStrength);
        Assert.True(engine.DopplerTracking);
        Assert.True(loaded.GetNode<AudioListener3D>("Ears").Current);

        // Saving the loaded copy writes the same file (only non-default values, stable order).
        Assert.Equal(text, Encoding.UTF8.GetString(SceneSaver.ToJson(loaded, "scn_00000000a0d1")));
        root.Free();
        loaded.Free();
    }

    [Fact]
    public void DefaultAudioNodesWriteNoProperties()
    {
        var root = new Node { Name = "Root" };
        Own(root, root, new AudioPlayer3D { Name = "Silent" });
        var text = Encoding.UTF8.GetString(SceneSaver.ToJson(root, "scn_00000000a0d2"));
        Assert.DoesNotContain("\"props\"", text, StringComparison.Ordinal);
        root.Free();
    }

    [Fact]
    public void SceneAudioNodesPlayOnceInsideATreeWithAnAudioServer()
    {
        var root = new Node3D { Name = "Level" };
        Own(root, root, new AudioPlayer3D
        {
            Name = "Hum",
            Stream = new AudioStream { File = AudioTestUtil.Asset("sine440_mono16_44k.wav"), Loop = true },
            Autoplay = true,
            UnitSize = 100f,
        });
        var scene = PackedScene.Parse(SceneSaver.ToJson(root, "scn_00000000a0d3"));
        root.Free();

        var tree = new SceneTree();
        using var server = AudioTestUtil.CreateServer(tree);
        tree.ChangeScene(scene.Instantiate());
        var hum = tree.CurrentScene!.GetNode<AudioPlayer3D>("Hum");
        Assert.True(hum.Playing);
        Assert.True(AudioTestUtil.Peak(AudioTestUtil.Render(server, 4096, tree), 0) > 0.4f);
        tree.Shutdown();
        Assert.Equal(0, server.Stats.ActiveVoices);
    }

    [Fact]
    public void TheShowcaseAmbiencePlaysFromItsSceneFile()
    {
        var previous = AssetDatabase.Current;
        AssetDatabase.Current = new AssetDatabase(AppContext.BaseDirectory); // "Content/..." resolves like a game's
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Content", "Scenes", "Showcase.mscene");
            var tree = new SceneTree();
            using var server = AudioTestUtil.CreateServer(tree);
            tree.ChangeScene(PackedScene.Parse(File.ReadAllBytes(path)).Instantiate());
            var ambience = tree.CurrentScene!.GetNode<AudioPlayer3D>("Box/Ambience");
            Assert.True(ambience.Stream!.IsStreamed);
            Assert.True(ambience.Playing);
            Assert.Equal(-18f, ambience.VolumeDb);

            server.Flush();
            Assert.True(server.WaitForStreams(4096, TimeSpan.FromSeconds(5)));
            var output = AudioTestUtil.Render(server, 4096, tree);
            Assert.True(AudioTestUtil.Peak(output, 0) > 0.001f, "the ambience is silent");
            Assert.True(AudioTestUtil.Peak(output, 0) < 0.2f, "the ambience should be quiet");
            tree.Shutdown();
        }
        finally
        {
            AssetDatabase.Current = previous;
        }
    }

    private static T Own<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
