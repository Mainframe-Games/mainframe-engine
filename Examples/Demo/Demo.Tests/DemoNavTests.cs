using MainframeEngine;
using Silk.NET.Input;

namespace Demo.Tests;

/// <summary>RmlUi is process-global and single-threaded: tests that create a <see cref="UiServer"/> must not run in parallel.</summary>
[CollectionDefinition(nameof(SerialRmlUi), DisableParallelization = true)]
public sealed class SerialRmlUi;

/// <summary>DemoNav in a real scene tree with a headless UI server (no window, no renderer).</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class DemoNavTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly DemoNav _nav;
    private readonly List<int> _quits = [];
    private uint _frame;

    public DemoNavTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new System.Numerics.Vector2(1280, 720) }));
        _tree = new SceneTree(_servers);
        _tree.QuitRequested += _quits.Add;
        var layer = new DemoNavLayer();
        _tree.Root.AddChild(layer);
        _nav = layer.GetNode<DemoNav>("NavBar");
        Show(0);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    private void Show(int index)
    {
        _tree.ChangeScene(DemoScenes.All[index].Build());
        Tick();
    }

    [Fact]
    public void ActiveIdFollowsTheCurrentScene()
    {
        Assert.Equal(DemoScenes.All[0].Id, _nav.ActiveId);
        foreach (var i in new[] { 3, 7, 1 })
        {
            Show(i);
            Assert.Equal(DemoScenes.All[i].Id, _nav.ActiveId);
        }
    }

    [Fact]
    public void OpenIgnoresOutOfRangeIndices()
    {
        var scene = _tree.CurrentScene;
        _nav.Open(-1);
        _nav.Open(DemoScenes.All.Count);
        Tick();
        Assert.Same(scene, _tree.CurrentScene);
        Assert.Equal(DemoScenes.All[0].Id, _nav.ActiveId);
    }

    [Fact]
    public void OpenIgnoresTheAlreadyActiveTab()
    {
        var scene = _tree.CurrentScene;
        _nav.Open(0);
        Tick();
        Assert.Same(scene, _tree.CurrentScene);
    }

    [Fact]
    public void EscapeRequestsQuit()
    {
        _tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = true });
        Assert.Equal([0], _quits);
    }

    [Fact]
    public void ReleasingEscapeDoesNotQuit()
    {
        _tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = false });
        Assert.Empty(_quits);
    }
}
