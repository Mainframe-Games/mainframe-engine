using System.Numerics;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// G8c.4: a <see cref="River3D"/> in the curve tool (width and depth handles, new points continue the section, its own
/// ground snap) and its inspector's Carve / Uncarve / Fit to ground, each one undo step.
/// </summary>
public sealed class RiverEditingTests : IDisposable
{
    private static readonly Vector2 View = new(1000, 800);

    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };
    private readonly EditorCamera _camera = new() { Pivot = new Vector3(16, 0, 16), Distance = 30f, Yaw = 20f, Pitch = -55f };
    private readonly CurveEditor _editor = new(null);
    private readonly UndoRedo _history = new();
    private readonly Curve3D _curve = new() { BakeInterval = 0.1f };
    private readonly River3D _river;

    public RiverEditingTests()
    {
        _tree.ChangeScene(_scene);
        _curve.AddPoint(new Vector3(16, 1.6f, 28));
        _curve.AddPoint(new Vector3(16, 1.5f, 16));
        _curve.AddPoint(new Vector3(16, 1.4f, 4));
        for (var i = 0; i < 3; i++)
        {
            _curve.SetPointWidth(i, 4f);
            _curve.SetPointDepth(i, 0.6f);
        }

        _river = new River3D { Name = "River", Curve = _curve };
        _scene.AddChild(_river);
        _editor.EditForTests(_river);
        _editor.Tick();
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

    private void Drag(Vector3 from, Vector3 to)
    {
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(from), MouseButton.Left, EditorModifiers.None));
        _editor.Move3D(ViewOf, Pixel(to), EditorModifiers.None);
        _editor.Release(_history);
    }

    private Terrain3D AddTerrain(float height)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom((_, _) => height);
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        _scene.AddChild(terrain);
        _editor.TerrainForTests(terrain);
        return terrain;
    }

    [Fact]
    public void ARiverIsA3DTargetWithWidthAndDepthButNoTilt()
    {
        Assert.True(_editor.Is3D);
        Assert.True(_editor.IsRiver);
        var target = Assert.IsType<Curve3DTarget>(_editor.Target);
        Assert.True(target.HasWidth);
        Assert.False(target.HasTilt);
        Assert.Equal(4f, target.GetWidth(1));
    }

    [Fact]
    public void DraggingABankWidensTheRiverSymmetricallyInOneUndoStep()
    {
        _editor.SelectedPoint = 1;
        var before = (float[])_curve.Points.Clone();
        // The banks of point 1 (the river runs along −Z: across is ±X).
        var bank = new Vector3(18, 1.5f, 16);
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(bank), MouseButton.Left, EditorModifiers.None));
        _editor.Move3D(ViewOf, Pixel(new Vector3(19, 1.5f, 16)), EditorModifiers.None);
        Assert.StartsWith("Width 6.0", _editor.Label);
        _editor.Release(_history);
        Assert.Equal(6f, _curve.GetPointWidth(1), 1);
        Assert.Equal(4f, _curve.GetPointWidth(0)); // only the selected point
        Assert.True(_history.Undo());
        Assert.Equal(before, _curve.Points);
    }

    [Fact]
    public void DraggingTheBedHandleSetsTheDepth()
    {
        _editor.SelectedPoint = 1;
        var bed = new Vector3(16, 1.5f - 0.6f, 16);
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(bed), MouseButton.Left, EditorModifiers.None));
        _editor.Move3D(ViewOf, Pixel(new Vector3(16, 1.5f - 1.2f, 16)), EditorModifiers.None);
        Assert.StartsWith("Depth 1.2", _editor.Label);
        _editor.Release(_history);
        Assert.Equal(1.2f, _curve.GetPointDepth(1), 1);
    }

    [Fact]
    public void NewPointsContinueTheLastSection()
    {
        _curve.SetPointWidth(2, 7f);
        _curve.SetPointDepth(2, 0.9f);
        _editor.Tool = CurveTool.Add;
        Assert.True(_editor.Press3D(_history, ViewOf, Pixel(new Vector3(20, 1.4f, 2)), MouseButton.Left, EditorModifiers.None));
        _editor.Release(_history);
        Assert.Equal(4, _curve.PointCount);
        Assert.Equal(7f, _curve.GetPointWidth(3));
        Assert.Equal(0.9f, _curve.GetPointDepth(3));
        Assert.True(_history.Undo());
        Assert.Equal(3, _curve.PointCount);
    }

    [Fact]
    public void RiversKeepTheirOwnGroundSnapApartFromPaths()
    {
        Assert.True(_editor.GroundSnap); // a river starts snapped 0.3 m under the ground
        Assert.Equal(-0.3f, _editor.HeightAbove, 3);
        var path = new Path3D { Name = "Path", Curve = new Curve3D() };
        _scene.AddChild(path);
        _editor.EditForTests(path);
        _editor.Tick();
        Assert.False(_editor.GroundSnap); // paths keep theirs (off by default)
        _editor.HeightAbove = 1.5f;
        _editor.EditForTests(_river);
        _editor.Tick();
        Assert.Equal(-0.3f, _editor.HeightAbove, 3);
        _editor.EditForTests(path);
        _editor.Tick();
        Assert.Equal(1.5f, _editor.HeightAbove, 3);
    }

    private sealed class Context : IInspectorContext
    {
        public UndoRedo History { get; } = new();

        public void SetProperty(object target, ExportPropertyInfo property, object? value, string? mergeKey = null) =>
            History.Commit(SetPropertyAction.Create(target, property, value), mergeKey: mergeKey);
    }

    [Fact]
    public void TheInspectorCarvesAndUncarvesAsUndoSteps()
    {
        var terrain = AddTerrain(2f);
        _river.Terrain = terrain;
        var data = terrain.Data!;
        var ground = data.Heights.ToArray();
        var inspector = new River3DInspector();
        var context = new Context();
        Assert.Contains("data-action=\"river-carve\"", inspector.GetHeaderRml(_river));
        Assert.Contains("not carved", River3DInspector.Status(_river));

        inspector.OnAction(_river, "river-carve", context);
        Assert.NotEmpty(_river.CarveId);
        var carved = data.Heights.ToArray();
        Assert.NotEqual(ground, carved);
        Assert.Contains("· carved", River3DInspector.Status(_river));

        Assert.True(context.History.Undo());
        Assert.Equal(ground, data.Heights.ToArray());
        Assert.Equal("", _river.CarveId);
        Assert.True(context.History.Redo());
        Assert.Equal(carved, data.Heights.ToArray());

        inspector.OnAction(_river, "river-uncarve", context);
        Assert.Equal(ground, data.Heights.ToArray());
        Assert.True(context.History.Undo());
        Assert.Equal(carved, data.Heights.ToArray());
    }

    [Fact]
    public void FitToGroundIsOneCurveChange()
    {
        var terrain = AddTerrain(3f);
        _river.Terrain = terrain;
        var before = (float[])_curve.Points.Clone();
        var context = new Context();
        new River3DInspector().OnAction(_river, "river-fit", context);
        for (var i = 0; i < 3; i++)
            Assert.Equal(3f - River3DInspector.FitDepth, _curve.GetPointPosition(i).Y, 2);
        Assert.True(context.History.Undo());
        Assert.Equal(before, _curve.Points);
    }
}
