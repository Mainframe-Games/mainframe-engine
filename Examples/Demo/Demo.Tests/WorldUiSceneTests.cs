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

    [Fact]
    public void TheTerminalPanelOpensTheGateWhenItsButtonIsClicked()
    {
        Tick(3);
        var screen = Scene.GetNode<UiPanel3D>("Terminal/Screen");
        var terminal = screen.GetNode<WorldUiTerminal>("Ui");
        var gate = Scene.GetNode<Node3D>("Gate");
        var closedY = gate.Position.Y;
        Assert.True(screen.RenderCount > 0);
        Assert.False(terminal.Open);

        // A ray from 2 m in front of the panel through its button's centre (panel pixel 190, 206).
        var size = screen.QuadSize;
        var local = new Vector3((190f / screen.Resolution.X - 0.5f) * size.X, (0.5f - 206f / screen.Resolution.Y) * size.Y, 0f);
        var point = screen.GlobalTransform * local;
        var origin = screen.GlobalTransform * (local + new Vector3(0, 0, 2));
        screen.PushPointer(origin, point - origin);
        Assert.True(screen.HasPointer);
        Assert.Equal(190, screen.PointerPosition.X, 1);
        screen.PushButton(Silk.NET.Input.MouseButton.Left, true);
        screen.PushButton(Silk.NET.Input.MouseButton.Left, false);
        Tick();
        Assert.Equal(1, terminal.Presses);
        Assert.True(terminal.Open);
        Assert.True(screen.HasFocus);

        Tick(120); // the gate slides up
        Assert.True(gate.Position.Y > closedY + 3f, $"gate at {gate.Position.Y}");
    }

    [Fact]
    public void TabSwitchesTheTerminalToACrosshairAimedAtIt()
    {
        Tick(3);
        var panel = Scene.GetNode<WorldUiPanel>("Ui/Panel");
        var screen = Scene.GetNode<UiPanel3D>("Terminal/Screen");
        Assert.Equal(PanelInputMode.Mouse, screen.InputMode);
        _tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.Tab, Pressed = true });
        _tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.Tab, Pressed = false });
        Tick(2);
        Assert.True(panel.Crosshair);
        Assert.Equal(PanelInputMode.Crosshair, screen.InputMode);
        Assert.Same(screen, _tree.Servers.Get<UiServer>()!.HoveredPanel); // the camera aims the crosshair at it
        Assert.Equal("use", screen.ClickAction);
    }
}
