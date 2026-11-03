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

/// <summary>
/// Where the gizmo of several selected nodes sits and what they rotate and scale around (toolbar pivot button; Blender's
/// pivot point).
/// </summary>
public enum GizmoPivot
{
    /// <summary>The average of the targets' global positions; rotate and scale around it.</summary>
    Median,

    /// <summary>The active node (<see cref="Selection.Primary"/>); rotate and scale around its origin.</summary>
    Active,

    /// <summary>The gizmo at the median point; each node rotates and scales around its own origin.</summary>
    Individual,
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

    /// <summary>How close (points) the mouse must be to a handle to grab it (other handles' grab distance too).</summary>
    public const float GrabPixels = 9f;

    /// <summary>Width (points) of the shafts and rings; a hovered handle is <see cref="HoverLineWidth"/> wide.</summary>
    public const float LineWidth = 3f;

    /// <summary>Width (points) of a hovered or dragged handle's shaft or ring.</summary>
    public const float HoverLineWidth = 4.5f;

    /// <summary>How far (points) outside a drawn shaft, ring or head a click still grabs it.</summary>
    public const float GrabMargin = 5f;

    // Shapes as fractions of the handle length.
    internal const float CenterGap = 0.14f;   // the shafts start this far out, clear of the centre handle
    internal const float ConeHeight = 0.24f;
    internal const float ConeRadius = 0.075f;
    internal const float CubeHalf = 0.065f;
    internal const float CenterRadius = 0.075f;

    private const int RingSegments = 96;

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
    private Vector3 _dragStartDirection; // rotate: unit vector in the ring's plane from the pivot towards the grab point
    private float _dragRadians;          // rotate: the applied (snapped) turn, for the drawn sector

    public GizmoMode Mode { get; set; } = GizmoMode.Translate;

    /// <summary>Framebuffer pixels per point: handle sizes and grab distances are in points (2 on Retina).</summary>
    public float PixelScale
    {
        get;
        set => field = MathF.Max(0.25f, value);
    } = 1f;

    private float Size => SizePixels * PixelScale;
    private float Grab => GrabPixels * PixelScale;
    private float GrabMarginPixels => GrabMargin * PixelScale;

    /// <summary>Handles follow the node's rotation (local) or the world axes (global). Scale is always local.</summary>
    public bool Local { get; set; }

    public GizmoSnap Snap { get; set; }

    /// <summary>The pivot of a multi-node drag (lasts for the session, like the mode, space and snap).</summary>
    public GizmoPivot Pivot { get; set; }

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

    /// <summary>
    /// The handle drawn under <paramref name="mouse"/> (view pixels), or None: a shaft, head or ring within
    /// <see cref="GrabMargin"/> of its drawn edge (the nearest wins; a ring's front half before its back half).
    /// </summary>
    public GizmoHandle HitTest(EditorCamera camera, Vector2 viewSize, Vector3 origin, Quaternion rotation, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select || !camera.Project(origin, viewSize.X, viewSize.Y, out var center))
            return GizmoHandle.None;

        var length = Size * camera.WorldPerPixel(origin, viewSize.Y);
        var axes = Axes(rotation);
        var best = GizmoHandle.None;
        var bestDistance = GrabMarginPixels;
        var halfWidth = HoverLineWidth * PixelScale * 0.5f;

        if (Mode != GizmoMode.Rotate)
        {
            var centerRadius = MathF.Max(CenterRadius * Size, Grab * 0.6f);
            if (Vector2.Distance(mouse, center) - centerRadius <= GrabMarginPixels * 0.5f)
                return GizmoHandle.Center;
        }

