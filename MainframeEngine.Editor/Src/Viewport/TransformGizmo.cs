using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>What the transform gizmo edits (toolbar W/E/R; Q selects only).</summary>
public enum GizmoMode
{
    Select,
    Translate,
    Rotate,
    Scale,
}

/// <summary>The gizmo handle under the mouse or being dragged.</summary>
public enum GizmoHandle
{
    None,
    X,
    Y,
    Z,

    /// <summary>The centre: free move in the view plane (translate) or uniform scale.</summary>
    Center,
}

/// <summary>Snapping steps (toolbar snap toggle).</summary>
public readonly record struct GizmoSnap(bool Enabled, float Translate = 0.5f, float RotateDegrees = 15f, float Scale = 0.1f);

/// <summary>
/// Translate/rotate/scale handles for one node: constant on-screen size, hit-tested by screen-space distance to the
/// projected axes (translate, scale) or rings (rotate), dragged by intersecting the mouse ray with the axis line or
/// the ring's plane, local or global orientation, optional snapping. Pure math (unit-tested); the viewport draws it with
/// <see cref="Draw"/> into the overlay lines and applies the resulting transform.
/// </summary>
public sealed class TransformGizmo
{
    /// <summary>Handle length on screen, in pixels.</summary>
    public const float SizePixels = 90f;

    /// <summary>How close (pixels) the mouse must be to a handle to grab it.</summary>
    public const float GrabPixels = 9f;

    private const int RingSegments = 48;

    // Drag state.
    private Vector3 _startPosition;
    private Quaternion _startRotation;
    private Vector3 _startScale;
    private Vector3 _dragAxis;            // world axis of the dragged handle
    private Vector3 _dragStartPoint;      // where the mouse ray hit the axis/plane at the start
    private Vector2 _dragStartMouse;
    private Vector2 _dragCenterPixel;
    private float _dragWorldPerPixel;
    private float _dragLastAngle;
    private float _dragAngle;            // accumulated screen angle (unwrapped, so several turns work)

    public GizmoMode Mode { get; set; } = GizmoMode.Translate;

    /// <summary>Framebuffer pixels per point: handle sizes and grab distances are in points (2 on Retina).</summary>
    public float PixelScale
    {
        get;
        set => field = MathF.Max(0.25f, value);
    } = 1f;

    private float Size => SizePixels * PixelScale;
    private float Grab => GrabPixels * PixelScale;

    /// <summary>Handles follow the node's rotation (local) or the world axes (global). Scale is always local.</summary>
    public bool Local { get; set; }

    public GizmoSnap Snap { get; set; }

    /// <summary>The handle being dragged (None when idle).</summary>
    public GizmoHandle Active { get; private set; }

    /// <summary>The handle under the mouse (highlighted).</summary>
    public GizmoHandle Hovered { get; set; }

    public bool IsDragging => Active != GizmoHandle.None;

