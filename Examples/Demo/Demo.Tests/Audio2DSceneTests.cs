using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Audio2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly AudioServer _audio;
    private uint _frame;

    public Audio2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new Vector2(1280, 720) }));
        _audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, null);
        _servers.Register(_audio);
        _tree = new SceneTree(_servers);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheEmitterLoopsAStreamedSoundOnTheSfxBus()
    {
        _tree.ChangeScene(Audio2DScene.Build());
        var emitter = Scene.GetNode<AudioPlayer2D>("Sweeper/Emitter");
        Assert.Equal("SFX", emitter.Bus);
        Assert.Equal(AudioLoadMode.Stream, emitter.Stream!.LoadMode);
        Assert.True(emitter.Stream.Loop);
        Assert.True(Scene.GetNode<Camera2D>("Camera").Current);
    }

    [Fact]
    public void TheSweeperCrossesTheListenerWithinItsHalfWidth()
    {
        _tree.ChangeScene(Audio2DScene.Build());
        var sweeper = Scene.GetNode<Sweeper2D>("Sweeper");
        float min = 0f, max = 0f;
        for (var i = 0; i < 60 * 3; i++)
        {
            Tick();
            min = MathF.Min(min, sweeper.Position.X);
            max = MathF.Max(max, sweeper.Position.X);
            Assert.Equal(-140f, sweeper.Position.Y);
        }

        Assert.InRange(min, -sweeper.HalfWidth, -sweeper.HalfWidth * 0.9f);
        Assert.InRange(max, sweeper.HalfWidth * 0.9f, sweeper.HalfWidth);
    }

    [Fact]
    public void ClickingDropsAMarkerThatFadesAway()
    {
        _tree.ChangeScene(Audio2DScene.Build());
        var clicks = Scene.GetNode<ClickToPlay2D>("Clicks");
        _tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = new Vector2(100, 100) });
        Assert.Equal(1, clicks.ChildCount);
        for (var i = 0; i < 70; i++)
            Tick();
        Assert.Equal(0, clicks.ChildCount);
    }

    [Fact]
    public void TheBusFadersReturnToTheirLevelsWhenTheSceneLeaves()
    {
        _audio.Master.VolumeDb = -3f;
        _audio.GetBus("SFX")!.VolumeDb = -7f;
        _audio.GetBus("Music")!.VolumeDb = -11f;
        _tree.ChangeScene(Audio2DScene.Build());
        Tick();

        // The panel's sliders drive the server-global buses...
        _audio.Master.VolumeDb = -40f;
        _audio.GetBus("SFX")!.VolumeDb = -41f;
        _audio.GetBus("Music")!.VolumeDb = -42f;

        _tree.ChangeScene(new Node2D { Name = "other" });
        Tick();

        Assert.Equal(-3f, _audio.Master.VolumeDb);
        Assert.Equal(-7f, _audio.GetBus("SFX")!.VolumeDb);
        Assert.Equal(-11f, _audio.GetBus("Music")!.VolumeDb);
    }
}
