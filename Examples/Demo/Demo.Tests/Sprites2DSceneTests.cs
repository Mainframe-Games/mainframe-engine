using MainframeEngine;

namespace Demo.Tests;

/// <summary>Sprites 2D (ADR 0223): the generated sheet and SpriteFrames, the sprites' autoplay and the panel's controls.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class Sprites2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Sprites2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Sprites2DScene.Build());
        Tick();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private Sprites2DPanel Panel => Scene.GetNode<Sprites2DPanel>("Ui/Panel");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void TheCommittedSheetAndFramesAreTheGeneratedOnes()
    {
        var png = Png.ReadRgba8(ContentPaths.Resolve(Sprites2DScene.SheetPath));
        Assert.Equal((HeroArt.Width, HeroArt.Height), (png.Width, png.Height));
        Assert.Equal(HeroArt.Pixels(), png.Pixels);

        var frames = ResourceLoader.Load<SpriteFrames>(Sprites2DScene.FramesPath);
        try
        {
            Assert.Equal(HeroArt.Animations.Select(a => a.Name), frames.Animations.Select(a => a.Name));
            var expected = Sprites2DScene.CreateFrames(frames.Animations[0].Textures[0]!);
            for (var i = 0; i < expected.Animations.Count; i++)
            {
                Assert.Equal(expected.Animations[i].Speed, frames.Animations[i].Speed);
                Assert.Equal(expected.Animations[i].Frames, frames.Animations[i].Frames);
            }
        }
        finally
        {
            frames.Release();
        }
    }

    [Fact]
    public void EverySpriteAutoplaysAndThePatrolTurnsAround()
    {
        foreach (var name in new[] { "Showcase", "Patrol/Sprite", "Coins/Coin0", "Coins/Coin4" })
            Assert.True(Scene.GetNode<AnimatedSprite2D>(name).IsPlaying(), name);
        Assert.Equal(-1f, Scene.GetNode<AnimatedSprite2D>("Coins/Coin4").SpeedScale); // the backwards coin
        Assert.Equal(CanvasTextureFilter.Nearest, ((Node2D)Scene).TextureFilter);

        var patrol = Scene.GetNode<Patrol2D>("Patrol");
        var sprite = patrol.GetNode<AnimatedSprite2D>("Sprite");
        Tick(60 * 4); // 140 px/s over a 420 px walk: it reaches the end and turns
        Assert.True(sprite.FlipH);
        Assert.InRange(patrol.Position.X, patrol.MinX, patrol.MaxX);
    }

    [Fact]
    public void ThePanelDrivesTheShowcase()
    {
        var showcase = Panel.Showcase!;
        Panel.Animation = "run";
        Assert.Equal("run", showcase.Animation);
        Tick(30); // half a second at 10 fps: frames changed and the animation looped
        Assert.NotEqual("1 / 4", Panel.FrameText);
        Assert.True(Panel.Loops >= 1);

        Panel.Backwards = true;
        Assert.True(float.IsNegative(showcase.GetPlayingSpeed()));
        Panel.Flip = true;
        Assert.True(showcase.FlipH);

        Panel.Speed = 2f;
        Assert.Equal(2f, showcase.SpeedScale);
        Assert.Equal(-2f, Scene.GetNode<AnimatedSprite2D>("Coins/Coin4").SpeedScale);
        Assert.Equal(1f, Scene.GetNode<AnimatedSprite2D>("Coins/Coin0").SpeedScale);

        Panel.Paused = true;
        Assert.False(showcase.IsPlaying());
        Assert.True(Scene.GetNode<Patrol2D>("Patrol").Paused);
        Panel.Paused = false;
        Assert.True(showcase.IsPlaying());
    }
}
