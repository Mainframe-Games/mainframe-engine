using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>The curve bar's tool: what a left click on the view does.</summary>
public enum CurveTool : byte
{
    /// <summary>Drag points and handles; a click elsewhere selects nodes as usual.</summary>
    Select,

    /// <summary>A click adds a point at the end of the curve.</summary>
    Add,

    /// <summary>A click on the curve inserts a point there without changing the shape.</summary>
    Insert,

    /// <summary>A click on a point removes it.</summary>
    Delete,
}

/// <summary>
/// Curve editing in a 2D tab (G13, docs/design/paths.md#editor): while a <see cref="Path2D"/> is the selection, the curve
/// bar shows over the view and the view's buttons edit its curve. Points and their tangent handles drag (handles mirror
/// unless Shift is held), Cmd/Ctrl+click on empty space adds a point, Cmd/Ctrl+drag from a point pulls new handles, the
/// Insert tool splits a segment where it is clicked (de Casteljau: the shape is unchanged), a right click on a point or
/// the Delete tool removes it, and moves snap to the grid step when Snap is on. Every gesture is one
/// <see cref="SetCurvePointsAction"/>; Esc or a right click during a drag puts the curve back. Works on an
/// <see cref="ICurveTarget"/> so 3D paths can share it (G13.6).
/// </summary>
public sealed partial class CurveEditor
{
    private const float HandlePoints = 4f; // half a point square's side, in display points

    private static readonly Vector4 PointColor = new(0.95f, 0.95f, 1f, 1f);
    private static readonly Vector4 SelectedColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 HotColor = new(1f, 0.85f, 0.2f, 1f);
    private static readonly Vector4 TangentColor = new(0.55f, 0.75f, 1f, 1f);
    private static readonly Vector4 TangentLineColor = new(0.55f, 0.75f, 1f, 0.55f);
    private static readonly Vector4 GhostColor = new(1f, 1f, 1f, 0.6f);
    private static readonly Vector4 DeleteColor = new(1f, 0.3f, 0.25f, 1f);

    private readonly EditorWorkspace? _workspace;
    private CurveTool _tool;
    private ICurveTarget? _target;
    private Node? _targetPath;
    private Resource? _targetCurve;

    // 3D gestures (G13.6): the drag plane, vertical (Alt) drags, ground snap, the tilt ring.
    private Vector3 _planePoint, _planeNormal;
    private bool _vertical;
    private bool _groundSnap;
    private bool _riverSnap;                 // _groundSnap/_heightAbove are the river set (else the path set)
    private bool _otherGroundSnap = true;    // the other kind's (rivers start on, at −0.3 m)
    private float _otherHeightAbove = -0.3f;
    private bool _tiltHandles;
    private float _heightAbove;
    private Terrain3D? _testTerrain;
    private Terrain3D? _terrain;
    private int _terrainVersion = -1;
    private EditedScene? _terrainScene;

    // The gesture: what is dragged, the curve's points before it, and how the grab sits on the dragged thing.
    private CurvePart _dragPart;
    private int _dragIndex = -1;
    private float[]? _before;
    private string _gestureName = "";
    private Vector3 _grabOffset;
    private bool _mirror;

    private CurvePart _hoverPart;
    private int _hoverIndex = -1;
    private Vector3? _ghost;

    /// <summary>For the editor; tests pass null, choose the path with <see cref="EditForTests"/> and pass the history.</summary>
    public CurveEditor(EditorWorkspace? workspace)
    {
        _workspace = workspace;
    }

    /// <summary>Changes whenever the bar's state changes (the UI rebuilds on it).</summary>
    public int Version { get; private set; }

