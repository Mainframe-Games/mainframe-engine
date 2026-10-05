using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>Godot's Camera2D and content-scale maths (E9 of the Crash Site Defense port).</summary>
public sealed class Camera2DTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node2D _root = new() { Name = "Root" };

    public Camera2DTests()
    {
        _tree.ChangeScene(_root);
        _tree.Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        _tree.Root.ContentScaleAspect = ContentScaleAspect.Expand;
        _tree.Root.ContentScaleSize = new Vector2(1920, 1080);
        _tree.Root.SetSize(new Vector2(1920, 1080));
    }

    public void Dispose() => _tree.Shutdown();

    [Fact]
    public void CanvasItemsExpandWidensTheVisibleRectToTheWindowAspect()
    {
        // A 3024×1692 framebuffer (16:8.95) shows 1930 × 1080 canvas units, scaled by 3024/1930 and 1692/1080.
        var r = ContentScale.Compute(new Vector2(3024, 1692), ContentScaleMode.CanvasItems, ContentScaleAspect.Expand, new Vector2(1920, 1080));
        Assert.Equal(new Vector2(3024, 1692), r.RenderSize);
        Assert.Equal(new Vector2(1930, 1080), r.VisibleSize);
        Assert.Equal(Vector2.Zero, r.Margin);
        // Taller than 16:9: the width stays 1920 and the height grows.
        r = ContentScale.Compute(new Vector2(1600, 1200), ContentScaleMode.CanvasItems, ContentScaleAspect.Expand, new Vector2(1920, 1080));
        Assert.Equal(new Vector2(1920, 1440), r.VisibleSize);
        // Keep letterboxes: same visible size, a margin.
        r = ContentScale.Compute(new Vector2(1600, 1200), ContentScaleMode.CanvasItems, ContentScaleAspect.Keep, new Vector2(1920, 1080));
        Assert.Equal(new Vector2(1920, 1080), r.VisibleSize);
        Assert.Equal(new Vector2(1600, 900), r.RenderSize);
        Assert.Equal(new Vector2(0, 150), r.Margin);
        // Disabled: canvas units are pixels.
        r = ContentScale.Compute(new Vector2(800, 600), ContentScaleMode.Disabled, ContentScaleAspect.Keep, new Vector2(1920, 1080));
        Assert.Equal(new Vector2(800, 600), r.VisibleSize);
    }

    [Fact]
    public void ACentredCameraPutsItsPositionAtTheScreenCentreScaledByZoom()
    {
        var camera = new Camera2D { Position = new Vector2(100, 50) };
        _root.AddChild(camera);
        Assert.True(camera.Current);
        Assert.Same(camera, _tree.Root.ActiveCamera2D);
        var xform = _tree.Root.CanvasTransform;
        Assert.Equal(new Vector2(960, 540), xform.TransformPoint(new Vector2(100, 50)));
        Assert.Equal(new Vector2(100, 50), camera.GetScreenCenterPosition());

        camera.Zoom = new Vector2(2);
        xform = _tree.Root.CanvasTransform;
        Assert.Equal(new Vector2(960, 540), xform.TransformPoint(new Vector2(100, 50)));
        Assert.Equal(new Vector2(980, 540), xform.TransformPoint(new Vector2(110, 50))); // 2× closer

        camera.Offset = new Vector2(5, 0);
        Assert.Equal(new Vector2(950, 540), _tree.Root.CanvasTransform.TransformPoint(new Vector2(100, 50)));
    }

    [Fact]
    public void LimitsClampTheViewEdges()
    {
        var camera = new Camera2D { LimitLeft = 0, LimitTop = 0 };
        _root.AddChild(camera);
        // At the origin the view would show −960 … 960; the left/top limits push it to 0 … 1920.
        Assert.Equal(Vector2.Zero, _tree.Root.CanvasTransform.TransformPoint(Vector2.Zero));
        Assert.Equal(new Vector2(960, 540), camera.GetScreenCenterPosition());
    }

    [Fact]
    public void PositionSmoothingStepsOnProcessAndZoomSetsKeepTheSmoothedPosition()
    {
        var camera = new SmoothedCamera { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 8 };
        _root.AddChild(camera);
        Assert.Equal(Vector2.Zero, camera.GetScreenCenterPosition());

        // Frame 1: the internal update smooths towards the last frame's position (0), then the script moves the camera
        // to 100 and sets the zoom, which updates once more (towards 100) without keeping that step.
        camera.Target = new Vector2(100, 0);
        _tree.Tick(new GameTime { DeltaTime = 0.05f });
        var c = 8 * 0.05f;
        Assert.Equal(100 * c, camera.GetScreenCenterPosition().X, 3);
        // Frame 2: the kept smoothed position is still 0 (frame 1's internal step saw 0), so the internal step goes to
        // 0.4 × 100, then the zoom set goes from there.
        _tree.Tick(new GameTime { DeltaTime = 0.05f });
        var kept = 100 * c;
        Assert.Equal(kept + (100 - kept) * c, camera.GetScreenCenterPosition().X, 3);
    }

    // Like the game's LocalCamera: base (internal) process first, then follow a target and set the zoom.
    private sealed class SmoothedCamera : Camera2D
    {
        public Vector2 Target { get; set; }

        protected override void OnProcess(in GameTime gameTime)
        {
            base.OnProcess(gameTime);
            GlobalPosition = Target;
            Zoom = Vector2.One; // the game sets it every frame (a lerp towards 1)
        }
    }
}
