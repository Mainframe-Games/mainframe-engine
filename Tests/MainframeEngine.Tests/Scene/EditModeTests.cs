using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Scene;

/// <summary>Runs every callback the edit-mode switch gates; marked [Tool], so it keeps running in the editor.</summary>
[Tool]
internal sealed class ToolCallbacksNode : Node
{
    public int Process { get; private set; }
    public int Physics { get; private set; }
    public int Input { get; private set; }
    public int Unhandled { get; private set; }

    protected override void OnProcess(in GameTime gameTime) => Process++;
    protected override void OnPhysicsProcess(float delta) => Physics++;
    protected override void OnInput(InputEvent inputEvent) => Input++;
    protected override void OnUnhandledInput(InputEvent inputEvent) => Unhandled++;
}

/// <summary>The same callbacks without [Tool]: a game node, inert in the editor.</summary>
internal sealed class GameCallbacksNode : Node
{
    public int Process { get; private set; }
    public int Physics { get; private set; }
    public int Input { get; private set; }
    public int Unhandled { get; private set; }

    protected override void OnProcess(in GameTime gameTime) => Process++;
    protected override void OnPhysicsProcess(float delta) => Physics++;
    protected override void OnInput(InputEvent inputEvent) => Input++;
    protected override void OnUnhandledInput(InputEvent inputEvent) => Unhandled++;
}

/// <summary>[Tool] is not inherited (Godot: per script).</summary>
internal sealed class DerivedFromToolNode : ToolBaseNode;

[Tool]
internal class ToolBaseNode : Node;

public sealed class EditModeTests : IDisposable
{
    private sealed class CountingStepServer : IFixedStepServer
    {
        public int Steps { get; private set; }
        public int Before { get; private set; }
        public int After { get; private set; }

        public void FixedStep(float delta) => Steps++;
        public void BeforeFixedSteps() => Before++;
        public void AfterFixedSteps(float interpolationFraction) => After++;
        public void Dispose()
        {
        }
    }

    private readonly ServerRegistry _servers = new();
    private readonly CountingStepServer _physics = new();
    private readonly SceneTree _tree;

    public EditModeTests()
    {
        _servers.Register(_physics);
        _tree = new SceneTree(_servers);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private void Tick() => _tree.Tick(new GameTime { DeltaTime = 1f / 60f, FrameCount = 1 });

    [Fact]
    public void ToolAttributeIsDetectedPerTypeAndNotInherited()
    {
        Assert.True(new ToolCallbacksNode().IsTool);
        Assert.False(new GameCallbacksNode().IsTool);
        Assert.True(new ToolBaseNode().IsTool);
        Assert.False(new DerivedFromToolNode().IsTool);
        Assert.False(new Node().IsTool);
    }

    [Fact]
    public void InEditModeOnlyToolNodesProcessAndReceiveInput()
    {
        var tool = new ToolCallbacksNode { Name = "Tool" };
        var game = new GameCallbacksNode { Name = "Game" };
        _tree.Root.AddChild(tool);
        _tree.Root.AddChild(game);
        _tree.EditMode = true;

        Tick();
        _tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.A, Pressed = true });

        Assert.Equal((1, 1, 1, 1), (tool.Process, tool.Physics, tool.Input, tool.Unhandled));
        Assert.Equal((0, 0, 0, 0), (game.Process, game.Physics, game.Input, game.Unhandled));

        _tree.EditMode = false;
        Tick();
        _tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.A, Pressed = true });
        Assert.Equal((1, 1, 1, 1), (game.Process, game.Physics, game.Input, game.Unhandled));
    }

    [Fact]
    public void InEditModeTheFixedStepServersDoNotStep()
    {
        _tree.EditMode = true;
        Tick();
        Assert.Equal((0, 0, 0), (_physics.Steps, _physics.Before, _physics.After));

        _tree.EditMode = false;
        Tick();
        Assert.Equal((1, 1, 1), (_physics.Steps, _physics.Before, _physics.After));
    }

    [Fact]
    public void LifecycleCallbacksStillRunInEditMode()
    {
        _tree.EditMode = true;
        var log = new List<string>();
        var node = new LoggingNode { Name = "N", Log = log };

        _tree.Root.AddChild(node);
        Tick();
        node.QueueFree();
        Tick();

        Assert.Equal(["N:enter", "N:ready", "N:exit"], log);
    }

    [Fact]
    public void CameraOverrideRendersTheViewportInsteadOfItsCamerasAndGetsTheAspect()
    {
        var viewport = new SubViewport { Name = "View" };
        var sceneCamera = new Camera3D { Name = "Cam", Current = true };
        viewport.AddChild(sceneCamera);
        _tree.Root.AddChild(viewport);
        var editorCamera = new PerspectiveCamera { Position = new Vector3(1, 2, 3) };

        Assert.Same(sceneCamera.RenderCamera, RenderServer.GetRenderCamera(viewport, new Extent2D(200, 100)));

        viewport.CameraOverride = editorCamera;
        Assert.Same(editorCamera, RenderServer.GetRenderCamera(viewport, new Extent2D(200, 100)));
        Assert.Equal(2f, editorCamera.AspectRatio);
        Assert.True(sceneCamera.Current); // the scene's camera flags are untouched

        viewport.CameraOverride = null;
        Assert.Same(sceneCamera.RenderCamera, RenderServer.GetRenderCamera(viewport, new Extent2D(200, 100)));
    }

    [Fact]
    public void OverlayLinesAreASeparateBatch()
    {
        var viewport = new SubViewport();
        viewport.OverlayLines.AddLine(Vector3.Zero, Vector3.One, Vector4.One);

        Assert.Equal(1, viewport.OverlayLines.LineCount);
        Assert.Equal(0, viewport.DebugLines.LineCount);
        viewport.Free();
    }
}
