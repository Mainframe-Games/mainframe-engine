using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Basic2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Basic2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Basic2DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheSceneHoldsSkySunHillsMoonsAndLogo()
    {
        Assert.NotNull(Scene.GetNode<DemoShape2D>("Sky"));
        Assert.NotNull(Scene.GetNode<SunRays2D>("Sun/Rays"));
        Assert.NotNull(Scene.GetNode<DemoShape2D>("Sun/Disc"));
        foreach (var name in new[] { "FarHills", "MidHills", "NearHills" })
            Assert.Equal(DemoShapeKind.Polygon, Scene.GetNode<DemoShape2D>(name).Kind);
        Assert.Equal(6, Scene.GetNode<Orbit2D>("Orbit").Children.Count);
        Assert.NotNull(Scene.GetNode<Sprite2D>("Logo").Texture);
        Assert.NotNull(Scene.GetNode<Basic2DPanel>("Ui/Panel"));
    }

    [Fact]
    public void TheSkyIsADuskGradientOverAFlatBackdrop()
    {
        var gradient = Scene.GetNode<DemoShape2D>("Sky/Gradient");
        Assert.NotEqual(Vector4.Zero, gradient.ColorBottom);
        Assert.Equal(Scene.GetNode<DemoShape2D>("Sky").Color, gradient.Color);
        // The haze continues the gradient's bottom colour down to the sky's lower edge.
        var haze = Scene.GetNode<DemoShape2D>("Sky/Haze");
        Assert.Equal(gradient.ColorBottom, haze.Color);
        Assert.Equal(450f, haze.Position.Y - haze.Size.Y / 2);
        Assert.Equal(Basic2DScene.SkySize.Y / 2, haze.Position.Y + haze.Size.Y / 2);
    }

    [Fact]
    public void TheSkyCoversTheVisibleAreaAtMinimumZoomOnAFullHdCanvas()
    {
        var sky = Scene.GetNode<DemoShape2D>("Sky");
        Assert.True(sky.Size.X / 2 >= 960f / 0.5f, sky.Size.ToString());
        Assert.True(sky.Size.Y / 2 >= 540f / 0.5f, sky.Size.ToString());
        Assert.True(Scene.GetNode<DemoShape2D>("Sky/Gradient").Size.X / 2 >= 960f / 0.5f);
        Assert.True(Basic2DScene.HillHalfWidth >= 960f / 0.5f);
        Assert.True(Basic2DScene.HillBottom >= 540f / 0.5f);
    }

    [Fact]
    public void HillsSpanTheScreenAndCloseAtTheBottom()
    {
        foreach (var name in new[] { "FarHills", "MidHills", "NearHills" })
        {
            var points = Scene.GetNode<DemoShape2D>(name).Points;
            Assert.Equal(-2000f, points.Min(static p => p.X));
            Assert.Equal(2000f, points.Max(static p => p.X));
            Assert.Equal(new Vector2(2000, 1200), points[^2]);
            Assert.Equal(new Vector2(-2000, 1200), points[^1]);
        }
    }

    [Fact]
    public void PausingStopsTheOrbitAndTheRays()
    {
        var panel = Scene.GetNode<Basic2DPanel>("Ui/Panel");
        var orbit = Scene.GetNode<Orbit2D>("Orbit");
        var rays = Scene.GetNode<SunRays2D>("Sun/Rays");

        Tick();
        Tick();
        var running = orbit.RotationDegrees;
        Assert.NotEqual(0f, running);

        panel.Paused = true;
        Assert.True(orbit.Paused);
        Assert.True(rays.Paused);
        for (var i = 0; i < 10; i++)
            Tick();
        Assert.Equal(running, orbit.RotationDegrees);

        panel.Paused = false;
        Tick();
        Assert.NotEqual(running, orbit.RotationDegrees);
        Assert.False(rays.Paused);
    }

    [Fact]
    public void TheZoomBindingSetsTheCameraZoomOnBothAxes()
    {
        var panel = Scene.GetNode<Basic2DPanel>("Ui/Panel");
        panel.Zoom = 1.5f;
        Assert.Equal(new Vector2(1.5f, 1.5f), Scene.GetNode<Camera2D>("Camera").Zoom);
        Assert.Equal(1.5f, panel.Zoom);
    }
}
