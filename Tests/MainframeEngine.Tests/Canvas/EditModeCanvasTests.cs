using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// The canvas pieces the editor's 2D view needs: a 2D sub-viewport's debug lines drawn over its canvas, Godot's edit-mode
/// draw rule (built-in drawing only, plus [Tool] nodes), and a Camera2D that leaves an edited scene's view alone.
/// </summary>
public sealed class EditModeCanvasTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly CanvasServer _canvas;

    public EditModeCanvasTests()
    {
        _servers.Register(new RenderServer(new HeadlessRenderer()));
        _tree = new SceneTree(_servers);
        _canvas = new CanvasServer(_tree, () => new Vector2(320, 180));
        _tree.ChangeScene(new Node2D { Name = "Root" });
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _tree.Shutdown();
        _servers.Dispose();
    }

    private CanvasFrame Frame()
    {
        _canvas.Process(new GameTime { DeltaTime = 1f / 60f });
        return _canvas.Frame;
    }

    [Fact]
    public void ASubViewportsDebugLinesDrawOverItsCanvasThroughItsCanvasTransformAndAreConsumed()
    {
        var view = new SubViewport { Name = "View", Disable3D = true, Width = 100, Height = 50, UpdateMode = SubViewportUpdateMode.Always };
        _tree.CurrentScene!.AddChild(view);
        view.AddChild(new EditBox { Name = "Box" });
        view.CanvasTransform = Transform2D.FromTrs(new Vector2(10, 5), 0, new Vector2(2));
        view.DebugLines.AddLine(new Vector3(0, 0, 0), new Vector3(4, 0, 7), Vector4.One);

        var frame = Frame();
        var pass = frame.Passes.Single(p => ReferenceEquals(p.Viewport, view));
        var last = frame.Batches[pass.FirstBatch + pass.BatchCount - 1];
        Assert.Equal(CanvasPrimitive.Lines, last.Primitive);
        Assert.Equal(2, last.IndexCount);
        var a = frame.Vertices[(int)frame.Indices[last.FirstIndex]].Position;
        var b = frame.Vertices[(int)frame.Indices[last.FirstIndex + 1]].Position;
        Assert.Equal(new Vector2(10, 5), a);   // canvas (0, 0) → 2× + (10, 5); z ignored
        Assert.Equal(new Vector2(18, 5), b);
        Assert.Equal(0, view.DebugLines.LineCount);
        Assert.True(pass.BatchCount >= 2); // the box, then the lines
    }

    [Fact]
    public void EditModeRunsBuiltInDrawingAndToolNodesOnly()
    {
        var game = new EditBox { Name = "Game" };
        var tool = new ToolBox { Name = "Tool" };
        _tree.CurrentScene!.AddChild(game);
        _tree.CurrentScene.AddChild(tool);
        _tree.EditMode = true;
        _tree.EditModeScripts = static t => t.Assembly == typeof(EditModeCanvasTests).Assembly;   // this assembly plays the game
        game.QueueRedraw();
        tool.QueueRedraw();
        _tree.FlushCanvasRedraws();
        Assert.Equal(0, game.Draws);
        Assert.Equal(1, tool.Draws);

        _tree.EditMode = false;
        game.QueueRedraw();
        _tree.FlushCanvasRedraws();
        Assert.Equal(1, game.Draws);
    }

    [Fact]
    public void AScriptTypeSkipsItsOwnLifecycleButKeepsItsEngineBase()
    {
        _tree.EditMode = true;
        _tree.EditModeScripts = static t => t.Assembly == typeof(EditModeCanvasTests).Assembly;
        var sprite = new GameSprite { Name = "Sprite", Texture = Texture2D.FromPixels(4, 4, new byte[4 * 4 * 4]) };
        var tool = new ToolSprite { Name = "Tool", Texture = Texture2D.FromPixels(4, 4, new byte[4 * 4 * 4]) };
        _tree.CurrentScene!.AddChild(sprite);
        _tree.CurrentScene.AddChild(tool);
        _tree.FlushCanvasRedraws();

        Assert.Equal(0, sprite.Entered);
        Assert.Equal(0, sprite.Readied);
        Assert.False(sprite.DrawList.IsEmpty);   // Sprite2D's own enter-tree (queue its draw) still ran
        Assert.Equal(1, tool.Entered);
        Assert.Equal(1, tool.Readied);

        sprite.QueueFree();
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(0, sprite.Exited);

        _tree.EditMode = false;   // the same type at run time
        var live = new GameSprite { Name = "Live" };
        _tree.CurrentScene.AddChild(live);
        Assert.Equal(1, live.Entered);
        Assert.Equal(1, live.Readied);
    }

    [Fact]
    public void ACurrentCamera2DDoesNotMoveTheViewInEditMode()
    {
        _tree.EditMode = true;
        var camera = new Camera2D { Name = "Camera", Position = new Vector2(500, 300) };
        _tree.CurrentScene!.AddChild(camera);
        camera.MakeCurrent();
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(Transform2D.Identity, _tree.Root.CanvasTransform);
    }

    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Silk.NET.Maths.Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}

/// <summary>A 10×10 box counting its OnDraw calls.</summary>
public class EditBox : Node2D
{
    public int Draws { get; private set; }

    protected override void OnDraw()
    {
        Draws++;
        DrawRect(new Rect2(0, 0, 10, 10), Vector4.One);
    }
}

[Tool]
public sealed class ToolBox : EditBox;

/// <summary>A game Sprite2D counting its own lifecycle callbacks.</summary>
public class GameSprite : Sprite2D
{
    public int Entered { get; private set; }
    public int Readied { get; private set; }
    public int Exited { get; private set; }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Entered++;
    }

    protected override void OnReady()
    {
        base.OnReady();
        Readied++;
    }

    protected override void OnExitTree()
    {
        Exited++;
        base.OnExitTree();
    }
}

[Tool]
public sealed class ToolSprite : GameSprite;
