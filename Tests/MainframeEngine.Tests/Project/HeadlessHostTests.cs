using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Project;

/// <summary><c>--headless</c> (E13 of the Crash Site Defense port): the project's tree with no window, renderer or audio device.</summary>
[Collection(nameof(MainframeEngine.Tests.Localization.LocalizationState))] // the host configures the catalogs, like Engine's constructor
public sealed class HeadlessHostTests
{
    // No main scene (the session warns and runs the empty tree): the tests add their nodes to the root.
    private static ProjectSettings Settings()
    {
        var settings = new ProjectSettings { Name = "HeadlessTest" };
        settings.Audio.Enabled = false;
        return settings;
    }

    [Fact]
    public void HeadlessParses()
    {
        Assert.True(GameHostOptions.Parse(["--headless", "++", "--server"]).Headless);
        Assert.False(GameHostOptions.Parse(["++", "--headless"]).Headless); // the game's own flag after ++
    }

    [Fact]
    public void RunsForMaxFramesAtTheFixedStep()
    {
        using var host = new HeadlessHost(Settings(), new GameHostOptions { MaxFrames = 30, FixedFps = 60 });
        var counter = new CounterNode { Name = "Counter" };
        host.Tree.Root.AddChild(counter);

        Assert.Equal(ExitCode.Ok, host.Run());

        Assert.Equal(30u, host.Frames);
        Assert.Equal(30, counter.Processed);
        Assert.Equal(30, counter.PhysicsSteps); // 60 Hz physics at a fixed 1/60 s step: one step per update
    }

    [Fact]
    public void RegistersNetworkingAndPhysicsButNoRenderingOrUi()
    {
        using var host = new HeadlessHost(Settings());

        Assert.NotNull(host.Tree.Servers.Get<PhysicsServer2D>());
        Assert.NotNull(host.Tree.Servers.Get<PhysicsServer3D>());
        Assert.NotNull(host.Tree.Servers.Get<MainframeEngine.Networking.MultiplayerApi>());
        Assert.Null(host.Tree.Servers.Get<CanvasServer>());
        Assert.Null(host.Tree.Servers.Get<UiServer>());
        Assert.Null(host.Tree.Servers.Render);
        Assert.Null(host.Tree.Window);
        Assert.Equal(60, host.FrameRate); // no maxFps: the physics rate
    }

    [Fact]
    public void TheTreesQuitCodeEndsTheRun()
    {
        using var host = new HeadlessHost(Settings(), new GameHostOptions { FixedFps = 60 });
        var quitter = new QuitAfter { Name = "Quitter", Frames = 3 };
        host.Tree.Root.AddChild(quitter);

        Assert.Equal((ExitCode)3, host.Run());
        Assert.Equal(3u, host.Frames);
    }
}

/// <summary>Quits the tree with code 3 on its <see cref="Frames"/>th process.</summary>
internal sealed class QuitAfter : Node
{
    public int Frames { get; set; }
    private int _seen;

    protected override void OnProcess(in GameTime gameTime)
    {
        if (++_seen == Frames)
            Tree!.Quit(3);
    }
}
