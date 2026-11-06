using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>A node only <see cref="ProbeTool"/> handles.</summary>
public sealed class ToolProbeNode : Node3D;

/// <summary>A viewport tool that records what it gets, consumes left presses and can fail the save.</summary>
[EditorViewportTool(typeof(ToolProbeNode), Title = "Probe", Icon = "target")]
public sealed class ProbeTool : IEditorViewportTool
{
    public static readonly List<string> Log = [];
    public static bool FailSave { get; set; }

    public int DockVersion { get; private set; }

    public bool Handles(Node node) => node is ToolProbeNode;

    public void Activate(IViewportToolContext context, Node node)
    {
        Log.Add("activate " + node.Name);
        context.SetCursorLabel("probe");
    }

    public void Deactivate() => Log.Add("deactivate");

    public ViewportToolResult OnInput(in ViewportToolInput input)
    {
        if (input.Kind != ViewportToolInputKind.Motion)
            Log.Add(input.Kind + (input.Kind is ViewportToolInputKind.Press or ViewportToolInputKind.Release ? " " + input.Button : "") +
                    (input.Captured ? " captured" : ""));
        return input.Kind is ViewportToolInputKind.Press or ViewportToolInputKind.KeyDown && input.Key != Key.W
            ? ViewportToolResult.Consumed
            : ViewportToolResult.Pass;
    }

    public void DrawOverlay(DebugLines overlay)
    {
    }

    public string BuildDockRml() =>
        "<button class=\"tool-button small\" data-tool-action=\"ping\" data-tool-value=\"7\" data-tooltip=\"Ping\"><span class=\"icon icon-target icon-sm\"></span></button>";

    public void OnDockAction(string action, string? value)
    {
        Log.Add("dock " + action + " " + value);
        DockVersion++;
    }

    public void SaveExternalData()
    {
        Log.Add("save");
        if (FailSave)
            throw new IOException("probe save failed");
    }
}

