using MainframeEngine.EditorLink;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Project;

public sealed class GameHostOptionsTests
{
    [Fact]
    public void EveryFlagParsesAndUnknownArgumentsAreKept()
    {
        var o = GameHostOptions.Parse(
        [
            "--project", "/p/project.mfproj", "--scene", "scn_0123456789ab", "--editor-port", "5123", "--max-frames", "30",
            "--fixed-fps", "60", "--hidden", "--no-vsync", "--no-log-file", "--validation", "--locale", "pt-BR",
            "--screenshot", "out/shot.png", "--my-game-flag", "x",
        ]);

        Assert.Equal("/p/project.mfproj", o.ProjectPath);
        Assert.Equal("scn_0123456789ab", o.Scene);
        Assert.Equal(5123, o.EditorPort);
        Assert.Equal(30, o.MaxFrames);
        Assert.Equal(60, o.FixedFps);
        Assert.True(o.Hidden && o.NoVSync && o.Validation);
        Assert.False(o.LogFile);
        Assert.Equal("pt-BR", o.Locale);
        Assert.Equal("out/shot.png", o.ScreenshotPath);
        Assert.Equal(30, o.ScreenshotFrame); // the last frame of --max-frames
        Assert.Equal(60, GameHostOptions.Parse(["--screenshot", "a.png"]).ScreenshotFrame);
        Assert.Equal(["--my-game-flag", "x"], o.Remaining);
        Assert.Empty(o.UserArgs);

        // After ++ nothing is parsed: the game may reuse a host flag's name.
        var user = GameHostOptions.Parse(["--max-frames", "5", "++", "--screenshot", "game.png", "++", "--max-frames"]);
        Assert.Equal(5, user.MaxFrames);
        Assert.Null(user.ScreenshotPath);
        Assert.Equal(["--screenshot", "game.png", "++", "--max-frames"], user.UserArgs);
        Assert.Empty(user.Remaining);
    }

    [Fact]
    public void DefaultsChangeNothing()
    {
        var o = GameHostOptions.Parse([]);
        var engine = new ProjectSettings { Name = "G" }.ToEngineOptions();
        var applied = o.Apply(engine);

        Assert.True(o.LogFile);
        Assert.Null(o.EditorPort);
        Assert.Equal(engine.MaxFrames, applied.MaxFrames);
        Assert.Equal(engine.WindowVisible, applied.WindowVisible);
        Assert.Equal(engine.VSync, applied.VSync);
        Assert.Equal(engine.FixedDeltaTime, applied.FixedDeltaTime);
        Assert.Equal(engine.Locale, applied.Locale);
        Assert.False(applied.EnableFrameCapture);
    }

    [Fact]
    public void OverridesApplyToEngineOptions()
    {
        var settings = new ProjectSettings { Name = "G" };
        settings.Localization.DefaultLocale = "es";
        var applied = GameHostOptions.Parse(["--max-frames", "5", "--fixed-fps", "50", "--hidden", "--no-vsync", "--validation",
                "--locale", "de", "--screenshot", "s.png"])
            .Apply(settings.ToEngineOptions());

        Assert.Equal(5, applied.MaxFrames);
        Assert.Equal(0.02f, applied.FixedDeltaTime, 6);
        Assert.False(applied.WindowVisible);
        Assert.False(applied.VSync);
        Assert.True(applied.EnableValidation);
        Assert.Equal("de", applied.Locale); // the player's choice beats the project's default locale
        Assert.True(applied.EnableFrameCapture);
    }

