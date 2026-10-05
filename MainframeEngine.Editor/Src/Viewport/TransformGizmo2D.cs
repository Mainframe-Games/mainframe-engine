using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The 2D transform gizmo (Node2D scenes): move arrows along X and Y plus a centre square for free moves, a rotation ring
/// around Z, and scale handles (X, Y, centre: uniform). Everything is hit-tested and dragged in view pixels through the
/// 2D <see cref="EditorCamera"/> (z = 0 plane, units = pixels, y down as in Godot). Moves snap to the grid step when
/// <see cref="GizmoSnap.Enabled"/>, else to whole pixels when <see cref="PixelSnap"/>; rotations to the angle step and
/// scales to the scale step when snapping. Shares <see cref="GizmoMode"/>, <see cref="GizmoSnap"/> and the toolbar
/// state with the 3D gizmo; <see cref="GizmoHandle.Z"/> is the rotation ring. Pure math (unit-tested).
/// </summary>
public sealed class TransformGizmo2D
{
    /// <summary>Arrow length on screen, in points.</summary>
    public const float SizePoints = 80f;

    /// <summary>Grab distance, in points.</summary>
    public const float GrabPoints = 8f;

    private static readonly Vector4 XColor = new(0.96f, 0.32f, 0.32f, 1f);
    private static readonly Vector4 YColor = new(0.35f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 RingColor = new(0.4f, 0.6f, 1f, 1f);
    private static readonly Vector4 CenterColor = new(0.9f, 0.9f, 0.95f, 1f);
    private static readonly Vector4 HotColor = new(1f, 0.9f, 0.2f, 1f);

    private Vector2 _startPosition;
    private float _startRotation;
    private Vector2 _startScale;
    private Vector2 _startWorld;
    private Vector2 _origin;
    private Vector2 _axisX;
    private Vector2 _axisY;
    private float _startAngle;

    /// <summary>The toolbar's gizmo (mode, local/global, snapping and pixel scale come from it).</summary>
    public TransformGizmo2D(TransformGizmo shared)
    {
        Shared = shared ?? throw new ArgumentNullException(nameof(shared));
    }

    public TransformGizmo Shared { get; }

    public GizmoMode Mode => Shared.Mode;

    /// <summary>Moves snap to whole pixels (when grid snapping is off). On by default, like Godot's pixel snap.</summary>
    public bool PixelSnap { get; set; } = true;

    public GizmoHandle Active { get; private set; }

    public GizmoHandle Hovered { get; set; }

    public bool IsDragging => Active != GizmoHandle.None;

    private float Size => SizePoints * Shared.PixelScale;
    private float Grab => GrabPoints * Shared.PixelScale;

    /// <summary>The gizmo's world axes (y down in the world = down on screen) for a node at global rotation <paramref name="rotation"/>.</summary>
    public (Vector2 X, Vector2 Y) Axes(float rotation)
    {
        if (!Shared.Local && Mode != GizmoMode.Scale)
            return (Vector2.UnitX, Vector2.UnitY);
        var (sin, cos) = MathF.SinCos(rotation);
        return (new Vector2(cos, sin), new Vector2(-sin, cos));
    }

    /// <summary>The handle under view pixel <paramref name="mouse"/>, or None.</summary>
    public GizmoHandle HitTest(EditorCamera camera, Vector2 viewPixels, Vector2 origin, float rotation, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select)
            return GizmoHandle.None;
        var center = camera.WorldToScreen2D(origin, viewPixels);
        if (Mode == GizmoMode.Rotate)
            return MathF.Abs(Vector2.Distance(mouse, center) - Size) <= Grab ? GizmoHandle.Z : GizmoHandle.None;
        if (MathF.Abs(mouse.X - center.X) <= Grab * 1.25f && MathF.Abs(mouse.Y - center.Y) <= Grab * 1.25f)
            return GizmoHandle.Center;
        var (x, y) = Axes(rotation);
        var best = GizmoHandle.None;
        var bestDistance = Grab;
        Consider(GizmoHandle.X, ScreenDirection(x), ref best, ref bestDistance);
        Consider(GizmoHandle.Y, ScreenDirection(y), ref best, ref bestDistance);
        return best;

        void Consider(GizmoHandle handle, Vector2 direction, ref GizmoHandle found, ref float distance)
        {
            var d = DistanceToSegment(mouse, center, center + direction * Size);
            if (d < distance)
            {
                distance = d;
                found = handle;
            }
        }
    }

