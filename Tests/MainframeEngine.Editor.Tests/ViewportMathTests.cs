using System.Numerics;

namespace MainframeEngine.Editor.Tests;

public sealed class EditorCameraTests
{
    private static readonly Vector2 View = new(1200, 800);

    [Fact]
    public void ProjectionMatchesTheRenderCameraMatrices()
    {
        var camera = new EditorCamera { Pivot = new Vector3(1, 2, 3), Distance = 10, Yaw = 35, Pitch = -20 };
        camera.Apply();
        camera.RenderCamera.AspectRatio = View.X / View.Y;
        var viewProjection = camera.RenderCamera.ViewMatrix * camera.RenderCamera.ProjectionMatrix;

        foreach (var point in new[] { new Vector3(1, 2, 3), new Vector3(3, 0, 1), new Vector3(-2, 4, 5) })
        {
            Assert.True(camera.Project(point, View.X, View.Y, out var pixel));
            var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            var expected = new Vector2((ndc.X + 1) * 0.5f * View.X, (1 - ndc.Y) * 0.5f * View.Y); // Vulkan flipped viewport
            Assert.True(Vector2.Distance(expected, pixel) < 0.5f, $"{point}: {pixel} vs {expected}");
        }
    }

    [Fact]
    public void RaysGoThroughTheProjectedPixel()
    {
        var camera = new EditorCamera { Pivot = Vector3.Zero, Distance = 8, Yaw = -40, Pitch = -30 };
        var target = new Vector3(0.5f, 1, -2);
        Assert.True(camera.Project(target, View.X, View.Y, out var pixel));

        var (origin, direction) = camera.Ray(pixel.X, pixel.Y, View.X, View.Y);
        var toTarget = Vector3.Normalize(target - origin);
        Assert.True(Vector3.Dot(direction, toTarget) > 0.99999f);
        Assert.False(camera.Project(camera.Position - camera.Forward, View.X, View.Y, out _)); // behind
    }

    [Fact]
    public void OrbitKeepsThePivotLookKeepsThePositionZoomAndFrameChangeDistance()
    {
        var camera = new EditorCamera { Pivot = new Vector3(1, 0, 0), Distance = 5 };
        camera.Orbit(100, 0);
        Assert.Equal(new Vector3(1, 0, 0), camera.Pivot);
        Assert.Equal(5, Vector3.Distance(camera.Position, camera.Pivot), 3);

        var position = camera.Position;
        camera.Look(50, 20);
        Assert.True(Vector3.Distance(position, camera.Position) < 1e-4f);

        camera.Zoom(1);
        Assert.True(camera.Distance < 5);
        camera.Pitch = 120;
        Assert.Equal(89, camera.Pitch);

        camera.Frame(new Vector3(0, 3, 0), 2);
        Assert.Equal(new Vector3(0, 3, 0), camera.Pivot);
        Assert.True(camera.Distance > 2);
    }

    [Fact]
    public void PanMovesThePointUnderTheMouseWithIt()
    {
        var camera = new EditorCamera { Pivot = Vector3.Zero, Distance = 10, Yaw = 0, Pitch = 0 };
        camera.Project(Vector3.Zero, View.X, View.Y, out var before);
        camera.Pan(100, 0, View.Y);
        camera.Project(Vector3.Zero, View.X, View.Y, out var after);
        Assert.Equal(before.X + 100, after.X, 1);
        Assert.Equal(before.Y, after.Y, 1);
    }

    [Fact]
    public void FlyMovesAlongTheViewAxes()
    {
        var camera = new EditorCamera { Pivot = Vector3.Zero, Yaw = 0, Pitch = 0, FlySpeed = 2 };
        camera.Fly(new Vector3(0, 0, 1), 0.5f);
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), camera.Pivot) < 1e-5f);
        camera.Fly(new Vector3(1, 0, 0), 1f, fast: true);
        Assert.Equal(8, camera.Pivot.X, 4);
    }

    [Theory]
    [InlineData(EditorView.Front, 0, 0, -1)]
    [InlineData(EditorView.Right, -1, 0, 0)]
    [InlineData(EditorView.Top, 0, -1, 0)]
    public void AxisViewsLookAlongTheExpectedDirection(EditorView view, float x, float y, float z)
    {
        var camera = new EditorCamera();
        camera.SetView(view);
        Assert.True(Vector3.Dot(camera.Forward, new Vector3(x, y, z)) > 0.999f);
    }
}

public sealed class TransformGizmoTests
{
    private static readonly Vector2 View = new(1000, 800);
    private readonly EditorCamera _camera = new() { Pivot = Vector3.Zero, Distance = 10, Yaw = -30, Pitch = -25 };

    private Vector2 Pixel(Vector3 point)
    {
        _camera.Project(point, View.X, View.Y, out var pixel);
        return pixel;
    }

    private float Length => TransformGizmo.SizePixels * _camera.WorldPerPixel(Vector3.Zero, View.Y);