    [Theory]
    [InlineData("--scene")]
    [InlineData("--editor-port", "0")]
    [InlineData("--editor-port", "70000")]
    [InlineData("--editor-port", "port")]
    [InlineData("--max-frames", "-1")]
    [InlineData("--project", "--hidden")]
    [InlineData("--locale")]
    [InlineData("--screenshot")]
    public void BadValuesThrow(params string[] args)
    {
        var e = Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(args));
        Assert.Contains(args[0], e.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// <see cref="GameSession"/> on a headless tree: autoloads, start scene, reload, commands. Scenes go through the
/// process-wide loader cache and asset database, so this is serial with the other resource tests.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class GameSessionTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-session-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public GameSessionTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string SaveScene(string path, string rootName, params string[] children)
    {
        var root = new Node3D { Name = rootName };
        foreach (var child in children)
        {
            var node = new CounterNode { Name = child };
            root.AddChild(node);
            node.Owner = root;
        }

        return SceneSaver.Save(root, path);
    }

    private static ProjectSettings Settings(string? mainScene = null)
    {
        var settings = new ProjectSettings { Name = "SessionTest", MainScene = mainScene };
        settings.Input.Bind("jump", "key:Space");
        settings.Physics.MaxStepsPerFrame = 3;
        return settings;
    }

    [Fact]
    public void StartInstallsInputAutoloadsAndTheMainScene()
    {
        var mainUid = SaveScene("Content/Scenes/Main.mscene", "Main", "Player");
        SaveScene("Content/Autoload/Music.mscene", "MusicRoot");
        var settings = Settings(mainUid);
        settings.Autoloads.Add(new AutoloadSettings { Name = "Music", Scene = "Content/Autoload/Music.mscene" });
        settings.Autoloads.Add(new AutoloadSettings { Name = "Counter", Type = nameof(CounterNode) });
        settings.Autoloads.Add(new AutoloadSettings { Name = "Off", Type = nameof(CounterNode), Enabled = false });
        var tree = new SceneTree();
        using var session = new GameSession(tree, settings);

        Assert.True(session.Start());

        Assert.Same(settings.Input, tree.Input.Map);
        Assert.Equal(3, tree.MaxPhysicsStepsPerFrame);
        Assert.Equal(["Music", "Counter"], session.Autoloads.Select(n => n.Name));
        Assert.IsType<CounterNode>(tree.Root.GetNode("/root/Counter"));
        Assert.Equal("Music", tree.Root.GetNode("/root/Music").Name);
        Assert.Null(tree.Root.GetNodeOrNull("Off"));
        // Autoloads come first; the scene after them, so autoloads are ready when it enters.
        Assert.Equal(["Music", "Counter", "Main"], tree.Root.Children.Select(n => n.Name));
        Assert.Equal("Content/Scenes/Main.mscene", tree.CurrentScene!.SceneFilePath);
        Assert.NotNull(tree.CurrentScene.GetNodeOrNull("Player"));

        tree.Shutdown();
    }

    [Fact]
    public void SceneArgumentOverridesTheMainScene()
    {
        SaveScene("Content/Scenes/Main.mscene", "Main");
        SaveScene("Content/Scenes/Level2.mscene", "Level2");
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings("Content/Scenes/Main.mscene"), new GameHostOptions { Scene = "Content/Scenes/Level2.mscene" });

        Assert.Equal("Content/Scenes/Level2.mscene", session.StartScene);
        Assert.True(session.Start());
        Assert.Equal("Level2", tree.CurrentScene!.Name);
        tree.Shutdown();
    }

    [Fact]
    public void AMissingSceneFailsTheStartWithoutThrowing()
    {
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings("Content/Scenes/Nope.mscene"));

        Assert.False(session.Start());
        Assert.Null(tree.CurrentScene);
        tree.Shutdown();
    }

    [Fact]
    public void NoMainSceneRunsAnEmptyTree()
    {
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings());

        Assert.True(session.Start());
        Assert.Null(tree.CurrentScene);
        tree.Shutdown();
    }

    [Fact]
    public void BrokenAutoloadsAreSkipped()
    {
        var settings = Settings();
        settings.Autoloads.Add(new AutoloadSettings { Name = "Ghost", Type = "NoSuchType" });
        settings.Autoloads.Add(new AutoloadSettings { Name = "Missing", Scene = "Content/Nope.mscene" });
        settings.Autoloads.Add(new AutoloadSettings { Name = "Fine", Type = nameof(PlainNode) });
        var tree = new SceneTree();
        tree.Root.AddChild(new PlainNode { Name = "Taken" });
        settings.Autoloads.Add(new AutoloadSettings { Name = "Taken", Type = nameof(PlainNode) });
        using var session = new GameSession(tree, settings);

        Assert.True(session.Start());
        Assert.Equal(["Fine"], session.Autoloads.Select(n => n.Name));
        tree.Shutdown();
    }

    [Fact]
    public void ReloadScenePicksUpTheFileAsSavedByTheEditor()
    {
        var uid = SaveScene("Content/Scenes/Main.mscene", "Main", "Old");
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings(uid));
        session.Start();
        var before = tree.CurrentScene!;

        // The editor rewrites the file (another process: the cached scene is not updated).
        var edited = new Node3D { Name = "Main" };
        var added = new CounterNode { Name = "New" };
        edited.AddChild(added);
        added.Owner = edited;
        File.WriteAllBytes(Path.Combine(_project, "Content/Scenes/Main.mscene"), SceneSaver.ToJson(edited, uid));

        session.Apply(new EditorLinkCommand(EditorCommandKind.ReloadScene));

        Assert.NotSame(before, tree.CurrentScene);
        Assert.True(before.IsFreed);
        Assert.NotNull(tree.CurrentScene!.GetNodeOrNull("New"));
        Assert.Null(tree.CurrentScene.GetNodeOrNull("Old"));
        Assert.False(session.ReloadScene("Content/Scenes/Gone.mscene"));
        Assert.NotNull(tree.CurrentScene); // a failed reload keeps the running scene
        tree.Shutdown();
    }

    [Fact]
    public void CommandsPauseResumeAndStop()
    {
        var tree = new SceneTree();
        using var session = new GameSession(tree, Settings());
        ExitCode? quit = null;
        session.QuitRequested += code => quit = code;
        session.Start();

        session.Apply(new EditorLinkCommand(EditorCommandKind.Pause));
        Assert.True(tree.Paused);
        session.Apply(new EditorLinkCommand(EditorCommandKind.Resume));
        Assert.False(tree.Paused);
        session.Apply(new EditorLinkCommand(EditorCommandKind.Ping));
        Assert.Null(quit);
        session.Apply(new EditorLinkCommand(EditorCommandKind.Stop));
        Assert.Equal(ExitCode.Ok, quit);
        tree.Shutdown();
    }

    [Fact]
    public void GameAssembliesLoadByNameAndBadNamesAreLogged()
    {
        var settings = Settings();
        settings.Assemblies.Add(typeof(GameSessionTests).Assembly.GetName().Name!);
        settings.Assemblies.Add("No.Such.Game.Assembly");

        var loaded = GameSession.LoadGameAssemblies(settings, typeof(Node).Assembly);

        Assert.Equal([typeof(Node).Assembly, typeof(GameSessionTests).Assembly], loaded);
    }
}