/// <summary>The viewport-tools API (docs/design/editor.md#viewport-tool-api): activation, routing, capture, the dock tab and the save hook.</summary>
[Collection(nameof(SerialEditor))]
public sealed class ViewportToolTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public ViewportToolTests()
    {
        ProbeTool.Log.Clear();
        ProbeTool.FailSave = false;
    }

    public void Dispose()
    {
        ProbeTool.FailSave = false;
        _editor.Dispose();
    }

    private EditorWorkspace W => _editor.Workspace;

    private Vector2 Center
    {
        get
        {
            var rect = W.Viewport.ViewRect;
            return new Vector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);
        }
    }

    private ToolProbeNode AddProbe()
    {
        var scene = _editor.Scene;
        var probe = new ToolProbeNode { Name = "Probe" };
        scene.AddNode(probe, scene.Root);
        scene.Selection.Set(probe);
        _editor.Tick(2);
        return probe;
    }

    private void Push(InputEvent e)
    {
        _editor.Tree.PushInput(e);
        _editor.Tick();
    }

    [Fact]
    public void TheToolFollowsTheSingleSelectionAndTheDockTabFollowsTheTool()
    {
        var probe = AddProbe();
        Assert.IsType<ProbeTool>(W.Tools.Active);
        Assert.Same(probe, W.Tools.ActiveNode);
        Assert.Equal(["activate Probe"], ProbeTool.Log);
        Assert.Equal(InspectorTab.Tool, W.Inspector.Tab);
        Assert.False(W.Inspector.Document.GetElementById("tab-tool").IsClassSet("hidden"));
        Assert.Contains("data-tool-action=\"ping\"", W.Inspector.ToolDockRml);

        var other = new Node3D { Name = "Other" };
        _editor.Scene.AddNode(other, _editor.Scene.Root);
        _editor.Scene.Selection.Set([probe, other]); // two nodes: no tool
        _editor.Tick(2);
        Assert.Null(W.Tools.Active);
        Assert.Equal("deactivate", ProbeTool.Log[^1]);
        Assert.Equal(InspectorTab.Properties, W.Inspector.Tab);
        Assert.True(W.Inspector.Document.GetElementById("tab-tool").IsClassSet("hidden"));

        _editor.Scene.Selection.Set(probe);
        _editor.Tick(2);
        Assert.Same(probe, W.Tools.ActiveNode);
        Assert.Single(W.Tools.InstancesOf(_editor.Scene)); // one instance per tab and tool type
    }

    [Fact]
    public void ALeftPressGoesToTheToolAndCapturesTheMouseUntilRelease()
    {
        AddProbe();
        Push(new InputEventMouseMotion { Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Center });
        Assert.True(W.Tools.Captured);
        Assert.True(W.Viewport.IsInteracting);
        Push(new InputEventMouseMotion { Position = Center + new Vector2(40, 0) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Center + new Vector2(40, 0) });
        Assert.False(W.Tools.Captured);
        Assert.False(W.Viewport.IsInteracting);
        Assert.Equal(["activate Probe", "Press Left", "Release Left captured"], ProbeTool.Log);
        Assert.Null(W.Viewport.IsPicking ? "picking" : null); // the click never became a pick
    }

    [Fact]
    public void NavigationNeverReachesTheTool()
    {
        AddProbe();
        Push(new InputEventMouseMotion { Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Middle, Pressed = true, Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Middle, Pressed = false, Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Right, Pressed = true, Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Right, Pressed = false, Position = Center });
        Push(new InputEventKey { Key = Key.AltLeft, Pressed = true });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Center });
        Push(new InputEventKey { Key = Key.AltLeft, Pressed = false });
        Assert.Equal(["activate Probe"], ProbeTool.Log.Where(l => !l.StartsWith("Key", StringComparison.Ordinal)));
    }

    [Fact]
    public void EscapeDuringACapturedDragSendsCancel()
    {
        AddProbe();
        Push(new InputEventMouseMotion { Position = Center });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Center });
        Push(new InputEventKey { Key = Key.Escape, Pressed = true });
        Assert.False(W.Tools.Captured);
        Assert.False(W.Viewport.IsInteracting);
        Assert.Equal("Cancel", ProbeTool.Log[^1]);
    }

    [Fact]
    public void KeysOverTheViewGoToTheToolBeforeShortcuts()
    {
        AddProbe();
        W.Gizmo.Mode = GizmoMode.Select;
        Push(new InputEventMouseMotion { Position = Center });
        Push(new InputEventKey { Key = Key.R, Pressed = true }); // consumed by the tool: no rotate mode
        Assert.Equal(GizmoMode.Select, W.Gizmo.Mode);
        Push(new InputEventKey { Key = Key.W, Pressed = true }); // passed: the shortcut runs
        Assert.Equal(GizmoMode.Translate, W.Gizmo.Mode);
    }

    [Fact]
    public void DockClicksReachTheTool()
    {
        AddProbe();
        var button = W.Inspector.Document.GetElementById("tool-body").QuerySelector("[data-tool-action=ping]");
        Assert.False(button.IsNull);
        button.Click();
        _editor.Tick();
        Assert.Contains("dock ping 7", ProbeTool.Log);
    }

    [Fact]
    public void SavingCallsEveryToolOfTheTabFirstAndAThrowFailsTheSave()
    {
        AddProbe();
        var scene = _editor.Scene;
        _editor.Scene.Selection.Clear();
        _editor.Tick(2); // deactivated, but the instance still gets the save hook
        var path = Path.Combine(_editor.Directory, "Assets", "Probe.mfscene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        W.Session.Save(scene, path);
        Assert.Contains("save", ProbeTool.Log);
        Assert.True(File.Exists(path));

        File.Delete(path);
        scene.AddNode(new Node3D { Name = "Dirty" }, scene.Root);
        ProbeTool.FailSave = true;
        Assert.Throws<IOException>(() => W.Session.Save(scene, path));
        Assert.False(File.Exists(path));
        Assert.True(scene.History.IsDirty);
    }
}
