using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// The viewport's 2D editing mode (scenes whose root is a <see cref="Node2D"/>, or View › 2D): an orthographic view of
/// the z = 0 plane in pixels (y down, as in Godot), panned with the middle or right button (or Alt+left), zoomed around the mouse with
/// the wheel; a pixel grid; Node2D markers, collision shape outlines (physics debug draw), Camera2D frames; CPU picking
/// (shapes, then node origins); and the <see cref="TransformGizmo2D"/> with pixel snapping.
/// </summary>
public sealed partial class ViewportController
{
    private static readonly Vector4 GridMinor = new(0.55f, 0.58f, 0.65f, 0.12f);
    private static readonly Vector4 GridMajor = new(0.55f, 0.58f, 0.65f, 0.28f);
    private static readonly Vector4 AxisX2D = new(0.96f, 0.32f, 0.32f, 0.55f);
    private static readonly Vector4 AxisY2D = new(0.35f, 0.85f, 0.35f, 0.55f);
    private const float MarkerPoints = 6f;
    private const float PickPoints = 10f;

    private readonly List<Node2D> _nodes2D = [];
    private int _nodes2DVersion = -1;
    private Node2D? _gizmoNode2D;
    private Vector2 _startPosition2D;
    private float _startRotation2D;
    private Vector2 _startScale2D;

    /// <summary>The 2D transform gizmo (shares mode, space and snapping with <see cref="EditorWorkspace.Gizmo"/>).</summary>
    public TransformGizmo2D Gizmo2D { get; }

    private const string Info2D = "2D · pixels";

    /// <summary>Switches the active tab between the 2D and 3D views (View › 2D View; Node2D roots open in 2D).</summary>
    public void Toggle2D()
    {
        if (_workspace.Session.Active is not { } scene)
            return;
        CancelDrag();
        scene.Camera.Is2D = !scene.Camera.Is2D;
        if (_grid is { IsFreed: false } grid)
            grid.Visible = GridVisible && !scene.Camera.Is2D;
        scene.Viewport.CameraOverride = scene.Camera.ActiveCamera;
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool Press2D(EditedScene scene, InputEventMouseButton e)
    {
        var alt = (_workspace.Modifiers & EditorModifiers.Alt) != 0;
        switch (e.Button)
        {
            case MouseButton.Left when alt:
                _drag = DragKind.Pan;
                return true;
            case MouseButton.Left:
                if (TryBeginGizmo2D(scene))
                    return true;
                _drag = DragKind.Click;
                return true;
            case MouseButton.Middle or MouseButton.Right:
                _drag = DragKind.Pan;
                return true;
            default:
                return false;
        }
    }

    private static bool Release2DMatches(DragKind drag, MouseButton button) => drag switch
    {
        DragKind.Click or DragKind.Gizmo => button == MouseButton.Left,
        DragKind.Pan => button is MouseButton.Left or MouseButton.Middle or MouseButton.Right,
        _ => false,
    };

    private Node2D? GizmoTarget2D(EditedScene scene) =>
        _workspace.Gizmo.Mode != GizmoMode.Select && scene.Selection.Primary is Node2D node && scene.IsEditable(node) && node.IsInsideTree
            ? node
            : null;

    private bool TryBeginGizmo2D(EditedScene scene)
    {
        if (GizmoTarget2D(scene) is not { } node)
            return false;
        var local = LocalPixel(_mouse);
        var global = node.GlobalTransform;
        var handle = Gizmo2D.HitTest(scene.Camera, ViewPixels, global.Origin, global.Rotation, local);
        if (handle == GizmoHandle.None)
            return false;
        _gizmoNode2D = node;
        _startPosition2D = node.Position;
        _startRotation2D = node.RotationDegrees;
        _startScale2D = node.Scale;
        Gizmo2D.BeginDrag(handle, scene.Camera, ViewPixels, local, global.Origin, global.Rotation, global.Origin, node.Scale);
        _drag = DragKind.Gizmo;
        return true;
    }

    private void UpdateGizmo2D(EditedScene scene)
    {
        if (_gizmoNode2D is not { IsFreed: false } node)
            return;
        var (position, rotation, scale) = Gizmo2D.Drag(scene.Camera, ViewPixels, LocalPixel(_mouse));
        switch (_workspace.Gizmo.Mode)
        {
            case GizmoMode.Translate:
                node.GlobalPosition = position;
                if (Gizmo2D.PixelSnap && !_workspace.Gizmo.Snap.Enabled && node.ParentNode2D is not null)
                    node.Position = new Vector2(MathF.Round(node.Position.X), MathF.Round(node.Position.Y));
                break;
            case GizmoMode.Rotate:
                var parentRotation = node.ParentNode2D?.GlobalRotation ?? 0f;
                node.Rotation = rotation - parentRotation;
                break;
            case GizmoMode.Scale:
                node.Scale = scale;
                break;
        }

        _workspace.Inspector.RefreshValues();
    }

    private void EndGizmo2D(EditedScene scene, bool commit)
    {
        Gizmo2D.EndDrag();
        if (_gizmoNode2D is not { IsFreed: false } node)
        {
            _gizmoNode2D = null;
            return;
        }

        _gizmoNode2D = null;
        var info = Serialization.TypeRegistry.GetRequired(node.GetType());
        var actions = new List<IEditorAction>(3);
        if (node.Position != _startPosition2D && info.FindProperty("Position") is { } position)
            actions.Add(new SetPropertyAction(node, position, _startPosition2D, node.Position));
        if (node.RotationDegrees != _startRotation2D && info.FindProperty("RotationDegrees") is { } rotation)
            actions.Add(new SetPropertyAction(node, rotation, _startRotation2D, node.RotationDegrees));
        if (node.Scale != _startScale2D && info.FindProperty("Scale") is { } scale)
            actions.Add(new SetPropertyAction(node, scale, _startScale2D, node.Scale));
        if (actions.Count == 0)
            return;
        if (!commit)
        {
            foreach (var action in actions)
                action.Undo();
            return;
        }

        var name = _workspace.Gizmo.Mode switch { GizmoMode.Rotate => "Rotate", GizmoMode.Scale => "Scale", _ => "Move" } + " " + node.Name;
        scene.History.Commit(new CompositeAction(name, [.. actions]), alreadyApplied: true);
    }

    // ── Picking (CPU: 2D nodes have no object-ID geometry) ──────────────────────────────────────────────────────

    /// <summary>
    /// The topmost 2D node under view pixel <paramref name="pixel"/>: a collision shape containing it (rectangles and
    /// circles), else the nearest node origin within reach. Later nodes in tree order are on top.
    /// </summary>
    public Node? Pick2D(EditedScene scene, Vector2 pixel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        RefreshNodes2D(scene);
        var world = scene.Camera.ScreenToWorld2D(pixel, ViewPixels);
        for (var i = _nodes2D.Count - 1; i >= 0; i--)
            if (_nodes2D[i] is CollisionShape2D { Shape: { } shape, IsInsideTree: true } collision && Contains(collision, shape, world))
                return scene.SelectableFor(collision);

        Node? best = null;
        var bestDistance = PickPoints * ViewScale;
        for (var i = _nodes2D.Count - 1; i >= 0; i--)
        {
            var node = _nodes2D[i];
            if (!node.IsInsideTree)
                continue;
            var at = scene.Camera.WorldToScreen2D(node.GlobalPosition, ViewPixels);
            var distance = Vector2.Distance(at, pixel);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = node;
            }
        }

        return best is null ? null : scene.SelectableFor(best);
    }

