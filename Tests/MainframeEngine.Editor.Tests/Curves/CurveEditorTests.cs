using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G13: the curve tool's operations and gestures on a <see cref="Path2D"/>, each one undo step.</summary>
public sealed class CurveEditorTests
{
    private const float Pixel = 1f; // one display point = one canvas pixel

    private sealed class Rig
    {
        public Rig(params Vector2[] points)
        {
            Curve = new Curve2D { BakeInterval = 0.5f };
            foreach (var p in points)
                Curve.AddPoint(p);
            Path = new Path2D { Name = "Path", Curve = Curve, Position = new Vector2(100, 50) };
            Editor.EditForTests(Path);
        }

        public Curve2D Curve { get; }

        public Path2D Path { get; }

        public CurveEditor Editor { get; } = new(null);

        public UndoRedo History { get; } = new();

        // Path-local point → the view's world space (the path sits at (100, 50)).
        public static Vector2 World(Vector2 local) => local + new Vector2(100, 50);

        public bool Press(Vector2 local, MouseButton button = MouseButton.Left, EditorModifiers modifiers = EditorModifiers.None) =>
            Editor.Press(History, World(local), Pixel, button, modifiers);

        public void Drag(Vector2 fromLocal, Vector2 toLocal, EditorModifiers modifiers = EditorModifiers.None)
        {
            Assert.True(Press(fromLocal, modifiers: modifiers));
            Editor.Move(World(toLocal), Pixel, modifiers);
            Editor.Release(History);
        }

        public float[] Points => (float[])Curve.Points.Clone();
    }

    private static void AssertUndoRedo(Rig rig, float[] before, float[] after, int steps = 1)
    {
        Assert.Equal(after, rig.Curve.Points);
        for (var i = 0; i < steps; i++)
            Assert.True(rig.History.Undo());
        Assert.Equal(before, rig.Curve.Points);
        for (var i = 0; i < steps; i++)
            Assert.True(rig.History.Redo());
        Assert.Equal(after, rig.Curve.Points);
    }

