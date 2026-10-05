using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Audio3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Audio3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Audio3DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheListenerIsCurrent() => Assert.True(Scene.GetNode<AudioListener3D>("Listener").Current);

    [Fact]
    public void TheEmitterLoopsAStreamedSoundOnTheSfxBusWithInverseAttenuation()
    {
        var emitter = Scene.GetNode<AudioPlayer3D>("Orbit/Emitter");
        Assert.Equal(AttenuationModel.Inverse, emitter.AttenuationModel);
        Assert.Equal("SFX", emitter.Bus);
        Assert.Equal(AudioLoadMode.Stream, emitter.Stream!.LoadMode);
        Assert.True(emitter.Stream.Loop);
        Assert.Equal("Content/Audio/ambient_hum.ogg", emitter.Stream.File);
    }

    [Fact]
    public void TheOrbiterStaysOnItsCircleAndMoves()
    {
        var orbit = Scene.GetNode<Orbiter>("Orbit");
        var start = orbit.Position;
        for (var i = 0; i < 30; i++)
        {
            Tick();
            var p = orbit.Position;
            Assert.Equal(orbit.Radius, MathF.Sqrt(p.X * p.X + p.Z * p.Z), 3);
            Assert.Equal(start.Y, p.Y);
        }

        Assert.NotEqual(start, orbit.Position);
    }
}
