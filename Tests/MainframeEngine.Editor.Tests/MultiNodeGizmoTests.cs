using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G7.1: the multi-node gizmo's pivot math (pure).</summary>
public sealed class MultiGizmoMathTests
{
    private static GizmoPose3D Pose(Vector3 p, Quaternion? r = null, Vector3? s = null) => new(p, r ?? Quaternion.Identity, s ?? Vector3.One);

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void PivotIsTheMedianOrTheActiveNode()
    {
        Vector3[] positions = [new(0, 0, 0), new(4, 0, 0), new(2, 6, 0)];
        Near(new Vector3(2, 2, 0), MultiGizmo.Pivot(GizmoPivot.Median, positions, 2));
        Near(new Vector3(2, 2, 0), MultiGizmo.Pivot(GizmoPivot.Individual, positions, 2));
        Near(new Vector3(4, 0, 0), MultiGizmo.Pivot(GizmoPivot.Active, positions, 1));
        Assert.Equal(new Vector2(1, 1), MultiGizmo.Pivot(GizmoPivot.Median, [new Vector2(0, 0), new Vector2(2, 2)], 0));
    }

    [Fact]
    public void TranslateMovesEveryNodeByThePivotDeltaOrAlongItsOwnLocalAxis()
    {
        var pivot = new Vector3(1, 0, 0);
        var start = Pose(new Vector3(5, 1, 0));
        var moved = MultiGizmo.Apply(GizmoMode.Translate, GizmoPivot.Median, GizmoHandle.X, local: false, pivot, Quaternion.Identity,
            pivot + new Vector3(2, 0, 0), Quaternion.Identity, Vector3.One, start);
        Near(new Vector3(7, 1, 0), moved.Position);

        // Local: the active node's X is world X; the other node is turned 90° about Y, so its own X is world -Z.
        var turned = Pose(new Vector3(0, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var local = MultiGizmo.Apply(GizmoMode.Translate, GizmoPivot.Median, GizmoHandle.X, local: true, pivot, Quaternion.Identity,
            pivot + new Vector3(3, 0, 0), Quaternion.Identity, Vector3.One, turned);
        Near(new Vector3(0, 0, -3), local.Position);
    }

    [Fact]
    public void RotateTurnsPositionsAroundThePivotOrEachOrigin()
    {
        var pivot = Vector3.Zero;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2); // +X → -Z
        var start = Pose(new Vector3(2, 0, 0));
        var around = MultiGizmo.Apply(GizmoMode.Rotate, GizmoPivot.Median, GizmoHandle.Y, false, pivot, Quaternion.Identity,
            pivot, turn, Vector3.One, start);
        Near(new Vector3(0, 0, -2), around.Position);
        Near(Vector3.Transform(Vector3.UnitX, turn), Vector3.Transform(Vector3.UnitX, around.Rotation));

        var individual = MultiGizmo.Apply(GizmoMode.Rotate, GizmoPivot.Individual, GizmoHandle.Y, false, pivot, Quaternion.Identity,
            pivot, turn, Vector3.One, start);
        Near(new Vector3(2, 0, 0), individual.Position);
        Near(Vector3.Transform(Vector3.UnitX, turn), Vector3.Transform(Vector3.UnitX, individual.Rotation));
    }

    [Fact]
    public void ScaleMovesPositionsAwayFromThePivotAndScalesEachNode()
    {
        var pivot = new Vector3(1, 1, 1);
        var start = Pose(new Vector3(2, 1, 1), s: new Vector3(1, 2, 1));
        var scaled = MultiGizmo.Apply(GizmoMode.Scale, GizmoPivot.Median, GizmoHandle.Center, false, pivot, Quaternion.Identity,
            pivot, Quaternion.Identity, new Vector3(2), start);
        Near(new Vector3(3, 1, 1), scaled.Position);
        Near(new Vector3(2, 4, 2), scaled.Scale);

        var individual = MultiGizmo.Apply(GizmoMode.Scale, GizmoPivot.Individual, GizmoHandle.Center, false, pivot, Quaternion.Identity,
            pivot, Quaternion.Identity, new Vector3(2), start);
        Near(start.Position, individual.Position);
    }

    [Fact]
    public void TwoDimensionalPosesFollowThePivot()
    {
        var start = new GizmoPose2D(new Vector2(10, 0), 0f, Vector2.One);
        var rotated = MultiGizmo.Apply(GizmoMode.Rotate, GizmoPivot.Median, GizmoHandle.Z, false, Vector2.Zero, 0f,
            Vector2.Zero, MathF.PI / 2, Vector2.One, start);
        Assert.True(Vector2.Distance(new Vector2(0, 10), rotated.Position) < 1e-4f); // y down: a quarter turn clockwise on screen
        Assert.Equal(MathF.PI / 2, rotated.Rotation, 4);

        var scaled = MultiGizmo.Apply(GizmoMode.Scale, GizmoPivot.Median, GizmoHandle.X, false, Vector2.Zero, 0f,
            Vector2.Zero, 0f, new Vector2(3, 1), start);
        Assert.Equal(new Vector2(30, 0), scaled.Position);
        Assert.Equal(new Vector2(3, 1), scaled.Scale);

        var moved = MultiGizmo.Apply(GizmoMode.Translate, GizmoPivot.Median, GizmoHandle.Center, false, Vector2.Zero, 0f,
            new Vector2(5, -2), 0f, Vector2.One, start);
        Assert.Equal(new Vector2(15, -2), moved.Position);
    }
}

/// <summary>G7.1: the gizmo moves, rotates and scales every selected node as one undo entry; Esc and right click cancel.</summary>
[Collection(nameof(SerialEditor))]
public sealed class MultiNodeGizmoTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private Node3D Add(string name, Vector3 position, Node? parent = null)
    {
        var node = new Node3D { Name = name, Position = position };
        _editor.Scene.AddNode(node, parent ?? _editor.Scene.Root);
        return node;
    }