    [Fact]
    public void DraggingAPointIsOneUndoStep()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        var before = rig.Points;
        Assert.True(rig.Press(new Vector2(101, 1))); // within the grab radius
        rig.Editor.Move(Rig.World(new Vector2(120, 30)), Pixel, EditorModifiers.None);
        rig.Editor.Move(Rig.World(new Vector2(130, 40)), Pixel, EditorModifiers.None);
        rig.Editor.Release(rig.History);
        Assert.Equal(new Vector2(129, 39), rig.Curve.GetPointPosition(1)); // keeps the grab offset
        Assert.Equal(1, rig.Editor.SelectedPoint);
        AssertUndoRedo(rig, before, rig.Points);
    }

    [Fact]
    public void ADragThatMovesNothingCommitsNothing()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        Assert.True(rig.Press(new Vector2(100, 0)));
        rig.Editor.Release(rig.History);
        Assert.False(rig.History.CanUndo);
    }

    [Fact]
    public void EscapePutsTheCurveBack()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        var before = rig.Points;
        Assert.True(rig.Press(new Vector2(100, 0)));
        rig.Editor.Move(Rig.World(new Vector2(150, 80)), Pixel, EditorModifiers.None);
        rig.Editor.CancelDrag();
        Assert.Equal(before, rig.Curve.Points);
        Assert.False(rig.History.CanUndo);
        Assert.False(rig.Editor.IsDragging);
    }

    [Fact]
    public void CommandClickAddsAPointAndTheAddToolToo()
    {
        var rig = new Rig(Vector2.Zero);
        var before = rig.Points;
        Assert.True(rig.Press(new Vector2(60, 20), modifiers: EditorModifiers.Command));
        rig.Editor.Release(rig.History);
        Assert.Equal(2, rig.Curve.PointCount);
        Assert.Equal(new Vector2(60, 20), rig.Curve.GetPointPosition(1));
        AssertUndoRedo(rig, before, rig.Points);

        rig.Editor.RunCommand("curve.tool.add", rig.History);
        rig.Drag(new Vector2(200, 0), new Vector2(210, 5)); // add then drag: one step
        Assert.Equal(3, rig.Curve.PointCount);
        Assert.Equal(new Vector2(210, 5), rig.Curve.GetPointPosition(2));
        Assert.True(rig.History.Undo());
        Assert.Equal(2, rig.Curve.PointCount);
    }

    [Fact]
    public void AddingToAPathWithoutACurveCreatesOne()
    {
        var editor = new CurveEditor(null);
        var path = new Path2D { Name = "Empty" };
        editor.EditForTests(path);
        var history = new UndoRedo();
        Assert.True(editor.Press(history, new Vector2(5, 5), Pixel, MouseButton.Left, EditorModifiers.Command));
        editor.Release(history);
        Assert.Equal(1, path.Curve!.PointCount);
        Assert.True(history.Undo());
        Assert.Equal(0, path.Curve!.PointCount);
        Assert.True(history.Undo());
        Assert.Null(path.Curve);
    }

    [Fact]
    public void InsertKeepsTheShape()
    {
        var curve = new Curve2D { BakeInterval = 0.5f };
        curve.SetSmoothPoints([new(0, 0), new(100, 60), new(200, -20), new(300, 40)], closed: true);
        var path = new Path2D { Curve = curve };
        var target = new Curve2DTarget(path);
        var reference = new Curve2D { BakeInterval = 0.5f, Closed = true, Points = (float[])curve.Points.Clone() };

        CurveEditing.Insert(target, 1, 0.37f);
        CurveEditing.Insert(target, 4, 0.5f); // the seam (segment 4 of 5)
        Assert.Equal(6, curve.PointCount);
        Assert.Equal(reference.GetBakedLength(), curve.GetBakedLength(), 1);
        foreach (var p in curve.GetBakedPoints())
            Assert.True(Vector2.Distance(reference.GetClosestPoint(p), p) < 0.01f, $"insert moved the curve at {p}");
    }

    [Fact]
    public void TheInsertToolSplitsTheClickedSegment()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0), new Vector2(100, 100));
        var before = rig.Points;
        rig.Editor.Tool = CurveTool.Insert;
        Assert.True(rig.Press(new Vector2(102, 40)));
        rig.Editor.Release(rig.History);
        Assert.Equal(4, rig.Curve.PointCount);
        Assert.Equal(100f, rig.Curve.GetPointPosition(2).X, 2);
        Assert.Equal(40f, rig.Curve.GetPointPosition(2).Y, 1);
        AssertUndoRedo(rig, before, rig.Points);
        Assert.True(rig.Press(new Vector2(50, 70))); // the Insert tool swallows clicks off the curve
        Assert.Equal(4, rig.Curve.PointCount);
    }

    [Fact]
    public void DeleteByToolRightClickAndKey()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0), new Vector2(200, 0), new Vector2(300, 0));
        var before = rig.Points;
        Assert.True(rig.Press(new Vector2(100, 0), MouseButton.Right));
        Assert.Equal(3, rig.Curve.PointCount);
        AssertUndoRedo(rig, before, rig.Points);

        rig.Editor.Tool = CurveTool.Delete;
        Assert.True(rig.Press(new Vector2(0, 0)));
        Assert.Equal(new Vector2(200, 0), rig.Curve.GetPointPosition(0));

        rig.Editor.SelectedPoint = 1;
        Assert.Equal("curve.delete_point", CurveEditor.ShortcutFor(Key.Delete, EditorModifiers.None, hasSelectedPoint: true));
        Assert.Null(CurveEditor.ShortcutFor(Key.Delete, EditorModifiers.None, hasSelectedPoint: false));
        Assert.True(rig.Editor.RunCommand("curve.delete_point", rig.History));
        Assert.Equal(1, rig.Curve.PointCount);
        Assert.Equal(-1, rig.Editor.SelectedPoint);
        Assert.Equal(3, rig.History.Position);
    }

    [Fact]
    public void HandlesMirrorUnlessShiftAndCommandPullsNewOnes()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        rig.Drag(new Vector2(100, 0), new Vector2(130, 10), EditorModifiers.Command); // pull handles out of point 1
        Assert.Equal(new Vector2(30, 10), rig.Curve.GetPointOut(1));
        Assert.Equal(new Vector2(-30, -10), rig.Curve.GetPointIn(1));
        Assert.Equal(1, rig.History.Position);

        rig.Drag(new Vector2(70, -10), new Vector2(70, 20)); // the in handle, mirrored
        Assert.Equal(new Vector2(-30, 20), rig.Curve.GetPointIn(1));
        Assert.Equal(new Vector2(30, -20), rig.Curve.GetPointOut(1));

        rig.Drag(new Vector2(130, -20), new Vector2(150, -20), EditorModifiers.Shift); // broken: the in handle stays
        Assert.Equal(new Vector2(50, -20), rig.Curve.GetPointOut(1));
        Assert.Equal(new Vector2(-30, 20), rig.Curve.GetPointIn(1));
        Assert.Equal(3, rig.History.Position);
    }

    [Fact]
    public void MovesSnapToTheGridStep()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        rig.Editor.SnapEnabled = true;
        rig.Editor.SnapStep = 16f;
        rig.Drag(new Vector2(100, 0), new Vector2(121, 9));
        // World (221, 59) snaps to (224, 64); the path sits at (100, 50).
        Assert.Equal(new Vector2(124, 14), rig.Curve.GetPointPosition(1));
    }

    [Fact]
    public void CloseReverseAndSmoothAreOneStepEach()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0), new Vector2(100, 100));
        Assert.True(rig.Editor.RunCommand("curve.close", rig.History));
        Assert.True(rig.Curve.Closed);
        Assert.True(rig.History.Undo());
        Assert.False(rig.Curve.Closed);
        Assert.True(rig.History.Redo());

        var before = rig.Points;
        rig.Editor.SelectedPoint = 0;
        Assert.True(rig.Editor.RunCommand("curve.reverse", rig.History));
        Assert.Equal(new Vector2(100, 100), rig.Curve.GetPointPosition(0));
        Assert.Equal(2, rig.Editor.SelectedPoint); // the same point
        AssertUndoRedo(rig, before, rig.Points);

        before = rig.Points;
        rig.Editor.SelectedPoint = 1;
        Assert.True(rig.Editor.RunCommand("curve.smooth", rig.History));
        Assert.NotEqual(Vector2.Zero, rig.Curve.GetPointOut(1));
        Assert.Equal(-rig.Curve.GetPointOut(1), rig.Curve.GetPointIn(1));
        Assert.Equal(Vector2.Zero, rig.Curve.GetPointOut(0)); // only the selected point
        AssertUndoRedo(rig, before, rig.Points);
    }

    [Fact]
    public void TheSelectToolLeavesEmptySpaceToSelection()
    {
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        rig.Editor.SelectedPoint = 1;
        Assert.False(rig.Press(new Vector2(50, 80)));
        Assert.Equal(-1, rig.Editor.SelectedPoint);
        Assert.False(rig.Press(new Vector2(50, 80), MouseButton.Right)); // right on nothing pans
        Assert.False(rig.Press(new Vector2(100, 0), modifiers: EditorModifiers.Alt));
    }

    [Fact]
    public void UndoAfterALaterDragRestoresTheEarlierState()
    {
        // The history's arrays must never be the curve's live storage.
        var rig = new Rig(Vector2.Zero, new Vector2(100, 0));
        var start = rig.Points;
        rig.Drag(new Vector2(100, 0), new Vector2(100, 50));
        var middle = rig.Points;
        Assert.True(rig.History.Undo());
        rig.Drag(new Vector2(0, 0), new Vector2(-40, 0));
        Assert.True(rig.History.Undo());
        Assert.Equal(start, rig.Curve.Points);
        Assert.NotEqual(middle, start);
    }

    [Fact]
    public void TheBarHasIconButtonsWithTooltips()
    {
        var rig = new Rig(Vector2.Zero);
        var rml = rig.Editor.BarRml();
        foreach (var command in new[] { "curve.tool.select", "curve.tool.add", "curve.tool.insert", "curve.tool.delete", "curve.close", "curve.reverse", "curve.smooth" })
            Assert.Contains($"data-command=\"{command}\"", rml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tooltip=\"\"", rml, StringComparison.Ordinal);
    }

    [Fact]
    public void NewPathsStartWithACurve()
    {
        var path2D = new Path2D();
        NewNodeDefaults.Apply(path2D);
        Assert.NotNull(path2D.Curve);
        var path3D = new Path3D();
        NewNodeDefaults.Apply(path3D);
        Assert.True(path3D.Curve!.UpVectorEnabled);
    }
}
