using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor viewport's behaviour, a <c>[Tool]</c> node: sizes the active scene's <see cref="SubViewport"/> to the view
/// rectangle, renders it with the tab's <see cref="EditorCamera"/> (<see cref="SceneViewport.CameraOverride"/>), publishes
/// the result to the UI as <c>engine://editor-viewport</c>, and turns mouse/keyboard input over the view into camera
/// moves (orbit, pan, zoom, fly), clicks into GPU picking (object-ID pass) and handle drags into gizmo edits committed to
/// the undo history. Each frame it draws the editor-only visuals — grid, selection boxes, light/camera/audio icons and
/// the gizmo — into the view's debug and overlay lines. Steady-state frames allocate nothing.
/// </summary>
[Tool]
public sealed class ViewportController : Node
{
    private static readonly Vector4 SelectionColor = new(1f, 0.62f, 0.15f, 1f);
    private static readonly Vector4 LightColor = new(1f, 0.9f, 0.35f, 0.9f);
    private static readonly Vector4 CameraColor = new(0.78f, 0.6f, 1f, 0.9f);
    private static readonly Vector4 AudioColor = new(0.3f, 0.85f, 0.95f, 0.9f);
    private static readonly Vector4 NodeColor = new(0.75f, 0.78f, 0.85f, 0.7f);
    private const float IconPixels = 14f;
    private const float ClickPixels = 4f;

    private readonly EditorWorkspace _workspace;
    private readonly List<Node3D> _iconNodes = [];
    private readonly bool[] _flyKeys = new bool[6]; // A D Q E S W  →  -x +x -y +y -z +z
    private EditedScene? _scene;
    private int _iconsVersion = -1;
    private SubViewport? _registered;
    private Grid3D? _grid;

    // Mouse state (window points).
    private Vector2 _mouse;
    private Vector2 _pressAt;
    private DragKind _drag;
    private bool _fast;
    private PickHandle _pick;
    private EditedScene? _pickScene;
    private bool _pickAdditive;

    // Gizmo drag: the node and its exported transform values before the drag.
    private Node3D? _gizmoNode;
    private Vector3 _startPosition;
    private Vector3 _startRotationDegrees;
    private Vector3 _startScale;

    private enum DragKind
    {
        None,
        Click,     // LMB pressed, may become a pick
        Gizmo,
        Orbit,
        Pan,
        Fly,
    }

