using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>E5 2D editing: the orthographic editor camera, the 2D gizmo math, grid steps, and the mode in the editor.</summary>
public sealed class Camera2DMathTests
{
    private static readonly Vector2 View = new(1000, 600);

    [Fact]
    public void ScreenAndWorldConvertBothWaysWithYDown()
    {
        var camera = new EditorCamera { Is2D = true, Center2D = new Vector2(100, 50), Zoom2D = 2f };
        Assert.Equal(new Vector2(100, 50), camera.ScreenToWorld2D(View / 2, View));
        Assert.Equal(new Vector2(110, 50), camera.ScreenToWorld2D(View / 2 + new Vector2(20, 0), View));
        Assert.Equal(new Vector2(100, 60), camera.ScreenToWorld2D(View / 2 + new Vector2(0, 20), View)); // down on screen = +y (Godot)
        var world = new Vector2(-37.5f, 210f);
        var pixel = camera.WorldToScreen2D(world, View);
        Assert.True(Vector2.Distance(world, camera.ScreenToWorld2D(pixel, View)) < 1e-3f);

        Assert.True(camera.Project(new Vector3(world, 0), View.X, View.Y, out var projected));
        Assert.Equal(pixel, projected);
        var (origin, direction) = camera.Ray(pixel.X, pixel.Y, View.X, View.Y);
        Assert.Equal(Vector3.UnitZ, direction); // the z = 0 plane is seen from −Z
        Assert.True(Vector2.Distance(world, new Vector2(origin.X, origin.Y)) < 1e-3f);
        Assert.Equal(0.5f, camera.WorldPerPixel(Vector3.Zero, View.Y));
        Assert.Same(camera.OrthoCamera, camera.ActiveCamera);
    }

    [Fact]
    public void TheOrthographicCameraShowsTheSameRegion()
    {
        var camera = new EditorCamera { Is2D = true, Center2D = new Vector2(20, -10), Zoom2D = 4f };
        camera.Apply(View);
        var viewProjection = camera.OrthoCamera.ViewMatrix * camera.OrthoCamera.ProjectionMatrix;
        foreach (var world in new[] { new Vector2(20, -10), new Vector2(60, 30), new Vector2(-50, 5) })
        {
            var clip = Vector4.Transform(new Vector4(world, 0, 1), viewProjection);
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            var expected = new Vector2((ndc.X + 1) * 0.5f * View.X, (1 - ndc.Y) * 0.5f * View.Y);
            Assert.True(Vector2.Distance(expected, camera.WorldToScreen2D(world, View)) < 0.01f, $"{world}");
            Assert.InRange(clip.Z / clip.W, 0f, 1f);
        }
    }

    [Fact]
    public void ZoomKeepsTheAnchorAndPanFollowsTheMouse()
    {
        var camera = new EditorCamera { Is2D = true };
        var anchor = new Vector2(800, 100);
        var before = camera.ScreenToWorld2D(anchor, View);
        camera.Zoom2DAt(3, anchor, View);
        Assert.True(camera.Zoom2D > 1.9f);
        Assert.True(Vector2.Distance(before, camera.ScreenToWorld2D(anchor, View)) < 1e-3f);

        var grabbed = camera.ScreenToWorld2D(new Vector2(500, 300), View);
        camera.Pan2D(40, -25);
        Assert.True(Vector2.Distance(grabbed, camera.ScreenToWorld2D(new Vector2(540, 275), View)) < 1e-3f);

        camera.Zoom2D = 1000;
        Assert.Equal(64f, camera.Zoom2D);
        camera.Frame2D(new Vector2(-100, -50), new Vector2(100, 50), View);
        Assert.Equal(Vector2.Zero, camera.Center2D);
        Assert.Equal(4f, camera.Zoom2D);
        camera.Frame2D(new Vector2(0, 0), new Vector2(2000, 100), View);
        Assert.Equal(0.4f, camera.Zoom2D, 3);
    }

    [Fact]
    public void GridStepsArePowersOfTwoAtLeastSixteenPixelsApart()
    {
        Assert.Equal(16f, ViewportController.GridStep2D(1f));
        Assert.Equal(4f, ViewportController.GridStep2D(4f));
        Assert.Equal(1f, ViewportController.GridStep2D(64f));
        Assert.Equal(128f, ViewportController.GridStep2D(0.125f));
    }
}

public sealed class TransformGizmo2DTests
{
    private static readonly Vector2 View = new(800, 600);
    private static readonly Vector2 Center = View / 2;