    /// <summary>World axis directions for a node with global rotation <paramref name="rotation"/>.</summary>
    public (Vector3 X, Vector3 Y, Vector3 Z) Axes(Quaternion rotation)
    {
        if (!Local && Mode != GizmoMode.Scale)
            return (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        return (Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
    }

    private static Vector3 AxisOf(GizmoHandle handle, (Vector3 X, Vector3 Y, Vector3 Z) axes) => handle switch
    {
        GizmoHandle.X => axes.X,
        GizmoHandle.Y => axes.Y,
        GizmoHandle.Z => axes.Z,
        _ => Vector3.Zero,
    };

    /// <summary>The handle within <see cref="GrabPixels"/> of <paramref name="mouse"/> (view pixels), or None.</summary>
    public GizmoHandle HitTest(EditorCamera camera, Vector2 viewSize, Vector3 origin, Quaternion rotation, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select || !camera.Project(origin, viewSize.X, viewSize.Y, out var center))
            return GizmoHandle.None;

        var length = Size * camera.WorldPerPixel(origin, viewSize.Y);
        var axes = Axes(rotation);
        var best = GizmoHandle.None;
        var bestDistance = Grab;

        if (Mode != GizmoMode.Rotate && Vector2.Distance(mouse, center) <= Grab * 1.2f)
            return GizmoHandle.Center;

        for (var h = GizmoHandle.X; h <= GizmoHandle.Z; h++)
        {
            var axis = AxisOf(h, axes);
            float distance;
            if (Mode == GizmoMode.Rotate)
            {
                distance = DistanceToRing(camera, viewSize, origin, axis, length, mouse);
            }
            else
            {
                if (!camera.Project(origin + axis * length, viewSize.X, viewSize.Y, out var end))
                    continue;
                distance = DistanceToSegment(mouse, center, end);
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = h;
            }
        }

        return best;
    }

    /// <summary>Starts dragging <paramref name="handle"/> of a node at the given global pose.</summary>
    public void BeginDrag(GizmoHandle handle, EditorCamera camera, Vector2 viewSize, Vector2 mouse,
        Vector3 position, Quaternion rotation, Vector3 scale)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (handle == GizmoHandle.None || Mode == GizmoMode.Select)
            return;
        Active = handle;
        _startPosition = position;
        _startRotation = rotation;
        _startScale = scale;
        _dragStartMouse = mouse;
        _dragAxis = AxisOf(handle, Axes(rotation));
        camera.Project(position, viewSize.X, viewSize.Y, out _dragCenterPixel);
        _dragWorldPerPixel = camera.WorldPerPixel(position, viewSize.Y);
        var (origin, direction) = camera.Ray(mouse.X, mouse.Y, viewSize.X, viewSize.Y);
        _dragStartPoint = handle == GizmoHandle.Center
            ? IntersectPlane(origin, direction, position, camera.Forward)
            : ClosestPointOnAxis(origin, direction, position, _dragAxis);
        _dragLastAngle = MathF.Atan2(mouse.Y - _dragCenterPixel.Y, mouse.X - _dragCenterPixel.X);
        _dragAngle = 0f;
    }

    /// <summary>
    /// The node's new global pose for the mouse at <paramref name="mouse"/> while dragging (snapped when
    /// <see cref="Snap"/> is enabled).
    /// </summary>
    public (Vector3 Position, Quaternion Rotation, Vector3 Scale) Drag(EditorCamera camera, Vector2 viewSize, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (Active == GizmoHandle.None)
            return (_startPosition, _startRotation, _startScale);
        var (origin, direction) = camera.Ray(mouse.X, mouse.Y, viewSize.X, viewSize.Y);

        switch (Mode)
        {
            case GizmoMode.Translate:
                {
                    Vector3 delta;
                    if (Active == GizmoHandle.Center)
                    {
                        delta = IntersectPlane(origin, direction, _startPosition, camera.Forward) - _dragStartPoint;
                        if (Snap.Enabled)
                            delta = new Vector3(SnapTo(delta.X, Snap.Translate), SnapTo(delta.Y, Snap.Translate), SnapTo(delta.Z, Snap.Translate));
                    }
                    else
                    {
                        var along = Vector3.Dot(ClosestPointOnAxis(origin, direction, _startPosition, _dragAxis) - _dragStartPoint, _dragAxis);
                        if (Snap.Enabled)
                            along = SnapTo(along, Snap.Translate);
                        delta = _dragAxis * along;
                    }

                    return (_startPosition + delta, _startRotation, _startScale);
                }

            case GizmoMode.Rotate:
                {
                    var current = MathF.Atan2(mouse.Y - _dragCenterPixel.Y, mouse.X - _dragCenterPixel.X);
                    _dragAngle += WrapAngle(current - _dragLastAngle);
                    _dragLastAngle = current;
                    // Screen y points down, so a counter-clockwise screen turn is a negative angle; it is a positive turn
                    // about an axis pointing at the viewer.
                    var sign = Vector3.Dot(_dragAxis, camera.Forward) > 0 ? 1f : -1f;
                    var radians = _dragAngle * sign;
                    if (Snap.Enabled)
                        radians = float.DegreesToRadians(SnapTo(float.RadiansToDegrees(radians), Snap.RotateDegrees));
                    var turn = Quaternion.CreateFromAxisAngle(_dragAxis, radians);
                    return (_startPosition, Quaternion.Normalize(turn * _startRotation), _startScale);
                }

            case GizmoMode.Scale:
                {
                    float factor;
                    if (Active == GizmoHandle.Center)
                    {
                        factor = 1f + (mouse.X - _dragStartMouse.X - (mouse.Y - _dragStartMouse.Y)) / (Size * 1.5f);
                    }
                    else
                    {
                        var along = Vector3.Dot(ClosestPointOnAxis(origin, direction, _startPosition, _dragAxis) - _startPosition, _dragAxis);
                        var start = Vector3.Dot(_dragStartPoint - _startPosition, _dragAxis);
                        factor = MathF.Abs(start) < 1e-5f ? 1f : along / start;
                    }

                    if (Snap.Enabled)
                        factor = 1f + SnapTo(factor - 1f, Snap.Scale);
                    factor = MathF.Max(factor, 0.001f);
                    var scale = Active switch
                    {
                        GizmoHandle.X => _startScale with { X = _startScale.X * factor },
                        GizmoHandle.Y => _startScale with { Y = _startScale.Y * factor },
                        GizmoHandle.Z => _startScale with { Z = _startScale.Z * factor },
                        _ => _startScale * factor,
                    };
                    return (_startPosition, _startRotation, scale);
                }

            default:
                return (_startPosition, _startRotation, _startScale);
        }
    }