    public CurveTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            _tool = value;
            _ghost = null;
            Touch();
        }
    }

    /// <summary>The selected point of the edited curve (-1: none). Delete and Smooth act on it.</summary>
    public int SelectedPoint { get; set; } = -1;

    /// <summary>Snap moves to <see cref="SnapStep"/> (the toolbar's Snap; tests set it).</summary>
    public bool SnapEnabled { get; set; }

    /// <summary>The move step when <see cref="SnapEnabled"/> (the toolbar's translate step, in pixels in 2D).</summary>
    public float SnapStep { get; set; } = 8f;

    /// <summary>True during a drag.</summary>
    public bool IsDragging => _before is not null;

    /// <summary>
    /// The edited path: the tab's selected <see cref="Path2D"/> in a 2D view, or <see cref="Path3D"/> in a 3D view, or null.
    /// </summary>
    public Node? PathNode
    {
        get
        {
            if (_workspace is null)
                return _testPath;
            var scene = _workspace.Session.Active;
            if (scene is null || _workspace.Mode != EditorMode.Game || _workspace.Simulation.IsSimulating(scene))
                return null;
            Node? path = scene.Camera.Is2D
                ? scene.Selection.Primary as Path2D
                : scene.Selection.Primary is Path3D or River3D ? scene.Selection.Primary : null; // G8c.4: rivers too
            return path is { IsInsideTree: true } && scene.IsEditable(path) ? path : null;
        }
    }

    private Node? _testPath;

    /// <summary>Tests: edit <paramref name="path"/> (a <see cref="Path2D"/> or <see cref="Path3D"/>) without a workspace.</summary>
    internal void EditForTests(Node? path) => _testPath = path;

    /// <summary>Tests: the terrain ground snap and Fit to Ground use (the editor finds the scene's first <see cref="Terrain3D"/>).</summary>
    internal void TerrainForTests(Terrain3D? terrain) => _testTerrain = terrain;

    /// <summary>True while the curve bar shows.</summary>
    public bool IsActive => PathNode is not null;

    /// <summary>The edited path is a <see cref="Path3D"/> or a <see cref="River3D"/> (the 3D tools show: ground snap, height, tilt, Fit to Ground).</summary>
    public bool Is3D => PathNode is Path3D or River3D;

    /// <summary>The edited curve is a <see cref="River3D"/>'s: width and depth handles, cross-sections, its own ground snap.</summary>
    public bool IsRiver => PathNode is River3D;

    /// <summary>New and dragged points go onto the terrain under the mouse, <see cref="HeightAbove"/> above it (3D).</summary>
    public bool GroundSnap
    {
        get => _groundSnap;
        set
        {
            _groundSnap = value;
            Touch();
        }
    }

    /// <summary>Metres above the terrain for ground snap and Fit to Ground (negative: below).</summary>
    public float HeightAbove
    {
        get => _heightAbove;
        set
        {
            _heightAbove = value;
            Touch();
        }
    }

    /// <summary>The selected point shows its tilt ring (3D).</summary>
    public bool TiltHandles
    {
        get => _tiltHandles;
        set
        {
            _tiltHandles = value;
            Touch();
        }
    }

    /// <summary>The terrain ground snap and Fit to Ground use: the edited scene's first <see cref="Terrain3D"/> with data.</summary>
    public Terrain3D? Terrain
    {
        get
        {
            if (_workspace is null)
                return _testTerrain;
            if (_workspace.Session.Active is not { } scene)
                return null;
            if (!ReferenceEquals(scene, _terrainScene) || scene.Version != _terrainVersion)
            {
                _terrainScene = scene;
                _terrainVersion = scene.Version;
                _terrain = FindTerrain(scene.Root);
            }

            return _terrain is { IsInsideTree: true, Data: not null } terrain ? terrain : null;
        }
    }

    private static Terrain3D? FindTerrain(Node node)
    {
        if (node is Terrain3D terrain)
            return terrain;
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
            if (FindTerrain(children[i]) is { } found)
                return found;
        return null;
    }

    /// <summary>The edited curve as a target, or null (no path, or a path without a curve).</summary>
    public ICurveTarget? Target
    {
        get
        {
            var path = PathNode;
            Resource? curve = path switch
            {
                Path2D p2 => p2.Curve,
                Path3D p3 => p3.Curve,
                River3D river => river.Curve,
                _ => null,
            };
            if (curve is null)
                return _target = null;
            if (!ReferenceEquals(path, _targetPath) || !ReferenceEquals(curve, _targetCurve))
            {
                _targetPath = path;
                _targetCurve = curve;
                _target = path switch
                {
                    Path3D path3D => new Curve3DTarget(path3D),
                    River3D river => new Curve3DTarget(river, river.Curve!),
                    _ => new Curve2DTarget((Path2D)path!),
                };
            }

            return _target;
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A button went down at <paramref name="world"/> (view world space); <paramref name="pixel"/> is the world size of
    /// one display point. True when the curve tool took the click (a drag may follow).
    /// </summary>
    public bool Press(UndoRedo history, Vector2 world, float pixel, MouseButton button, EditorModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (PathNode is not Path2D path || (modifiers & EditorModifiers.Alt) != 0 || button is not (MouseButton.Left or MouseButton.Right))
            return false;
        if (IsDragging)
            return true;
        var at = new Vector3(world, 0f);
        var radius = TransformGizmo.GrabPixels * pixel;
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        var target = Target;
        var index = -1;
        var part = target is null ? CurvePart.None : CurveEditing.Hit(target, at, radius, handles: _tool == CurveTool.Select, out index);

        if (button == MouseButton.Right)
        {
            // Godot: a right click on a point removes it; anywhere else the button pans.
            if (target is null || part != CurvePart.Point)
                return false;
            RemovePoint(history, target, index);
            return true;
        }

        if (_tool == CurveTool.Delete)
        {
            if (target is not null && part == CurvePart.Point)
                RemovePoint(history, target, index);
            return true;
        }

        if (target is not null && part == CurvePart.Point && command && _tool == CurveTool.Select)
        {
            // Pull new, mirrored handles out of the point.
            Begin(target, "Pull Curve Handles", CurvePart.Out, index, Vector3.Zero, mirror: true);
            target.SetIn(index, Vector3.Zero);
            target.SetOut(index, Vector3.Zero);
            SelectedPoint = index;
            return true;
        }

        if (target is not null && part == CurvePart.Point)
        {
            SelectedPoint = index;
            Touch();
            Begin(target, "Move Curve Point", CurvePart.Point, index, target.ToGlobal(target.GetPosition(index)) - at, mirror: false);
            return true;
        }

        if (target is not null && part is CurvePart.In or CurvePart.Out)
        {
            var handle = target.GetPosition(index) + (part == CurvePart.In ? target.GetIn(index) : target.GetOut(index));
            Begin(target, "Move Curve Handle", part, index, target.ToGlobal(handle) - at, mirror: !shift);
            return true;
        }

        if (_tool == CurveTool.Insert)
        {
            if (target is not null && CurveEditing.HitSegment(target, at, radius, out var segment, out var t, out _))
            {
                Begin(target, "Insert Curve Point", CurvePart.Point, -1, Vector3.Zero, mirror: false);
                _dragIndex = CurveEditing.Insert(target, segment, t);
                SelectedPoint = _dragIndex;
                Touch();
            }

            return true;
        }

        if (_tool == CurveTool.Add || command)
        {
            target ??= CreateCurve(history, path);
            Begin(target, "Add Curve Point", CurvePart.Point, -1, Vector3.Zero, mirror: false);
            target.AddPoint(target.ToLocal(Snap(at)), Vector3.Zero, Vector3.Zero, -1);
            _dragIndex = target.PointCount - 1;
            SelectedPoint = _dragIndex;
            Touch();
            return true;
        }

        if (SelectedPoint >= 0)
        {
            SelectedPoint = -1;
            Touch();
        }

        return false; // the Select tool leaves empty space to node picking and box selection
    }

    /// <summary>The mouse moved to <paramref name="world"/>: drags, or updates the hover highlight.</summary>
    public void Move(Vector2 world, float pixel, EditorModifiers modifiers)
    {
        var at = new Vector3(world, 0f);
        if (IsDragging)
        {
            if (_target is not { } target || _dragIndex < 0 || _dragIndex >= target.PointCount)
                return;
            ApplyDrag(target, Snap(at + _grabOffset), modifiers);
            return;
        }

        _ghost = null;
        if (Target is not { } hover)
        {
            _hoverPart = CurvePart.None;
            return;
        }

        var radius = TransformGizmo.GrabPixels * pixel;
        _hoverPart = CurveEditing.Hit(hover, at, radius, handles: _tool == CurveTool.Select, out _hoverIndex);
        if (_hoverPart == CurvePart.None && _tool == CurveTool.Insert && CurveEditing.HitSegment(hover, at, radius, out _, out _, out var point))
            _ghost = point;
    }

    // A point or handle drag reaches `global` (view world space); handles mirror unless Shift breaks it.
    private void ApplyDrag(ICurveTarget target, Vector3 global, EditorModifiers modifiers)
    {
        var position = target.GetPosition(_dragIndex);
        switch (_dragPart)
        {
            case CurvePart.Point:
                target.SetPosition(_dragIndex, target.ToLocal(global));
                break;
            case CurvePart.In or CurvePart.Out:
                var offset = target.ToLocal(global) - position;
                var mirror = _mirror && (modifiers & EditorModifiers.Shift) == 0;
                if (_dragPart == CurvePart.Out)
                {
                    target.SetOut(_dragIndex, offset);
                    if (mirror)
                        target.SetIn(_dragIndex, -offset);
                }
                else
                {
                    target.SetIn(_dragIndex, offset);
                    if (mirror)
                        target.SetOut(_dragIndex, -offset);
                }

                break;
        }
    }

    // ── 3D views (G13.6) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 3D view the curve tool works in: its camera, size in view pixels, and view pixels per display point.</summary>
    public readonly record struct View3D(EditorCamera Camera, Vector2 ViewPixels, float Scale);

    private const float TiltRingPoints = 36f; // the tilt ring's radius in display points

    private Vector3 _dragStart;   // the dragged point's global position when the drag began (vertical drags keep its X, Z)
    private Vector3 _tiltPoint, _tiltAxis, _tiltZero;
    private string? _label;

    /// <summary>The text next to the mouse while a tilt handle is hovered or dragged ("Tilt 12.0°"), else null.</summary>
    public string? Label => _label;

    /// <summary>
    /// A button went down at view pixel <paramref name="pixel"/> of a 3D view. As <see cref="Press"/>, with the points and
    /// handles hit on screen, drags in the plane through the grabbed point facing the camera (Alt+drag a point: only up
    /// and down), new points on the terrain with <see cref="GroundSnap"/> or else level with the last point, and the
    /// selected point's tilt ring with <see cref="TiltHandles"/>.
    /// </summary>
    public bool Press3D(UndoRedo history, in View3D view, Vector2 pixel, MouseButton button, EditorModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (PathNode is not (Path3D or River3D) || button is not (MouseButton.Left or MouseButton.Right))
            return false;
        var path = PathNode!;
        if (IsDragging)
            return true;
        var projector = new CameraProjector(view.Camera, view.ViewPixels);
        var radius = TransformGizmo.GrabPixels * view.Scale;
        var alt = (modifiers & EditorModifiers.Alt) != 0;
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        var target = Target;

        // G8c.4: the selected river point's width (either bank) and depth handles.
        if (button == MouseButton.Left && !alt && target is { HasWidth: true } && SectionHit(target, view, pixel, radius) is { } section)
        {
            Begin(target, section == CurvePart.Width ? "Set River Width" : "Set River Depth", section, SelectedPoint, Vector3.Zero, mirror: false);
            UpdateSectionLabel(target, section);
            return true;
        }

        if (button == MouseButton.Left && !alt && target is { HasTilt: true } && TiltHit(target, view, pixel, radius))
        {
            Begin(target, "Tilt Curve Point", CurvePart.Tilt, SelectedPoint, Vector3.Zero, mirror: false);
            UpdateTiltLabel(target);
            return true;
        }

        var index = -1;
        var part = target is null ? CurvePart.None : CurveEditing.HitScreen(target, projector, pixel, radius, handles: _tool == CurveTool.Select, out index);
        if (alt && part != CurvePart.Point)
            return false; // Alt+drag elsewhere orbits
        if (button == MouseButton.Right)
        {
            if (target is null || part != CurvePart.Point)
                return false;
            RemovePoint(history, target, index);
            return true;
        }

        if (_tool == CurveTool.Delete)
        {
            if (target is not null && part == CurvePart.Point)
                RemovePoint(history, target, index);
            return true;
        }

        if (target is not null && part == CurvePart.Point && command && _tool == CurveTool.Select)
        {
            var at = target.ToGlobal(target.GetPosition(index));
            Begin(target, "Pull Curve Handles", CurvePart.Out, index, Vector3.Zero, mirror: true);
            target.SetIn(index, Vector3.Zero);
            target.SetOut(index, Vector3.Zero);
            SelectedPoint = index;
            BeginPlane(view, pixel, at, vertical: false);
            return true;
        }

        if (target is not null && part == CurvePart.Point)
        {
            SelectedPoint = index;
            Touch();
            Begin(target, "Move Curve Point", CurvePart.Point, index, Vector3.Zero, mirror: false);
            BeginPlane(view, pixel, target.ToGlobal(target.GetPosition(index)), alt);
            return true;
        }

        if (target is not null && part is CurvePart.In or CurvePart.Out)
        {
            var handle = target.GetPosition(index) + (part == CurvePart.In ? target.GetIn(index) : target.GetOut(index));
            Begin(target, "Move Curve Handle", part, index, Vector3.Zero, mirror: !shift);
            BeginPlane(view, pixel, target.ToGlobal(handle), vertical: false);
            return true;
        }

        if (_tool == CurveTool.Insert)
        {
            if (target is not null && CurveEditing.HitSegmentScreen(target, projector, pixel, radius, out var segment, out var t))
            {
                Begin(target, "Insert Curve Point", CurvePart.Point, -1, Vector3.Zero, mirror: false);
                _dragIndex = CurveEditing.Insert(target, segment, t);
                SelectedPoint = _dragIndex;
                BeginPlane(view, pixel, target.ToGlobal(target.GetPosition(_dragIndex)), vertical: false);
                Touch();
            }

            return true;
        }

        if (_tool == CurveTool.Add || command)
        {
            target ??= CreateCurve(history, path);
            var at = Snap(AddPosition(target, view, pixel));
            Begin(target, "Add Curve Point", CurvePart.Point, -1, Vector3.Zero, mirror: false);
            target.AddPoint(target.ToLocal(at), Vector3.Zero, Vector3.Zero, -1);
            _dragIndex = target.PointCount - 1;
            if (target.HasWidth)
            {
                // A river's new point continues the last one's section (the first: 3 m wide, 0.6 m deep).
                target.SetWidth(_dragIndex, _dragIndex > 0 ? target.GetWidth(_dragIndex - 1) : 3f);
                target.SetDepth(_dragIndex, _dragIndex > 0 ? target.GetDepth(_dragIndex - 1) : 0.6f);
            }

            SelectedPoint = _dragIndex;
            BeginPlane(view, pixel, at, vertical: false);
            Touch();
            return true;
        }

        if (SelectedPoint >= 0)
        {
            SelectedPoint = -1;
            Touch();
        }

        return false;
    }

    /// <summary>The mouse moved to view pixel <paramref name="pixel"/> of a 3D view: drags, or updates the hover highlight.</summary>
    public void Move3D(in View3D view, Vector2 pixel, EditorModifiers modifiers)
    {
        var (origin, direction) = view.Camera.Ray(pixel.X, pixel.Y, view.ViewPixels.X, view.ViewPixels.Y);
        if (IsDragging)
        {
            if (_target is not { } target || _dragIndex < 0 || _dragIndex >= target.PointCount)
                return;
            if (_dragPart is CurvePart.Width or CurvePart.Depth)
            {
                DragSection(target, origin, direction);
                return;
            }

            if (_dragPart == CurvePart.Tilt)
            {
                if (RayPlane(origin, direction, _tiltPoint, _tiltAxis, out var on))
                {
                    var v = on - _tiltPoint;
                    var degrees = float.RadiansToDegrees(MathF.Atan2(Vector3.Dot(Vector3.Cross(_tiltZero, v), _tiltAxis), Vector3.Dot(_tiltZero, v)));
                    target.SetTilt(_dragIndex, float.DegreesToRadians(SnapAngle(degrees)));
                    UpdateTiltLabel(target);
                }

                return;
            }

            if (!RayPlane(origin, direction, _planePoint, _planeNormal, out var hit))
                return;
            var global = hit + _grabOffset;
            if (_vertical)
                global = new Vector3(_dragStart.X, global.Y, _dragStart.Z);
            else if (_groundSnap && _dragPart == CurvePart.Point && Terrain is { } terrain &&
                     terrain.Raycast(origin, direction, 100000f, out var ground))
                global = ground.Position + Vector3.UnitY * _heightAbove;
            ApplyDrag(target, Snap(global), modifiers);
            return;
        }

        _ghost = null;
        _label = null;
        if (Target is not { } hover)
        {
            _hoverPart = CurvePart.None;
            return;
        }

        var projector = new CameraProjector(view.Camera, view.ViewPixels);
        var radius = TransformGizmo.GrabPixels * view.Scale;
        if (hover.HasWidth && SectionHit(hover, view, pixel, radius) is { } sectionPart)
        {
            _hoverPart = sectionPart;
            _hoverIndex = SelectedPoint;
            UpdateSectionLabel(hover, sectionPart);
            return;
        }

        if (hover.HasTilt && TiltHit(hover, view, pixel, radius))
        {
            _hoverPart = CurvePart.Tilt;
            _hoverIndex = SelectedPoint;
            UpdateTiltLabel(hover);
            return;
        }

        _hoverPart = CurveEditing.HitScreen(hover, projector, pixel, radius, handles: _tool == CurveTool.Select, out _hoverIndex);
        if (_hoverPart == CurvePart.None && _tool == CurveTool.Insert &&
            CurveEditing.HitSegmentScreen(hover, projector, pixel, radius, out var segment, out var t))
            _ghost = hover.ToGlobal(CurveEditing.Sample(hover, segment, t));
    }

    /// <summary>The points, tangent handles, the tilt ring and the insert ghost in a 3D view (overlay lines).</summary>
    public void Draw3D(DebugLines lines, in View3D view)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!Is3D || Target is not { } target)
            return;
        var camera = view.Camera;
        for (var i = 0; i < target.PointCount; i++)
        {
            var local = target.GetPosition(i);
            var p = target.ToGlobal(local);
            var half = HandlePoints * view.Scale * camera.WorldPerPixel(p, view.ViewPixels.Y);
            if (_tool == CurveTool.Select)
            {
                DrawTangent3D(lines, target, camera, p, local + target.GetIn(i), target.GetIn(i), Hot(CurvePart.In, i), view);
                DrawTangent3D(lines, target, camera, p, local + target.GetOut(i), target.GetOut(i), Hot(CurvePart.Out, i), view);
            }

            var color = Hot(CurvePart.Point, i) ? (_tool == CurveTool.Delete ? DeleteColor : HotColor)
                : i == SelectedPoint ? SelectedColor
                : PointColor;
            HandleDrawing.Square(lines, p, camera.Right, camera.Up, half, color);
            if (i == SelectedPoint || i == 0)
                HandleDrawing.Square(lines, p, camera.Right, camera.Up, half * 0.5f, color);
        }

        if (target.HasWidth)
            DrawSections(lines, target, view);

        if (TiltRing(target, view, out var center, out var axis, out var zero, out var handle, out var ringRadius))
        {
            var hot = Hot(CurvePart.Tilt, SelectedPoint);
            HandleDrawing.Circle(lines, center, zero, Vector3.Cross(axis, zero), ringRadius, hot ? HotColor : TangentLineColor);
            lines.AddLine(center, handle, hot ? HotColor : TangentColor);
            var half = HandlePoints * view.Scale * camera.WorldPerPixel(handle, view.ViewPixels.Y);
            HandleDrawing.Square(lines, handle, camera.Right, camera.Up, half, hot ? HotColor : TangentColor);
        }

        if (_ghost is { } ghost)
            HandleDrawing.Square(lines, ghost, camera.Right, camera.Up, HandlePoints * 0.75f * view.Scale * camera.WorldPerPixel(ghost, view.ViewPixels.Y), GhostColor);
    }

    private static void DrawTangent3D(DebugLines lines, ICurveTarget target, EditorCamera camera, Vector3 point, Vector3 handleLocal, Vector3 offset, bool hot, in View3D view)
    {
        if (offset == Vector3.Zero)
            return;
        var h = target.ToGlobal(handleLocal);
        lines.AddLine(point, h, TangentLineColor);
        var r = HandlePoints * 0.9f * view.Scale * camera.WorldPerPixel(h, view.ViewPixels.Y);
        var x = camera.Right * r;
        var y = camera.Up * r;
        var color = hot ? HotColor : TangentColor;
        lines.AddLine(h - x, h - y, color);
        lines.AddLine(h - y, h + x, color);
        lines.AddLine(h + x, h + y, color);
        lines.AddLine(h + y, h - x, color);
    }

    // The selected point's tilt ring: around the curve's tangent, through its untilted up vector, the handle at the tilt.
    private bool TiltRing(ICurveTarget target, in View3D view, out Vector3 center, out Vector3 axis, out Vector3 zero, out Vector3 handle, out float radius)
    {
        center = axis = zero = handle = default;
        radius = 0;
        if (!_tiltHandles || !target.HasTilt || (uint)SelectedPoint >= (uint)target.PointCount)
            return false;
        center = target.ToGlobal(target.GetPosition(SelectedPoint));
        target.FrameAt(SelectedPoint, out axis, out zero);
        radius = TiltRingPoints * view.Scale * view.Camera.WorldPerPixel(center, view.ViewPixels.Y);
        var rolled = Vector3.Transform(zero, Quaternion.CreateFromAxisAngle(axis, target.GetTilt(SelectedPoint)));
        handle = center + rolled * radius;
        return true;
    }

    private bool TiltHit(ICurveTarget target, in View3D view, Vector2 pixel, float radius)
    {
        if (!TiltRing(target, view, out var center, out var axis, out var zero, out var handle, out _) ||
            !view.Camera.Project(handle, view.ViewPixels.X, view.ViewPixels.Y, out var at) || Vector2.Distance(at, pixel) > radius)
            return false;
        _tiltPoint = center;
        _tiltAxis = axis;
        _tiltZero = zero;
        return true;
    }

    private void UpdateTiltLabel(ICurveTarget target) =>
        _label = (uint)SelectedPoint < (uint)target.PointCount
            ? "Tilt " + float.RadiansToDegrees(target.GetTilt(SelectedPoint)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°"
            : null;

    // The drag plane through `point`: facing the camera, or for a vertical drag the upright plane facing it.
    private void BeginPlane(in View3D view, Vector2 pixel, Vector3 point, bool vertical)
    {
        _vertical = vertical;
        _dragStart = point;
        _planePoint = point;
        var forward = view.Camera.Forward;
        if (vertical)
        {
            var flat = new Vector3(forward.X, 0, forward.Z);
            forward = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitZ;
        }

        _planeNormal = forward;
        var (origin, direction) = view.Camera.Ray(pixel.X, pixel.Y, view.ViewPixels.X, view.ViewPixels.Y);
        _grabOffset = RayPlane(origin, direction, _planePoint, _planeNormal, out var hit) ? point - hit : Vector3.Zero;
    }

    // Where a new point goes: on the terrain under the mouse (ground snap), else level with the last point (or the path).
    private Vector3 AddPosition(ICurveTarget target, in View3D view, Vector2 pixel)
    {
        var (origin, direction) = view.Camera.Ray(pixel.X, pixel.Y, view.ViewPixels.X, view.ViewPixels.Y);
        if (_groundSnap && Terrain is { } terrain && terrain.Raycast(origin, direction, 100000f, out var hit))
            return hit.Position + Vector3.UnitY * _heightAbove;
        var level = target.PointCount > 0
            ? target.ToGlobal(target.GetPosition(target.PointCount - 1)).Y
            : target.Node is Node3D node ? node.GlobalPosition.Y : 0f;
        if (MathF.Abs(direction.Y) > 1e-4f && (level - origin.Y) / direction.Y is var t and > 0f)
            return origin + direction * t;
        return origin + direction * 10f;
    }

    private static bool RayPlane(Vector3 origin, Vector3 direction, Vector3 point, Vector3 normal, out Vector3 hit)
    {
        var denominator = Vector3.Dot(direction, normal);
        hit = default;
        if (MathF.Abs(denominator) < 1e-6f)
            return false;
        var t = Vector3.Dot(point - origin, normal) / denominator;
        if (t < 0f)
            return false;
        hit = origin + direction * t;
        return true;
    }

    private float SnapAngle(float degrees)
    {
        var (enabled, step) = _workspace is { } workspace ? (workspace.Gizmo.Snap.Enabled, workspace.Gizmo.Snap.RotateDegrees) : (SnapEnabled, SnapAngleStep);
        return enabled && step > 0 ? MathF.Round(degrees / step) * step : degrees;
    }

    /// <summary>The tilt step in degrees when <see cref="SnapEnabled"/> (tests; the editor uses the toolbar's rotate step).</summary>
    public float SnapAngleStep { get; set; } = 15f;

    /// <summary>The drag's button was released: commits the gesture (one undo entry when the curve changed).</summary>
    public void Release(UndoRedo? history = null)
    {
        if (_before is not { } before || _target is not { } target)
            return;
        var after = (float[])target.PointsProperty.GetValue(target.Curve)!;
        history ??= _workspace?.Session.Active?.History;
        if (!before.AsSpan().SequenceEqual(after))
            history?.Commit(new SetCurvePointsAction(_gestureName, target.Curve, target.PointsProperty, before, after), alreadyApplied: true);
        EndGesture();
    }

    /// <summary>Esc or a right click during a drag: the curve goes back to how it was.</summary>
    public void CancelDrag()
    {
        if (_before is { } before && _target is { } target)
            target.PointsProperty.SetValue(target.Curve, before);
        EndGesture();
    }

    /// <summary>Per frame: forgets the gesture and the point selection when the edited path changes.</summary>
    public void Tick()
    {
        var path = PathNode;
        if (ReferenceEquals(path, _targetPath))
            return;
        if (IsDragging)
            CancelDrag();
        _target = null;
        _targetPath = null;
        _targetCurve = null;
        SelectedPoint = -1;
        _hoverPart = CurvePart.None;
        _ghost = null;
        // Rivers keep their own ground snap (on, 0.3 m under the terrain: the carve makes the channel); paths theirs.
        if (path is not null && path is River3D != _riverSnap)
        {
            _riverSnap = path is River3D;
            (_groundSnap, _otherGroundSnap) = (_otherGroundSnap, _groundSnap);
            (_heightAbove, _otherHeightAbove) = (_otherHeightAbove, _heightAbove);
        }

        _ = Target;
        Touch();
    }

    /// <summary>Drops every reference into the edited scenes (tab closed, code reload).</summary>
    public void ReleaseReferences()
    {
        CancelDrag();
        _target = null;
        _targetPath = null;
        _targetCurve = null;
        SelectedPoint = -1;
    }

    private void Begin(ICurveTarget target, string name, CurvePart part, int index, Vector3 grabOffset, bool mirror)
    {
        // The curve edits a fresh copy, so the array the history keeps as "before" is never written in place.
        var current = (float[])target.PointsProperty.GetValue(target.Curve)!;
        target.PointsProperty.SetValue(target.Curve, current.Clone());
        _before = current;
        _gestureName = name;
        _dragPart = part;
        _dragIndex = index;
        _grabOffset = grabOffset;
        _mirror = mirror;
    }

    private void EndGesture()
    {
        _before = null;
        _label = null;
        _dragPart = CurvePart.None;
        _dragIndex = -1;
    }

    private void RemovePoint(UndoRedo history, ICurveTarget target, int index)
    {
        Begin(target, "Delete Curve Point", CurvePart.None, -1, Vector3.Zero, mirror: false);
        target.RemovePoint(index);
        if (SelectedPoint == index)
            SelectedPoint = -1;
        else if (SelectedPoint > index)
            SelectedPoint--;
        Release(history);
        Touch();
    }

    private ICurveTarget CreateCurve(UndoRedo history, Node path)
    {
        var property = TypeRegistry.GetNearest(path.GetType())!.FindProperty("Curve")!;
        Resource curve = path is Path3D ? new Curve3D { UpVectorEnabled = true } : path is River3D ? new Curve3D() : new Curve2D();
        history.Commit(SetPropertyAction.Create(path, property, curve));
        return Target!;
    }

    private Vector3 Snap(Vector3 global)
    {
        var (enabled, step) = _workspace is { } workspace ? (workspace.Gizmo.Snap.Enabled, workspace.Gizmo.Snap.Translate) : (SnapEnabled, SnapStep);
        if (!enabled || !(step > 0))
            return global;
        return new Vector3(MathF.Round(global.X / step) * step, MathF.Round(global.Y / step) * step, MathF.Round(global.Z / step) * step);
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a curve bar command (<c>curve.*</c>); false when it does not apply.</summary>
    public bool RunCommand(string id, UndoRedo? history = null)
    {
        history ??= _workspace?.Session.Active?.History;
        switch (id)
        {
            case "curve.tool.select": Tool = CurveTool.Select; return true;
            case "curve.tool.add": Tool = CurveTool.Add; return true;
            case "curve.tool.insert": Tool = CurveTool.Insert; return true;
            case "curve.tool.delete": Tool = CurveTool.Delete; return true;
            case "curve.ground": GroundSnap = !GroundSnap; return true;
            case "curve.tilt": TiltHandles = !TiltHandles; return true;
            case "curve.height.less": HeightAbove -= 0.5f; return true;
            case "curve.height.more": HeightAbove += 0.5f; return true;
        }

        if (history is null || Target is not { } target || IsDragging)
            return false;
        switch (id)
        {
            case "curve.close":
                history.Commit(SetPropertyAction.Create(target.Curve, target.ClosedProperty, !target.Closed));
                Touch();
                return true;
            case "curve.reverse" when target.PointCount >= 2:
                Begin(target, "Reverse Curve", CurvePart.None, -1, Vector3.Zero, mirror: false);
                target.Reverse();
                if (SelectedPoint >= 0)
                    SelectedPoint = target.PointCount - 1 - SelectedPoint;
                Release(history);
                Touch();
                return true;
            case "curve.smooth" when target.PointCount >= 2:
                Begin(target, "Smooth Curve", CurvePart.None, -1, Vector3.Zero, mirror: false);
                if (SelectedPoint >= 0 && SelectedPoint < target.PointCount)
                    CurveEditing.Smooth(target, SelectedPoint);
                else
                    for (var i = 0; i < target.PointCount; i++)
                        CurveEditing.Smooth(target, i);
                Release(history);
                return true;
            case "curve.delete_point" when SelectedPoint >= 0 && SelectedPoint < target.PointCount:
                RemovePoint(history, target, SelectedPoint);
                return true;
            case "curve.fit_ground" when target is Curve3DTarget curve3D && Terrain is { } terrain && target.PointCount > 0:
                Begin(target, "Fit Curve to Ground", CurvePart.None, -1, Vector3.Zero, mirror: false);
                CurveTerrainFit.Fit(curve3D.Curve3D, curve3D.Path.GlobalTransform, terrain, _heightAbove);
                Release(history);
                return true;
            case "curve.deselect" when SelectedPoint >= 0:
                SelectedPoint = -1;
                Touch();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The curve bar's keys while it shows: Delete/Backspace remove the selected point, Esc deselects it.</summary>
    public static string? ShortcutFor(Key key, EditorModifiers modifiers, bool hasSelectedPoint)
    {
        if (modifiers != EditorModifiers.None || !hasSelectedPoint)
            return null;
        return key switch
        {
            Key.Delete or Key.Backspace => "curve.delete_point",
            Key.Escape => "curve.deselect",
            _ => null,
        };
    }

    /// <summary>The curve bar's markup: icon buttons with tooltips ("Title — what it does").</summary>
    public string BarRml()
    {
        var rml = new StringBuilder(2048);
        var closed = Target?.Closed ?? false;
        Button(rml, "curve.tool.select", "pointer", _tool == CurveTool.Select, "Select Points — drag points and handles (Shift: break the mirror); Cmd/Ctrl+drag a point pulls new handles; Cmd/Ctrl+click adds a point; right click removes one");
        Button(rml, "curve.tool.add", "circle-plus", _tool == CurveTool.Add, "Add Point — click to add a point at the end of the curve");
        Button(rml, "curve.tool.insert", "plus", _tool == CurveTool.Insert, "Insert Point — click on the curve to split it there without changing its shape");
        Button(rml, "curve.tool.delete", "trash", _tool == CurveTool.Delete, "Delete Point — click a point to remove it (Delete removes the selected point)");
        rml.Append("<span class=\"paint-sep\"></span>");
        Button(rml, "curve.close", "repeat", closed, closed ? "Open Curve — remove the segment from the last point back to the first" : "Close Curve — join the last point back to the first");
        Button(rml, "curve.reverse", "arrows-exchange", false, "Reverse — run the curve the other way (followers' Progress counts from the other end)");
        Button(rml, "curve.smooth", "wave-sine", false, "Smooth — give the selected point (or every point when none is selected) smooth, mirrored handles");
        if (Is3D)
        {
            var terrain = Terrain is not null;
            rml.Append("<span class=\"paint-sep\"></span>");
            Button(rml, "curve.tilt", "rotate", _tiltHandles, "Tilt Handles — the selected point's tilt ring: drag its handle to roll the curve there (snaps to the rotate step)");
            Button(rml, "curve.ground", "mountain", _groundSnap, terrain
                ? "Snap to Ground — new and dragged points go onto the terrain under the mouse, Height Above it"
                : "Snap to Ground — needs a Terrain3D in the scene (none found)");
            Button(rml, "curve.height.less", "minus", false, "Lower — Height Above the terrain −0.5 m");
            rml.Append("<span class=\"paint-value mono\" data-tooltip=\"Height Above — metres above the terrain for Snap to Ground and Fit to Ground\">")
                .Append(_heightAbove.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" m</span>");
            Button(rml, "curve.height.more", "plus", false, "Raise — Height Above the terrain +0.5 m");
            Button(rml, "curve.fit_ground", "arrow-bar-to-down", false, terrain
                ? "Fit to Ground — move every point onto the terrain, Height Above it"
                : "Fit to Ground — needs a Terrain3D in the scene (none found)");
        }

        return rml.ToString();
    }

    private static void Button(StringBuilder rml, string command, string icon, bool active, string tooltip) =>
        rml.Append("<button class=\"tool-button").Append(active ? " active" : "").Append("\" data-command=\"").Append(command)
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-").Append(icon).Append("\"></span></button>");

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The points, tangent handles and the insert ghost (overlay lines of the 2D view); allocates nothing.</summary>
    public void Draw(DebugLines lines, float pixel)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Is3D || Target is not { } target)
            return;
        var half = HandlePoints * pixel;
        var right = Vector3.UnitX;
        var down = Vector3.UnitY;
        for (var i = 0; i < target.PointCount; i++)
        {
            var local = target.GetPosition(i);
            var p = target.ToGlobal(local);
            if (_tool == CurveTool.Select)
            {
                DrawTangent(lines, target, p, local + target.GetIn(i), target.GetIn(i), Hot(CurvePart.In, i), half);
                DrawTangent(lines, target, p, local + target.GetOut(i), target.GetOut(i), Hot(CurvePart.Out, i), half);
            }

            var color = Hot(CurvePart.Point, i) ? (_tool == CurveTool.Delete ? DeleteColor : HotColor)
                : i == SelectedPoint ? SelectedColor
                : PointColor;
            HandleDrawing.Square(lines, p, right, down, half, color);
            if (i == SelectedPoint || i == 0)
                HandleDrawing.Square(lines, p, right, down, half * 0.5f, color); // the first point is marked (the curve's start)
        }

        if (_ghost is { } ghost)
            HandleDrawing.Square(lines, ghost, right, down, half * 0.75f, GhostColor);
    }

    private bool Hot(CurvePart part, int index) =>
        IsDragging ? _dragPart == part && _dragIndex == index : _hoverPart == part && _hoverIndex == index;

    private static void DrawTangent(DebugLines lines, ICurveTarget target, Vector3 point, Vector3 handleLocal, Vector3 offset, bool hot, float half)
    {
        if (offset == Vector3.Zero)
            return;
        var h = target.ToGlobal(handleLocal);
        var color = hot ? HotColor : TangentColor;
        lines.AddLine(point, h, TangentLineColor);
        // A diamond.
        var r = half * 0.9f;
        var x = new Vector3(r, 0, 0);
        var y = new Vector3(0, r, 0);
        lines.AddLine(h - x, h - y, color);
        lines.AddLine(h - y, h + x, color);
        lines.AddLine(h + x, h + y, color);
        lines.AddLine(h + y, h - x, color);
    }

    private void Touch() => Version++;
}
