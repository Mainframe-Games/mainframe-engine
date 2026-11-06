using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class WorldUiSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public WorldUiSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _tree = new SceneTree(_servers);
        _tree.Root.SetSize(new Vector2(1280, 720));
        _tree.ChangeScene(WorldUiScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void SignsAreLabelsAndWalkersCarryNameplatesOnOneHost()
    {
        Tick(3);
        Assert.Equal(["NOODLES", "Ramen  9", "Udon  7"], Scene.GetNode<Label3D>("MenuSign").GetLines());
        Assert.True(Scene.GetNode<Label3D>("Notice").GetLines().Count > 1); // autowrapped
        var host = Scene.GetNode<UiMarkerHost>("Ui/Markers");
        Assert.Equal(8, host.Markers.Count); // six nameplates, the prompt, the waypoint
        Assert.Contains(host.Markers, m => m.Kind == "waypoint" && m.Offscreen == MarkerOffscreen.ClampToEdge);
    }

    [Fact]
    public void ThePanelTogglesOcclusionAndTheDebugTags()
    {
        Tick();
        var panel = Scene.GetNode<WorldUiPanel>("Ui/Panel");
        var walker = Scene.GetNode<WorldUiWalker>("Walkers/Nell");
        panel.Occlusion = false;
        Assert.Equal(MarkerOcclusion.None, walker.GetNode<UiMarker3D>("Nameplate").Occlusion);
        panel.DebugTags = true;
        Assert.True(walker.GetNode<Label3D>("DebugTag").Visible);
        Assert.True(walker.GetNode<Label3D>("DebugTag").FixedSize);
    }
}