    [Fact]
    public void HitTestFindsTheAxisUnderTheMouse()
    {
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        Assert.Equal(GizmoHandle.X, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, Pixel(Vector3.UnitX * Length * 0.7f)));
        Assert.Equal(GizmoHandle.Y, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, Pixel(Vector3.UnitY * Length * 0.7f)));
        Assert.Equal(GizmoHandle.Z, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, Pixel(Vector3.UnitZ * Length * 0.7f)));
        Assert.Equal(GizmoHandle.Center, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, Pixel(Vector3.Zero)));
        Assert.Equal(GizmoHandle.None, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, new Vector2(5, 5)));

        gizmo.Mode = GizmoMode.Select;
        Assert.Equal(GizmoHandle.None, gizmo.HitTest(_camera, View, Vector3.Zero, Quaternion.Identity, Pixel(Vector3.UnitX * Length * 0.7f)));
    }

    [Fact]
    public void TranslateDragFollowsTheAxisAndSnaps()
    {
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate };
        var start = Pixel(Vector3.UnitX * Length * 0.5f);
        gizmo.BeginDrag(GizmoHandle.X, _camera, View, start, Vector3.Zero, Quaternion.Identity, Vector3.One);
        Assert.True(gizmo.IsDragging);

        var (position, rotation, scale) = gizmo.Drag(_camera, View, Pixel(new Vector3(Length * 0.5f + 1.3f, 0, 0)));
        Assert.Equal(1.3f, position.X, 2);
        Assert.Equal(0f, position.Y, 3);
        Assert.Equal(0f, position.Z, 3);
        Assert.Equal(Quaternion.Identity, rotation);
        Assert.Equal(Vector3.One, scale);

        gizmo.Snap = new GizmoSnap(true, Translate: 0.5f);
        (position, _, _) = gizmo.Drag(_camera, View, Pixel(new Vector3(Length * 0.5f + 1.3f, 0, 0)));
        Assert.Equal(1.5f, position.X, 3);
        gizmo.EndDrag();
        Assert.False(gizmo.IsDragging);
    }

    [Fact]
    public void LocalHandlesFollowTheNodeRotation()
    {
        var gizmo = new TransformGizmo { Mode = GizmoMode.Translate, Local = true };
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var (x, _, _) = gizmo.Axes(rotation);
        Assert.True(Vector3.Distance(x, -Vector3.UnitZ) < 1e-5f);
        gizmo.Local = false;
        Assert.Equal(Vector3.UnitX, gizmo.Axes(rotation).X);
        gizmo.Mode = GizmoMode.Scale; // scale is always local
        Assert.True(Vector3.Distance(gizmo.Axes(rotation).X, -Vector3.UnitZ) < 1e-5f);
    }

    [Fact]
    public void RotateDragTurnsAboutTheAxisAndSnapsToTheAngleStep()
    {
        var gizmo = new TransformGizmo { Mode = GizmoMode.Rotate, Snap = new GizmoSnap(true, RotateDegrees: 15f) };
        var center = Pixel(Vector3.Zero);
        gizmo.BeginDrag(GizmoHandle.Y, _camera, View, center + new Vector2(100, 0), Vector3.Zero, Quaternion.Identity, Vector3.One);

        // A quarter turn on screen in small steps (the angle accumulates across the ±180° seam too).
        (Vector3, Quaternion, Vector3) result = default;
        for (var i = 1; i <= 10; i++)
        {
            var a = i * MathF.PI / 20;
            result = gizmo.Drag(_camera, View, center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 100);
        }

        var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(result.Item2.W), 0, 1));
        Assert.Equal(90f, float.RadiansToDegrees(angle), 1);
        var axis = Vector3.Normalize(new Vector3(result.Item2.X, result.Item2.Y, result.Item2.Z));
        Assert.True(MathF.Abs(axis.Y) > 0.999f);
    }

    [Fact]
    public void ScaleDragScalesOneAxisOrUniformly()
    {
        var gizmo = new TransformGizmo { Mode = GizmoMode.Scale };
        gizmo.BeginDrag(GizmoHandle.X, _camera, View, Pixel(Vector3.UnitX * Length * 0.5f), Vector3.Zero, Quaternion.Identity, new Vector3(2, 1, 1));
        var (_, _, scale) = gizmo.Drag(_camera, View, Pixel(Vector3.UnitX * Length));
        Assert.Equal(4f, scale.X, 1);
        Assert.Equal(1f, scale.Y);
        gizmo.EndDrag();

        gizmo.BeginDrag(GizmoHandle.Center, _camera, View, new Vector2(500, 400), Vector3.Zero, Quaternion.Identity, Vector3.One);
        (_, _, scale) = gizmo.Drag(_camera, View, new Vector2(500 + TransformGizmo.SizePixels * 1.5f, 400));
        Assert.Equal(new Vector3(2), scale);
    }

    [Fact]
    public void DrawingAddsLinesToTheBatch()
    {
        var lines = new DebugLines();
        foreach (var mode in new[] { GizmoMode.Translate, GizmoMode.Rotate, GizmoMode.Scale })
        {
            lines.Clear();
            new TransformGizmo { Mode = mode }.Draw(lines, _camera, View, Vector3.Zero, Quaternion.Identity);
            Assert.True(lines.LineCount > 3, mode.ToString());
        }

        lines.Clear();
        new TransformGizmo { Mode = GizmoMode.Select }.Draw(lines, _camera, View, Vector3.Zero, Quaternion.Identity);
        Assert.Equal(0, lines.LineCount);
    }

    [Fact]
    public void GeometryHelpers()
    {
        Assert.Equal(1f, TransformGizmo.DistanceToSegment(new Vector2(5, 1), Vector2.Zero, new Vector2(10, 0)));
        Assert.Equal(new Vector3(0, 0, 0), TransformGizmo.IntersectPlane(new Vector3(0, 5, 0), -Vector3.UnitY, Vector3.Zero, Vector3.UnitY));
        Assert.Equal(1.5f, TransformGizmo.SnapTo(1.3f, 0.5f));
        Assert.Equal(1.3f, TransformGizmo.SnapTo(1.3f, 0));
        var closest = TransformGizmo.ClosestPointOnAxis(new Vector3(3, 5, 0), -Vector3.UnitY, Vector3.Zero, Vector3.UnitX);
        Assert.True(Vector3.Distance(new Vector3(3, 0, 0), closest) < 1e-5f);
    }
}
