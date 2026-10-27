using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G7.4: box selection (2D bounds, 3D icons; the GPU region is a render test), modifiers, and the node lock.</summary>
[Collection(nameof(SerialEditor))]
public sealed class BoxSelectionTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private Vector2 Window2D(EditedScene scene, Vector2 world)
    {
        var pixel = scene.Camera.WorldToScreen2D(world, W.Viewport.ViewPixels);
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    private (EditedScene Scene, CollisionShape2D Box, CollisionShape2D Big, Node2D Marker, Node2D Hidden, Node2D Locked) Scene2D()
    {
        var scene = W.Session.NewScene("Node2D");
        var box = new CollisionShape2D { Name = "Box", Position = new Vector2(0, 0), Shape = new RectangleShape2D { Size = new Vector2(40, 40) } };
        var big = new CollisionShape2D { Name = "Big", Position = new Vector2(0, 0), Shape = new CircleShape2D { Radius = 500 } };
        var marker = new Node2D { Name = "Marker", Position = new Vector2(60, 10) };
        var hidden = new Node2D { Name = "Hidden", Position = new Vector2(30, 30), Visible = false };
        var locked = new Node2D { Name = "Locked", Position = new Vector2(-30, -30) };
        foreach (var node in (Node2D[])[box, big, marker, hidden, locked])
            scene.AddNode(node, scene.Root);
        scene.SetLocked(locked, true);
        scene.Selection.Clear();
        _editor.Tick(2);
        return (scene, box, big, marker, hidden, locked);
    }

    [Fact]
    public void A2DBoxSelectsEnclosedBoundsAndOriginsButNotHiddenOrLockedNodes()
    {
        var (scene, box, _, marker, _, _) = Scene2D();
        W.Viewport.BoxSelect(scene, Window2D(scene, new Vector2(-100, -100)), Window2D(scene, new Vector2(100, 100)), BoxSelectMode.Replace);
        Assert.Equal([box, marker], scene.Selection.Nodes); // the circle is not enclosed; tree order
        Assert.Same(marker, scene.Selection.Primary);

        W.Viewport.BoxSelect(scene, Window2D(scene, new Vector2(-30, -30)), Window2D(scene, new Vector2(30, 30)), BoxSelectMode.Replace);
        Assert.Equal([box], scene.Selection.Nodes);
        W.Viewport.BoxSelect(scene, Window2D(scene, new Vector2(50, 0)), Window2D(scene, new Vector2(70, 20)), BoxSelectMode.Add);
        Assert.Equal([box, marker], scene.Selection.Nodes);
        W.Viewport.BoxSelect(scene, Window2D(scene, new Vector2(-100, -100)), Window2D(scene, new Vector2(100, 100)), BoxSelectMode.Toggle);
        Assert.Empty(scene.Selection.Nodes); // both toggled off
        W.Viewport.BoxSelect(scene, Window2D(scene, new Vector2(900, 900)), Window2D(scene, new Vector2(950, 950)), BoxSelectMode.Replace);
        Assert.Empty(scene.Selection.Nodes);
    }

    [Fact]
    public void ADragFromEmptySpaceDrawsTheRectangleAndSelectsOnRelease()
    {
        var (scene, box, _, marker, _, _) = Scene2D();
        W.Gizmo.Mode = GizmoMode.Translate;
        var from = Window2D(scene, new Vector2(-100, -100));
        var to = Window2D(scene, new Vector2(100, 100));
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = from });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = to });
        _editor.Tick();
        Assert.True(W.Viewport.IsDrawingBox);
        Assert.True(W.ViewportPanel.Document.GetElementById("box-select").IsClassSet("shown"));
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = to });
        _editor.Tick();
        Assert.False(W.Viewport.IsDrawingBox);
        Assert.False(W.ViewportPanel.Document.GetElementById("box-select").IsClassSet("shown"));
        Assert.Equal([box, marker], scene.Selection.Nodes);
    }

    [Fact]
    public void EscapeCancelsTheRectangle()
    {
        var (scene, _, _, _, _, _) = Scene2D();
        var from = Window2D(scene, new Vector2(-100, -100));
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = from });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = Window2D(scene, new Vector2(100, 100)) });
        _editor.Tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = true });
        _editor.Tick();
        Assert.False(W.Viewport.IsInteracting);
        Assert.Empty(scene.Selection.Nodes);
    }

    [Fact]
    public void LockedNodesCannotBeClickedOrMovedButTheTreeStillSelectsThem()
    {
        var (scene, _, _, _, _, locked) = Scene2D();
        var picked = W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(-30, -30), W.Viewport.ViewPixels));
        Assert.NotSame(locked, picked); // the click goes to the shape under it
        Assert.IsType<CollisionShape2D>(picked);
        scene.Selection.Set(locked);
        W.Gizmo.Mode = GizmoMode.Translate;
        Assert.Empty(W.Viewport.GizmoTargets2D(scene));
    }

    [Fact]
    public void A3DBoxSelectsIconNodesWithoutAGpu()
    {
        var scene = _editor.Scene;
        scene.Camera.Pivot = Vector3.Zero;
        scene.Camera.Distance = 10;
        scene.Camera.Yaw = 0;
        scene.Camera.Pitch = 0;
        var lamp = new OmniLight3D { Name = "Lamp", Position = new Vector3(-1, 0, 0) };
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(1, 0, 0) };
        var far = new OmniLight3D { Name = "Far", Position = new Vector3(30, 0, 0) };
        scene.AddNode(lamp, scene.Root);
        scene.AddNode(sun, scene.Root);
        scene.AddNode(far, scene.Root);
        _editor.Tick();
        var view = W.Viewport.ViewPixels;
        var rect = W.Viewport.ViewRect;
        Vector2 Window(Vector3 world)
        {
            Assert.True(scene.Camera.Project(world, view.X, view.Y, out var pixel));
            return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
        }

        W.Viewport.BoxSelect(scene, Window(new Vector3(-2, 1, 0)), Window(new Vector3(2, -1, 0)), BoxSelectMode.Replace);
        Assert.False(W.Viewport.IsBoxSelecting); // headless: no region readback, the icons alone
        Assert.Equal([lamp, sun], scene.Selection.Nodes);
    }

    [Fact]
    public void SelectionRangesRaiseChangedOnce()
    {
        var selection = new Selection();
        var a = new Node { Name = "A" };
        var b = new Node { Name = "B" };
        var c = new Node { Name = "C" };
        var raised = 0;
        selection.Changed += () => raised++;
        selection.Set([a, b]);
        Assert.Equal(1, raised);
        selection.Set([a, b]);
        Assert.Equal(1, raised); // unchanged
        selection.AddRange([c, a]);
        Assert.Equal([b, c, a], selection.Nodes);
        selection.ToggleRange([b, c]);
        Assert.Equal([a], selection.Nodes);
        Assert.Equal(3, raised);
        a.Free();
        b.Free();
        c.Free();
    }

    [Fact]
    public void TheLockIsAnUndoablePersistentGroupSavedWithTheScene()
    {
        var scene = _editor.Scene;
        var crate = new Node3D { Name = "Crate" };
        scene.AddNode(crate, scene.Root);
        _editor.Tick();
        var row = W.SceneTree.Model.Rows.ToList().FindIndex(r => ReferenceEquals(r.Node, crate));
        Assert.True(W.SceneTree.ToggleLock(row));
        Assert.True(EditorLock.IsLocked(crate));
        Assert.Equal("Lock Crate", scene.History.UndoAction!.Name);
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root));
        Assert.Contains(EditorLock.Group, json, StringComparison.Ordinal);
        var instance = PackedScene.Pack(scene.Root).Instantiate();
        Assert.True(EditorLock.IsLocked(instance.GetNode("Crate")));
        instance.Free();

        scene.History.Undo();
        Assert.False(EditorLock.IsLocked(crate));
        Assert.DoesNotContain(EditorLock.Group, System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root)), StringComparison.Ordinal);
        scene.History.Redo();
        Assert.True(EditorLock.IsLocked(crate));
    }
}