    public ViewportController(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Whether the reference grid is drawn.</summary>
    public bool GridVisible
    {
        get;
        set
        {
            field = value;
            if (_grid is not null)
                _grid.Visible = value;
        }
    } = true;

    /// <summary>The view rectangle in window points (dp).</summary>
    public LayoutRect ViewRect => _workspace.Layout.ViewportImage;

    /// <summary>View size in framebuffer pixels (the sub-viewport's target size).</summary>
    public Vector2 ViewPixels
    {
        get
        {
            var rect = ViewRect;
            var scale = _workspace.Host.PixelScale;
            return new Vector2(MathF.Max(1, MathF.Round(rect.Width * scale)), MathF.Max(1, MathF.Round(rect.Height * scale)));
        }
    }

    /// <summary>True while the mouse flies the camera (RMB held).</summary>
    public bool IsFlying => _drag == DragKind.Fly;

    /// <summary>True while the mouse drives the view (gizmo drag, orbit, pan, fly or a pending click).</summary>
    public bool IsInteracting => _drag != DragKind.None;

    /// <summary>True while a pick request is waiting for the GPU.</summary>
    public bool IsPicking => _pick.IsValid;

    /// <summary>Called when the active tab changed.</summary>
    public void OnActiveSceneChanged()
    {
        CancelDrag();
        _scene = _workspace.Session.Active;
        _iconsVersion = -1;
        // The previous tab's target may already be disposed (tab closed): stop the UI from sampling it.
        if (_registered is not null)
            Tree?.Servers.Get<UiServer>()?.UnregisterTexture(ViewportPanel.TextureName);
        _registered = null;
        _workspace.ViewportPanel?.UpdateImage(false);
        if (_scene is null)
            return;

        if (_grid is { IsFreed: false })
            _grid.Free();
        _grid = new Grid3D { Name = "EditorGrid", Visible = GridVisible };
        _scene.Viewport.AddChild(_grid);
        _scene.Viewport.CameraOverride = _scene.Camera.RenderCamera;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        var scene = _workspace.Session.Active;
        if (!ReferenceEquals(scene, _scene))
            OnActiveSceneChanged();
        if (scene is null)
            return;

        var viewport = scene.Viewport;
        var pixels = ViewPixels;
        if (viewport.Width != (int)pixels.X || viewport.Height != (int)pixels.Y)
        {
            viewport.Width = (int)pixels.X;
            viewport.Height = (int)pixels.Y;
        }

        PublishTarget(viewport);

        if (_drag == DragKind.Fly)
        {
            var move = new Vector3(Axis(0, 1), Axis(2, 3), Axis(4, 5));
            if (move != Vector3.Zero)
                scene.Camera.Fly(move, gameTime.DeltaTime, _fast || (_workspace.Modifiers & EditorModifiers.Shift) != 0);
        }

        scene.Camera.Apply();
        _workspace.Gizmo.PixelScale = _workspace.Host.PixelScale;
        _workspace.ViewportPanel.SetInfo(_drag == DragKind.Fly ? FlyInfo : PerspectiveInfo);
        PollPick(scene);
        DrawEditorVisuals(scene, pixels);
    }

    private const string PerspectiveInfo = "Perspective";
    private const string FlyInfo = "Perspective · fly (WASD QE, wheel: speed)";

    private float Axis(int negative, int positive) => (_flyKeys[positive] ? 1f : 0f) - (_flyKeys[negative] ? 1f : 0f);

    // The colour target exists after the view first rendered; publish it once per tab (it follows resizes).
    private void PublishTarget(SubViewport viewport)
    {
        if (ReferenceEquals(_registered, viewport) || viewport.ColorTarget is not { } target || Tree?.Servers.Get<UiServer>() is not { } ui)
            return;
        ui.RegisterTexture(ViewportPanel.TextureName, target);
        _registered = viewport;
        _workspace.ViewportPanel.UpdateImage(true);
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_scene is not { } scene || _workspace.IsDialogOpen)
            return;
        switch (inputEvent)
        {
            case InputEventMouseButton button:
                _mouse = button.Position;
                if (button.Pressed ? Press(scene, button) : Release(scene, button))
                    Handled();
                break;
            case InputEventMouseMotion motion:
                var delta = motion.Position - _mouse;
                _mouse = motion.Position;
                if (Move(scene, delta))
                    Handled();
                break;
            case InputEventMouseWheel wheel when ViewRect.Contains(_mouse.X, _mouse.Y) || _drag == DragKind.Fly:
                if (_drag == DragKind.Fly)
                    scene.Camera.FlySpeed *= MathF.Pow(1.2f, wheel.Delta.Y);
                else
                    scene.Camera.Zoom(wheel.Delta.Y);
                Handled();
                break;
            case InputEventKey key when _drag == DragKind.Fly:
                if (FlyKey(key))
                    Handled();
                break;
        }
    }

    private void Handled() => GetViewport()?.SetInputAsHandled();

    private bool Press(EditedScene scene, InputEventMouseButton e)
    {
        if (_drag != DragKind.None || !ViewRect.Contains(e.Position.X, e.Position.Y))
            return false;
        _pressAt = e.Position;
        var alt = (_workspace.Modifiers & EditorModifiers.Alt) != 0;
        var shift = (_workspace.Modifiers & EditorModifiers.Shift) != 0;
        switch (e.Button)
        {
            case MouseButton.Left when alt:
                _drag = DragKind.Orbit;
                return true;
            case MouseButton.Left:
                if (TryBeginGizmo(scene))
                    return true;
                _drag = DragKind.Click;
                return true;
            case MouseButton.Middle:
                _drag = shift ? DragKind.Pan : DragKind.Orbit;
                return true;
            case MouseButton.Right:
                _drag = DragKind.Fly;
                Array.Clear(_flyKeys);
                _fast = false;
                return true;
            default:
                return false;
        }
    }