    private static (TransformGizmo Shared, TransformGizmo2D Gizmo, EditorCamera Camera) Create(GizmoMode mode)
    {
        var shared = new TransformGizmo { Mode = mode };
        return (shared, new TransformGizmo2D(shared), new EditorCamera { Is2D = true });
    }

    [Fact]
    public void HandlesAreHitInScreenSpace()
    {
        var (_, gizmo, camera) = Create(GizmoMode.Translate);
        Assert.Equal(GizmoHandle.Center, gizmo.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(3, -3)));
        Assert.Equal(GizmoHandle.X, gizmo.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(60, 2)));
        Assert.Equal(GizmoHandle.Y, gizmo.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(-2, 60))); // down (+y)
        Assert.Equal(GizmoHandle.None, gizmo.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(60, 60)));

        var (shared, rotate, _) = Create(GizmoMode.Rotate);
        Assert.Equal(GizmoHandle.Z, rotate.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(0, 80)));
        Assert.Equal(GizmoHandle.None, rotate.HitTest(camera, View, Vector2.Zero, 0, Center));
        shared.Mode = GizmoMode.Select;
        Assert.Equal(GizmoHandle.None, rotate.HitTest(camera, View, Vector2.Zero, 0, Center + new Vector2(0, 80)));
    }

    [Fact]
    public void AxisMovesStayOnTheAxisAndSnapToPixelsOrSteps()
    {
        var (shared, gizmo, camera) = Create(GizmoMode.Translate);
        camera.Zoom2D = 2f; // 2 screen pixels per unit
        var start = new Vector2(10.25f, 5.5f);
        var origin = camera.WorldToScreen2D(start, View);
        gizmo.BeginDrag(GizmoHandle.X, camera, View, origin + new Vector2(40, 0), start, 0, start, Vector2.One);
        var (position, _, _) = gizmo.Drag(camera, View, origin + new Vector2(40 + 31, 17));
        Assert.Equal(new Vector2(26f, 5.5f), position); // +15.5 units along X, rounded to a whole pixel; Y untouched

        gizmo.PixelSnap = false;
        (position, _, _) = gizmo.Drag(camera, View, origin + new Vector2(40 + 31, 17));
        Assert.Equal(new Vector2(25.75f, 5.5f), position);

        shared.Snap = new GizmoSnap(true, Translate: 8f);
        gizmo.BeginDrag(GizmoHandle.Center, camera, View, origin, start, 0, start, Vector2.One);
        (position, _, _) = gizmo.Drag(camera, View, origin + new Vector2(30, -30));
        Assert.Equal(new Vector2(24f, -8f), position); // (25.25, -9.5) snapped to 8
    }

    [Fact]
    public void LocalAxesFollowTheNodesRotation()
    {
        var (shared, gizmo, camera) = Create(GizmoMode.Translate);
        shared.Local = true;
        var rotation = MathF.PI / 2; // X axis points down (rotation is clockwise on screen)
        Assert.Equal(GizmoHandle.X, gizmo.HitTest(camera, View, Vector2.Zero, rotation, Center + new Vector2(1, 60)));
        gizmo.BeginDrag(GizmoHandle.X, camera, View, Center + new Vector2(0, 40), Vector2.Zero, rotation, Vector2.Zero, Vector2.One);
        var (position, _, _) = gizmo.Drag(camera, View, Center + new Vector2(25, 70));
        Assert.Equal(new Vector2(0, 30), position);
    }

    [Fact]
    public void RotationFollowsTheMouseAngleAndSnaps()
    {
        var (shared, gizmo, camera) = Create(GizmoMode.Rotate);
        gizmo.BeginDrag(GizmoHandle.Z, camera, View, Center + new Vector2(80, 0), Vector2.Zero, 0.1f, Vector2.Zero, Vector2.One);
        var (_, rotation, _) = gizmo.Drag(camera, View, Center + new Vector2(0, 80)); // a quarter turn clockwise on screen = +π/2 (y down)
        Assert.Equal(0.1f + MathF.PI / 2, rotation, 4);
        shared.Snap = new GizmoSnap(true);
        (_, rotation, _) = gizmo.Drag(camera, View, Center + new Vector2(0, 80));
        Assert.Equal(float.DegreesToRadians(90), rotation, 4);
    }

    [Fact]
    public void ScaleHandlesScaleTheirAxisOrBoth()
    {
        var (shared, gizmo, camera) = Create(GizmoMode.Scale);
        gizmo.BeginDrag(GizmoHandle.X, camera, View, Center + new Vector2(50, 0), Vector2.Zero, 0, Vector2.Zero, new Vector2(1, 3));
        var (_, _, scale) = gizmo.Drag(camera, View, Center + new Vector2(100, 10));
        Assert.Equal(new Vector2(2, 3), scale);

        gizmo.BeginDrag(GizmoHandle.Center, camera, View, Center + new Vector2(20, 0), Vector2.Zero, 0, Vector2.Zero, new Vector2(1, 2));
        (_, _, scale) = gizmo.Drag(camera, View, Center + new Vector2(0, -30));
        Assert.Equal(new Vector2(1.5f, 3f), scale);

        shared.Snap = new GizmoSnap(true);
        (_, _, scale) = gizmo.Drag(camera, View, Center + new Vector2(0, -36));
        Assert.Equal(1.8f, scale.X, 4);
        Assert.Equal(3.6f, scale.Y, 4);
    }
}

