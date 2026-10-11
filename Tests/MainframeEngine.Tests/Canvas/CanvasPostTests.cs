using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>The 2D canvas's post-processing (ADR 0185): the environment's settings in the frame, the layer split, the maths.</summary>
public sealed class CanvasPostTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public CanvasPostTests()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(400, 300));
        _tree.ChangeScene(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    private sealed class Box(Vector4 color) : Node2D
    {
        protected override void OnDraw() => DrawRect(new Rect2(0, 0, 10, 10), color);
    }

    private CanvasFrame Frame()
    {
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        return _server.Frame;
    }

    [Fact]
    public void WithoutAnEnvironmentNothingIsPostProcessed()
    {
        _root.AddChild(new Box(Vector4.One) { Name = "Box" });

        var frame = Frame();
        Assert.False(frame.Post.Enabled);
        Assert.Equal(-1, frame.Passes[^1].PostSplit);
    }

    [Fact]
    public void TheEnvironmentsSettingsReachTheFrameEveryFrame()
    {
        var environment = new CanvasEnvironment { Name = "Env", Saturation = 0.5f, VignetteStrength = 0.3f };
        _root.AddChild(environment);
        _root.AddChild(new Box(Vector4.One) { Name = "Box" });

        Assert.Equal(0.5f, Frame().Post.Saturation);
        environment.Saturation = 0.25f;
        var post = Frame().Post;
        Assert.True(post.Enabled);
        Assert.Equal(0.25f, post.Saturation);
        Assert.Equal(0.3f, post.VignetteStrength);

        environment.Enabled = false;
        Assert.False(Frame().Post.Enabled);
    }

    [Fact]
    public void LayersAboveMaxLayerDrawAfterTheSplitAndNeverMergeAcrossIt()
    {
        _root.AddChild(new CanvasEnvironment { Name = "Env", MaxLayer = 0 });
        _root.AddChild(new Box(Vector4.One) { Name = "World" });
        var below = new CanvasLayer { Name = "Below", Layer = -1 };
        below.AddChild(new Box(Vector4.One) { Name = "Back" });
        _root.AddChild(below);
        var hud = new CanvasLayer { Name = "Hud", Layer = 1 };
        hud.AddChild(new Box(Vector4.One) { Name = "Hud" }); // same texture, colour and blend: would merge without the split
        _root.AddChild(hud);

        var frame = Frame();
        var pass = frame.Passes[^1];
        Assert.Equal(pass.FirstBatch + 1, pass.PostSplit); // layer −1 and the root canvas merged into one post-processed batch
        Assert.Equal(2, pass.BatchCount);
    }

    [Fact]
    public void TheEnvironmentOutsideTheRootViewportIsIgnoredOnceRemoved()
    {
        var environment = new CanvasEnvironment { Name = "Env" };
        _root.AddChild(environment);
        _root.AddChild(new Box(Vector4.One) { Name = "Box" });
        Assert.True(Frame().Post.Enabled);

        _root.RemoveChild(environment);
        Assert.False(Frame().Post.Enabled);
    }

    [Fact]
    public void DefaultGradeIsTheIdentity()
    {
        var s = new CanvasPostSettings { Enabled = true };
        var c = new Vector3(0.7f, 0.2f, 0.4f);
        var g = CanvasPostMath.Grade(c, s);
        Assert.Equal(c.X, g.X, 5);
        Assert.Equal(c.Y, g.Y, 5);
        Assert.Equal(c.Z, g.Z, 5);
    }

    [Fact]
    public void ZeroSaturationIsGreyAndTintMultipliesByStrength()
    {
        var grey = CanvasPostMath.Grade(new Vector3(0.9f, 0.1f, 0.3f), new CanvasPostSettings { Saturation = 0f });
        Assert.Equal(grey.X, grey.Y, 5);
        Assert.Equal(grey.Y, grey.Z, 5);

        var tinted = CanvasPostMath.Grade(new Vector3(0.5f), new CanvasPostSettings { Tint = new Vector4(0f, 1f, 1f, 0.5f) });
        Assert.Equal(0.25f, tinted.X, 5);
        Assert.Equal(0.5f, tinted.Y, 5);
    }

    [Fact]
    public void TheVignetteIsClearInTheMiddleAndFullAtTheCorners()
    {
        var s = new CanvasPostSettings { VignetteStrength = 0.8f, VignetteRadius = 0.3f, VignetteSoftness = 0.3f };
        Assert.Equal(0f, CanvasPostMath.Vignette(new Vector2(0.5f), 16f / 9f, s));
        Assert.Equal(0.8f, CanvasPostMath.Vignette(Vector2.Zero, 16f / 9f, s), 5);
    }

    [Fact]
    public void TheBrightPassKeepsNothingBelowTheKneeAndTheExcessAboveTheThreshold()
    {
        Assert.Equal(0f, CanvasPostMath.BrightPass(new Vector3(0.2f), 0.8f, 0.5f));
        var weight = CanvasPostMath.BrightPass(new Vector3(1f), 0.5f, 0.5f);
        Assert.Equal(0.5f, weight, 4); // well above the knee: luma 1 − threshold 0.5, over luma 1
    }
}