    private bool Release(EditedScene scene, InputEventMouseButton e)
    {
        var drag = _drag;
        if (drag == DragKind.None)
            return false;
        var matches = drag switch
        {
            DragKind.Click or DragKind.Gizmo => e.Button == MouseButton.Left,
            DragKind.Orbit => e.Button is MouseButton.Left or MouseButton.Middle,
            DragKind.Pan => e.Button == MouseButton.Middle,
            DragKind.Fly => e.Button == MouseButton.Right,
            _ => false,
        };
        if (!matches)
            return true;
        _drag = DragKind.None;
        if (drag == DragKind.Gizmo)
            EndGizmo(scene, commit: true);
        else if (drag == DragKind.Click && Vector2.Distance(e.Position, _pressAt) <= ClickPixels)
            Pick(scene, e.Position, (_workspace.Modifiers & (EditorModifiers.Command | EditorModifiers.Shift)) != 0);
        else if (drag == DragKind.Fly)
            Array.Clear(_flyKeys);
        return true;
    }

    private bool Move(EditedScene scene, Vector2 delta)
    {
        var scale = _workspace.Host.PixelScale;
        switch (_drag)
        {
            case DragKind.Orbit:
                scene.Camera.Orbit(delta.X, delta.Y);
                return true;
            case DragKind.Pan:
                scene.Camera.Pan(delta.X * scale, delta.Y * scale, ViewPixels.Y);
                return true;
            case DragKind.Fly:
                scene.Camera.Look(delta.X, delta.Y);
                return true;
            case DragKind.Gizmo:
                UpdateGizmo(scene);
                return true;
            case DragKind.Click:
                return true;
            default:
                // Hover highlight of the gizmo handles.
                if (ViewRect.Contains(_mouse.X, _mouse.Y) && GizmoTarget(scene) is { } node)
                    _workspace.Gizmo.Hovered = _workspace.Gizmo.HitTest(scene.Camera, ViewPixels, node.GlobalPosition, node.GlobalRotation, LocalPixel(_mouse));
                else
                    _workspace.Gizmo.Hovered = GizmoHandle.None;
                return false;
        }
    }

    private bool FlyKey(InputEventKey key)
    {
        var index = key.Key switch
        {
            Key.A => 0,
            Key.D => 1,
            Key.Q => 2,
            Key.E => 3,
            Key.S => 4,
            Key.W => 5,
            _ => -1,
        };
        if (key.Key is Key.ShiftLeft or Key.ShiftRight)
        {
            // Noted but never consumed: the workspace tracks modifiers from the same events.
            _fast = key.Pressed;
            return false;
        }

        if (index < 0)
            return false;
        _flyKeys[index] = key.Pressed;
        return true;
    }

    private void CancelDrag()
    {
        if (_drag == DragKind.Gizmo && _scene is { } scene)
            EndGizmo(scene, commit: false);
        _drag = DragKind.None;
        _fast = false;
        Array.Clear(_flyKeys);
    }

    /// <summary>Window point → view pixel (origin top-left of the view).</summary>
    public Vector2 LocalPixel(Vector2 windowPoint)
    {
        var rect = ViewRect;
        return (windowPoint - new Vector2(rect.X, rect.Y)) * _workspace.Host.PixelScale;
    }

    // ── Picking ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects what is under window point <paramref name="windowPoint"/>: an icon (lights, cameras, audio — nodes without
    /// geometry) when one is within reach, else the GPU object-ID pick (meshes), completed a couple of frames later.
    /// </summary>
    public void Pick(EditedScene scene, Vector2 windowPoint, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var pixel = LocalPixel(windowPoint);
        if (PickIcon(scene, pixel) is { } icon)
        {
            Select(scene, icon, additive);
            return;
        }

        _pick = scene.Viewport.RequestPick((int)pixel.X, (int)pixel.Y);
        _pickScene = scene;
        _pickAdditive = additive;
        if (!_pick.IsValid && !additive)
            scene.Selection.Clear(); // no GPU (headless): a click on nothing deselects
    }