    private void Select(params Node[] nodes)
    {
        _editor.Scene.Selection.Set(nodes[0]);
        for (var i = 1; i < nodes.Length; i++)
            _editor.Scene.Selection.Add(nodes[i]);
        _editor.Tick();
    }

    // The window point of the gizmo's X arrow, 70% along it (the QA script's gizmo-drag point).
    private Vector2 XHandle()
    {
        var scene = _editor.Scene;
        Assert.True(W.Viewport.TryGetGizmoPose(scene, out var pivot, out var rotation));
        var view = W.Viewport.ViewPixels;
        var length = TransformGizmo.SizePixels * W.Gizmo.PixelScale * scene.Camera.WorldPerPixel(pivot, view.Y);
        Assert.True(scene.Camera.Project(pivot + W.Gizmo.Axes(rotation).X * length * 0.7f, view.X, view.Y, out var pixel));
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    private void Press(Vector2 at, MouseButton button = MouseButton.Left)
    {
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = at });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = button, Pressed = true, Position = at });
    }

    private void MoveTo(Vector2 at) => _editor.Tree.PushInput(new InputEventMouseMotion { Position = at });

    private void Release(Vector2 at, MouseButton button = MouseButton.Left)
    {
        _editor.Tree.PushInput(new InputEventMouseButton { Button = button, Pressed = false, Position = at });
        _editor.Tick();
    }

    [Fact]
    public void TargetsAreTheSelectedEditableVisibleUnlockedNodesWithoutNestedOnes()
    {
        var a = Add("A", new Vector3(-2, 0, 0));
        var b = Add("B", new Vector3(2, 0, 0));
        var child = Add("Child", new Vector3(0, 1, 0), a);
        var hidden = Add("Hidden", Vector3.Zero);
        hidden.Visible = false;
        var locked = Add("Locked", Vector3.Zero);
        locked.AddToGroup(EditorLock.Group, persistent: true);
        W.Gizmo.Mode = GizmoMode.Translate;
        Select(a, child, b, hidden, locked);

        Assert.Equal([a, b], W.Viewport.GizmoTargets(_editor.Scene));
        Assert.True(W.Viewport.TryGetGizmoPose(_editor.Scene, out var pivot, out _));
        Assert.Equal(Vector3.Zero, pivot); // the median of A and B
        W.Gizmo.Pivot = GizmoPivot.Active;
        Assert.True(W.Viewport.TryGetGizmoPose(_editor.Scene, out pivot, out _));
        Assert.Equal(b.GlobalPosition, pivot); // the primary (B) is the last target
        W.Gizmo.Mode = GizmoMode.Select;
        Assert.Empty(W.Viewport.GizmoTargets(_editor.Scene));
    }

    [Fact]
    public void ADragMovesEveryNodeAsOneUndoEntry()
    {
        var a = Add("A", new Vector3(-2, 0, 0));
        var b = Add("B", new Vector3(2, 0, 1));
        var c = Add("C", new Vector3(0, 0, -1));
        W.Gizmo.Mode = GizmoMode.Translate;
        Select(a, b, c);
        var history = _editor.Scene.History.Actions.Count;

        var grab = XHandle();
        Press(grab);
        Assert.True(W.Viewport.IsDraggingGizmo);
        MoveTo(grab + new Vector2(60, 0));
        Release(grab + new Vector2(60, 0));

        var dx = a.Position.X + 2;
        Assert.True(dx > 0.1f, $"moved {dx}");
        Assert.Equal(dx, b.Position.X - 2, 4);
        Assert.Equal(dx, c.Position.X, 4);
        Assert.Equal(1f, b.Position.Z); // only along X
        Assert.Equal(history + 1, _editor.Scene.History.Actions.Count);
        Assert.Equal("Move 3 nodes", _editor.Scene.History.UndoAction!.Name);

        _editor.Scene.History.Undo();
        Assert.Equal(new Vector3(-2, 0, 0), a.Position);
        Assert.Equal(new Vector3(2, 0, 1), b.Position);
        Assert.Equal(new Vector3(0, 0, -1), c.Position);
        _editor.Scene.History.Redo();
        Assert.Equal(dx, c.Position.X, 4);
    }

    [Fact]
    public void RotatingAroundTheMedianKeepsTheGroupTogether()
    {
        var a = Add("A", new Vector3(-1, 0, 0));
        var b = Add("B", new Vector3(1, 0, 0));
        W.Gizmo.Mode = GizmoMode.Rotate;
        Select(a, b);
        Assert.True(W.Viewport.TryGetGizmoPose(_editor.Scene, out var pivot, out var axes));
        var view = W.Viewport.ViewPixels;
        var length = TransformGizmo.SizePixels * W.Gizmo.PixelScale * _editor.Scene.Camera.WorldPerPixel(pivot, view.Y);
        var (u, v) = TransformGizmo.Perpendiculars(W.Gizmo.Axes(axes).Y);
        var onRing = Vector3.Normalize(u + v); // 45° between the rings' crossings: only the Y ring is there
        Assert.True(_editor.Scene.Camera.Project(pivot + onRing * length, view.X, view.Y, out var ring));
        var rect = W.Viewport.ViewRect;
        var grab = new Vector2(rect.X, rect.Y) + ring / W.Viewport.ViewScale;
        Assert.True(_editor.Scene.Camera.Project(pivot, view.X, view.Y, out var center));
        var radial = Vector2.Normalize(ring - center);
        var sideways = new Vector2(-radial.Y, radial.X) * 80f; // across the ring: the screen angle changes
        Press(grab);
        Assert.True(W.Viewport.IsDraggingGizmo);
        MoveTo(grab + sideways);
        Release(grab + sideways);

        Assert.True(Vector3.Distance(a.Position, new Vector3(-1, 0, 0)) > 0.05f);
        Assert.True((a.Position + b.Position).Length() < 1e-3f, "the median stays put");
        Assert.Equal(2f, Vector3.Distance(a.Position, b.Position), 3);
        Assert.Equal("Rotate 2 nodes", _editor.Scene.History.UndoAction!.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeOrARightClickCancelsTheDrag(bool escape)
    {
        var a = Add("A", new Vector3(-2, 0, 0));
        var b = Add("B", new Vector3(2, 0, 0));
        W.Gizmo.Mode = GizmoMode.Translate;
        Select(a, b);
        var history = _editor.Scene.History.Actions.Count;

        var grab = XHandle();
        Press(grab);
        MoveTo(grab + new Vector2(60, 0));
        _editor.Tick();
        Assert.NotEqual(-2f, a.Position.X);
        if (escape)
            _editor.Tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = true });
        else
            Press(grab + new Vector2(60, 0), MouseButton.Right);
        _editor.Tick();

        Assert.False(W.Viewport.IsInteracting);
        Assert.Equal(new Vector3(-2, 0, 0), a.Position);
        Assert.Equal(new Vector3(2, 0, 0), b.Position);
        Release(grab + new Vector2(60, 0)); // the left button's release changes nothing
        Assert.Equal(history, _editor.Scene.History.Actions.Count);
    }

    [Fact]
    public void ThePivotButtonCyclesItsModes()
    {
        Assert.Equal(GizmoPivot.Median, W.Gizmo.Pivot);
        W.Commands.Execute("gizmo.pivot");
        Assert.Equal(GizmoPivot.Active, W.Gizmo.Pivot);
        W.Commands.Execute("gizmo.pivot");
        Assert.Equal(GizmoPivot.Individual, W.Gizmo.Pivot);
        W.Commands.Execute("gizmo.pivot");
        Assert.Equal(GizmoPivot.Median, W.Gizmo.Pivot);
    }

    [Fact]
    public void A2DDragMovesEverySelectedNode2D()
    {
        var scene = W.Session.NewScene("Node2D");
        var a = new Node2D { Name = "A", Position = new Vector2(-50, 0) };
        var b = new Node2D { Name = "B", Position = new Vector2(50, 20) };
        scene.AddNode(a, scene.Root);
        scene.AddNode(b, scene.Root);
        W.Gizmo.Mode = GizmoMode.Translate;
        scene.Selection.Set(a);
        scene.Selection.Add(b);
        _editor.Tick(2);

        Assert.True(W.Viewport.TryGetGizmoPose2D(scene, out var pivot, out _));
        Assert.Equal(new Vector2(0, 10), pivot);
        var view = W.Viewport.ViewPixels;
        var center = scene.Camera.WorldToScreen2D(pivot, view);
        var rect = W.Viewport.ViewRect;
        Vector2 Window(Vector2 pixel) => new Vector2(rect.X, rect.Y) + pixel / _editor.Host.PixelScale;
        Press(Window(center + new Vector2(50, 0))); // on the X arrow
        Assert.True(W.Viewport.IsDraggingGizmo);
        MoveTo(Window(center + new Vector2(90.4f, 7)));
        Release(Window(center + new Vector2(90.4f, 7)));

        var dx = a.Position.X + 50;
        Assert.True(dx > 30, $"moved {dx}");
        Assert.Equal(MathF.Round(dx), dx); // whole pixels
        Assert.Equal(new Vector2(50 + dx, 20), b.Position);
        Assert.Equal("Move 2 nodes", scene.History.UndoAction!.Name);
        scene.History.Undo();
        Assert.Equal(new Vector2(-50, 0), a.Position);
    }
}