[Collection(nameof(SerialEditor))]
public sealed class Editing2DTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private Vector2 WindowPoint(Vector2 viewPixel)
    {
        var rect = W.Layout.ViewportImage;
        return new Vector2(rect.X, rect.Y) + viewPixel / _editor.Host.PixelScale;
    }

    [Fact]
    public void ANode2DSceneOpensIn2DAndTheGizmoMovesNodesWithPixelSnapAndUndo()
    {
        var scene = W.Session.NewScene("Node2D");
        _editor.Tick(2);
        Assert.True(scene.Camera.Is2D);
        Assert.Same(scene.Camera.OrthoCamera, scene.Viewport.CameraOverride);
        var child = new Node2D { Name = "Player", Position = new Vector2(0, 0) };
        scene.AddNode(child, scene.Root);
        W.Gizmo.Mode = GizmoMode.Translate;
        _editor.Tick();

        var view = W.Viewport.ViewPixels;
        var center = scene.Camera.WorldToScreen2D(Vector2.Zero, view);
        var grab = WindowPoint(center + new Vector2(50, 0)); // on the X arrow
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = grab });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = grab });
        Assert.True(W.Viewport.IsInteracting);
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = WindowPoint(center + new Vector2(50 + 40.6f, 12)) });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = WindowPoint(center + new Vector2(90.6f, 12)) });
        _editor.Tick();

        Assert.Equal(new Vector2(41, 0), child.Position);
        Assert.Equal("Move Player", scene.History.UndoAction!.Name);
        scene.History.Undo();
        Assert.Equal(Vector2.Zero, child.Position);
    }

    [Fact]
    public void ClicksPick2DNodesByShapeOrOrigin()
    {
        var scene = W.Session.NewScene("Node2D");
        var marker = new Node2D { Name = "Marker", Position = new Vector2(-200, 0) };
        var body = new Node2D { Name = "Body", Position = new Vector2(150, 40) };
        var shape = new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = new Vector2(60, 40) } };
        scene.AddNode(marker, scene.Root);
        scene.AddNode(body, scene.Root);
        scene.AddNode(shape, body);
        _editor.Tick();
        var view = W.Viewport.ViewPixels;

        Assert.Same(shape, W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(170, 55), view)));
        Assert.Same(marker, W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(-204, 3), view)));
        Assert.Null(W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(0, 300), view)));

        W.Viewport.Pick(scene, WindowPoint(scene.Camera.WorldToScreen2D(new Vector2(-200, 0), view)));
        Assert.Same(marker, scene.Selection.Primary);
        W.Commands.Execute("view.frame");
        Assert.Equal(new Vector2(-200, 0), scene.Camera.Center2D);
    }

    [Fact]
    public void TheViewMenuSwitchesBetween2DAnd3D()
    {
        var scene = _editor.Scene; // a Node3D scene
        Assert.False(scene.Camera.Is2D);
        Assert.True(W.Commands.Execute("view.2d"));
        _editor.Tick();
        Assert.True(scene.Camera.Is2D);
        Assert.Same(scene.Camera.OrthoCamera, scene.Viewport.CameraOverride);
        Assert.Contains(W.Commands.MenuItems("view"), i => i.Command == "view.2d" && i.Label == "3D View");
        W.Commands.Execute("view.2d");
        _editor.Tick();
        Assert.Same(scene.Camera.RenderCamera, scene.Viewport.CameraOverride);
    }
}
