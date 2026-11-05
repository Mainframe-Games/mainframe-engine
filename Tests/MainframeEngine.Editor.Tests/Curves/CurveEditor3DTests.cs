using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G13.6: the curve tool on a <see cref="Path3D"/> in a 3D view: camera-plane and vertical drags, adding level
/// with the curve or on the terrain, the tilt ring, insert, Fit to Ground; each one undo step.</summary>
public sealed class CurveEditor3DTests : IDisposable
{
    private static readonly Vector2 View = new(1000, 800);

    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };
    private readonly EditorCamera _camera = new() { Pivot = new Vector3(16, 0, 16), Distance = 40f, Yaw = 25f, Pitch = -50f };
    private readonly CurveEditor _editor = new(null);
    private readonly UndoRedo _history = new();
    private readonly Curve3D _curve = new() { BakeInterval = 0.1f, UpVectorEnabled = true };
    private readonly Path3D _path;

    public CurveEditor3DTests()
    {
        _tree.ChangeScene(_scene);
        _curve.AddPoint(new Vector3(8, 1, 8));
        _curve.AddPoint(new Vector3(16, 1, 12));
        _curve.AddPoint(new Vector3(24, 1, 8));
        _path = new Path3D { Name = "Path", Curve = _curve };
        _scene.AddChild(_path);
        _editor.EditForTests(_path);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private CurveEditor.View3D ViewOf => new(_camera, View, 1f);

    private Vector2 Pixel(Vector3 world)
    {
        Assert.True(_camera.Project(world, View.X, View.Y, out var pixel));
        return pixel;
    }

    private void Drag(Vector3 from, Vector3 to, EditorModifiers modifiers = EditorModifiers.None)
    {
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(from), MouseButton.Left, modifiers));
        _editor.Move3D(ViewOf, Pixel(to), modifiers);
        _editor.Release(_history);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"{actual} is not {expected}");

    private Terrain3D AddTerrain(Func<float, float, float> height)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(height);
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        _scene.AddChild(terrain);
        _editor.TerrainForTests(terrain);
        return terrain;
    }

    [Fact]
    public void APointDragsInThePlaneFacingTheCamera()
    {
        var before = (float[])_curve.Points.Clone();
        var p = _curve.GetPointPosition(1);
        var to = p + _camera.Right * 2f + _camera.Up * 1f;
        Drag(p, to);
        Near(to, _curve.GetPointPosition(1), 0.01f);
        Assert.Equal(1, _editor.SelectedPoint);
        var after = (float[])_curve.Points.Clone();
        Assert.True(_history.Undo());
        Assert.Equal(before, _curve.Points);
        Assert.True(_history.Redo());
        Assert.Equal(after, _curve.Points);
    }

    [Fact]
    public void AltDragsAPointStraightUp()
    {
        var p = _curve.GetPointPosition(0);
        var flatRight = Vector3.Normalize(new Vector3(_camera.Right.X, 0, _camera.Right.Z));
        Drag(p, p + new Vector3(0, 2.5f, 0) + flatRight * 0.7f, EditorModifiers.Alt);
        var moved = _curve.GetPointPosition(0);
        Assert.Equal(p.X, moved.X, 3);
        Assert.Equal(p.Z, moved.Z, 3);
        Assert.Equal(p.Y + 2.5f, moved.Y, 2);
    }

    [Fact]
    public void AltElsewhereIsLeftToOrbiting()
    {
        Assert.False(_editor.Press3D(_history, ViewOf, Pixel(new Vector3(16, 0, 20)), MouseButton.Left, EditorModifiers.Alt));
    }

    [Fact]
    public void TheAddToolAddsLevelWithTheLastPoint()
    {
        _editor.Tool = CurveTool.Add;
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(new Vector3(28, 1, 14)), MouseButton.Left, EditorModifiers.None));
        _editor.Release(_history);
        Assert.Equal(4, _curve.PointCount);
        Near(new Vector3(28, 1, 14), _curve.GetPointPosition(3), 0.01f);
        Assert.True(_history.Undo());
        Assert.Equal(3, _curve.PointCount);
    }

    [Fact]
    public void InsertSplitsTheClickedSegment()
    {
        _editor.Tool = CurveTool.Insert;
        var on = _path.GlobalTransform.TransformPoint(_curve.Sample(0, 0.4f));
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(on), MouseButton.Left, EditorModifiers.None));
        _editor.Release(_history);
        Assert.Equal(4, _curve.PointCount);
        Near(on, _curve.GetPointPosition(1), 0.05f);
    }

    [Fact]
    public void TheTiltRingRollsTheSelectedPoint()
    {
        _editor.TiltHandles = true;
        _editor.SelectedPoint = 1;
        var target = (Curve3DTarget)_editor.Target!;
        target.FrameAt(1, out var tangent, out var up);
        var center = _curve.GetPointPosition(1);
        var radius = 36f * _camera.WorldPerPixel(center, View.Y);
        var side = Vector3.Cross(tangent, up);
        Drag(center + up * radius, center + side * radius);
        Assert.Equal(MathF.PI / 2, _curve.GetPointTilt(1), 2);
        Assert.True(_history.Undo());
        Assert.Equal(0f, _curve.GetPointTilt(1));

        _editor.SnapEnabled = true;
        _editor.SnapAngleStep = 15f;
        var angle = float.DegreesToRadians(37f);
        var to = center + (up * MathF.Cos(angle) + side * MathF.Sin(angle)) * radius;
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(center + up * radius), MouseButton.Left, EditorModifiers.None));
        _editor.Move3D(ViewOf, Pixel(to), EditorModifiers.None);
        Assert.Equal("Tilt 30.0°", _editor.Label);
        _editor.Release(_history);
        Assert.Equal(float.DegreesToRadians(30f), _curve.GetPointTilt(1), 4);
        Assert.Null(_editor.Label);
    }

    [Fact]
    public void GroundSnapAddsAndDragsOntoTheTerrain()
    {
        static float Height(float x, float z) => 2f + 0.1f * x;
        AddTerrain(Height);
        _editor.GroundSnap = true;
        _editor.HeightAbove = 1.5f;
        _editor.Tool = CurveTool.Add;
        var ground = new Vector3(20, Height(20, 20), 20);
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(ground), MouseButton.Left, EditorModifiers.None));
        _editor.Release(_history);
        var added = _curve.GetPointPosition(3);
        Assert.Equal(20f, added.X, 1);
        Assert.Equal(20f, added.Z, 1);
        Assert.Equal(Height(added.X, added.Z) + 1.5f, added.Y, 2);

        _editor.Tool = CurveTool.Select;
        var target = new Vector3(10, Height(10, 6), 6);
        Drag(_curve.GetPointPosition(0), target);
        var dragged = _curve.GetPointPosition(0);
        Assert.Equal(Height(dragged.X, dragged.Z) + 1.5f, dragged.Y, 2);
    }

    [Fact]
    public void FitToGroundMovesEveryPointOntoTheTerrainInOneStep()
    {
        static float Height(float x, float z) => 1f + 0.05f * z;
        AddTerrain(Height);
        _curve.SetPointPosition(0, new Vector3(8, 9, 8));
        _curve.SetPointPosition(2, new Vector3(24, -3, 8));
        var before = (float[])_curve.Points.Clone();
        _editor.HeightAbove = 0.5f;
        Assert.True(_editor.RunCommand("curve.fit_ground", _history));
        for (var i = 0; i < _curve.PointCount; i++)
        {
            var p = _curve.GetPointPosition(i);
            Assert.Equal(Height(p.X, p.Z) + 0.5f, p.Y, 2); // the terrain stores heights quantised
        }

        Assert.True(_history.Undo());
        Assert.Equal(before, _curve.Points);
    }

    [Fact]
    public void WithoutATerrainFitToGroundDoesNothing()
    {
        Assert.False(_editor.RunCommand("curve.fit_ground", _history));
        Assert.Contains("none found", _editor.BarRml(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBarShowsThe3DTools()
    {
        var rml = _editor.BarRml();
        foreach (var command in new[] { "curve.tilt", "curve.ground", "curve.height.less", "curve.height.more", "curve.fit_ground" })
            Assert.Contains($"data-command=\"{command}\"", rml, StringComparison.Ordinal);
        Assert.True(_editor.RunCommand("curve.height.more", _history));
        Assert.Equal(0.5f, _editor.HeightAbove);
        Assert.Contains("0.5 m", _editor.BarRml(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewPath3DGetsACurveWithUpVectors()
    {
        var empty = new Path3D { Name = "Empty" };
        _scene.AddChild(empty);
        _editor.EditForTests(empty);
        _editor.Tool = CurveTool.Add;
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(new Vector3(10, 0, 10)), MouseButton.Left, EditorModifiers.None));
        _editor.Release(_history);
        Assert.True(empty.Curve!.UpVectorEnabled);
        Assert.Equal(1, empty.Curve.PointCount);
        Near(new Vector3(10, 0, 10), empty.Curve.GetPointPosition(0), 0.01f);
    }
}