    /// <summary>Ends the drag (the caller commits the change to the undo history).</summary>
    public void EndDrag() => Active = GizmoHandle.None;

    /// <summary>World units per pixel captured at drag start (for tests and constant-size drawing).</summary>
    public float DragWorldPerPixel => _dragWorldPerPixel;

    /// <summary>Draws the handles into <paramref name="lines"/> (the viewport's overlay lines, no depth test).</summary>
    public void Draw(DebugLines lines, EditorCamera camera, Vector2 viewSize, Vector3 origin, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select)
            return;
        var length = Size * camera.WorldPerPixel(origin, viewSize.Y);
        var axes = Axes(rotation);
        for (var h = GizmoHandle.X; h <= GizmoHandle.Z; h++)
        {
            var axis = AxisOf(h, axes);
            var color = ColorOf(h);
            switch (Mode)
            {
                case GizmoMode.Translate:
                    ThickLine(lines, camera, origin, origin + axis * length, length / Size, color);
                    DrawCone(lines, origin + axis * length, axis, length * 0.14f, length * 0.05f, color);
                    break;
                case GizmoMode.Scale:
                    ThickLine(lines, camera, origin, origin + axis * length, length / Size, color);
                    DrawCube(lines, origin + axis * length, axes, length * 0.05f, color);
                    break;
                case GizmoMode.Rotate:
                    DrawRing(lines, origin, axis, length, color);
                    DrawRing(lines, origin, axis, length + length / Size * PixelScale, color);
                    break;
            }
        }