        for (var h = GizmoHandle.X; h <= GizmoHandle.Z; h++)
        {
            var axis = AxisOf(h, axes);
            float distance;
            if (Mode == GizmoMode.Rotate)
            {
                distance = DistanceToRing(camera, viewSize, origin, axis, length, mouse) - halfWidth;
            }
            else
            {
                if (AxisVisibility(camera, origin, axis) <= 0.05f
                    || !camera.Project(origin + axis * (length * CenterGap), viewSize.X, viewSize.Y, out var start)
                    || !camera.Project(origin + axis * length, viewSize.X, viewSize.Y, out var end))
                    continue;
                var head = Mode == GizmoMode.Translate ? origin + axis * (length * (1f - ConeHeight * 0.4f)) : origin + axis * length;
                camera.Project(head, viewSize.X, viewSize.Y, out var headPixel);
                var headRadius = (Mode == GizmoMode.Translate ? ConeRadius * 1.6f : CubeHalf * 1.5f) * Size;
                distance = MathF.Min(DistanceToSegment(mouse, start, end) - halfWidth, Vector2.Distance(mouse, headPixel) - headRadius);
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
        _dragRadians = 0f;
        if (Mode == GizmoMode.Rotate && handle != GizmoHandle.Center)
        {
            var onPlane = IntersectPlane(origin, direction, position, _dragAxis) - position;
            onPlane -= _dragAxis * Vector3.Dot(onPlane, _dragAxis);
            _dragStartDirection = onPlane.LengthSquared() > 1e-12f ? Vector3.Normalize(onPlane) : Perpendiculars(_dragAxis).U;
        }
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
                    _dragRadians = radians;
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

    /// <summary>
    /// Draws the handles into <paramref name="list"/> (the view's overlay gizmos, no depth test): shafts and rings
    /// <see cref="LineWidth"/> points wide, solid shaded cones (translate) or cubes (scale), the centre handle, far
    /// handles first. A hovered handle is wider and lighter; while dragging, the dragged one is yellow and the rest fade.
    /// </summary>
    public void Draw(GizmoDrawList list, EditorCamera camera, Vector2 viewSize, Vector3 origin, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);
        if (Mode == GizmoMode.Select)
            return;
        list.SetCamera(camera.Position, camera.Forward, camera.Up, orthographic: false);
        var length = Size * camera.WorldPerPixel(origin, viewSize.Y);
        var axes = Axes(rotation);
        if (Mode == GizmoMode.Rotate)
        {
            DrawRotate(list, camera, origin, axes, length);
            return;
        }

        // Far to near: the solid heads have no depth buffer, so a near head must draw over a far one.
        Span<int> order = [0, 1, 2];
        Span<float> depth = stackalloc float[3];
        for (var i = 0; i < 3; i++)
            depth[i] = Vector3.Dot(AxisOf(GizmoHandle.X + i, axes), camera.Forward);
        for (var i = 1; i < 3; i++)
            for (var j = i; j > 0 && depth[order[j]] > depth[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);

        var centerDrawn = false; // after the axes pointing away from the camera, before those pointing at it
        for (var k = 0; k < 3; k++)
        {
            var h = GizmoHandle.X + order[k];
            if (depth[order[k]] < 0f && !centerDrawn)
            {
                DrawCenter(list, camera, origin, axes, length);
                centerDrawn = true;
            }

            var axis = AxisOf(h, axes);
            var visibility = AxisVisibility(camera, origin, axis);
            if (visibility <= 0f)
                continue;
            var color = ColorOf(h);
            color.W *= visibility;
            var width = WidthOf(h);
            var shaftStart = origin + axis * (length * CenterGap);
            if (Mode == GizmoMode.Translate)
            {
                var coneBase = origin + axis * (length * (1f - ConeHeight));
                list.Line(shaftStart, coneBase, color, width);
                list.Cone(origin + axis * length, axis, length * ConeHeight, length * ConeRadius * (IsLit(h) ? 1.15f : 1f), color);
            }
            else
            {
                var cubeCenter = origin + axis * (length - length * CubeHalf);
                list.Line(shaftStart, cubeCenter, color, width);
                var half = length * CubeHalf * (IsLit(h) ? 1.15f : 1f);
                list.Box(cubeCenter, axes.X * half, axes.Y * half, axes.Z * half, color);
            }
        }

        if (!centerDrawn)
            DrawCenter(list, camera, origin, axes, length);
    }

    private void DrawCenter(GizmoDrawList list, EditorCamera camera, Vector3 origin, (Vector3 X, Vector3 Y, Vector3 Z) axes, float length)
    {
        var lit = IsLit(GizmoHandle.Center);
        var color = lit ? (Active == GizmoHandle.Center ? Highlight : Hover(CenterColor)) : CenterColor;
        if (IsDragging && Active != GizmoHandle.Center)
            color.W *= DimmedAlpha;
        var radius = length * CenterRadius * (lit ? 1.2f : 1f);
        if (Mode == GizmoMode.Scale)
        {
            list.Box(origin, axes.X * radius, axes.Y * radius, axes.Z * radius, color);
            return;
        }

        // A screen-facing disc with a dark rim, like a small sphere.
        var pixel = length / Size;
        list.Disc(origin, camera.Right, camera.Up, radius + pixel * 1.5f * PixelScale, Outline with { W = Outline.W * color.W });
        list.Disc(origin, camera.Right, camera.Up, radius, color);
    }

    private void DrawRotate(GizmoDrawList list, EditorCamera camera, Vector3 origin, (Vector3 X, Vector3 Y, Vector3 Z) axes, float length)
    {
        // The silhouette of the sphere the rings lie on, then every ring's far half (faint), then the near halves.
        list.Circle(origin, camera.Right, camera.Up, length, SilhouetteColor, PixelScale * 1.5f, RingSegments);
        for (var pass = 0; pass < 2; pass++)
            for (var h = GizmoHandle.X; h <= GizmoHandle.Z; h++)
            {
                var axis = AxisOf(h, axes);
                var color = ColorOf(h);
                if (pass == 0)
                    color.W *= BackAlpha;
                var width = WidthOf(h);
                var (u, v) = Perpendiculars(axis);
                var previous = origin + u * length;
                for (var i = 1; i <= RingSegments; i++)
                {
                    var (sin, cos) = MathF.SinCos(i * MathF.Tau / RingSegments);
                    var point = origin + (u * cos + v * sin) * length;
                    var back = Vector3.Dot((previous + point) * 0.5f - origin, camera.Forward) > 0f;
                    if (back == (pass == 0))
                        list.Line(previous, point, color, width);
                    previous = point;
                }
            }

        if (Active is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z && MathF.Abs(_dragRadians) > 1e-4f)
            DrawSector(list, origin, _dragAxis, length);
    }

    // The turn so far: a translucent pie from the grab direction through the applied angle, edged by two spokes.
    private void DrawSector(GizmoDrawList list, Vector3 origin, Vector3 axis, float radius)
    {
        var start = _dragStartDirection;
        var side = Vector3.Cross(axis, start);
        var steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(_dragRadians) / MathF.Tau * RingSegments), 1, RingSegments * 4);
        var previous = origin + start * radius;
        for (var i = 1; i <= steps; i++)
        {
            var (sin, cos) = MathF.SinCos(_dragRadians * i / steps);
            var point = origin + (start * cos + side * sin) * radius;
            list.Triangle(origin, previous, point, SectorColor);
            previous = point;
        }

        var width = LineWidth * PixelScale * 0.6f;
        list.Line(origin, origin + start * radius, Highlight, width);
        list.Line(origin, previous, Highlight, width);
    }