public sealed class GameSessionTreeSnapshotTests
{
    [Fact]
    public void TheSnapshotIsDepthFirstInChildOrderAndCappedAtTheProtocolLimit()
    {
        var root = new Node { Name = "root" };
        var a = new Node { Name = "A" };
        a.AddChild(new Node2D { Name = "A1" });
        a.AddChild(new Node { Name = "A2" });
        root.AddChild(a);
        root.AddChild(new Node3D { Name = "B" });

        var nodes = GameSession.SnapshotTree(root, out var truncated);
        Assert.False(truncated);
        Assert.Equal(
            [new(0, "root", "Node"), new(1, "A", "Node"), new(2, "A1", "Node2D"), new(2, "A2", "Node"), new EditorLinkTreeNode(1, "B", "Node3D")],
            nodes);

        for (var i = 0; i < EditorLinkProtocol.MaxTreeNodes; i++)
            root.AddChild(new Node { Name = "N" + i });
        nodes = GameSession.SnapshotTree(root, out truncated);
        Assert.True(truncated);
        Assert.Equal(EditorLinkProtocol.MaxTreeNodes, nodes.Length);
        root.Free();
    }
}

/// <summary>A session talking to a real <see cref="EditorLinkServer"/>. Adds a log sink, so serial with the Log tests.</summary>
[Collection(nameof(Debugging.SerialConsole))]
public sealed class GameSessionEditorLinkTests
{
    [Fact]
    public void TheSessionStreamsLogsReportsStatusAndObeysTheEditor()
    {
        using var server = new EditorLinkServer();
        var tree = new SceneTree();
        var settings = new ProjectSettings { Name = "Linked" };
        var session = new GameSession(tree, settings, new GameHostOptions { EditorPort = server.Port });
        ExitCode? quit = null;
        session.QuitRequested += code => quit = code;
        var seen = new List<EditorLinkMessage>();
        try
        {
            Assert.NotNull(session.Link);
            Assert.Equal("Linked", Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello).Hello.ProjectName);
            Assert.True(session.Start());
            Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Status && m.Status.State == GameRunState.Running);

            Log.Info("[Gameplay] session-link marker");
            var log = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "session-link marker").Log;
            Assert.Equal("Gameplay", log.Category);

            Assert.Equal(1, server.SendCommand(new EditorLinkCommand(EditorCommandKind.Pause)));
            var time = new GameTime { DeltaTime = 1f / 60f, FramesPerSecond = 60 };
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                return tree.Paused;
            }));
            Assert.Equal(60f, Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Status && m.Status.State == GameRunState.Paused).Status.FramesPerSecond);

            // The remote scene tree: a snapshot on request, depth-first from the root.
            tree.Root.AddChild(new Node2D { Name = "World" });
            Assert.Equal(1, server.SendCommand(new EditorLinkCommand(EditorCommandKind.RequestTree)));
            EditorLinkMessage snapshot = default;
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                while (server.TryRead(out var m))
                {
                    seen.Add(m);
                    if (m.Type == EditorLinkMessageType.Tree)
                        snapshot = m;
                }

                return snapshot.Type == EditorLinkMessageType.Tree;
            }));
            Assert.False(snapshot.TreeTruncated);
            Assert.Equal(0, snapshot.Tree![0].Depth);
            Assert.Contains(new EditorLinkTreeNode(1, "World", nameof(Node2D)), snapshot.Tree);

            Assert.Equal(1, server.SendCommand(new EditorLinkCommand(EditorCommandKind.Stop)));
            Assert.True(Wait.Until(() =>
            {
                session.Update(time);
                return quit is not null;
            }));
        }
        finally
        {
            session.Shutdown(ExitCode.Error);
            tree.Shutdown();
        }

        Assert.Equal((int)ExitCode.Error, Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Goodbye).ExitCode);
        Assert.Contains(seen, m => m.Type == EditorLinkMessageType.Status && m.Status.State == GameRunState.Stopping);
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Disconnected);
        Assert.Null(session.Link);
        Assert.DoesNotContain(Log.Sinks, s => s is EditorLinkLogSink);
    }
}