        if (Mode != GizmoMode.Rotate)
        {
            var c = Hovered == GizmoHandle.Center || Active == GizmoHandle.Center ? Highlight : new Vector4(0.9f, 0.9f, 0.9f, 1f);
            var r = length * 0.07f;
            var right = camera.Right * r;
            var up = camera.Up * r;
            lines.AddLine(origin - right - up, origin + right - up, c);
            lines.AddLine(origin + right - up, origin + right + up, c);
            lines.AddLine(origin + right + up, origin - right + up, c);
            lines.AddLine(origin - right + up, origin - right - up, c);
        }
    }

    private static readonly Vector4 Highlight = new(1f, 0.85f, 0.2f, 1f);

    private Vector4 ColorOf(GizmoHandle handle)
    {
        if (handle == Active || (Active == GizmoHandle.None && handle == Hovered))
            return Highlight;
        return handle switch
        {
            GizmoHandle.X => new Vector4(0.96f, 0.26f, 0.32f, 1f),
            GizmoHandle.Y => new Vector4(0.45f, 0.86f, 0.25f, 1f),
            _ => new Vector4(0.25f, 0.55f, 1f, 1f),
        };
    }

    // Lines are one framebuffer pixel wide: draw handles as a few parallel lines (screen-space offsets) to read on HiDPI.
    private void ThickLine(DebugLines lines, EditorCamera camera, Vector3 a, Vector3 b, float worldPerPixel, Vector4 color)
    {
        var side = Vector3.Cross(b - a, camera.Forward);
        side = side.LengthSquared() > 1e-12f ? Vector3.Normalize(side) * worldPerPixel : Vector3.Zero;
        var count = Math.Max(1, (int)MathF.Round(PixelScale * 1.5f));
        for (var i = 0; i < count; i++)
        {
            var offset = side * (i - (count - 1) * 0.5f);
            lines.AddLine(a + offset, b + offset, color);
        }
    }

    private static void DrawCone(DebugLines lines, Vector3 tip, Vector3 axis, float height, float radius, Vector4 color)
    {
        var (u, v) = Perpendiculars(axis);
        var baseCenter = tip - axis * height;
        const int segments = 8;
        var previous = baseCenter + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var a = i * MathF.Tau / segments;
            var point = baseCenter + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            lines.AddLine(previous, point, color);
            lines.AddLine(point, tip, color);
            previous = point;
        }
    }

    private static void DrawCube(DebugLines lines, Vector3 center, (Vector3 X, Vector3 Y, Vector3 Z) axes, float half, Vector4 color)
    {
        var transform = new Transform3D(new Basis(axes.X * half, axes.Y * half, axes.Z * half), center);
        lines.AddBox(transform, Vector3.One, color);
    }

    private static void DrawRing(DebugLines lines, Vector3 center, Vector3 axis, float radius, Vector4 color)
    {
        var (u, v) = Perpendiculars(axis);
        var previous = center + u * radius;
        for (var i = 1; i <= RingSegments; i++)
        {
            var a = i * MathF.Tau / RingSegments;
            var point = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            lines.AddLine(previous, point, color);
            previous = point;
        }
    }

    private static float DistanceToRing(EditorCamera camera, Vector2 viewSize, Vector3 center, Vector3 axis, float radius, Vector2 mouse)
    {
        var (u, v) = Perpendiculars(axis);
        var best = float.MaxValue;
        var hasPrevious = camera.Project(center + u * radius, viewSize.X, viewSize.Y, out var previous);
        for (var i = 1; i <= RingSegments; i++)
        {
            var a = i * MathF.Tau / RingSegments;
            var visible = camera.Project(center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius, viewSize.X, viewSize.Y, out var point);
            if (visible && hasPrevious)
                best = MathF.Min(best, DistanceToSegment(mouse, previous, point));
            previous = point;
            hasPrevious = visible;
        }

        return best;
    }

    internal static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var reference = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, reference));
        var v = Vector3.Cross(axis, u);
        return (u, v);
    }

    internal static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        var t = lengthSquared < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>The point on the line (<paramref name="point"/>, <paramref name="axis"/>) closest to the ray.</summary>
    internal static Vector3 ClosestPointOnAxis(Vector3 rayOrigin, Vector3 rayDirection, Vector3 point, Vector3 axis)
    {
        var w = point - rayOrigin;
        var b = Vector3.Dot(axis, rayDirection);
        var denominator = 1f - b * b; // both unit vectors
        if (MathF.Abs(denominator) < 1e-6f)
            return point; // the ray runs along the axis: no movement
        var d = Vector3.Dot(axis, w);
        var e = Vector3.Dot(rayDirection, w);
        var t = (b * e - d) / denominator;
        return point + axis * t;
    }

    /// <summary>The ray's intersection with the plane through <paramref name="point"/> with <paramref name="normal"/> (the point when parallel).</summary>
    internal static Vector3 IntersectPlane(Vector3 rayOrigin, Vector3 rayDirection, Vector3 point, Vector3 normal)
    {
        var denominator = Vector3.Dot(normal, rayDirection);
        if (MathF.Abs(denominator) < 1e-6f)
            return point;
        var t = Vector3.Dot(point - rayOrigin, normal) / denominator;
        return rayOrigin + rayDirection * t;
    }

    internal static float SnapTo(float value, float step) => step <= 0 ? value : MathF.Round(value / step) * step;

    private static float WrapAngle(float radians)
    {
        while (radians > MathF.PI)
            radians -= MathF.Tau;
        while (radians < -MathF.PI)
            radians += MathF.Tau;
        return radians;
    }
}