    /// <summary>Picks view pixel <paramref name="pixel"/> directly (smoke tests use a known pixel).</summary>
    public void PickPixel(EditedScene scene, Vector2 pixel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _pick = scene.Viewport.RequestPick((int)pixel.X, (int)pixel.Y);
        _pickScene = scene;
        _pickAdditive = false;
    }

    private void PollPick(EditedScene scene)
    {
        if (!_pick.IsValid)
            return;
        if (!ReferenceEquals(_pickScene, scene))
        {
            _pick = default;
            return;
        }

        if (!scene.Viewport.TryGetPickResult(_pick, out var result))
            return;
        _pick = default;
        var node = result.Hit ? scene.SelectableFor(result.Node) : null;
        if (node is not null)
            Select(scene, node, _pickAdditive);
        else if (!_pickAdditive)
            scene.Selection.Clear();
    }

    private static void Select(EditedScene scene, Node node, bool additive)
    {
        if (additive)
            scene.Selection.Toggle(node);
        else
            scene.Selection.Set(node);
    }

    private Node? PickIcon(EditedScene scene, Vector2 pixel)
    {
        RefreshIcons(scene);
        var size = ViewPixels;
        Node? best = null;
        var bestDistance = IconPixels * _workspace.Host.PixelScale;
        foreach (var node in _iconNodes)
        {
            if (!scene.Camera.Project(node.GlobalPosition, size.X, size.Y, out var at))
                continue;
            var distance = Vector2.Distance(at, pixel);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = scene.SelectableFor(node);
            }
        }

