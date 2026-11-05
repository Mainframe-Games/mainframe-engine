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
public sealed class CurveEditor
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
    private Path2D? _targetPath;
    private Resource? _targetCurve;

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

    /// <summary>The edited path: the 2D tab's selected <see cref="Path2D"/>, or null.</summary>
    public Path2D? Path
    {
        get
        {
            if (_workspace is null)
                return _testPath;
            var scene = _workspace.Session.Active;
            if (scene is null || !scene.Camera.Is2D || _workspace.Mode != EditorMode.Game || _workspace.Simulation.IsSimulating(scene))
                return null;
            return scene.Selection.Primary is Path2D { IsInsideTree: true } path && scene.IsEditable(path) ? path : null;
        }
    }

    private Path2D? _testPath;

    /// <summary>Tests: edit <paramref name="path"/> without a workspace.</summary>
    internal void EditForTests(Path2D? path) => _testPath = path;

    /// <summary>True while the curve bar shows.</summary>
    public bool IsActive => Path is not null;

    /// <summary>The edited curve as a target, or null (no path, or a path without a curve).</summary>
    public ICurveTarget? Target
    {
        get
        {
            var path = Path;
            if (path?.Curve is not { } curve)
                return _target = null;
            if (!ReferenceEquals(path, _targetPath) || !ReferenceEquals(curve, _targetCurve))
            {
                _targetPath = path;
                _targetCurve = curve;
                _target = new Curve2DTarget(path);
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
        if (Path is not { } path || (modifiers & EditorModifiers.Alt) != 0 || button is not (MouseButton.Left or MouseButton.Right))
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
            var global = Snap(at + _grabOffset);
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
        var path = Path;
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

    private ICurveTarget CreateCurve(UndoRedo history, Path2D path)
    {
        var property = TypeRegistry.GetNearest(typeof(Path2D))!.FindProperty(nameof(Path2D.Curve))!;
        history.Commit(SetPropertyAction.Create(path, property, new Curve2D()));
        return Target!;
    }

    private Vector3 Snap(Vector3 global)
    {
        var (enabled, step) = _workspace is { } workspace ? (workspace.Gizmo.Snap.Enabled, workspace.Gizmo.Snap.Translate) : (SnapEnabled, SnapStep);
        if (!enabled || !(step > 0))
            return global;
        return new Vector3(MathF.Round(global.X / step) * step, MathF.Round(global.Y / step) * step, global.Z);
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
        if (Target is not { } target)
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
