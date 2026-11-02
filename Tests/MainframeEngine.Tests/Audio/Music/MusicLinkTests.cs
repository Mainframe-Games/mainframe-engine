using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;
using MainframeEngine.EditorLink;
using MainframeEngine.Tests.Project;
using static MainframeEngine.Tests.Audio.Music.MusicTestStems;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// The editor link's adaptive music on the game side (protocol 6, <see cref="MusicLink"/> through <see cref="GameSession"/>):
/// the commands drive a real <see cref="AdaptiveMusicPlayer"/> on the manual null device, reports go out only while the
/// editor watches, and <c>ReloadMusic</c> switches a playing player to a new bake read from the editor's project folder at
/// its next pass, keeping the parameters.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class MusicLinkTests : IDisposable
{
    private readonly MusicTestStems _stems = new();
    private readonly AssetDatabase _previous = AssetDatabase.Current;
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AudioClipCache.Clear();
        AssetDatabase.Current = _previous;
        _stems.Dispose();
        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a stem may still be open on the streaming thread
            }
        }
    }

    // Explore and Combat (one bar each, looping), a Hit stinger; area (Calm / Combat) moves Explore to Combat at the bar.
    private MusicManifest Theme(float exploreDc = 0.25f)
    {
        var manifest = Manifest(
            Section("explore", 1, stems: ("base", _stems.Body(1, exploreDc, 1f), null)),
            Section("combat", 1, stems: ("base", _stems.Body(1, 0.5f, 1f), null)),
            Section("hit", 1, loop: false, kind: MusicSectionKind.Stinger));
        manifest.Sections[2].Stems[MusicManifestSection.MixStem] = new MusicStemSet { Body = _stems.Impulse((int)Bar, 0, 0.9f) };
        manifest.Uid = "mus_00000000a6a6";
        manifest.Song = "sng_00000000a6a6";
        manifest.Parameters.Add(new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1 });
        manifest.Parameters.Add(new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] });
        manifest.Transitions.Add(new MusicTransitionInfo
        {
            From = ["explore"],
            To = "combat",
            Quantize = MusicQuantize.Bar,
            When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue("Combat") }] },
        });
        return manifest;
    }

    private static (SceneTree Tree, AudioServer Server, AdaptiveMusicPlayer Player) Play(AdaptiveMusic music)
    {
        var tree = new SceneTree();
        var server = AudioTestUtil.CreateServer(tree);
        var level = new Node { Name = "Level" };
        level.AddChild(new AdaptiveMusicPlayer { Name = "Music", Music = music, Autoplay = true });
        tree.ChangeScene(level);
        var player = level.GetNode<AdaptiveMusicPlayer>("Music");
        Assert.True(player.Playing);
        return (tree, server, player);
    }

    private static string Join(params string[] fields) => string.Join(EditorLinkProtocol.MusicArgumentSeparator, fields);

    [Fact]
    public void CommandsDriveThePlayerNamedByItsPath()
    {
        var (tree, server, player) = Play(InMemory(Theme()));
        var session = new GameSession(tree, new ProjectSettings { Name = "Music" });
        try
        {
            AudioTestUtil.Render(server, 800, tree);
            session.Apply(new EditorLinkCommand(EditorCommandKind.SetMusicParameter, Join("/root/Level/Music", "intensity", "0.75")));
            Assert.Equal(0.75f, player.Instance!.Director!.Parameters.GetTarget(0));
            session.Apply(new EditorLinkCommand(EditorCommandKind.SetMusicParameter, Join("/root/Level/Music", "area", "Combat"))); // a label
            Assert.Equal(1f, player.Instance.Director.Parameters.GetTarget(1));
            AudioTestUtil.Render(server, 800, tree);
            Assert.Equal("combat", player.PendingTransition);

            session.Apply(new EditorLinkCommand(EditorCommandKind.GoToMusicSection, Join("/root/Level/Music", "explore")));
            AudioTestUtil.Render(server, 800, tree);
            Assert.Equal("explore", player.PendingTransition); // GoToSection replaces the rule's transition

            var stung = false;
            player.Instance.Director.Parameters.SetTarget(1, 0, immediate: true);
            session.Apply(new EditorLinkCommand(EditorCommandKind.TriggerMusicStinger, Join("/root/Level/Music", "hit")));
            for (var i = 0; i < 60 && !stung; i++)
                stung = AudioTestUtil.Peak(AudioTestUtil.Render(server, 800, tree), 0) > 1f; // the stinger's impulse over the body
            Assert.True(stung);

            // A wrong path or a malformed argument is ignored (logged), never thrown.
            session.Apply(new EditorLinkCommand(EditorCommandKind.SetMusicParameter, Join("/root/Nope", "intensity", "1")));
            session.Apply(new EditorLinkCommand(EditorCommandKind.TriggerMusicStinger, "/root/Level/Music"));
            Assert.Equal(0.75f, player.Instance.Director.Parameters.GetTarget(0));
        }
        finally
        {
            session.Dispose();
            tree.Shutdown();
            server.Dispose();
        }
    }

    [Fact]
    public void ReportsGoOutOnlyWhileTheEditorWatches()
    {
        using var link = new EditorLinkServer();
        var (tree, server, player) = Play(InMemory(Theme()));
        var session = new GameSession(tree, new ProjectSettings { Name = "Music" }, new GameHostOptions { EditorPort = link.Port });
        var seen = new List<EditorLinkMessage>();
        var time = new GameTime { DeltaTime = 1f / 60f, FramesPerSecond = 60 };
        try
        {
            Wait.For(link, seen, m => m.Type == EditorLinkMessageType.Hello);
            Assert.True(Wait.Until(() => session.Link!.IsConnected));
            for (var i = 0; i < 20; i++)
            {
                session.Update(time);
                AudioTestUtil.Render(server, 800, tree);
            }

            Assert.True(Wait.Until(() =>
            {
                while (link.TryRead(out var m))
                    seen.Add(m);
                return seen.Exists(m => m.Type == EditorLinkMessageType.Status);
            }));
            Assert.DoesNotContain(seen, m => m.Type == EditorLinkMessageType.Music); // not watched: nothing

            link.SendCommand(new EditorLinkCommand(EditorCommandKind.WatchMusic));
            EditorLinkMessage report = default;
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                AudioTestUtil.Render(server, 800, tree);
                while (link.TryRead(out var m))
                {
                    if (m.Type == EditorLinkMessageType.Music && m.Music is [{ Section: "explore" }])
                        report = m;
                }

                return report.Type == EditorLinkMessageType.Music;
            }));
            var music = report.Music![0];
            Assert.Equal(("/root/Level/Music", "mus_00000000a6a6", "sng_00000000a6a6"), (music.NodePath, music.Music, music.Song));
            Assert.Equal(["intensity", "area"], music.Parameters.Select(p => p.Name));
            Assert.Equal(["base"], music.Layers.Select(l => l.Layer));
            Assert.Equal(1, music.Bar);

            player.SetParameter("area", "Combat"); // the pending transition shows with its countdown
            EditorLinkMessage pending = default;
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                AudioTestUtil.Render(server, 800, tree);
                while (link.TryRead(out var m))
                {
                    if (m.Type == EditorLinkMessageType.Music && m.Music is [{ Pending: "combat" }])
                        pending = m;
                }

                return pending.Type == EditorLinkMessageType.Music;
            }));
            Assert.InRange(pending.Music![0].PendingSeconds, 0f, 0.5f);

            link.SendCommand(new EditorLinkCommand(EditorCommandKind.UnwatchMusic));
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                return session.Music is { Watching: false };
            }));
            Thread.Sleep(100);
            while (link.TryRead(out _))
            {
            }

            for (var i = 0; i < 30; i++)
            {
                session.Update(time);
                AudioTestUtil.Render(server, 800, tree);
            }

            Thread.Sleep(200);
            var after = new List<EditorLinkMessage>();
            while (link.TryRead(out var m))
                after.Add(m);
            Assert.DoesNotContain(after, m => m.Type == EditorLinkMessageType.Music);
        }
        finally
        {
            session.Dispose();
            tree.Shutdown();
            server.Dispose();
        }
    }

    [Fact]
    public void ReloadMusicSwitchesAtTheNextPassFromTheEditorsProjectAndKeepsTheParameters()
    {
        // The game's own copy (its build output) and the editor's source project, where the new bake is.
        var build = Folder("build");
        var source = Folder("source");
        File.WriteAllBytes(Path.Combine(build, "Assets", "Music", "Theme.mmusic"), MusicManifestFormat.Write(Theme(exploreDc: 0.25f)));
        var rebaked = Theme(exploreDc: 0.125f);
        foreach (var section in rebaked.Sections)
        {
            foreach (var set in section.Stems.Values)
            {
                if (set?.Body is { } body)
                {
                    var name = Path.GetFileName(body.Path);
                    File.Copy(body.Path, Path.Combine(source, "Assets", "Music", "Theme.stems", name));
                    body.Path = "Assets/Music/Theme.stems/" + name; // project-relative: only the source project has it
                }
            }
        }

        File.WriteAllBytes(Path.Combine(source, "Assets", "Music", "Theme.mmusic"), MusicManifestFormat.Write(rebaked));
        AssetDatabase.Current = new AssetDatabase(build);
        ResourceLoader.ClearCache();
        var original = ResourceLoader.Load<AdaptiveMusic>("Assets/Music/Theme.mmusic");
        var (tree, server, player) = Play(original);
        var session = new GameSession(tree, new ProjectSettings { Name = "Music" });
        try
        {
            player.SetParameterImmediate("intensity", 0.6f);
            Assert.True(server.WaitForStreams(4096, TimeSpan.FromSeconds(5)));
            AudioTestUtil.Render(server, 800, tree);
            Assert.Equal(0.25f, AudioTestUtil.Render(server, 800, tree)[0], 1e-4f);

            session.Apply(new EditorLinkCommand(EditorCommandKind.ReloadMusic, Join("mus_00000000a6a6", source)));
            Assert.Equal(1, session.Music!.PendingReloads);
            var time = new GameTime { DeltaTime = 1f / 60f, FramesPerSecond = 60 };
            for (var i = 0; i < 400 && session.Music.PendingReloads > 0; i++)
            {
                session.Update(time);
                AudioTestUtil.Render(server, 800, tree);
                if (i < 10)
                    Thread.Sleep(20); // the worker loads the bake meanwhile
            }

            Assert.Equal(0, session.Music.PendingReloads);

            Assert.NotSame(original, player.Music);
            Assert.Equal("mus_00000000a6a6", player.Music!.Uid);
            Assert.True(player.Playing);
            Assert.Equal("explore", player.CurrentSection ?? player.Instance!.Director!.Plan.Sections[player.Instance.Director.CurrentSection].Id);
            Assert.Equal(0.6f, player.Instance!.Director!.Parameters.GetTarget(0));
            Assert.Equal(0, player.Music.MissingStems); // every stem came from the source project

            var heard = 0f;
            for (var i = 0; i < 120 && MathF.Abs(heard - 0.125f) >= 1e-4f; i++)
            {
                session.Update(time);
                server.WaitForStreams(1024, TimeSpan.FromMilliseconds(200));
                heard = AudioTestUtil.Render(server, 800, tree)[400];
            }

            Assert.True(MathF.Abs(heard - 0.125f) < 1e-4f, $"the new bake's body was not heard (last {heard})");
        }
        finally
        {
            session.Dispose();
            tree.Shutdown();
            server.Dispose();
            original.Release();
        }
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mf-music-link-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Assets", "Music", "Theme.stems"));
        _folders.Add(folder);
        return folder;
    }
}