        return best;
    }

    // ── Gizmo ────────────────────────────────────────────────────────────────────────────────────────────────────

    private Node3D? GizmoTarget(EditedScene scene) =>
        _workspace.Gizmo.Mode != GizmoMode.Select && scene.Selection.Primary is Node3D node && scene.IsEditable(node) && node.IsInsideTree
            ? node
            : null;

    private bool TryBeginGizmo(EditedScene scene)
    {
        if (GizmoTarget(scene) is not { } node)
            return false;
        var gizmo = _workspace.Gizmo;
        var local = LocalPixel(_mouse);
        var handle = gizmo.HitTest(scene.Camera, ViewPixels, node.GlobalPosition, node.GlobalRotation, local);
        if (handle == GizmoHandle.None)
            return false;
        _gizmoNode = node;
        _startPosition = node.Position;
        _startRotationDegrees = node.RotationDegrees;
        _startScale = node.Scale;
        gizmo.BeginDrag(handle, scene.Camera, ViewPixels, local, node.GlobalPosition, node.GlobalRotation, node.Scale);
        _drag = DragKind.Gizmo;
        return true;
    }

    private void UpdateGizmo(EditedScene scene)
    {
        if (_gizmoNode is not { IsFreed: false } node)
            return;
        var (position, rotation, scale) = _workspace.Gizmo.Drag(scene.Camera, ViewPixels, LocalPixel(_mouse));
        switch (_workspace.Gizmo.Mode)
        {
            case GizmoMode.Translate:
                node.GlobalPosition = position;
                break;
            case GizmoMode.Rotate:
                var parentRotation = node.Parent is Node3D parent ? parent.GlobalRotation : Quaternion.Identity;
                node.Rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * rotation);
                break;
            case GizmoMode.Scale:
                node.Scale = scale;
                break;
        }

        _workspace.Inspector.RefreshValues(); // the fields follow the drag (committed on release)
    }

    // Commits the drag as one history entry (the node already shows the result), or restores the start on cancel.
    private void EndGizmo(EditedScene scene, bool commit)
    {
        _workspace.Gizmo.EndDrag();
        if (_gizmoNode is not { IsFreed: false } node)
        {
            _gizmoNode = null;
            return;
        }

        _gizmoNode = null;
        var info = Serialization.TypeRegistry.GetRequired(node.GetType());
        var actions = new List<IEditorAction>(3);
        AddChange(actions, node, info, "Position", _startPosition, node.Position);
        AddChange(actions, node, info, "RotationDegrees", _startRotationDegrees, node.RotationDegrees);
        AddChange(actions, node, info, "Scale", _startScale, node.Scale);
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

    private static void AddChange(List<IEditorAction> actions, Node3D node, Serialization.NodeTypeInfo info, string property, Vector3 before, Vector3 after)
    {
        if (before == after || info.FindProperty(property) is not { } p)
            return;
        actions.Add(new SetPropertyAction(node, p, before, after));
    }

    // ── Frame selected ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Points the camera at the selection (F): mesh bounds when known, else the node positions.</summary>
    public void FrameSelection()
    {
        if (_workspace.Session.Active is not { } scene)
            return;
        var bounds = Aabb.Empty;
        foreach (var node in scene.Selection.Nodes)
            if (node is Node3D n3)
                bounds = bounds.Merge(WorldBounds(n3));
        if (bounds.IsEmpty)
        {
            scene.Camera.Frame(Vector3.Zero, 4f);
            return;
        }

        scene.Camera.Frame(bounds.Center, MathF.Max(0.5f, bounds.Extents.Length()));
    }

    /// <summary>Points the camera at <paramref name="node"/> and selects it.</summary>
    public void FrameSelectionOf(Node3D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_workspace.Session.Active is not { } scene)
            return;
        var bounds = WorldBounds(node);
        scene.Camera.Frame(bounds.Center, MathF.Max(0.5f, bounds.Extents.Length()));
    }

    private static Aabb WorldBounds(Node3D node)
    {
        if (node is MeshInstance3D { Mesh: { } mesh } && !mesh.Bounds.IsEmpty)
            return mesh.Bounds.Transform(node.ModelMatrix);
        var p = node.GlobalPosition;
        return new Aabb(p - Vector3.One * 0.5f, p + Vector3.One * 0.5f);
    }

    // ── Editor-only visuals ──────────────────────────────────────────────────────────────────────────────────────

    // Icon nodes are collected when the scene changes, not every frame.
    private void RefreshIcons(EditedScene scene)
    {
        if (_iconsVersion == scene.Version)
            return;
        _iconsVersion = scene.Version;
        _iconNodes.Clear();
        Collect(scene, scene.Root);
    }

    private void Collect(EditedScene scene, Node node)
    {
        if (node is Light3D or Camera3D or AudioPlayer3D or AudioListener3D)
            _iconNodes.Add((Node3D)node);
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
            Collect(scene, children[i]);
    }

    private void DrawEditorVisuals(EditedScene scene, Vector2 pixels)
    {
        RefreshIcons(scene);
        var lines = scene.Viewport.DebugLines;
        var overlay = scene.Viewport.OverlayLines;
        var camera = scene.Camera;

        for (var i = 0; i < _iconNodes.Count; i++)
        {
            var node = _iconNodes[i];
            if (!node.IsInsideTree)
                continue;
            DrawIcon(lines, camera, pixels.Y, node, scene.Selection.Contains(node));
        }

        var selected = scene.Selection.Nodes;
        for (var i = 0; i < selected.Count; i++)
            if (selected[i] is Node3D { IsInsideTree: true } node)
                DrawSelection(overlay, camera, pixels.Y, node);

        if (GizmoTarget(scene) is { } target)
            _workspace.Gizmo.Draw(overlay, camera, pixels, target.GlobalPosition, target.GlobalRotation);
    }

    private static void DrawSelection(DebugLines lines, EditorCamera camera, float viewHeight, Node3D node)
    {
        if (node is MeshInstance3D { Mesh: { } mesh } && !mesh.Bounds.IsEmpty)
        {
            var bounds = mesh.Bounds;
            var global = node.GlobalTransform;
            var box = new Transform3D(global.Basis, global.TransformPoint(bounds.Center));
            lines.AddBox(box, bounds.Extents * 1.02f + new Vector3(0.002f), SelectionColor);
            return;
        }

        var p = node.GlobalPosition;
        var r = camera.WorldPerPixel(p, viewHeight) * IconPixels * 0.9f;
        lines.AddLine(p - Vector3.UnitX * r, p + Vector3.UnitX * r, SelectionColor);
        lines.AddLine(p - Vector3.UnitY * r, p + Vector3.UnitY * r, SelectionColor);
        lines.AddLine(p - Vector3.UnitZ * r, p + Vector3.UnitZ * r, SelectionColor);
    }

    private static void DrawIcon(DebugLines lines, EditorCamera camera, float viewHeight, Node3D node, bool selected)
    {
        var p = node.GlobalPosition;
        var unit = camera.WorldPerPixel(p, viewHeight);
        var r = unit * IconPixels * 0.5f;
        var right = camera.Right;
        var up = camera.Up;
        var forward = node.GlobalForward;
        switch (node)
        {
            case DirectionalLight3D:
                Circle(lines, p, right, up, r, LightColor);
                lines.AddLine(p, p + forward * unit * 60f, LightColor);
                break;
            case OmniLight3D omni:
                Circle(lines, p, right, up, r, LightColor);
                for (var i = 0; i < 8; i++)
                {
                    var a = i * MathF.Tau / 8f;
                    var d = right * MathF.Cos(a) + up * MathF.Sin(a);
                    lines.AddLine(p + d * r * 1.3f, p + d * r * 1.9f, LightColor);
                }

                if (selected)
                    lines.AddSphere(new Transform3D(Basis.Identity, p), omni.Range, LightColor with { W = 0.35f });
                break;
            case SpotLight3D spot:
                Circle(lines, p, right, up, r, LightColor);
                var length = selected ? spot.Range : unit * 60f;
                var radius = MathF.Tan(float.DegreesToRadians(Math.Clamp(spot.OuterConeAngle, 1f, 89f))) * length;
                var (u, v) = TransformGizmo.Perpendiculars(forward);
                var end = p + forward * length;
                for (var i = 0; i < 4; i++)
                {
                    var a = i * MathF.Tau / 4f;
                    lines.AddLine(p, end + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius, LightColor);
                }

                Circle(lines, end, u, v, radius, LightColor);
                break;
            case Camera3D cam:
                DrawCamera(lines, cam, unit, selected);
                break;
            case AudioPlayer3D audio:
                Diamond(lines, p, right, up, r, AudioColor);
                if (selected && audio.MaxDistance > 0)
                    lines.AddSphere(new Transform3D(Basis.Identity, p), audio.MaxDistance, AudioColor with { W = 0.35f });
                break;
            default:
                Diamond(lines, p, right, up, r, NodeColor);
                break;
        }
    }

    private static void DrawCamera(DebugLines lines, Camera3D camera, float unit, bool selected)
    {
        var global = camera.GlobalTransform;
        var p = global.Origin;
        var forward = -Vector3.Normalize(global.Basis.Z);
        var right = Vector3.Normalize(global.Basis.X);
        var up = Vector3.Normalize(global.Basis.Y);
        var depth = selected ? MathF.Min(camera.Far, 6f) : unit * 40f;
        var halfHeight = MathF.Tan(float.DegreesToRadians(camera.Fov) * 0.5f) * depth;
        var halfWidth = halfHeight * 16f / 9f;
        var center = p + forward * depth;
        Span<Vector3> corners =
        [
            center + right * halfWidth + up * halfHeight,
            center - right * halfWidth + up * halfHeight,
            center - right * halfWidth - up * halfHeight,
            center + right * halfWidth - up * halfHeight,
        ];
        for (var i = 0; i < 4; i++)
        {
            lines.AddLine(p, corners[i], CameraColor);
            lines.AddLine(corners[i], corners[(i + 1) % 4], CameraColor);
        }

        // "Up" marker triangle.
        var top = center + up * halfHeight * 1.35f;
        lines.AddLine(corners[0], top, CameraColor);
        lines.AddLine(top, corners[1], CameraColor);
    }

    private static void Circle(DebugLines lines, Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color)
    {
        const int segments = 16;
        var previous = center + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var a = i * MathF.Tau / segments;
            var point = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            lines.AddLine(previous, point, color);
            previous = point;
        }
    }

    private static void Diamond(DebugLines lines, Vector3 p, Vector3 right, Vector3 up, float r, Vector4 color)
    {
        lines.AddLine(p + up * r, p + right * r, color);
        lines.AddLine(p + right * r, p - up * r, color);
        lines.AddLine(p - up * r, p - right * r, color);
        lines.AddLine(p - right * r, p + up * r, color);
    }

    protected override void OnExitTree()
    {
        CancelDrag();
        base.OnExitTree();
    }
}