    private static bool Contains(CollisionShape2D node, Shape2D shape, Vector2 world)
    {
        var local = node.GlobalTransform.AffineInverse().TransformPoint(world);
        return shape switch
        {
            RectangleShape2D rect => MathF.Abs(local.X) <= rect.Size.X * 0.5f && MathF.Abs(local.Y) <= rect.Size.Y * 0.5f,
            CircleShape2D circle => local.Length() <= circle.Radius,
            _ => false,
        };
    }

    private void RefreshNodes2D(EditedScene scene)
    {
        if (_nodes2DVersion == scene.Version)
            return;
        _nodes2DVersion = scene.Version;
        _nodes2D.Clear();
        Collect2D(scene.Root);
    }

    private void Collect2D(Node node)
    {
        if (node is Node2D n2)
            _nodes2D.Add(n2);
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
            Collect2D(children[i]);
    }

    // ── Framing ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void FrameSelection2D(EditedScene scene)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var node in scene.Selection.Nodes)
        {
            if (node is not Node2D n2)
                continue;
            var p = n2.GlobalPosition;
            var half = n2 is CollisionShape2D { Shape: RectangleShape2D rect } ? rect.Size * 0.5f
                : n2 is CollisionShape2D { Shape: CircleShape2D circle } ? new Vector2(circle.Radius)
                : new Vector2(32f);
            min = Vector2.Min(min, p - half);
            max = Vector2.Max(max, p + half);
        }

        if (min.X > max.X)
        {
            scene.Camera.Reset2D();
            return;
        }

