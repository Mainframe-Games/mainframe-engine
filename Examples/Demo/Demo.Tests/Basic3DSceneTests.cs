using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Basic3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;

    public Basic3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Basic3DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node3D Scene => (Node3D)_tree.CurrentScene!;

    [Fact]
    public void AllFourLightsAreInTheWorld() => Assert.Equal(4, Scene.GetWorld3D()!.Lights.Count);

    [Fact]
    public void HidingTheSunRemovesItFromTheWorldsLights()
    {
        var lights = Scene.GetWorld3D()!.Lights;
        Scene.GetNode<Light3D>("Lights/Sun").Visible = false;
        Assert.Equal(3, lights.Count);
        Scene.GetNode<Light3D>("Lights/Sun").Visible = true;
        Assert.Equal(4, lights.Count);
    }

    [Fact]
    public void HidingTheLampsLeavesOnlyTheSun()
    {
        var lights = Scene.GetWorld3D()!.Lights;
        foreach (var name in new[] { "Blue", "WarmSpot", "MintSpot" })
            Scene.GetNode<Light3D>("Lights/" + name).Visible = false;
        Assert.Equal(1, lights.Count);
    }
}