    private static readonly Vector4 Highlight = new(1f, 0.82f, 0.18f, 1f);
    private static readonly Vector4 CenterColor = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Vector4 Outline = new(0.08f, 0.08f, 0.1f, 0.85f);
    private static readonly Vector4 SilhouetteColor = new(0.85f, 0.85f, 0.85f, 0.35f);
    private static readonly Vector4 SectorColor = new(1f, 0.82f, 0.18f, 0.22f);
    private const float BackAlpha = 0.28f;
    private const float DimmedAlpha = 0.3f;

    private bool IsLit(GizmoHandle handle) => handle == Active || (Active == GizmoHandle.None && handle == Hovered);

    private float WidthOf(GizmoHandle handle) => (IsLit(handle) ? HoverLineWidth : LineWidth) * PixelScale;

    private Vector4 ColorOf(GizmoHandle handle)
    {
        if (handle == Active)
            return Highlight;
        var color = handle switch
        {
            GizmoHandle.X => new Vector4(0.98f, 0.24f, 0.3f, 1f),
            GizmoHandle.Y => new Vector4(0.48f, 0.9f, 0.22f, 1f),
            _ => new Vector4(0.2f, 0.52f, 1f, 1f),
        };
        if (Active == GizmoHandle.None && handle == Hovered)
            return Hover(color);
        if (IsDragging)
            color.W *= DimmedAlpha;
        return color;
    }

    // Lighter, toward white: the hovered handle.
    private static Vector4 Hover(Vector4 color) =>
        new(color.X + (1f - color.X) * 0.45f, color.Y + (1f - color.Y) * 0.45f, color.Z + (1f - color.Z) * 0.45f, color.W);

    // 1, fading to 0 as the axis turns to point at the camera (its shaft shrinks to a dot and cannot be dragged well).
    private static float AxisVisibility(EditorCamera camera, Vector3 origin, Vector3 axis)
    {
        var toOrigin = origin - camera.Position;
        var along = toOrigin.LengthSquared() > 1e-12f ? MathF.Abs(Vector3.Dot(Vector3.Normalize(toOrigin), axis)) : 0f;
        return Math.Clamp((0.985f - along) / 0.04f, 0f, 1f);
    }

    // Screen distance to the ring; its far half counts as GrabMargin farther, so where the halves cross the near one wins.
    private float DistanceToRing(EditorCamera camera, Vector2 viewSize, Vector3 center, Vector3 axis, float radius, Vector2 mouse)
    {
        var (u, v) = Perpendiculars(axis);
        var best = float.MaxValue;
        var previousPoint = center + u * radius;
        var hasPrevious = camera.Project(previousPoint, viewSize.X, viewSize.Y, out var previous);
        for (var i = 1; i <= RingSegments; i++)
        {
            var a = i * MathF.Tau / RingSegments;
            var world = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            var visible = camera.Project(world, viewSize.X, viewSize.Y, out var point);
            if (visible && hasPrevious)
            {
                var back = Vector3.Dot((previousPoint + world) * 0.5f - center, camera.Forward) > 0f;
                best = MathF.Min(best, DistanceToSegment(mouse, previous, point) + (back ? GrabMarginPixels : 0f));
            }

            previous = point;
            previousPoint = world;
            hasPrevious = visible;
        }

        return best;
    }

    internal static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis) => GizmoDrawList.Perpendiculars(axis);

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