        scene.Camera.Frame2D(min, max, ViewPixels);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawEditorVisuals2D(EditedScene scene, Vector2 pixels)
    {
        RefreshNodes2D(scene);
        var lines = scene.Viewport.DebugLines;
        var overlay = scene.Viewport.OverlayLines;
        var camera = scene.Camera;
        if (GridVisible)
            DrawGrid2D(lines, camera, pixels);

        var unit = 1f / camera.Zoom2D;
        var marker = MarkerPoints * ViewScale * unit;
        for (var i = 0; i < _nodes2D.Count; i++)
        {
            var node = _nodes2D[i];
            if (!node.IsInsideTree)
                continue;
            var selected = scene.Selection.Contains(node);
            var p = new Vector3(node.GlobalPosition, 0f);
            if (node is Camera2D cam)
                DrawCamera2D(overlay, cam, selected ? SelectionColor : CameraColor);
            else if (node is not CollisionShape2D)
            {
                var color = selected ? SelectionColor : NodeColor;
                overlay.AddLine(p - new Vector3(marker, 0, 0), p + new Vector3(marker, 0, 0), color);
                overlay.AddLine(p - new Vector3(0, marker, 0), p + new Vector3(0, marker, 0), color);
            }

            if (selected && node is CollisionShape2D { Shape: { } shape } shapeNode)
                DrawShapeSelection(overlay, shapeNode, shape, unit);
        }

        if (GizmoTarget2D(scene) is { } target)
        {
            var global = target.GlobalTransform;
            Gizmo2D.Draw(overlay, camera, pixels, global.Origin, global.Rotation);
        }
    }

    private static void DrawShapeSelection(DebugLines lines, CollisionShape2D node, Shape2D shape, float unit)
    {
        var transform = node.GlobalTransform;
        var pad = 2f * unit;
        switch (shape)
        {
            case RectangleShape2D rect:
                var h = rect.Size * 0.5f + new Vector2(pad);
                Span<Vector2> corners = [new(-h.X, -h.Y), new(h.X, -h.Y), new(h.X, h.Y), new(-h.X, h.Y)];
                lines.AddPolygon2D(transform, corners, SelectionColor);
                break;
            case CircleShape2D circle:
                lines.AddCircle2D(transform, Vector2.Zero, circle.Radius + pad, SelectionColor);
                break;
            default:
                var p = new Vector3(transform.Origin, 0f);
                lines.AddLine(p - new Vector3(8f * unit, 0, 0), p + new Vector3(8f * unit, 0, 0), SelectionColor);
                lines.AddLine(p - new Vector3(0, 8f * unit, 0), p + new Vector3(0, 8f * unit, 0), SelectionColor);
                break;
        }
    }

    // The game's view of a Camera2D: the project's window size divided by its zoom (Godot's zoom), centred on it.
    private void DrawCamera2D(DebugLines lines, Camera2D camera, Vector4 color)
    {
        var window = _workspace.Session.Project?.Window;
        var size = new Vector2(window?.Width ?? WindowSettings.DefaultWidth, window?.Height ?? WindowSettings.DefaultHeight) / camera.Zoom;
        var c = camera.GlobalPosition;
        var h = size * 0.5f;
        var a = new Vector3(c.X - h.X, c.Y - h.Y, 0);
        var b = new Vector3(c.X + h.X, c.Y - h.Y, 0);
        var d = new Vector3(c.X + h.X, c.Y + h.Y, 0);
        var e = new Vector3(c.X - h.X, c.Y + h.Y, 0);
        lines.AddLine(a, b, color);
        lines.AddLine(b, d, color);
        lines.AddLine(d, e, color);
        lines.AddLine(e, a, color);
    }

    /// <summary>
    /// The 2D grid: power-of-two pixel steps chosen so lines are at least 16 screen pixels apart, every 8th line brighter,
    /// the X and Y axes coloured. Drawn slightly behind the z = 0 plane.
    /// </summary>
    internal static float GridStep2D(float zoom)
    {
        var step = 1f;
        while (step * zoom < 16f && step < 1 << 20)
            step *= 2f;
        return step;
    }

    private static void DrawGrid2D(DebugLines lines, EditorCamera camera, Vector2 pixels)
    {
        var step = GridStep2D(camera.Zoom2D);
        var min = camera.ScreenToWorld2D(Vector2.Zero, pixels); // top-left (y down)
        var max = camera.ScreenToWorld2D(pixels, pixels);
        const float z = 1f; // behind the z = 0 plane (the 2D view looks along +Z)
        var x0 = MathF.Floor(min.X / step);
        var x1 = MathF.Ceiling(max.X / step);
        for (var i = x0; i <= x1; i++)
        {
            var x = i * step;
            var color = x == 0 ? AxisY2D : (long)i % 8 == 0 ? GridMajor : GridMinor;
            lines.AddLine(new Vector3(x, min.Y, z), new Vector3(x, max.Y, z), color);
        }

        var y0 = MathF.Floor(min.Y / step);
        var y1 = MathF.Ceiling(max.Y / step);
        for (var i = y0; i <= y1; i++)
        {
            var y = i * step;
            var color = y == 0 ? AxisX2D : (long)i % 8 == 0 ? GridMajor : GridMinor;
            lines.AddLine(new Vector3(min.X, y, z), new Vector3(max.X, y, z), color);
        }
    }
}