    // World direction → screen direction: both are y down.
    private static Vector2 ScreenDirection(Vector2 world) => world;

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Starts dragging <paramref name="handle"/> of a node at global <paramref name="origin"/> (position, rotation, scale are its values).</summary>
    public void BeginDrag(GizmoHandle handle, EditorCamera camera, Vector2 viewPixels, Vector2 mouse, Vector2 origin, float rotation, Vector2 position, Vector2 scale)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Active = handle;
        _origin = origin;
        _startPosition = position;
        _startRotation = rotation;
        _startScale = scale;
        (_axisX, _axisY) = Axes(rotation);
        _startWorld = camera.ScreenToWorld2D(mouse, viewPixels);
        _startAngle = MathF.Atan2(_startWorld.Y - origin.Y, _startWorld.X - origin.X);
    }

    /// <summary>
    /// The dragged result for view pixel <paramref name="mouse"/>: the new global position, rotation (radians) and
    /// scale (the values not edited by the mode are the start values).
    /// </summary>
    public (Vector2 Position, float Rotation, Vector2 Scale) Drag(EditorCamera camera, Vector2 viewPixels, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var world = camera.ScreenToWorld2D(mouse, viewPixels);
        var delta = world - _startWorld;
        var snap = Shared.Snap;
        switch (Mode)
        {
            case GizmoMode.Translate:
                {
                    var move = Active switch
                    {
                        GizmoHandle.X => _axisX * Vector2.Dot(delta, _axisX),
                        GizmoHandle.Y => _axisY * Vector2.Dot(delta, _axisY),
                        _ => delta,
                    };
                    var position = _startPosition + move;
                    // World-aligned arrows snap only their axis (the other keeps its exact value); rotated (local) ones both.
                    var snapped = _axisX == Vector2.UnitX ? Active : GizmoHandle.Center;
                    if (snap.Enabled && snap.Translate > 0)
                        position = SnapVector(position, snap.Translate, snapped);
                    else if (PixelSnap)
                        position = SnapVector(position, 1f, snapped);
                    return (position, _startRotation, _startScale);
                }

            case GizmoMode.Rotate:
                {
                    var angle = MathF.Atan2(world.Y - _origin.Y, world.X - _origin.X) - _startAngle;
                    // Unwrap to (-π, π] so a drag across ±180° does not jump.
                    angle = MathF.IEEERemainder(angle, MathF.Tau);
                    var rotation = _startRotation + angle;
                    if (snap.Enabled && snap.RotateDegrees > 0)
                    {
                        var step = float.DegreesToRadians(snap.RotateDegrees);
                        rotation = MathF.Round(rotation / step) * step;
                    }

                    return (_startPosition, rotation, _startScale);
                }

            case GizmoMode.Scale:
                {
                    var start = _startWorld - _origin;
                    var now = world - _origin;
                    var scale = _startScale;
                    switch (Active)
                    {
                        case GizmoHandle.X:
                            scale.X *= Ratio(Vector2.Dot(now, _axisX), Vector2.Dot(start, _axisX));
                            break;
                        case GizmoHandle.Y:
                            scale.Y *= Ratio(Vector2.Dot(now, _axisY), Vector2.Dot(start, _axisY));
                            break;
                        default:
                            // Uniform: the mouse distance from the centre relative to where the drag started.
                            var factor = Ratio(now.Length(), start.Length());
                            scale *= factor;
                            break;
                    }

                    if (snap.Enabled && snap.Scale > 0)
                        scale = new Vector2(MathF.Round(scale.X / snap.Scale) * snap.Scale, MathF.Round(scale.Y / snap.Scale) * snap.Scale);
                    return (_startPosition, _startRotation, scale);
                }

            default:
                return (_startPosition, _startRotation, _startScale);
        }
    }

    private static float Ratio(float now, float start) => MathF.Abs(start) < 1e-3f ? 1f : now / start;

    // Snaps only the dragged axes (an X move keeps the Y value exactly).
    private static Vector2 SnapVector(Vector2 value, float step, GizmoHandle handle) => handle switch
    {
        GizmoHandle.X => value with { X = MathF.Round(value.X / step) * step },
        GizmoHandle.Y => value with { Y = MathF.Round(value.Y / step) * step },
        _ => new Vector2(MathF.Round(value.X / step) * step, MathF.Round(value.Y / step) * step),
    };

    public void EndDrag() => Active = GizmoHandle.None;

    /// <summary>Draws the handles of a node at global <paramref name="origin"/> into <paramref name="lines"/> (z = 0 plane).</summary>
    public void Draw(DebugLines lines, EditorCamera camera, Vector2 viewPixels, Vector2 origin, float rotation)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select)
            return;
        var unit = 1f / camera.Zoom2D;
        var length = Size * unit;
        var o = new Vector3(origin, 0f);
        if (Mode == GizmoMode.Rotate)
        {
            var color = Hot(GizmoHandle.Z) ? HotColor : RingColor;
            const int segments = 64;
            var previous = o + new Vector3(length, 0, 0);
            for (var i = 1; i <= segments; i++)
            {
                var (sin, cos) = MathF.SinCos(i * MathF.Tau / segments);
                var point = o + new Vector3(cos * length, sin * length, 0);
                lines.AddLine(previous, point, color);
                previous = point;
            }

            // A spoke shows the current angle.
            var (s, c) = MathF.SinCos(rotation);
            lines.AddLine(o, o + new Vector3(c, s, 0) * length, color);
            return;
        }

        var (x, y) = Axes(rotation);
        DrawAxis(lines, o, x, length, unit, Hot(GizmoHandle.X) ? HotColor : XColor);
        DrawAxis(lines, o, y, length, unit, Hot(GizmoHandle.Y) ? HotColor : YColor);
        var half = Grab * unit;
        var centerColor = Hot(GizmoHandle.Center) ? HotColor : CenterColor;
        var a = o + new Vector3(-half, -half, 0);
        var b = o + new Vector3(half, -half, 0);
        var c2 = o + new Vector3(half, half, 0);
        var d = o + new Vector3(-half, half, 0);
        lines.AddLine(a, b, centerColor);
        lines.AddLine(b, c2, centerColor);
        lines.AddLine(c2, d, centerColor);
        lines.AddLine(d, a, centerColor);
    }

    private bool Hot(GizmoHandle handle) => Active == handle || (Active == GizmoHandle.None && Hovered == handle);

    private void DrawAxis(DebugLines lines, Vector3 origin, Vector2 axis, float length, float unit, Vector4 color)
    {
        var direction = new Vector3(axis, 0f);
        var side = new Vector3(-axis.Y, axis.X, 0f);
        var end = origin + direction * length;
        lines.AddLine(origin, end, color);
        // Lines are one pixel wide: a parallel line makes the shaft readable.
        lines.AddLine(origin + side * unit, end + side * unit, color);
        var tip = 9f * Shared.PixelScale * unit;
        if (Mode == GizmoMode.Scale)
        {
            // A square end for scale handles.
            var h = tip * 0.5f;
            lines.AddLine(end + side * h, end + direction * tip + side * h, color);
            lines.AddLine(end + direction * tip + side * h, end + direction * tip - side * h, color);
            lines.AddLine(end + direction * tip - side * h, end - side * h, color);
            lines.AddLine(end - side * h, end + side * h, color);
            return;
        }

        lines.AddLine(end + direction * tip, end + side * tip * 0.5f, color);
        lines.AddLine(end + direction * tip, end - side * tip * 0.5f, color);
        lines.AddLine(end + side * tip * 0.5f, end - side * tip * 0.5f, color);
    }
}
